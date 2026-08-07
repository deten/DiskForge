namespace DiskForge.Core.Model;

/// <summary>A mounted filesystem sitting on a partition.</summary>
public sealed record VolumeInfo
{
    public string? DriveLetter { get; init; }
    public string? Label { get; init; }
    public string FileSystem { get; init; } = "RAW";
    public ulong SizeBytes { get; init; }
    public ulong FreeBytes { get; init; }

    /// <summary>
    /// Storage-management unique id / device path (<c>\\?\Volume{…}\</c>), used to correlate with
    /// partitions <b>and to dismount the volume before a raw write</b>. Never put anything else here:
    /// <see cref="Operations"/>' volume releaser feeds it straight to the Win32 volume APIs.
    /// </summary>
    public string? UniqueId { get; init; }

    /// <summary>
    /// The filesystem's own UUID, read from its superblock. Separate from <see cref="UniqueId"/>,
    /// which is a Windows device path — conflating the two breaks volume dismounting.
    /// </summary>
    public string? FileSystemUuid { get; init; }

    public BitLockerInfo BitLocker { get; init; } = BitLockerInfo.NotEncryptable;

    /// <summary>
    /// False when this volume was identified by reading its superblock directly rather than by
    /// Windows mounting it — true for Linux filesystems, which Windows cannot mount. The filesystem
    /// type and label are real; <see cref="FreeBytes"/>/<see cref="UsedBytes"/> are not known, so the
    /// UI must not present them as a measurement.
    /// </summary>
    public bool UsageKnown { get; init; } = true;

    /// <summary>
    /// True when Windows itself has this volume mounted. A Linux filesystem read off the platter has
    /// no Windows volume behind it, so operations that go through Windows (label, drive letter) do
    /// not apply to it.
    /// </summary>
    public bool MountedByWindows { get; init; } = true;

    public ulong UsedBytes => SizeBytes > FreeBytes ? SizeBytes - FreeBytes : 0;
}
