using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.Core.Planning;

/// <summary>How a region of the disk map differs from what is actually on the drive right now.</summary>
public enum PendingChange
{
    /// <summary>Nothing staged touches this region — it is real.</summary>
    None = 0,

    /// <summary>Free space that only exists because a staged delete/clean will release it.</summary>
    Delete = 1,

    /// <summary>A partition that does not exist yet; a staged create will make it.</summary>
    Create = 2,

    /// <summary>A real partition whose filesystem a staged format will replace.</summary>
    Reformat = 3,

    /// <summary>A real partition getting a new label / drive letter — extent and data untouched.</summary>
    Modify = 4,

    /// <summary>A real partition on a disk a staged clone will overwrite wholesale.</summary>
    Overwrite = 5,

    /// <summary>A real partition a staged resize will grow or shrink. Its data survives.</summary>
    Resize = 6
}

/// <summary>One slice of a projected disk map: what would be there once the staged batch is applied.</summary>
public sealed record PlannedRegion
{
    /// <summary>The region as it will look after Apply. Unallocated regions are synthetic, as always.</summary>
    public required PartitionInfo Partition { get; init; }

    public PendingChange Pending { get; init; } = PendingChange.None;

    /// <summary>Human sentence explaining the staged change, shown in the segment's tooltip.</summary>
    public string? PendingNote { get; init; }

    /// <summary>
    /// The staged operation responsible, when exactly one is. Null for real regions, and also for
    /// free space produced by several deletes at once — there is no single op to point at.
    /// </summary>
    public IDiskOperation? PendingOperation { get; init; }

    public bool IsPending => Pending != PendingChange.None;
}

/// <summary>A disk plus the map it will have after the staged batch is applied.</summary>
public sealed record PlannedDisk
{
    /// <summary>
    /// The disk with its *projected* partition list. Operations validate against this while the user
    /// is staging, which is what lets a create be planned into space a queued delete will free.
    /// </summary>
    public required PhysicalDiskInfo Disk { get; init; }

    /// <summary>The projected map, one entry per <see cref="PhysicalDiskInfo.Partitions"/> entry.</summary>
    public required IReadOnlyList<PlannedRegion> Regions { get; init; }

    public bool HasPendingChanges => Regions.Any(r => r.IsPending);
}

/// <summary>
/// The real snapshot, the projected snapshot, and the per-disk diff between them. Produced by
/// <see cref="LayoutProjector"/> whenever the staged batch changes.
/// </summary>
public sealed record PlannedState
{
    /// <summary>What is physically on the machine — the ground truth every write op re-checks.</summary>
    public required SystemState Actual { get; init; }

    /// <summary>What the machine would look like after Apply. Never used to execute anything.</summary>
    public required SystemState Projected { get; init; }

    public required IReadOnlyList<PlannedDisk> Disks { get; init; }

    public bool HasPendingChanges => Disks.Any(d => d.HasPendingChanges);

    public PlannedDisk? FindDisk(int number) => Disks.FirstOrDefault(d => d.Disk.Number == number);

    /// <summary>A plan with nothing staged — projected state is the real state.</summary>
    public static PlannedState Unchanged(SystemState actual) =>
        LayoutProjector.Project(actual, Array.Empty<IDiskOperation>());
}
