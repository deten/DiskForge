namespace DiskForge.Core.Operations;

/// <summary>
/// What actually writes a Linux filesystem. Windows ships no ext4/btrfs/xfs driver, so DiskForge
/// hands the block device to a real Linux mkfs toolchain instead of pretending to implement one.
/// The only implementation today is the WSL2 backend (<c>WslLinuxFormatBackend</c>); the interface
/// exists so a bundled-e2fsprogs backend can be added without touching the operations.
/// </summary>
public interface ILinuxFormatBackend
{
    /// <summary>
    /// Writes the filesystem onto one already-existing partition. The implementation must positively
    /// identify the target device by disk size + partition offset/size before writing, and must not
    /// write at all when that identification is ambiguous (§1 anti-wrong-target).
    /// </summary>
    Task<LinuxFormatOutcome> FormatAsync(
        LinuxFormatRequest request, IProgress<OpProgress> progress, CancellationToken ct);

    /// <summary>Re-reads the filesystem signature of the same partition, for VerifyAsync.</summary>
    Task<LinuxFormatOutcome> ProbeSignatureAsync(LinuxFormatRequest request, CancellationToken ct);
}

/// <summary>
/// The exact extent to format, expressed the same way the partition map does (byte offsets) so the
/// backend can prove the Linux device node it found is the partition the user picked.
/// </summary>
public sealed record LinuxFormatRequest
{
    public required int DiskNumber { get; init; }

    /// <summary>Size of the whole physical disk — the first identity check on the attached device.</summary>
    public required ulong DiskSizeBytes { get; init; }

    /// <summary>Byte offset of the target partition from the start of the disk.</summary>
    public required ulong PartitionOffsetBytes { get; init; }

    public required ulong PartitionSizeBytes { get; init; }

    public required FileSystemType FileSystem { get; init; }

    public string Label { get; init; } = "";

    /// <summary>Run mkfs' bad-block scan (mke2fs -c). Ignored by tools that have no equivalent.</summary>
    public bool BadBlockScan { get; init; }

    /// <summary>
    /// Volumes Windows has mounted on this disk. They must be dismounted before the disk can be handed
    /// to the WSL kernel — a mounted volume keeps Windows holding the device.
    /// </summary>
    public IReadOnlyList<string> VolumePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True for removable media, which Windows refuses to take offline ("Removable media cannot be set
    /// to offline"). The backend must not fall back to that trick for these disks.
    /// </summary>
    public bool DiskIsRemovable { get; init; }
}

/// <summary>Result of a backend format or signature probe.</summary>
public sealed record LinuxFormatOutcome
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>The device node that was actually written, e.g. <c>/dev/sdd1</c>.</summary>
    public string? DeviceNode { get; init; }

    /// <summary>Filesystem type <c>blkid</c> reports after the write (ext4, btrfs, …).</summary>
    public string? DetectedType { get; init; }

    public string? DetectedLabel { get; init; }
    public string? Uuid { get; init; }

    /// <summary>Step-by-step trace, mirrored into the DiskForge log.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    public static LinuxFormatOutcome Failed(string error, IReadOnlyList<string>? log = null)
        => new() { Success = false, Error = error, Log = log ?? Array.Empty<string>() };
}
