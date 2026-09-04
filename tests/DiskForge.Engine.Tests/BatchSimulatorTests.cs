using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The dry run. What matters is the <i>order</i>: each operation must be simulated against the layout
/// the ones before it would leave, exactly as <c>OperationExecutor</c> will run them. Simulating every
/// operation against the real disk would call a create that depends on a queued delete "blocked", and
/// simulating against the whole projection would call a create whose delete was removed "feasible".
/// Both are wrong in the direction that matters. Pure, no hardware.
/// </summary>
public class BatchSimulatorTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;

    /// <summary>A 64 GB removable GPT disk carrying the given partitions.</summary>
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

    [Fact]
    public void EmptyBatch_SimulatesToNothing()
    {
        var result = BatchSimulator.Simulate(State(Part(1, MB, 32 * GB)), Array.Empty<IDiskOperation>());
        Assert.Empty(result);
        Assert.True(BatchSimulator.AllFeasible(result));
    }

    [Fact]
    public void DeleteThenCreateIntoTheFreedSpace_BothSimulateFeasible()
    {
        // Against the real disk the create would be refused: the space is still occupied. Against the
        // projection after the delete it is free, which is what the executor will actually see.
        var state = State(Part(1, MB, 32 * GB));
        var ops = new IDiskOperation[] { Delete(1, MB), Create(MB, 16 * GB) };

        var result = BatchSimulator.Simulate(state, ops);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].Result.Feasible, result[0].Result.BlockingReason);
        Assert.True(result[1].Result.Feasible, result[1].Result.BlockingReason);
        Assert.True(BatchSimulator.AllFeasible(result));
    }

    [Fact]
    public void CreateWithoutItsDelete_SimulatesBlocked_WithTheReason()
    {
        // The user removed the delete and kept the create. Apply would fail here; the dry run must say so
        // rather than letting the whole-batch projection make the space look free.
        var state = State(Part(1, MB, 32 * GB));
        var ops = new IDiskOperation[] { Create(MB, 16 * GB) };

        var result = BatchSimulator.Simulate(state, ops);

        Assert.Single(result);
        Assert.False(result[0].Result.Feasible);
        Assert.False(string.IsNullOrWhiteSpace(result[0].Result.BlockingReason));
        Assert.False(BatchSimulator.AllFeasible(result));
    }

    [Fact]
    public void EachOperationSeesOnlyItsPredecessors_NotItsSuccessors()
    {
        // Create first, delete second: the create must be judged against the real disk (occupied), not
        // against a layout in which the later delete has already freed the space.
        var state = State(Part(1, MB, 32 * GB));
        var ops = new IDiskOperation[] { Create(MB, 16 * GB), Delete(1, MB) };

        var result = BatchSimulator.Simulate(state, ops);

        Assert.False(result[0].Result.Feasible);
        Assert.True(result[1].Result.Feasible, result[1].Result.BlockingReason);
    }

    [Fact]
    public void Results_CarryIndexDescriptorAndPlannedSteps()
    {
        var state = State(Part(1, MB, 32 * GB));
        var result = BatchSimulator.Simulate(state, new IDiskOperation[] { Delete(1, MB) });

        var only = Assert.Single(result);
        Assert.Equal(0, only.Index);
        Assert.True(only.Descriptor.IsDestructive);
        Assert.NotEmpty(only.Result.PlannedSteps);
    }

    [Fact]
    public void Simulating_NeverMutatesTheRealState()
    {
        var state = State(Part(1, MB, 32 * GB));
        var before = state.FindDisk(3)!.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes, p.IsUnallocated)).ToList();

        BatchSimulator.Simulate(state, new IDiskOperation[] { Delete(1, MB), Create(MB, 16 * GB) });

        var after = state.FindDisk(3)!.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes, p.IsUnallocated)).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void AnOperationWhoseSimulateThrows_IsReportedBlocked_NotAsACrash()
    {
        var state = State(Part(1, MB, 32 * GB));
        var result = BatchSimulator.Simulate(state, new IDiskOperation[] { new ThrowingOperation() });

        var only = Assert.Single(result);
        Assert.False(only.Result.Feasible);
        Assert.Contains("boom", only.Result.BlockingReason);
    }

    private sealed class ThrowingOperation : IDiskOperation
    {
        public OperationDescriptor Describe() => new("Throws", "for the test", false, 3);
        public DriveCapability RequiredCapabilities() => DriveCapability.None;
        public ValidationResult Validate(SystemState state) => ValidationResult.Ok();
        public SimulationResult Simulate(SystemState state) => throw new InvalidOperationException("boom");
        public Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifyResult> VerifyAsync() => throw new NotSupportedException();
    }
}
