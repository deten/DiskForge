using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Guard + boot-intelligence tests for CloneDiskOperation.Validate — the safety spine of cloning.
/// The boot-topology checks (the "boots cleanly" logic) are the heart of this suite: they must never
/// let a user believe a clone will boot when it won't. Pure logic, no hardware.
/// </summary>
public class CloneDiskOperationTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private const ulong MB = 1024UL * 1024;

    // ---------- fixtures ----------

    private static PartitionInfo Part(int num, PartitionKind kind, ulong offset, ulong size,
        string? letter = null, bool isSystem = false, BitLockerInfo? bl = null)
        => new()
        {
            PartitionNumber = num, Kind = kind, OffsetBytes = offset, SizeBytes = size,
            DriveLetter = letter, IsSystem = isSystem,
            // Model a volume whenever the partition is lettered, is the ESP, or carries encryption
            // state we want to assert on (an unmounted BitLocker data volume has no letter).
            Volume = (letter is null && !isSystem && bl is null) ? null : new VolumeInfo
            {
                DriveLetter = letter, Label = "V", FileSystem = "NTFS",
                SizeBytes = size, FreeBytes = size / 2, BitLocker = bl ?? BitLockerInfo.NotEncryptable
            }
        };

    private static PhysicalDiskInfo Disk(
        int number, ulong size, bool removable = true, bool system = false, bool boot = false,
        uint sector = 512, DriveCapability caps = DriveCapability.Clone,
        params PartitionInfo[] parts)
        => new()
        {
            Number = number,
            FriendlyName = $"Disk{number}",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            PartitionStyle = PartitionStyle.Gpt,
            LogicalSectorSize = sector,
            IsRemovable = removable,
            IsSystemDisk = system,
            IsBootDisk = boot,
            Capabilities = new DriveCapabilities { Supported = caps },
            Partitions = parts
        };

    private static SystemState State(int? systemDisk = null, bool elevated = true, params PhysicalDiskInfo[] disks)
        => new() { Disks = disks, SystemDiskNumber = systemDisk, IsElevated = elevated };

    // A plain data disk with one unmounted partition — a safe clone source.
    private static PhysicalDiskInfo DataDisk(int number, ulong size = 16 * GB, bool removable = true)
        => Disk(number, size, removable: removable, parts: Part(1, PartitionKind.Basic, MB, size - MB));

    private static CloneDiskSettings Settings(int src, int dst, bool allowInternal = true, bool allowLive = false)
        => new()
        {
            SourceDiskNumber = src, TargetDiskNumber = dst,
            AllowNonRemovableTarget = allowInternal, AllowLiveCrashConsistent = allowLive
        };

    // ---------- happy path ----------

    [Fact]
    public void ValidClone_DataDiskToLargerTarget_IsAccepted()
    {
        var src = DataDisk(3, 16 * GB);
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(3, 4));
        Assert.True(op.Validate(State(disks: new[] { src, dst })).IsValid);
    }

    [Fact]
    public void Clone_IsDestructive_ToTheTarget()
    {
        var op = new CloneDiskOperation(Settings(3, 4));
        var d = op.Describe();
        Assert.True(d.IsDestructive);
        Assert.Equal(4, d.TargetDiskNumber);
    }

    // ---------- disk-level guards ----------

    [Fact]
    public void SourceEqualsTarget_IsRejected()
    {
        var op = new CloneDiskOperation(Settings(3, 3));
        Assert.False(op.Validate(State(disks: new[] { DataDisk(3) })).IsValid);
    }

    [Fact]
    public void TargetIsSystemDisk_IsRejected()
    {
        var src = DataDisk(3);
        var dst = Disk(2, 32 * GB, removable: false, system: true, parts: Part(1, PartitionKind.Basic, MB, GB));
        var op = new CloneDiskOperation(Settings(3, 2));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("system/boot disk"));
    }

    [Fact]
    public void TargetIsBootDisk_IsRejected()
    {
        var src = DataDisk(3);
        var dst = Disk(1, 32 * GB, removable: false, boot: true, parts: Part(1, PartitionKind.Basic, MB, GB));
        var op = new CloneDiskOperation(Settings(3, 1));
        Assert.False(op.Validate(State(disks: new[] { src, dst })).IsValid);
    }

    [Fact]
    public void TargetBySystemStateNumber_IsRejected()
    {
        var src = DataDisk(3);
        var dst = DataDisk(2, 32 * GB, removable: false);
        var op = new CloneDiskOperation(Settings(3, 2));
        Assert.False(op.Validate(State(systemDisk: 2, disks: new[] { src, dst })).IsValid);
    }

    [Fact]
    public void InternalTarget_IsRejectedUnlessAllowed()
    {
        var src = DataDisk(3);
        var dst = DataDisk(4, 32 * GB, removable: false);

        var blocked = new CloneDiskOperation(Settings(3, 4, allowInternal: false));
        Assert.False(blocked.Validate(State(disks: new[] { src, dst })).IsValid);

        var allowed = new CloneDiskOperation(Settings(3, 4, allowInternal: true));
        Assert.True(allowed.Validate(State(disks: new[] { src, dst })).IsValid);
    }

    [Fact]
    public void TargetTooSmall_IsRejected()
    {
        var src = DataDisk(3, 32 * GB);
        var dst = DataDisk(4, 16 * GB);
        var op = new CloneDiskOperation(Settings(3, 4));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("too small"));
    }

    [Fact]
    public void EqualSizeTarget_IsAccepted()
    {
        var src = DataDisk(3, 16 * GB);
        var dst = DataDisk(4, 16 * GB);
        var op = new CloneDiskOperation(Settings(3, 4));
        Assert.True(op.Validate(State(disks: new[] { src, dst })).IsValid);
    }

    [Fact]
    public void MissingCloneCapability_IsRejected()
    {
        var src = Disk(3, 16 * GB, caps: DriveCapability.None, parts: Part(1, PartitionKind.Basic, MB, GB));
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(3, 4));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.False(r.IsValid);
        Assert.Equal(DriveCapability.Clone, r.MissingCapabilities);
    }

    [Fact]
    public void UnknownDisks_AreRejected()
    {
        var op = new CloneDiskOperation(Settings(3, 9));
        Assert.False(op.Validate(State(disks: new[] { DataDisk(3) })).IsValid);
    }

    // ---------- live-source consistency ----------

    [Fact]
    public void RunningBootDiskAsSource_IsRejected()
    {
        var src = Disk(1, 16 * GB, removable: false, boot: true,
            parts: Part(1, PartitionKind.Basic, MB, 8 * GB, letter: "C"));
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(1, 4));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("running Windows disk"));
    }

    [Fact]
    public void RunningBootDiskWithEspElsewhere_MentionsSplitBoot()
    {
        // Mirrors the dev machine: boot disk hosts C: but has NO ESP of its own.
        var src = Disk(1, 16 * GB, removable: false, boot: true,
            parts: new[]
            {
                Part(1, PartitionKind.MicrosoftReserved, MB, 16 * MB),
                Part(2, PartitionKind.Basic, 17 * MB, 8 * GB, letter: "C")
            });
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(1, 4));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("split-boot"));
    }

    [Fact]
    public void LiveMountedDataSource_RequiresAcknowledgment()
    {
        var src = Disk(3, 16 * GB, parts: Part(1, PartitionKind.Basic, MB, 8 * GB, letter: "G"));
        var dst = DataDisk(4, 32 * GB);

        var noAck = new CloneDiskOperation(Settings(3, 4, allowLive: false));
        Assert.False(noAck.Validate(State(disks: new[] { src, dst })).IsValid);

        var ack = new CloneDiskOperation(Settings(3, 4, allowLive: true));
        var r = ack.Validate(State(disks: new[] { src, dst }));
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("crash-consistent"));
    }

    // ---------- encryption ----------

    [Fact]
    public void BitLockerSource_WarnsButDoesNotBlock()
    {
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted };
        var src = Disk(3, 16 * GB, parts: Part(1, PartitionKind.Basic, MB, 8 * GB, bl: bl));
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(3, 4));
        var r = op.Validate(State(disks: new[] { src, dst }));
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("BitLocker"));
    }

    // ---------- boot classification (the "boots cleanly" intelligence) ----------

    [Fact]
    public void ClassifyBoot_DataDisk_IsNotBootable()
    {
        var d = DataDisk(3);
        Assert.Equal(BootHandling.NotBootable, CloneDiskOperation.ClassifyBoot(d));
    }

    [Fact]
    public void ClassifyBoot_SelfContainedBootable_RebuildsBootFiles()
    {
        // C: + its own EFI System Partition on the same disk.
        var d = Disk(5, 64 * GB, removable: false,
            parts: new[]
            {
                Part(1, PartitionKind.Efi, MB, 100 * MB, isSystem: true),
                Part(2, PartitionKind.Basic, 200 * MB, 60UL * GB, letter: "C")
            });
        Assert.Equal(BootHandling.RebuildBootFiles, CloneDiskOperation.ClassifyBoot(d));
    }

    [Fact]
    public void ClassifyBoot_OsWithoutEsp_MeansEspOnAnotherDisk()
    {
        var d = Disk(1, 64 * GB, removable: false, boot: true,
            parts: Part(1, PartitionKind.Basic, MB, 60UL * GB, letter: "C"));
        Assert.Equal(BootHandling.EspOnAnotherDisk, CloneDiskOperation.ClassifyBoot(d));
    }

    [Fact]
    public void SelfContainedBootableSource_PlansBcdbootStep()
    {
        // A self-contained OS disk we can actually recognize: it carries C: AND its own ESP. (We only
        // promise a boot rebuild when the OS is detectable — never for an unidentifiable offline disk.)
        var src = Disk(5, 32 * GB, removable: false,
            parts: new[]
            {
                Part(1, PartitionKind.Efi, MB, 100 * MB, isSystem: true),
                Part(2, PartitionKind.Basic, 200 * MB, 20UL * GB, letter: "C")
            });
        var dst = DataDisk(4, 64 * GB);
        // C: makes the source "live", so acknowledge crash-consistency to keep the plan feasible.
        var op = new CloneDiskOperation(Settings(5, 4, allowLive: true));
        var sim = op.Simulate(State(disks: new[] { src, dst }));
        Assert.True(sim.Feasible);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("bcdboot", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- copy extent sizing ----------

    [Fact]
    public void CopyExtent_FullSector_IsWholeDiskAlignedUp()
    {
        var src = Disk(3, 16 * GB + 300, sector: 512, parts: Part(1, PartitionKind.Basic, MB, GB));
        var op = new CloneDiskOperation(Settings(3, 4) with { Method = CloneMethod.FullSector });
        var extent = op.CopyExtentBytes(src);
        Assert.True(extent >= (long)(16 * GB + 300));
        Assert.Equal(0, extent % 512);
    }

    [Fact]
    public void CopyExtent_UsedExtent_StopsAfterLastPartition()
    {
        // Last partition ends at 2 GB on a 16 GB disk → used-extent copies far less than the whole disk.
        var src = Disk(3, 16 * GB, sector: 512,
            parts: Part(1, PartitionKind.Basic, MB, 2 * GB));
        var op = new CloneDiskOperation(Settings(3, 4) with { Method = CloneMethod.UsedExtent });
        var extent = op.CopyExtentBytes(src);
        Assert.True(extent < (long)(3 * GB));
        Assert.True(extent >= (long)(2 * GB));
        Assert.Equal(0, extent % 512);
    }

    // ---------- simulation ----------

    [Fact]
    public void Simulate_Infeasible_WhenValidationFails()
    {
        var op = new CloneDiskOperation(Settings(3, 3));
        var sim = op.Simulate(State(disks: new[] { DataDisk(3) }));
        Assert.False(sim.Feasible);
        Assert.NotNull(sim.BlockingReason);
    }

    [Fact]
    public void Simulate_IncludesVerifyStep_WhenEnabled()
    {
        var src = DataDisk(3, 16 * GB);
        var dst = DataDisk(4, 32 * GB);
        var op = new CloneDiskOperation(Settings(3, 4)); // VerifyAfter defaults true
        var sim = op.Simulate(State(disks: new[] { src, dst }));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("verify", StringComparison.OrdinalIgnoreCase));
    }
}
