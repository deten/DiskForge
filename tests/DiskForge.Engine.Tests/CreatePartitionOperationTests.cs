using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Exercises the guards in CreatePartitionOperation.Validate. The safety-critical one is the extent
/// check: a create must only ever land wholly inside a single unallocated gap, never over a neighbour.
/// Pure logic, no hardware required.
/// </summary>
public class CreatePartitionOperationTests
{
    private const ulong GB = 1024UL * 1024 * 1024;
    private const ulong MB = 1024UL * 1024;

    // ---------- fixtures ----------

    // Layout: [1 MiB reserved][ 8 GB partition 1 ][ 24 GB unallocated ] on a 32 GB disk.
    private const ulong GapStart = MB + 8 * GB;
    private const ulong GapSize = 32 * GB - GapStart;

    private static PartitionInfo Existing(int number = 1, ulong offset = MB, ulong size = 8 * GB, string letter = "E")
        => new()
        {
            PartitionNumber = number,
            Kind = PartitionKind.Basic,
            OffsetBytes = offset,
            SizeBytes = size,
            DriveLetter = letter,
            Volume = new VolumeInfo
            {
                DriveLetter = letter, Label = "DATA", FileSystem = "NTFS",
                SizeBytes = size, FreeBytes = size / 2, BitLocker = BitLockerInfo.NotEncryptable
            }
        };

    private static PartitionInfo Gap(ulong offset = GapStart, ulong size = GapSize)
        => new() { Kind = PartitionKind.Unallocated, OffsetBytes = offset, SizeBytes = size };

    private static PhysicalDiskInfo Disk(
        int number = 3, bool removable = true, bool system = false, ulong size = 32 * GB,
        PartitionStyle style = PartitionStyle.Gpt,
        DriveCapability caps = DriveCapability.PartitionEdit | DriveCapability.Format,
        params PartitionInfo[] parts)
        => new()
        {
            Number = number,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            Media = DiskMediaType.Ssd,
            PartitionStyle = style,
            IsRemovable = removable,
            IsSystemDisk = system,
            Capabilities = new DriveCapabilities { Supported = caps },
            Partitions = parts.Length > 0 ? parts : new[] { Existing(), Gap() }
        };

    private static SystemState State(PhysicalDiskInfo disk, int? systemDisk = null)
        => new() { Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true };

    private static CreatePartitionSettings Create(
        ulong offset = GapStart, ulong size = 8 * GB, int disk = 3, bool allowInternal = false,
        string? letter = null, string label = "NEW", FileSystemType fs = FileSystemType.Exfat)
        => new()
        {
            DiskNumber = disk, OffsetBytes = offset, SizeBytes = size,
            DriveLetter = letter, Label = label, FileSystem = fs, AllowNonRemovable = allowInternal
        };

    // ---------- happy path ----------

    [Fact]
    public void ValidCreate_InFreeGap_IsAccepted()
    {
        var op = new CreatePartitionOperation(Create());
        Assert.True(op.Validate(State(Disk())).IsValid);
    }

    [Fact]
    public void Create_IsNotDestructive()
    {
        // Creating only ever consumes free space, so it must not demand the typed FORMAT confirmation.
        Assert.False(new CreatePartitionOperation(Create()).Describe().IsDestructive);
    }

    [Fact]
    public void FillingTheEntireGap_IsAccepted()
    {
        var op = new CreatePartitionOperation(Create(size: GapSize));
        Assert.True(op.Validate(State(Disk())).IsValid);
    }

    // ---------- the anti-clobber gate ----------

    [Fact]
    public void OverlappingAnExistingPartition_IsRejected()
    {
        // Starts 1 GB before the gap, so it would eat the tail of partition 1.
        var op = new CreatePartitionOperation(Create(offset: GapStart - GB, size: 4 * GB));
        var result = op.Validate(State(Disk()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already used by partition 1"));
    }

    [Fact]
    public void ExtentSpanningPastTheGapIntoAnotherPartition_IsRejected()
    {
        // Layout: [P1 8 GB][2 GB gap][P2 8 GB] — a 4 GB create in the gap runs into P2.
        var p1 = Existing(1, MB, 8 * GB);
        var gap = Gap(MB + 8 * GB, 2 * GB);
        var p2 = Existing(2, MB + 10 * GB, 8 * GB, "F");
        var op = new CreatePartitionOperation(Create(offset: MB + 8 * GB, size: 4 * GB));

        var result = op.Validate(State(Disk(parts: new[] { p1, gap, p2 })));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already used by partition 2"));
    }

    [Fact]
    public void SpaceNotInsideAnyGap_IsRejected()
    {
        // A disk that is fully allocated has no free space to create into.
        var full = Disk(parts: new[] { Existing(1, MB, 32 * GB - MB) });
        var op = new CreatePartitionOperation(Create(offset: MB, size: GB));
        Assert.False(op.Validate(State(full)).IsValid);
    }

    [Fact]
    public void ExtendingPastTheEndOfDisk_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(offset: GapStart, size: GapSize + GB));
        var result = op.Validate(State(Disk()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("past the end of the disk"));
    }

    // ---------- disk-level guards ----------

    [Fact]
    public void SystemDisk_IsRejected()
    {
        var op = new CreatePartitionOperation(Create());
        var result = op.Validate(State(Disk(system: true)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/boot disk"));
    }

    [Fact]
    public void SystemDiskByStateNumber_IsRejected()
    {
        var op = new CreatePartitionOperation(Create());
        Assert.False(op.Validate(State(Disk(number: 3), systemDisk: 3)).IsValid);
    }

    [Fact]
    public void InternalDisk_IsRejectedUnlessExplicitlyAllowed()
    {
        var op = new CreatePartitionOperation(Create(allowInternal: false));
        Assert.False(op.Validate(State(Disk(removable: false))).IsValid);

        var allowed = new CreatePartitionOperation(Create(allowInternal: true));
        var result = allowed.Validate(State(Disk(removable: false)));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("INTERNAL"));
    }

    [Fact]
    public void MissingPartitionEditCapability_IsRejected()
    {
        var op = new CreatePartitionOperation(Create());
        var result = op.Validate(State(Disk(caps: DriveCapability.Format)));
        Assert.False(result.IsValid);
        Assert.Equal(DriveCapability.PartitionEdit, result.MissingCapabilities);
    }

    [Fact]
    public void UninitializedDisk_IsRejected()
    {
        var op = new CreatePartitionOperation(Create());
        var result = op.Validate(State(Disk(style: PartitionStyle.Raw)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no partition table"));
    }

    [Fact]
    public void UnknownDisk_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(disk: 9));
        Assert.False(op.Validate(State(Disk())).IsValid);
    }

    // ---------- geometry ----------

    [Fact]
    public void UnalignedOffset_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(offset: GapStart + 512));
        var result = op.Validate(State(Disk()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("aligned"));
    }

    [Fact]
    public void TooSmall_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(size: 1 * MB));
        var result = op.Validate(State(Disk()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("too small"));
    }

    // ---------- MBR constraints ----------

    [Fact]
    public void MbrDiskWithFourPrimaries_IsRejected()
    {
        var parts = new[]
        {
            Existing(1, MB, 4 * GB, "E"), Existing(2, MB + 4 * GB, 4 * GB, "F"),
            Existing(3, MB + 8 * GB, 4 * GB, "G"), Existing(4, MB + 12 * GB, 4 * GB, "H"),
            Gap(MB + 16 * GB, 16 * GB - MB)
        };
        var op = new CreatePartitionOperation(Create(offset: MB + 16 * GB, size: 8 * GB));
        var result = op.Validate(State(Disk(style: PartitionStyle.Mbr, parts: parts)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MBR partition table"));
    }

    /// <summary>
    /// Three is the real cap, not four. Filling the last MBR slot makes New-Partition create an
    /// extended *container* rather than a primary, and a filesystem written into one is reachable
    /// from neither Windows nor Linux — which is exactly what happened to a real btrfs partition.
    /// </summary>
    [Fact]
    public void MbrDiskWithThreePartitions_RefusesAFourth()
    {
        var parts = new[]
        {
            Existing(1, MB, 4 * GB, "E"), Existing(2, MB + 4 * GB, 4 * GB, "F"),
            Existing(3, MB + 8 * GB, 4 * GB, "G"),
            Gap(MB + 12 * GB, 20 * GB - MB)
        };
        var op = new CreatePartitionOperation(Create(offset: MB + 12 * GB, size: 8 * GB));
        var result = op.Validate(State(Disk(style: PartitionStyle.Mbr, parts: parts)));

        Assert.False(result.IsValid);
        // The message has to explain the *why*, or "why am I limited to 3?" is unanswerable.
        Assert.Contains(result.Errors, e => e.Contains("extended") && e.Contains("GPT"));
    }

    [Fact]
    public void GptDisk_AllowsMoreThanFourPartitions()
    {
        var parts = new[]
        {
            Existing(1, MB, 4 * GB, "E"), Existing(2, MB + 4 * GB, 4 * GB, "F"),
            Existing(3, MB + 8 * GB, 4 * GB, "G"), Existing(4, MB + 12 * GB, 4 * GB, "H"),
            Gap(MB + 16 * GB, 16 * GB - 2 * MB)
        };
        var op = new CreatePartitionOperation(Create(offset: MB + 16 * GB, size: 8 * GB));
        var result = op.Validate(State(Disk(style: PartitionStyle.Gpt, parts: parts)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void MbrBeyond2Tib_IsRejected()
    {
        // 3 TB MBR disk with a gap that crosses the 2 TiB addressing wall.
        var big = 3 * 1024UL * GB;
        var parts = new[] { Existing(1, MB, GB), Gap(MB + GB, big - MB - GB) };
        var op = new CreatePartitionOperation(Create(offset: 2000UL * GB, size: 200UL * GB));
        var result = op.Validate(State(Disk(size: big, style: PartitionStyle.Mbr, parts: parts)));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("2 TiB"));
    }

    // ---------- filesystem / label / letter ----------

    [Fact]
    public void Fat32OverLimit_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(size: 33 * GB, fs: FileSystemType.Fat32));
        var big = Disk(size: 64 * GB, parts: new[] { Existing(1, MB, 8 * GB), Gap(GapStart, 64 * GB - GapStart) });
        var result = op.Validate(State(big));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("FAT32"));
    }

    [Fact]
    public void BadLabel_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(label: "bad:name"));
        Assert.False(op.Validate(State(Disk())).IsValid);
    }

    [Fact]
    public void DriveLetterInUse_IsRejected()
    {
        var op = new CreatePartitionOperation(Create(letter: "E")); // taken by the existing partition
        var result = op.Validate(State(Disk()));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already in use"));
    }

    [Fact]
    public void FreeDriveLetter_IsAccepted()
    {
        var op = new CreatePartitionOperation(Create(letter: "X"));
        Assert.True(op.Validate(State(Disk())).IsValid);
    }

    [Fact]
    public void SkippingFormat_DoesNotRequireFormatCapabilityOrLabel()
    {
        var settings = Create(label: "") with { FormatNew = false };
        var op = new CreatePartitionOperation(settings);
        Assert.Equal(DriveCapability.PartitionEdit, op.RequiredCapabilities());
        Assert.True(op.Validate(State(Disk(caps: DriveCapability.PartitionEdit))).IsValid);
    }

    // ---------- simulation ----------

    [Fact]
    public void Simulate_IsInfeasible_WhenValidationFails()
    {
        var op = new CreatePartitionOperation(Create(offset: GapStart - GB, size: 4 * GB));
        var sim = op.Simulate(State(Disk()));
        Assert.False(sim.Feasible);
        Assert.NotNull(sim.BlockingReason);
    }

    [Fact]
    public void Simulate_ListsStepsAndTouchesNothing_WhenValid()
    {
        var op = new CreatePartitionOperation(Create(letter: "X"));
        var sim = op.Simulate(State(Disk()));
        Assert.True(sim.Feasible);
        Assert.Contains(sim.PlannedSteps, s => s.Contains("Create"));
        Assert.Contains(sim.PlannedSteps, s => s.Contains("X:"));
    }

    [Fact]
    public void PreviewScript_PassesExactBytesAndPipesToFormat()
    {
        // New-Partition must get exact byte values (diskpart's MB-only granularity would misalign).
        var op = new CreatePartitionOperation(Create(letter: "X", label: "NEW"));
        var script = op.PreviewScript();
        Assert.Contains($"-Offset {GapStart}", script);
        Assert.Contains($"-Size {8 * GB}", script);
        Assert.Contains("-DriveLetter X", script);
        Assert.Contains("Format-Volume", script);
        Assert.Contains("'NEW'", script);
    }

    [Fact]
    public void PreviewScript_OmitsFormat_WhenNotFormatting()
    {
        var op = new CreatePartitionOperation(Create() with { FormatNew = false });
        Assert.DoesNotContain("Format-Volume", op.PreviewScript());
    }
}
