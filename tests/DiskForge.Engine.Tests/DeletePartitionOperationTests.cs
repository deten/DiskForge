using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Exercises the anti-wrong-target guards in DeletePartitionOperation.Validate — the safety-critical
/// logic that must never let a delete land on a system/EFI/encrypted partition or the wrong disk.
/// Pure logic, no hardware required.
/// </summary>
public class DeletePartitionOperationTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private const ulong MB = 1024UL * 1024;

    // ---------- fixtures ----------

    private static PartitionInfo Part(
        int number = 1, PartitionKind kind = PartitionKind.Basic, ulong offset = MB, ulong size = 8 * GB,
        string? letter = "E", bool system = false, bool boot = false, BitLockerInfo? bl = null)
        => new()
        {
            PartitionNumber = number,
            Kind = kind,
            OffsetBytes = offset,
            SizeBytes = size,
            DriveLetter = letter,
            IsSystem = system,
            IsBoot = boot,
            Volume = new VolumeInfo
            {
                DriveLetter = letter, Label = "DATA", FileSystem = "NTFS",
                SizeBytes = size, FreeBytes = size / 2,
                BitLocker = bl ?? BitLockerInfo.NotEncryptable
            }
        };

    private static PhysicalDiskInfo Disk(
        int number = 3, bool removable = true, bool system = false, ulong size = 32 * GB,
        DriveCapability caps = DriveCapability.PartitionEdit, params PartitionInfo[] parts)
        => new()
        {
            Number = number,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            PartitionStyle = PartitionStyle.Gpt,
            IsRemovable = removable,
            IsSystemDisk = system,
            Capabilities = new DriveCapabilities { Supported = caps },
            Partitions = parts.Length > 0 ? parts : new[] { Part() }
        };

    private static SystemState State(PhysicalDiskInfo disk, int? systemDisk = null)
        => new() { Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true };

    private static DeletePartitionSettings Delete(
        int disk = 3, int part = 1, bool allowInternal = false, ulong? offset = null)
        => new()
        {
            DiskNumber = disk, PartitionNumber = part,
            AllowNonRemovable = allowInternal, OffsetBytes = offset
        };

    // ---------- happy path ----------

    [Fact]
    public void ValidDelete_OnRemovableDataPartition_IsAccepted()
    {
        var op = new DeletePartitionOperation(Delete());
        var result = op.Validate(State(Disk()));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("permanently erased"));
    }

    [Fact]
    public void Delete_IsMarkedDestructive()
    {
        // Drives the DESTRUCTIVE badge and the typed confirmation at Apply (§1.3, §1.10).
        Assert.True(new DeletePartitionOperation(Delete()).Describe().IsDestructive);
    }

    // ---------- disk-level guards ----------

    [Fact]
    public void SystemDisk_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete());
        var result = op.Validate(State(Disk(system: true)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/boot disk"));
    }

    [Fact]
    public void SystemDiskByStateNumber_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete());
        Assert.False(op.Validate(State(Disk(number: 3), systemDisk: 3)).IsValid);
    }

    [Fact]
    public void InternalDisk_IsRejectedUnlessExplicitlyAllowed()
    {
        var op = new DeletePartitionOperation(Delete(allowInternal: false));
        Assert.False(op.Validate(State(Disk(removable: false))).IsValid);

        var allowed = new DeletePartitionOperation(Delete(allowInternal: true));
        var result = allowed.Validate(State(Disk(removable: false)));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("INTERNAL"));
    }

    [Fact]
    public void MissingPartitionEditCapability_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete());
        var result = op.Validate(State(Disk(caps: DriveCapability.Format)));
        Assert.False(result.IsValid);
        Assert.Equal(DriveCapability.PartitionEdit, result.MissingCapabilities);
    }

    [Fact]
    public void UnknownDisk_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete(disk: 9));
        Assert.False(op.Validate(State(Disk())).IsValid);
    }

    [Fact]
    public void UnknownPartition_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete(part: 7));
        Assert.False(op.Validate(State(Disk())).IsValid);
    }

    // ---------- protected-partition guards ----------

    [Theory]
    [InlineData(PartitionKind.Efi)]
    [InlineData(PartitionKind.MicrosoftReserved)]
    [InlineData(PartitionKind.Recovery)]
    [InlineData(PartitionKind.System)]
    public void ProtectedPartitionKinds_AreRejected(PartitionKind kind)
    {
        var op = new DeletePartitionOperation(Delete());
        var result = op.Validate(State(Disk(parts: new[] { Part(kind: kind) })));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/EFI/recovery"));
    }

    [Fact]
    public void SystemFlaggedPartition_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete());
        Assert.False(op.Validate(State(Disk(parts: new[] { Part(system: true) }))).IsValid);
    }

    [Fact]
    public void BootFlaggedPartition_IsRejected()
    {
        var op = new DeletePartitionOperation(Delete());
        Assert.False(op.Validate(State(Disk(parts: new[] { Part(boot: true) }))).IsValid);
    }

    // ---------- encryption gate (§1A.3) ----------

    [Fact]
    public void BitLockerProtectedVolume_IsRejected()
    {
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted };
        var op = new DeletePartitionOperation(Delete());
        var result = op.Validate(State(Disk(parts: new[] { Part(bl: bl) })));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BitLocker"));
    }

    [Fact]
    public void BitLockerConvertingVolume_IsRejected()
    {
        var bl = new BitLockerInfo
        {
            Protection = BitLockerProtection.Off,
            Conversion = BitLockerConversion.EncryptionInProgress,
            ConversionPercent = 37
        };
        var op = new DeletePartitionOperation(Delete());
        Assert.False(op.Validate(State(Disk(parts: new[] { Part(bl: bl) }))).IsValid);
    }

    // ---------- stale-plan guard ----------

    [Fact]
    public void OffsetMismatch_IsRejected()
    {
        // Partition 1 moved since staging — the number may now point at something else entirely.
        var op = new DeletePartitionOperation(Delete(offset: 999 * MB));
        var result = op.Validate(State(Disk(parts: new[] { Part(offset: MB) })));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("layout changed"));
    }

    [Fact]
    public void MatchingOffset_IsAccepted()
    {
        var op = new DeletePartitionOperation(Delete(offset: MB));
        Assert.True(op.Validate(State(Disk(parts: new[] { Part(offset: MB) }))).IsValid);
    }

    // ---------- simulation ----------

    [Fact]
    public void Simulate_IsInfeasible_WhenValidationFails()
    {
        var op = new DeletePartitionOperation(Delete());
        var sim = op.Simulate(State(Disk(parts: new[] { Part(kind: PartitionKind.Efi) })));
        Assert.False(sim.Feasible);
        Assert.NotNull(sim.BlockingReason);
    }

    [Fact]
    public void Simulate_ReportsSpaceBecomingFree_WhenValid()
    {
        var op = new DeletePartitionOperation(Delete());
        var sim = op.Simulate(State(Disk()));
        Assert.True(sim.Feasible);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("unallocated free space"));
    }
}
