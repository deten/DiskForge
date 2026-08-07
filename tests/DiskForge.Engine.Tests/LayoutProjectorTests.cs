using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The staged-batch preview. These matter because the projection is what the user *plans against*:
/// if it shows free space that a delete will not actually free, they stage a create that then fails at
/// Apply. It is pure arithmetic over a captured state, so it can be pinned down exactly.
/// </summary>
public class LayoutProjectorTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;

    /// <summary>A 64 GB removable GPT disk carrying one 32 GB NTFS partition starting at 1 MiB.</summary>
    private static SystemState State(params PartitionInfo[] parts)
    {
        var disk = new PhysicalDiskInfo
        {
            Number = 3,
            FriendlyName = "USB",
            SizeBytes = 64 * GB,
            PartitionStyle = PartitionStyle.Gpt,
            IsRemovable = true,
            Capabilities = new DriveCapabilities
            {
                Supported = DriveCapability.PartitionEdit | DriveCapability.Format
            },
            Partitions = DiskMap.Build(parts, 64 * GB, PartitionStyle.Gpt)
        };
        return new SystemState { Disks = new[] { disk }, IsElevated = true };
    }

    private static PartitionInfo Part(int number, ulong offset, ulong size, string? letter = "E")
        => new()
        {
            PartitionNumber = number,
            OffsetBytes = offset,
            SizeBytes = size,
            Kind = PartitionKind.Basic,
            DriveLetter = letter,
            Volume = new VolumeInfo
            {
                DriveLetter = letter, Label = "OLD", FileSystem = "NTFS",
                SizeBytes = size, FreeBytes = size / 2
            }
        };

    private static DeletePartitionOperation Delete(int number, ulong offset) =>
        new(new DeletePartitionSettings
        {
            DiskNumber = 3, PartitionNumber = number, OffsetBytes = offset, DriveLetter = "E"
        });

    private static CreatePartitionOperation Create(ulong offset, ulong size) =>
        new(new CreatePartitionSettings
        {
            DiskNumber = 3, OffsetBytes = offset, SizeBytes = size,
            FileSystem = FileSystemType.Exfat, Label = "NEW", DriveLetter = null
        });

    private static PlannedDisk Project(SystemState state, params IDiskOperation[] ops) =>
        LayoutProjector.Project(state, ops).FindDisk(3)!;

    [Fact]
    public void NothingStaged_ProjectsTheRealLayoutUnchanged()
    {
        var state = State(Part(1, MB, 32 * GB));
        var planned = Project(state);

        Assert.False(planned.HasPendingChanges);
        Assert.All(planned.Regions, r => Assert.Equal(PendingChange.None, r.Pending));
        Assert.Equal(state.FindDisk(3)!.Partitions.Count, planned.Regions.Count);
    }

    [Fact]
    public void QueuedDelete_ShowsThePartitionsSpaceAsPendingFreeSpace()
    {
        var state = State(Part(1, MB, 32 * GB));
        var planned = Project(state, Delete(1, MB));

        // The partition is gone from the projection and its extent is now free space, flagged as
        // only-free-because-of-a-queued-delete rather than silently indistinguishable from real space.
        Assert.DoesNotContain(planned.Regions, r => r.Partition.PartitionNumber == 1);

        var freed = planned.Regions.Single(r => r.Pending == PendingChange.Delete);
        Assert.True(freed.Partition.IsUnallocated);
        Assert.Equal(MB, freed.Partition.OffsetBytes);
        Assert.Contains("Queued for deletion", freed.PendingNote);
    }

    [Fact]
    public void QueuedDelete_LeavesTheRealCaptureAlone()
    {
        var state = State(Part(1, MB, 32 * GB));
        LayoutProjector.Project(state, new[] { Delete(1, MB) });

        // The projection must never mutate what was captured — every write op re-validates against it.
        Assert.Contains(state.FindDisk(3)!.Partitions, p => p.PartitionNumber == 1);
    }

    [Fact]
    public void DeleteThenCreate_LetsTheNewPartitionBePlannedIntoTheFreedSpace()
    {
        var state = State(Part(1, MB, 32 * GB));
        var plan = LayoutProjector.Project(state, new IDiskOperation[] { Delete(1, MB), Create(MB, 10 * GB) });
        var planned = plan.FindDisk(3)!;

        var created = planned.Regions.Single(r => r.Pending == PendingChange.Create);
        Assert.Equal(MB, created.Partition.OffsetBytes);
        Assert.Equal(10 * GB, created.Partition.SizeBytes);
        Assert.Equal("exFAT", created.Partition.Volume!.FileSystem);
        Assert.Null(created.Partition.PartitionNumber);   // Windows assigns one only at Apply

        // The leftover of the freed extent is still offered as free space for a second partition.
        var leftover = planned.Regions.Single(r => r.Partition.IsUnallocated && r.Partition.OffsetBytes == MB + 10 * GB);
        Assert.True(leftover.Partition.SizeBytes > 0);
    }

    [Fact]
    public void CreateIntoFreedSpace_ValidatesAgainstTheProjectionButNotAgainstRealState()
    {
        var state = State(Part(1, MB, 32 * GB));
        var create = Create(MB, 10 * GB);

        // This is the behaviour the staged flow depends on: the create is refused against the drive as
        // it stands (the space is occupied), and allowed once the queued delete is folded in. The
        // projection it is checked against is the batch *already* staged — an operation is never
        // validated against a layout that includes its own effect.
        Assert.False(create.Validate(state).IsValid);

        var projected = LayoutProjector.Project(state, new IDiskOperation[] { Delete(1, MB) }).Projected;
        Assert.True(create.Validate(projected).IsValid);
    }

    [Fact]
    public void TwoAdjacentDeletes_MergeIntoOneFreeRegion()
    {
        var state = State(Part(1, MB, 16 * GB), Part(2, MB + 16 * GB, 16 * GB, letter: "F"));
        var planned = Project(state, Delete(1, MB), Delete(2, MB + 16 * GB));

        // Merging matters: after both deletes really run, Windows reports one gap, and
        // CreatePartitionOperation refuses an extent that spans two separate unallocated regions.
        var free = planned.Regions.Where(r => r.Partition.IsUnallocated).ToList();
        Assert.Single(free);
        Assert.Equal(MB, free[0].Partition.OffsetBytes);
        Assert.Equal(PendingChange.Delete, free[0].Pending);
        Assert.True(free[0].Partition.SizeBytes >= 32 * GB);
    }

    [Fact]
    public void QueuedFormat_KeepsTheExtentAndShowsTheNewFilesystem()
    {
        var state = State(Part(1, MB, 32 * GB));
        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = 3, Scope = FormatScope.ReformatPartition, PartitionNumber = 1,
            FileSystem = FileSystemType.Ext4, Label = "LINUX"
        });

        var region = Project(state, format).Regions.Single(r => r.Pending == PendingChange.Reformat);
        Assert.Equal(MB, region.Partition.OffsetBytes);
        Assert.Equal(32 * GB, region.Partition.SizeBytes);
        Assert.Equal("ext4", region.Partition.Volume!.FileSystem);
        Assert.Equal(PartitionKind.Linux, region.Partition.Kind);
        // Windows cannot mount ext4, so the format drops the letter — the preview says so up front.
        Assert.Null(region.Partition.DriveLetter);
    }

    [Fact]
    public void QueuedCleanWholeDisk_ReplacesEveryPartitionWithOneSpanningTheDisk()
    {
        var state = State(Part(1, MB, 16 * GB), Part(2, MB + 16 * GB, 16 * GB, letter: "F"));
        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = 3, Scope = FormatScope.CleanWholeDisk, FileSystem = FileSystemType.Exfat, Label = "USB"
        });

        var planned = Project(state, format);
        var region = Assert.Single(planned.Regions);
        Assert.Equal(PendingChange.Create, region.Pending);
        Assert.Equal(DiskMap.HeadReserve, region.Partition.OffsetBytes);
        // Stops short of the backup GPT rather than claiming the last byte of the disk.
        Assert.Equal(64 * GB - DiskMap.HeadReserve - DiskMap.GptTailReserve, region.Partition.SizeBytes);
    }

    [Fact]
    public void QueuedRename_MarksTheRegionChangedWithoutTouchingItsExtent()
    {
        var state = State(Part(1, MB, 32 * GB));
        var rename = new SetVolumeLabelOperation(new SetVolumeLabelSettings
        { DiskNumber = 3, PartitionNumber = 1, DriveLetter = "E", NewLabel = "PHOTOS" });

        var region = Project(state, rename).Regions.Single(r => r.Partition.PartitionNumber == 1);
        Assert.Equal(PendingChange.Modify, region.Pending);
        Assert.Equal("PHOTOS", region.Partition.Volume!.Label);
        Assert.Equal(32 * GB, region.Partition.SizeBytes);
    }

    [Fact]
    public void StaleDelete_IsDroppedFromThePreviewRatherThanFreeingTheWrongSpace()
    {
        // The staged offset no longer matches anything: the layout moved under us. Guessing by
        // partition number here would draw the wrong partition as "about to be deleted".
        var state = State(Part(1, MB, 32 * GB));
        var planned = Project(state, Delete(7, 40 * GB));

        Assert.False(planned.HasPendingChanges);
        Assert.Contains(planned.Regions, r => r.Partition.PartitionNumber == 1);
    }

    /// <summary>
    /// The whole point of the feature: delete the existing partition, then plan two new ones into the
    /// space it frees, without applying anything in between. Each operation is checked the way the
    /// dashboard checks it — against the layout the operations before it leave behind, which is also
    /// the order <c>OperationExecutor</c> runs them in.
    /// </summary>
    [Fact]
    public void DeleteThenTwoCreates_EveryStepStaysValidAgainstItsPredecessors()
    {
        var state = State(Part(1, MB, 32 * GB));
        var batch = new IDiskOperation[]
        {
            Delete(1, MB),
            Create(MB, 12 * GB),
            Create(MB + 12 * GB, 20 * GB)
        };

        for (var i = 0; i < batch.Length; i++)
        {
            var before = LayoutProjector.Project(state, batch.Take(i).ToList()).Projected;
            var validation = batch[i].Validate(before);
            Assert.True(validation.IsValid,
                $"step {i} was refused: {string.Join(" ", validation.Errors)}");
        }

        var planned = LayoutProjector.Project(state, batch).FindDisk(3)!;
        Assert.Equal(2, planned.Regions.Count(r => r.Pending == PendingChange.Create));
        Assert.DoesNotContain(planned.Regions, r => r.Partition.PartitionNumber == 1);
    }

    [Fact]
    public void CreateWithoutItsDelete_IsNotDrawnOverTheStillPresentPartition()
    {
        // The user staged delete-then-create, then took the delete back off the queue. The create can
        // no longer be placed; drawing it on top of the surviving partition would show a layout that
        // cannot happen. It disappears from the map, and the operation itself is what gets flagged.
        var state = State(Part(1, MB, 32 * GB));
        var planned = Project(state, Create(MB, 10 * GB));

        Assert.DoesNotContain(planned.Regions, r => r.Pending == PendingChange.Create);
        Assert.Contains(planned.Regions, r => r.Partition.PartitionNumber == 1);
    }

    [Fact]
    public void RemovingTheDelete_RestoresTheOriginalLayout()
    {
        var state = State(Part(1, MB, 32 * GB));
        var withDelete = Project(state, Delete(1, MB));
        var cleared = Project(state);

        Assert.True(withDelete.HasPendingChanges);
        Assert.False(cleared.HasPendingChanges);
        Assert.Contains(cleared.Regions, r => r.Partition.PartitionNumber == 1);
    }
}
