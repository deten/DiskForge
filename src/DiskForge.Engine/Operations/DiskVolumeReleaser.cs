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
