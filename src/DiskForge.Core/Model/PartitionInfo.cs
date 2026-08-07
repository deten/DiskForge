namespace DiskForge.Core.Model;

/// <summary>Kind of a slot in the partition map — includes synthetic unallocated gaps.</summary>
public enum PartitionKind
{
    Unallocated = 0,
    Basic = 1,
    Efi = 2,
    MicrosoftReserved = 3,
    Recovery = 4,
    System = 5,
    Unknown = 6,

    /// <summary>Linux filesystem data or swap (ext4/btrfs/xfs/…). Windows cannot mount these.</summary>
    Linux = 7
}

/// <summary>
/// One region of a disk. Real partitions carry a partition number; synthetic
/// <see cref="PartitionKind.Unallocated"/> regions are inserted by the mapper to fill gaps.
/// </summary>
public sealed record PartitionInfo
{
    public int? PartitionNumber { get; init; }
    public ulong OffsetBytes { get; init; }
    public ulong SizeBytes { get; init; }
    public PartitionKind Kind { get; init; } = PartitionKind.Unknown;

    /// <summary>GPT partition type GUID, when the disk is GPT.</summary>
    public Guid? GptType { get; init; }

    /// <summary>MBR partition type byte, when the disk is MBR.</summary>
    public byte? MbrType { get; init; }

    public string? DriveLetter { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public bool IsHidden { get; init; }

    public VolumeInfo? Volume { get; init; }

    public bool IsUnallocated => Kind == PartitionKind.Unallocated;
    public ulong EndBytes => OffsetBytes + SizeBytes;
}
