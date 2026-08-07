using DiskForge.Core.Model;

namespace DiskForge.Core.Planning;

/// <summary>What a staged operation does to the disk map, for preview purposes only.</summary>
public enum LayoutChangeKind
{
    /// <summary>The target partition's extent goes back to being free space.</summary>
    DeletePartition = 0,

    /// <summary>A new partition appears in free space.</summary>
    CreatePartition = 1,

    /// <summary>An existing partition keeps its extent but gets a new filesystem.</summary>
    ReformatPartition = 2,

    /// <summary>Every partition on the disk goes away and one new one spans it.</summary>
    ClearDisk = 3,

    /// <summary>A volume's label changes; the extent is untouched.</summary>
    Relabel = 4,

    /// <summary>A partition's drive letter changes; the extent is untouched.</summary>
    Reletter = 5,

    /// <summary>The whole disk is about to be overwritten (clone target). Layout is unknown until then.</summary>
    OverwriteDisk = 6
}

/// <summary>
/// One declared effect a staged operation will have on the disk map. This is a *preview* description,
/// deliberately separate from the operation's real execution: it never touches a disk and is never
/// consulted by <c>Validate</c>/<c>Execute</c>. Its only job is to let the dashboard show the layout
/// the user is building up before they Apply.
/// </summary>
public sealed record LayoutChange
{
    public required LayoutChangeKind Kind { get; init; }
    public required int DiskNumber { get; init; }

    /// <summary>Short badge text shown on the affected segment, e.g. "Queued: delete partition 2".</summary>
    public required string Note { get; init; }

    /// <summary>Partition this targets. Matched only when <see cref="TargetOffsetBytes"/> misses.</summary>
    public int? TargetPartitionNumber { get; init; }

    /// <summary>Preferred way to find the target — a partition number can shift, an offset cannot.</summary>
    public ulong? TargetOffsetBytes { get; init; }

    /// <summary>Where a newly created partition starts.</summary>
    public ulong NewOffsetBytes { get; init; }

    /// <summary>Size of a newly created partition. Ignored when <see cref="SpanRestOfDisk"/> is set.</summary>
    public ulong NewSizeBytes { get; init; }

    /// <summary>The new partition fills the disk (whole-disk format), so the projector sizes it.</summary>
    public bool SpanRestOfDisk { get; init; }

    public PartitionKind NewKind { get; init; } = PartitionKind.Basic;

    /// <summary>Filesystem name as it will be reported, or null to leave the volume as-is.</summary>
    public string? FileSystem { get; init; }

    public string? Label { get; init; }
    public string? DriveLetter { get; init; }

    /// <summary>Drop the drive letter — a Linux format does this, because Windows can't mount the result.</summary>
    public bool ClearDriveLetter { get; init; }
}
