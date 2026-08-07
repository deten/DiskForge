using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static DiskForge.Engine.Native.NativeMethods;

namespace DiskForge.Engine.Native;

/// <summary>
/// Write-capable raw handles to <c>\\.\PhysicalDriveN</c> for the clone/image engines. Kept separate
/// from <see cref="PhysicalDriveProbe"/> (which is strictly read-only) so the probe's "no write path"
/// guarantee is not diluted.
///
/// Opening for write needs Administrator. Beyond that, Windows denies writes to sectors inside a
/// <b>mounted</b> volume, so the caller must release the disk's volumes first with
/// <c>DiskVolumeReleaser.Release(disk)</c>. Note that <c>Set-Disk -IsOffline</c> — the remedy this class
/// originally documented — is rejected outright for removable media, so it is not a substitute.
/// </summary>
public static class RawDiskAccess
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;

    /// <summary>Open a physical drive for reading. Shares read+write so a live source can be read
    /// (crash-consistent) without locking its volumes.</summary>
    public static SafeFileHandle OpenRead(int diskNumber)
        => Open(diskNumber, GENERIC_READ);

    /// <summary>Open a physical drive for read+write. The disk must be offline (no mounted volumes) or
    /// the writes over volume regions will fail with a sharing violation.</summary>
    public static SafeFileHandle OpenWrite(int diskNumber)
        => Open(diskNumber, GENERIC_READ | GENERIC_WRITE);

    private static SafeFileHandle Open(int diskNumber, uint access)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        var handle = CreateFile(
            path,
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {path} (win32 {err}). " +
                "Requires Administrator, and for writes the disk must be offline.");
        }
        return handle;
    }
}
