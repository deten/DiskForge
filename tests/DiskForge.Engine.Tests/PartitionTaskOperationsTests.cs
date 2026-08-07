using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

public class PartitionTaskOperationsTests
{
    private const ulong GB = 1024UL * 1024 * 1024;

    private static PartitionInfo Part(int num = 1, string? letter = "E", string fs = "NTFS",
        PartitionKind kind = PartitionKind.Basic, bool system = false, bool boot = false)
        => new()
        {
            PartitionNumber = num,
            Kind = kind,
            SizeBytes = 8 * GB,
            DriveLetter = letter,
            IsSystem = system,
            IsBoot = boot,
            Volume = new VolumeInfo { DriveLetter = letter, Label = "OLD", FileSystem = fs, SizeBytes = 8 * GB, FreeBytes = 4 * GB }
        };

    private static SystemState State(params PartitionInfo[] parts)
    {
        var disk = new PhysicalDiskInfo { Number = 3, FriendlyName = "USB", Partitions = parts };
        return new SystemState { Disks = new[] { disk }, IsElevated = true };
    }

    [Fact]
    public void Label_Valid_WhenWithinLimits()
    {
        var op = new SetVolumeLabelOperation(new SetVolumeLabelSettings
        { DiskNumber = 3, PartitionNumber = 1, DriveLetter = "E", NewLabel = "PHOTOS" });
        Assert.True(op.Validate(State(Part())).IsValid);
    }

    [Fact]
    public void Label_TooLongForFat32_Rejected()
    {
        var op = new SetVolumeLabelOperation(new SetVolumeLabelSettings
        { DiskNumber = 3, PartitionNumber = 1, DriveLetter = "E", NewLabel = "TWELVECHARSX" });
        var result = op.Validate(State(Part(fs: "FAT32")));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Label_NoVolume_Rejected()
    {
        var bare = new PartitionInfo { PartitionNumber = 1, Kind = PartitionKind.Basic, SizeBytes = GB };
        var op = new SetVolumeLabelOperation(new SetVolumeLabelSettings
        { DiskNumber = 3, PartitionNumber = 1, NewLabel = "X" });
        Assert.False(op.Validate(State(bare)).IsValid);
    }

    [Fact]
    public void Letter_Valid_WhenFree()
    {
        var op = new SetDriveLetterOperation(new SetDriveLetterSettings
        { DiskNumber = 3, PartitionNumber = 1, NewLetter = "G" });
        Assert.True(op.Validate(State(Part(letter: "E"))).IsValid);
    }

    [Fact]
    public void Letter_InUse_Rejected()
    {
        var op = new SetDriveLetterOperation(new SetDriveLetterSettings
        { DiskNumber = 3, PartitionNumber = 1, NewLetter = "F" });
        var result = op.Validate(State(Part(1, "E"), Part(2, "F")));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("in use"));
    }

    [Fact]
    public void Letter_SystemPartition_Rejected()
    {
        var op = new SetDriveLetterOperation(new SetDriveLetterSettings
        { DiskNumber = 3, PartitionNumber = 1, NewLetter = "G" });
        var result = op.Validate(State(Part(letter: "C", system: true)));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Ops_AreNonDestructive()
    {
        var label = new SetVolumeLabelOperation(new SetVolumeLabelSettings { DiskNumber = 3, PartitionNumber = 1, NewLabel = "X" });
        var letter = new SetDriveLetterOperation(new SetDriveLetterSettings { DiskNumber = 3, PartitionNumber = 1, NewLetter = "G" });
        Assert.False(label.Describe().IsDestructive);
        Assert.False(letter.Describe().IsDestructive);
    }
}
