using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Guard ladder for <see cref="CheckFilesystemOperation"/>. The read-only check is allowed almost
/// anywhere; the repair is a write and must follow the same refusals as every other write op, plus
/// one of its own: chkdsk /f on the running Windows volume can only schedule itself for the next boot,
/// and DiskForge does not queue work it cannot watch. Pure logic, no hardware.
/// </summary>
public class CheckFilesystemOperationTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private const ulong MB = 1024UL * 1024;

    // ---------- fixtures ----------

    private static PartitionInfo Part(
        int number = 1, PartitionKind kind = PartitionKind.Basic, ulong offset = MB, ulong size = 8 * GB,
        string? letter = "E", bool system = false, bool boot = false, string fs = "NTFS",
        bool mountedByWindows = true, BitLockerInfo? bl = null, bool hasVolume = true)
        => new()
        {
            PartitionNumber = number,
            Kind = kind,
            OffsetBytes = offset,
            SizeBytes = size,
            DriveLetter = letter,
            IsSystem = system,
            IsBoot = boot,
            Volume = hasVolume
                ? new VolumeInfo
                {
                    DriveLetter = letter, Label = "DATA", FileSystem = fs,
                    SizeBytes = size, FreeBytes = size / 2,
                    MountedByWindows = mountedByWindows,
                    UsageKnown = mountedByWindows,
                    BitLocker = bl ?? BitLockerInfo.NotEncryptable
                }
                : null
        };

    private static PhysicalDiskInfo Disk(
        int number = 3, bool removable = true, bool system = false, bool boot = false,
        bool offline = false, bool readOnly = false, params PartitionInfo[] parts)
        => new()
        {
            Number = number,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = 32 * GB,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            PartitionStyle = PartitionStyle.Gpt,
            IsRemovable = removable,
            IsSystemDisk = system,
            IsBootDisk = boot,
            IsOffline = offline,
            IsReadOnly = readOnly,
            Capabilities = new DriveCapabilities { Supported = DriveCapability.PartitionEdit },
            Partitions = parts.Length > 0 ? parts : new[] { Part() }
        };

    private static SystemState State(PhysicalDiskInfo disk, int? systemDisk = null)
        => new() { Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true };

    private static CheckFilesystemOperation Op(
        bool repair = false, int disk = 3, int part = 1, bool allowInternal = false, ulong? offset = null)
        => new(new CheckFilesystemSettings
        {
            DiskNumber = disk, PartitionNumber = part, Repair = repair,
            AllowNonRemovable = allowInternal, OffsetBytes = offset, DriveLetter = "E"
        });

    // ---------- the read-only check is permissive ----------

    [Fact]
    public void Check_OnRemovableNtfs_IsAccepted()
    {
        var v = Op().Validate(State(Disk()));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void Check_OnTheSystemDisk_IsAllowedWithAWarning()
    {
        // Read-only. The one place a repair is refused is fine for a scan, but a live volume can
        // produce findings that are only in-flight writes, and the user is told so.
        var disk = Disk(removable: false, system: true, boot: true, parts: Part(letter: "C", boot: true));
        var v = Op().Validate(State(disk, systemDisk: 3));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
        Assert.Contains(v.Warnings, w => w.Contains("in use by Windows"));
    }

    [Fact]
    public void Check_OnAnInternalDisk_NeedsNoAcknowledgment()
    {
        // Nothing is written, so the removable-only default does not apply.
        var v = Op().Validate(State(Disk(removable: false)));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
    }

    [Fact]
    public void Check_IsNotDestructive()
    {
        Assert.False(Op().Describe().IsDestructive);
        Assert.False(Op(repair: true).Describe().IsDestructive);
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("exFAT")]
    [InlineData("FAT32")]
    public void Check_AcceptsEveryFilesystemChkdskKnows(string fs)
    {
        var v = Op().Validate(State(Disk(parts: Part(fs: fs))));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
    }

    // ---------- refusals shared by check and repair ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinuxFilesystem_IsRefusedWithTheReason(bool repair)
    {
        // A Linux volume carries a VolumeInfo read from its superblock, but Windows has not mounted it
        // and chkdsk knows nothing about it.
        var disk = Disk(parts: Part(kind: PartitionKind.Linux, letter: null, fs: "ext4", mountedByWindows: false));
        var v = Op(repair).Validate(State(disk));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("ext4") && e.Contains("chkdsk"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartitionWithNoVolume_IsRefused(bool repair)
    {
        var v = Op(repair).Validate(State(Disk(parts: Part(hasVolume: false))));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("no formatted volume"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineDisk_IsRefused(bool repair)
    {
        var v = Op(repair).Validate(State(Disk(offline: true)));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("offline"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StagedOffsetMismatch_IsRefused(bool repair)
    {
        // The partition number still resolves, but the layout moved underneath the staged op.
        var v = Op(repair, offset: 512 * MB).Validate(State(Disk(parts: Part(offset: MB))));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("no longer at the offset"));
    }

    [Fact]
    public void MissingPartition_IsRefused()
    {
        var v = Op(part: 9).Validate(State(Disk()));
        Assert.False(v.IsValid);
    }

    // ---------- repair is a write and follows the write ladder ----------

    [Fact]
    public void Repair_OnRemovable_IsAcceptedAndWarnsAboutTheDismount()
    {
        var v = Op(repair: true).Validate(State(Disk()));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
        Assert.Contains(v.Warnings, w => w.Contains("dismounted"));
    }

    [Fact]
    public void Repair_OnTheSystemDisk_IsRefusedAndExplainsTheScheduleProblem()
    {
        var disk = Disk(removable: false, system: true, boot: true, parts: Part(letter: "C", boot: true));
        var v = Op(repair: true, allowInternal: true).Validate(State(disk, systemDisk: 3));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("system/boot disk") && e.Contains("next boot"));
    }

    [Fact]
    public void Repair_OnADiskThatIsOnlyTheSystemDiskByNumber_IsRefused()
    {
        // IsSystem and IsBoot can both be false on a disk SystemState still names as the system disk.
        var v = Op(repair: true, allowInternal: true).Validate(State(Disk(removable: false), systemDisk: 3));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Repair_OnAnInternalDisk_IsRefusedWithoutAcknowledgment()
    {
        var v = Op(repair: true).Validate(State(Disk(removable: false)));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("INTERNAL"));
    }

    [Fact]
    public void Repair_OnAnInternalDisk_IsAcceptedWithAcknowledgment()
    {
        var v = Op(repair: true, allowInternal: true).Validate(State(Disk(removable: false)));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
        Assert.Contains(v.Warnings, w => w.Contains("INTERNAL"));
    }

    [Theory]
    [InlineData(PartitionKind.Efi)]
    [InlineData(PartitionKind.MicrosoftReserved)]
    [InlineData(PartitionKind.Recovery)]
    public void Repair_OnProtectedPartitionKinds_IsRefused(PartitionKind kind)
    {
        var v = Op(repair: true).Validate(State(Disk(parts: Part(kind: kind, letter: null, fs: "FAT32"))));
        Assert.False(v.IsValid);
    }

    [Fact]
    public void Repair_OnAReadOnlyDisk_IsRefused()
    {
        var v = Op(repair: true).Validate(State(Disk(readOnly: true)));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("read-only"));
    }

    [Fact]
    public void Repair_OnABitLockerProtectedVolume_IsRefused()
    {
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted };
        var v = Op(repair: true).Validate(State(Disk(parts: Part(bl: bl))));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("BitLocker"));
    }

    [Fact]
    public void Check_OnABitLockerProtectedButUnlockedVolume_IsAllowed()
    {
        // Read-only on an unlocked volume is fine: chkdsk sees the decrypted filesystem.
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On, Conversion = BitLockerConversion.FullyEncrypted };
        var v = Op().Validate(State(Disk(parts: Part(bl: bl))));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
    }

    // ---------- simulate ----------

    [Fact]
    public void Simulate_Repair_ListsTheDismount()
    {
        var sim = Op(repair: true).Simulate(State(Disk()));
        Assert.True(sim.Feasible);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("Dismount"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("/f"));
    }

    [Fact]
    public void Simulate_Check_SaysNothingChanges()
    {
        var sim = Op().Simulate(State(Disk()));
        Assert.True(sim.Feasible);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("Nothing on the volume changes"));
    }

    [Fact]
    public void Simulate_Blocked_CarriesTheReason()
    {
        var sim = Op(repair: true).Simulate(State(Disk(removable: false)));
        Assert.False(sim.Feasible);
        Assert.Contains("INTERNAL", sim.BlockingReason);
    }

    [Fact]
    public void Verify_BeforeAnyRun_Fails()
    {
        var verify = Op().VerifyAsync().Result;
        Assert.False(verify.Verified);
    }
}
