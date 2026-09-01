using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Native;

/// <summary>Outcome of trying to release one volume so the sectors underneath it can be rewritten.</summary>
public sealed record DismountResult(string VolumePath, bool Dismounted, bool WasLocked, string? Error);

/// <summary>
/// Locks and dismounts mounted volumes so that raw writes to the disk underneath are permitted.
///
/// <b>Why this exists.</b> Since Vista, Windows denies direct sector writes that land inside a
/// <i>mounted</i> volume's extents — the write fails with ERROR_ACCESS_DENIED (win32 5) unless the
/// volume is dismounted, the writer holds the volume lock, or the whole disk is offline. That is what
/// bit `diskpart clean` on a USB stick whose exFAT volume Windows was still holding:
/// <c>VDS Basic Provider: Cannot zero sectors on disk \\?\PhysicalDrive3. Error code: 5</c>.
///
/// <b>Why not just take the disk offline.</b> <c>Set-Disk -IsOffline</c> is the usual answer and is what
/// the clone engine's docs assume, but Windows rejects it for removable media ("Removable media cannot
/// be set to offline") — and removable disks are exactly DiskForge's default-allowed target. Dismounting
/// the volume works on both.
///
/// This file deliberately carries its own P/Invokes: <see cref="NativeMethods"/> is the strictly
/// read-only probe surface and must stay that way.
/// </summary>
public static class VolumeControl
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    /// <summary>Locking fails while anything still has a file open; a short retry rides out transients.</summary>
    private const int LockAttempts = 5;
    private const int LockRetryDelayMs = 200;

    /// <summary>
    /// Releases one volume. The lock is attempted first because a clean lock+dismount is the polite
    /// path, but a dismount is issued either way: an unlockable volume (something holds a file open) is
    /// precisely the case that broke the format, and the user has already confirmed the erase.
    /// </summary>
    public static DismountResult Dismount(string volumePath)
    {
        var path = Normalize(volumePath);
        if (path is null)
            return new DismountResult(volumePath, false, false, "not a recognisable volume path");

        try
        {
            using var handle = CreateFile(
                path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
                return new DismountResult(path, false, false,
                    $"could not open the volume (win32 {Marshal.GetLastWin32Error()})");

            var locked = TryLock(handle);

            if (!DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return new DismountResult(path, false, locked,
                    $"dismount failed (win32 {Marshal.GetLastWin32Error()})");

            return new DismountResult(path, true, locked, null);
        }
        catch (Exception ex)
        {
            return new DismountResult(path, false, false, ex.Message);
        }
    }

    /// <summary>
    /// Forces a volume to write its cached data to disk, without dismounting it or invalidating
    /// anyone's open handles.
    ///
    /// This matters for any raw read of a live disk. NTFS acknowledges a write long before the lazy
    /// writer commits the file data and its MFT record, so a sector-level copy started moments after a
    /// file was written can legitimately read a filesystem that does not contain that file. It is not
    /// a theoretical race: a clone taken immediately after writing a file arrived on the target without
    /// it. Locking the volume would also flush, but a lock fails whenever anything has a file open,
    /// which on a real source is most of the time. Flushing the volume handle works regardless.
    ///
    /// This does not replace a VSS snapshot. It makes the copy consistent as of the moment it starts
    /// rather than missing writes that had already been acknowledged; anything written <i>during</i>
    /// the copy is still only crash-consistent.
    /// </summary>
    public static bool Flush(string volumePath)
    {
        var path = Normalize(volumePath);
        if (path is null) return false;

        try
        {
            using var handle = CreateFile(
                path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            return !handle.IsInvalid && FlushFileBuffers(handle);
        }
        catch
        {
            return false; // best effort: never fail an operation over a flush
        }
    }

    /// <summary>
    /// Locks and dismounts a volume and <b>keeps holding it</b> until the returned object is disposed.
    ///
    /// <see cref="Dismount"/> closes its handle immediately, which releases the lock and lets Windows
    /// remount the volume the moment anything touches it. That is fine for diskpart and mkfs, which
    /// rewrite the partition table in one short burst, but not for a clone: that write covers every
    /// sector of the disk and runs for minutes, so a volume remounting midway turns the rest of the
    /// copy into ERROR_ACCESS_DENIED. Holding the lock for the whole write is what stops that.
    /// </summary>
    public static HeldVolume Hold(string volumePath)
    {
        var path = Normalize(volumePath);
        if (path is null)
            return new HeldVolume(volumePath, null, false, false, "not a recognisable volume path");

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(
                path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                handle.Dispose();
                return new HeldVolume(path, null, false, false, $"could not open the volume (win32 {err})");
            }

            var locked = TryLock(handle);
            var dismounted = DeviceIoControl(
                handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            if (!dismounted)
            {
                var err = Marshal.GetLastWin32Error();
                // Keep the handle even so: an unlockable, undismountable volume is still less dangerous
                // held than released, and the caller decides whether to continue.
                return new HeldVolume(path, handle, locked, false, $"dismount failed (win32 {err})");
            }

            return new HeldVolume(path, handle, locked, true, null);
        }
        catch (Exception ex)
        {
            handle?.Dispose();
            return new HeldVolume(path, null, false, false, ex.Message);
        }
    }

    /// <summary>
    /// Tells Windows to re-read a disk's partition table.
    ///
    /// Without this, the volume layer keeps serving what it cached before the write: a drive letter
    /// that still points at a filesystem that no longer exists, typically showing 0 bytes and an
    /// Unknown health state. That phantom is not cosmetic — it is a mounted volume as far as Windows is
    /// concerned, so it can block the *next* operation's sector writes. Called after any change to the
    /// partition table or to a partition's contents.
    /// </summary>
    public static bool RefreshPartitionTable(int diskNumber)
    {
        try
        {
            using var handle = CreateFile(
                $@"\\.\PhysicalDrive{diskNumber}", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) return false;

            return DeviceIoControl(handle, IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false; // best effort: never fail an operation over a refresh
        }
    }

    private static bool TryLock(SafeFileHandle handle)
    {
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            if (DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return true;
            Thread.Sleep(LockRetryDelayMs);
        }
        return false;
    }

    /// <summary>
    /// Accepts what the enumeration actually produces — a volume GUID path (<c>\\?\Volume{…}\</c>) or a
    /// drive letter — and returns the trailing-slash-free device path CreateFile needs.
    /// </summary>
    public static string? Normalize(string volumePath)
    {
        if (string.IsNullOrWhiteSpace(volumePath)) return null;
        var v = volumePath.Trim();

        // "F", "F:", "F:\" → \\.\F:
        if (v.Length is 1 or 2 or 3 && char.IsLetter(v[0]) && (v.Length == 1 || v[1] == ':'))
            return $@"\\.\{char.ToUpperInvariant(v[0])}:";

        // \\?\Volume{GUID}\ → \\?\Volume{GUID}   (CreateFile rejects the trailing backslash)
        if (v.StartsWith(@"\\?\", StringComparison.Ordinal) || v.StartsWith(@"\\.\", StringComparison.Ordinal))
            return v.TrimEnd('\\');

        return null;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}

/// <summary>
/// A volume kept locked and dismounted for the lifetime of this object. Disposing it closes the
/// handle, which drops the lock and lets Windows mount the volume again.
/// </summary>
public sealed class HeldVolume : IDisposable
{
    private SafeFileHandle? _handle;

    internal HeldVolume(string volumePath, SafeFileHandle? handle, bool locked, bool dismounted, string? error)
    {
        VolumePath = volumePath;
        _handle = handle;
        Locked = locked;
        Dismounted = dismounted;
        Error = error;
    }

    public string VolumePath { get; }
    public bool Locked { get; }
    public bool Dismounted { get; }
    public string? Error { get; }

    /// <summary>True when the volume is both locked and dismounted, i.e. it cannot come back under us.</summary>
    public bool IsHeld => Locked && Dismounted;

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
