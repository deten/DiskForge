using DiskForge.Core.Model;
using DiskForge.Engine.Native;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Releases every volume Windows has mounted on a disk, so that whatever comes next (diskpart's
/// sector zeroing, a raw clone write, or handing the disk to the WSL kernel) is not blocked by the
/// mounted-volume write protection.
///
/// This is the step whose absence produced
/// <c>DiskPart has encountered an error: Access is denied</c> on a USB stick: Windows refuses raw
/// writes under a mounted volume, and diskpart could not dismount it on its own. Taking the disk
/// offline — the usual remedy, and what <see cref="RawDiskAccess"/>'s docs assume — is not available
/// for removable media, so the volumes are dismounted individually instead.
/// </summary>
public static class DiskVolumeReleaser
{
    /// <summary>
    /// Dismounts every mounted volume on <paramref name="disk"/>. Returns human-readable log lines;
    /// never throws, because failing to release a volume is not by itself a reason to abort — the
    /// caller's own operation will fail with a better message if it actually mattered.
    /// </summary>
    public static IReadOnlyList<string> Release(PhysicalDiskInfo disk)
        => Release(VolumePathsOn(disk), disk.Number);

    public static IReadOnlyList<string> Release(IEnumerable<string> volumePaths, int diskNumber)
    {
        var log = new List<string>();
        foreach (var path in volumePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = VolumeControl.Dismount(path);
            if (result.Dismounted)
            {
                var how = result.WasLocked ? "locked and dismounted" : "dismounted (could not lock first)";
                log.Add($"Disk {diskNumber}: {how} {result.VolumePath}.");
                Log.Information("Released volume {Volume} on disk {Disk} ({How})",
                    result.VolumePath, diskNumber, how);
            }
            else
            {
                log.Add($"Disk {diskNumber}: could not release {result.VolumePath} — {result.Error}.");
                Log.Warning("Could not release volume {Volume} on disk {Disk}: {Error}",
                    result.VolumePath, diskNumber, result.Error);
            }
        }

        if (log.Count == 0) log.Add($"Disk {diskNumber} had no mounted volumes to release.");
        return log;
    }

    /// <summary>
    /// Flushes every mounted volume on <paramref name="disk"/> so a raw read of it sees writes that
    /// Windows has acknowledged but not yet committed. Read-only in effect: nothing is dismounted and
    /// no open handle is disturbed. Best-effort and never throws.
    /// </summary>
    public static IReadOnlyList<string> FlushVolumes(PhysicalDiskInfo disk)
    {
        var log = new List<string>();
        foreach (var path in VolumePathsOn(disk).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (VolumeControl.Flush(path))
            {
                log.Add($"Disk {disk.Number}: flushed {path}.");
                Log.Information("Flushed volume {Volume} on disk {Disk}", path, disk.Number);
            }
            else
            {
                log.Add($"Disk {disk.Number}: could not flush {path}.");
                Log.Warning("Could not flush volume {Volume} on disk {Disk}", path, disk.Number);
            }
        }
        return log;
    }

    /// <summary>
    /// Releases every volume on <paramref name="disk"/> and <b>keeps them released</b> until the
    /// returned object is disposed. Use this instead of <see cref="Release(PhysicalDiskInfo)"/> for a
    /// write that runs long enough for Windows to remount underneath it — a whole-disk clone being the
    /// case that matters. Never throws; inspect <see cref="HeldDiskVolumes.AllHeld"/> to see whether
    /// every volume actually came down.
    /// </summary>
    public static HeldDiskVolumes Hold(PhysicalDiskInfo disk)
        => Hold(VolumePathsOn(disk), disk.Number);

    public static HeldDiskVolumes Hold(IEnumerable<string> volumePaths, int diskNumber)
    {
        var held = new List<HeldVolume>();
        var log = new List<string>();

        foreach (var path in volumePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var h = VolumeControl.Hold(path);
            held.Add(h);

            if (h.IsHeld)
            {
                log.Add($"Disk {diskNumber}: locked and dismounted {h.VolumePath} for the duration.");
                Log.Information("Holding volume {Volume} on disk {Disk}", h.VolumePath, diskNumber);
            }
            else
            {
                var what = h.Dismounted ? "dismounted but could not lock" : "could not release";
                log.Add($"Disk {diskNumber}: {what} {h.VolumePath}" +
                        (h.Error is null ? "." : $" — {h.Error}."));
                Log.Warning("Could not fully hold volume {Volume} on disk {Disk}: {Error}",
                    h.VolumePath, diskNumber, h.Error ?? what);
            }
        }

        if (held.Count == 0) log.Add($"Disk {diskNumber} had no mounted volumes to release.");
        return new HeldDiskVolumes(held, log);
    }

    /// <summary>
    /// Makes Windows re-read the disk after we have changed it, so the volume layer stops serving a
    /// stale cached layout (the classic symptom being a drive letter still pointing at a filesystem
    /// that no longer exists, reported as 0 bytes). Best-effort and never throws.
    /// </summary>
    public static void Refresh(int diskNumber)
    {
        if (VolumeControl.RefreshPartitionTable(diskNumber))
            Log.Information("Asked Windows to re-read the partition table on disk {Disk}", diskNumber);
        else
            Log.Warning("Could not refresh the partition table on disk {Disk}; " +
                        "Windows may briefly show a stale volume", diskNumber);
    }

    /// <summary>
    /// Every addressable volume on the disk. The volume GUID path is preferred over the drive letter —
    /// a partition can be mounted with no letter at all, and that volume still blocks sector writes.
    /// </summary>
    public static IReadOnlyList<string> VolumePathsOn(PhysicalDiskInfo disk)
    {
        var paths = new List<string>();
        foreach (var part in disk.Partitions.Where(p => !p.IsUnallocated))
        {
            if (part.Volume?.UniqueId is { Length: > 0 } id) paths.Add(id);
            else if (part.DriveLetter is { Length: > 0 } letter) paths.Add(letter);
        }
        return paths;
    }
}

/// <summary>
/// The volumes of one disk, kept locked and dismounted until this is disposed.
/// </summary>
public sealed class HeldDiskVolumes : IDisposable
{
    private readonly IReadOnlyList<HeldVolume> _held;

    internal HeldDiskVolumes(IReadOnlyList<HeldVolume> held, IReadOnlyList<string> log)
    {
        _held = held;
        Log = log;
    }

    /// <summary>Human-readable lines describing what happened to each volume.</summary>
    public IReadOnlyList<string> Log { get; }

    /// <summary>True when every volume is locked and dismounted, so none can remount mid-write.</summary>
    public bool AllHeld => _held.All(h => h.IsHeld);

    /// <summary>The volumes that could not be fully held, for a caller that wants to name them.</summary>
    public IReadOnlyList<string> NotHeld => _held.Where(h => !h.IsHeld).Select(h => h.VolumePath).ToList();

    public void Dispose()
    {
        foreach (var h in _held) h.Dispose();
    }
}
