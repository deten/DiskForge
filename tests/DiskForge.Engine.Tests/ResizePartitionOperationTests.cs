using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Guards for resizing a partition in place. This is the first operation that keeps a filesystem while
/// changing the extent underneath it, so the dangerous mistakes are different from the other write ops:
/// moving a boundary a filesystem cannot follow, growing into a neighbour, or shrinking over live data.
/// </summary>
public class ResizePartitionOperationTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;

    private static PartitionInfo Part(
        int number = 1, ulong offset = MB, ulong size = 8 * GB, string fs = "NTFS",
        ulong? used = null, PartitionKind kind = PartitionKind.Basic,
        bool system = false, bool boot = false, BitLockerInfo? bl = null)
        => new()
        {
            PartitionNumber = number,
            OffsetBytes = offset,
            SizeBytes = size,
            Kind = kind,
            DriveLetter = "E",
            IsSystem = system,
            IsBoot = boot,
            Volume = fs.Length == 0 ? null : new VolumeInfo
            {
                DriveLetter = "E", Label = "DATA", FileSystem = fs,
                SizeBytes = size, FreeBytes = size - (used ?? size / 2),
                BitLocker = bl ?? BitLockerInfo.NotEncryptable
            }
        };

    private static PhysicalDiskInfo Disk(
        ulong size = 32 * GB, bool removable = true, bool system = false,
        PartitionStyle style = PartitionStyle.Gpt, params PartitionInfo[] parts)
        => new()
        {
            Number = 3,
            FriendlyName = removable ? "SanDisk Ultra USB" : "Internal SSD",
            SizeBytes = size,
            Bus = removable ? StorageBus.Usb : StorageBus.Sata,
            IsRemovable = removable,
            IsSystemDisk = system,
            PartitionStyle = style,
            Capabilities = new DriveCapabilities { Supported = DriveCapability.PartitionEdit },
            Partitions = DiskMap.Build(parts.Length > 0 ? parts : new[] { Part() }, size, style)
        };

    private static SystemState State(PhysicalDiskInfo disk, int? systemDisk = null)
        => new() { Disks = new[] { disk }, SystemDiskNumber = systemDisk, IsElevated = true };

    private static ResizePartitionOperation Resize(
        ulong newSize, int part = 1, ulong? offset = MB, ulong? current = 8 * GB, bool allowInternal = false)
        => new(new ResizePartitionSettings
        {
            DiskNumber = 3, PartitionNumber = part, NewSizeBytes = newSize,
            OffsetBytes = offset, CurrentSizeBytes = current, DriveLetter = "E",
            AllowNonRemovable = allowInternal
        });

    // ---------- the filesystem has to be able to move with the extent ----------

    [Fact]
    public void Ntfs_CanBeResized()
    {
        var result = Resize(12 * GB).Validate(State(Disk()));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    /// <summary>
    /// The dangerous case. Windows will happily move a partition boundary, but exFAT and FAT32 have no
    /// in-place resize, so the filesystem would still believe it is the old size. That is silent
    /// corruption, so it must be refused rather than attempted.
    /// </summary>
    [Theory]
    [InlineData("exFAT")]
    [InlineData("FAT32")]
    public void FileSystemsWindowsCannotResize_AreRefused(string fs)
    {
        var result = Resize(12 * GB).Validate(State(Disk(parts: new[] { Part(fs: fs) })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cannot resize") && e.Contains(fs));
    }

    [Theory]
    [InlineData("ext4")]
    [InlineData("btrfs")]
    [InlineData("xfs")]
    public void LinuxFileSystems_AreRefusedWithTheirOwnReason(string fs)
    {
        var part = Part(fs: fs, kind: PartitionKind.Linux);
        var result = Resize(12 * GB).Validate(State(Disk(parts: new[] { part })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Linux filesystem"));
    }

    [Fact]
    public void RawPartition_IsRefused()
    {
        var result = Resize(12 * GB).Validate(State(Disk(parts: new[] { Part(fs: "RAW") })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no filesystem Windows can read"));
    }

    // ---------- growth needs free space directly after ----------

    [Fact]
    public void Growing_IntoAdjacentFreeSpace_IsAllowed()
    {
        var result = Resize(16 * GB).Validate(State(Disk()));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    /// <summary>A gap elsewhere is no use: an extent is contiguous and nothing relocates a neighbour.</summary>
    [Fact]
    public void Growing_IntoTheNextPartition_IsRefused()
    {
        var disk = Disk(parts: new[]
        {
            Part(1, MB, 8 * GB),
            Part(2, MB + 8 * GB, 8 * GB, fs: "NTFS")
        });

        var result = Resize(12 * GB).Validate(State(disk));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("partition 2") && e.Contains("directly after"));
    }

    [Fact]
    public void Growing_PastTheEndOfTheDisk_IsRefused()
    {
        var result = Resize(40 * GB).Validate(State(Disk(size: 32 * GB)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("past the end"));
    }

    /// <summary>GPT keeps a backup header at the tail, so the usable end is short of the disk end.</summary>
    [Fact]
    public void Growing_IntoTheGptBackupHeader_IsRefused()
    {
        var disk = Disk(size: 32 * GB, style: PartitionStyle.Gpt);
        var result = Resize(32 * GB - MB).Validate(State(disk));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("past the end"));
    }

    // ---------- shrinking cannot go over live data ----------

    [Fact]
    public void Shrinking_BelowUsedSpace_IsRefused()
    {
        // 6 GB in use inside an 8 GB partition; asking for 4 GB would drop 2 GB of files.
        var disk = Disk(parts: new[] { Part(used: 6 * GB) });
        var result = Resize(4 * GB).Validate(State(disk));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("in use"));
    }

    [Fact]
    public void Shrinking_AboveUsedSpace_IsAllowed()
    {
        var disk = Disk(parts: new[] { Part(used: 2 * GB) });
        var result = Resize(6 * GB).Validate(State(disk));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void Shrinking_ToATightFit_WarnsThatWindowsMayRefuse()
    {
        var disk = Disk(parts: new[] { Part(used: 5 * GB) });
        var result = Resize(5 * GB + 64 * MB).Validate(State(disk));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("immovable"));
    }

    /// <summary>
    /// A volume whose usage Windows cannot read gives no staging-time bound, so the operation must say
    /// the limit is only known at Apply rather than pretend it validated something.
    /// </summary>
    [Fact]
    public void Shrinking_WhenUsageIsUnknown_WarnsRatherThanGuessing()
    {
        var part = Part() with
        {
            Volume = new VolumeInfo
            {
                FileSystem = "NTFS", SizeBytes = 8 * GB, FreeBytes = 0, UsageKnown = false
            }
        };
        var result = Resize(4 * GB).Validate(State(Disk(parts: new[] { part })));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("only be known at Apply"));
    }

    // ---------- the standard guard ladder ----------

    [Fact]
    public void SystemDisk_IsRefused()
        => Assert.False(Resize(12 * GB).Validate(State(Disk(removable: false, system: true))).IsValid);

    [Fact]
    public void SystemDisk_ByStateNumber_IsRefused()
        => Assert.False(Resize(12 * GB).Validate(State(Disk(removable: false), systemDisk: 3)).IsValid);

    [Fact]
    public void InternalDisk_IsRefusedWithoutAcknowledgment()
    {
        var result = Resize(12 * GB).Validate(State(Disk(removable: false)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("INTERNAL"));
    }

    [Fact]
    public void InternalDisk_IsAllowedWithAcknowledgment()
    {
        var result = Resize(12 * GB, allowInternal: true).Validate(State(Disk(removable: false)));

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("INTERNAL"));
    }

    [Theory]
    [InlineData(PartitionKind.Efi)]
    [InlineData(PartitionKind.MicrosoftReserved)]
    [InlineData(PartitionKind.Recovery)]
    public void ProtectedPartitionKinds_AreRefused(PartitionKind kind)
    {
        var result = Resize(12 * GB).Validate(State(Disk(parts: new[] { Part(kind: kind) })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("system/EFI/recovery"));
    }

    [Fact]
    public void BitLockerProtectedVolume_IsRefused()
    {
        var bl = new BitLockerInfo { Protection = BitLockerProtection.On };
        var result = Resize(12 * GB).Validate(State(Disk(parts: new[] { Part(bl: bl) })));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BitLocker"));
    }

    /// <summary>A partition number alone can retarget once an earlier op shifts the layout.</summary>
    [Fact]
    public void StaleOffset_IsRefused()
    {
        var result = Resize(12 * GB, offset: 40 * GB).Validate(State(Disk()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no longer at the offset"));
    }

    [Fact]
    public void ResizingToTheSameSize_IsRefused()
    {
        var result = Resize(8 * GB).Validate(State(Disk()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already"));
    }

    // ---------- staged preview ----------

    [Fact]
    public void QueuedShrink_ShowsTheSmallerPartitionAndTheFreedSpace()
    {
        var state = State(Disk(parts: new[] { Part(used: 2 * GB) }));
        var planned = LayoutProjector.Project(state, new IDiskOperation[] { Resize(4 * GB) }).FindDisk(3)!;

        var resized = planned.Regions.Single(r => r.Partition.PartitionNumber == 1);
        Assert.Equal(PendingChange.Resize, resized.Pending);
        Assert.Equal(4 * GB, resized.Partition.SizeBytes);
        Assert.Contains("shrink", resized.PendingNote);

        // The released space has to show up as free, or the user cannot plan anything into it.
        Assert.Contains(planned.Regions, r => r.Partition.IsUnallocated && r.Partition.OffsetBytes == MB + 4 * GB);
    }

    [Fact]
    public void QueuedExtend_ShowsTheLargerPartition()
    {
        var state = State(Disk());
        var planned = LayoutProjector.Project(state, new IDiskOperation[] { Resize(16 * GB) }).FindDisk(3)!;

        var resized = planned.Regions.Single(r => r.Partition.PartitionNumber == 1);
        Assert.Equal(16 * GB, resized.Partition.SizeBytes);
        Assert.Contains("extend", resized.PendingNote);
    }

    /// <summary>The preview must not draw a partition on top of its neighbour.</summary>
    [Fact]
    public void QueuedExtend_OverANeighbour_IsNotDrawn()
    {
        var disk = Disk(parts: new[] { Part(1, MB, 8 * GB), Part(2, MB + 8 * GB, 8 * GB) });
        var planned = LayoutProjector.Project(State(disk), new IDiskOperation[] { Resize(12 * GB) }).FindDisk(3)!;

        var first = planned.Regions.Single(r => r.Partition.PartitionNumber == 1);
        Assert.Equal(8 * GB, first.Partition.SizeBytes);
        Assert.Equal(PendingChange.None, first.Pending);
    }

    [Fact]
    public void Describe_CallsAShrinkDestructiveAndAnExtendNot()
    {
        Assert.True(Resize(4 * GB).Describe().IsDestructive);
        Assert.False(Resize(16 * GB).Describe().IsDestructive);
    }
}
