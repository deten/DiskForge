using DiskForge.Core.Model;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Guards for the volume-release step — the fix for
/// <c>DiskPart has encountered an error: Access is denied</c> when formatting a USB stick.
/// Windows refuses to zero sectors underneath a volume it still has mounted, and (unlike a fixed disk)
/// removable media cannot be taken offline to get around it, so the volumes must be dismounted by path.
/// </summary>
public class VolumeReleaseTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;

    // ---------- which volumes get released ----------

    private static PhysicalDiskInfo Disk(params PartitionInfo[] parts) => new()
    {
        Number = 3,
        FriendlyName = "General UDisk",
        SizeBytes = 2 * GB,
        Bus = StorageBus.Usb,
        IsRemovable = true,
        Partitions = parts
    };

    private static PartitionInfo Part(int number, string? letter, string? volumePath, ulong offset = MB)
        => new()
        {
            PartitionNumber = number,
            Kind = PartitionKind.Basic,
            OffsetBytes = offset,
            SizeBytes = 512 * MB,
            DriveLetter = letter,
            Volume = volumePath is null && letter is null
                ? null
                : new VolumeInfo { DriveLetter = letter, UniqueId = volumePath, FileSystem = "exFAT" }
        };

    [Fact]
    public void VolumePathsOn_PrefersTheVolumeGuidPathOverTheDriveLetter()
    {
        var disk = Disk(Part(1, "F", @"\\?\Volume{11111111-1111-1111-1111-111111111111}\"));

        var paths = DiskVolumeReleaser.VolumePathsOn(disk);

        // The GUID path addresses the volume even when it has no letter, so it is the better handle.
        Assert.Equal(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\", Assert.Single(paths));
    }

    [Fact]
    public void VolumePathsOn_FallsBackToTheDriveLetter()
    {
        var disk = Disk(Part(1, "F", volumePath: null));
        Assert.Equal("F", Assert.Single(DiskVolumeReleaser.VolumePathsOn(disk)));
    }

    [Fact]
    public void VolumePathsOn_CoversEveryPartition_NotJustLetteredOnes()
    {
        var disk = Disk(
            Part(1, "F", @"\\?\Volume{aaaaaaaa-1111-1111-1111-111111111111}\"),
            Part(2, null, @"\\?\Volume{bbbbbbbb-2222-2222-2222-222222222222}\", offset: 600 * MB));

        // A mounted volume with no drive letter still blocks sector writes, so it must be released too.
        Assert.Equal(2, DiskVolumeReleaser.VolumePathsOn(disk).Count);
    }

    [Fact]
    public void VolumePathsOn_SkipsUnallocatedGapsAndPartitionsWithNoVolume()
    {
        var gap = new PartitionInfo { Kind = PartitionKind.Unallocated, OffsetBytes = 0, SizeBytes = MB };
        var bare = new PartitionInfo { PartitionNumber = 2, Kind = PartitionKind.Linux, OffsetBytes = 600 * MB, SizeBytes = MB };
        var disk = Disk(gap, Part(1, "F", null), bare);

        Assert.Equal("F", Assert.Single(DiskVolumeReleaser.VolumePathsOn(disk)));
    }

    [Fact]
    public void Release_ReportsWhenThereWasNothingMounted()
    {
        var line = Assert.Single(DiskVolumeReleaser.Release(Array.Empty<string>(), 3));
        Assert.Contains("no mounted volumes", line);
    }

    [Fact]
    public void Release_NeverThrows_OnAnUnusablePath()
    {
        // Best-effort by contract: a bad path is reported, not raised, so it cannot abort a format.
        var line = Assert.Single(DiskVolumeReleaser.Release(new[] { "not a volume" }, 3));
        Assert.Contains("could not release", line);
    }

    // ---------- volume path normalisation ----------

    [Theory]
    [InlineData("F", @"\\.\F:")]
    [InlineData("f", @"\\.\F:")]
    [InlineData("F:", @"\\.\F:")]
    [InlineData(@"F:\", @"\\.\F:")]
    public void Normalize_TurnsADriveLetterIntoADevicePath(string input, string expected)
        => Assert.Equal(expected, VolumeControl.Normalize(input));

    [Fact]
    public void Normalize_StripsTheTrailingBackslashFromAVolumeGuidPath()
    {
        // CreateFile rejects \\?\Volume{...}\ — the trailing separator has to go.
        Assert.Equal(@"\\?\Volume{11111111-1111-1111-1111-111111111111}",
            VolumeControl.Normalize(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData(@"C:\some\folder")]
    public void Normalize_RejectsWhatIsNotAVolume(string input)
        => Assert.Null(VolumeControl.Normalize(input));

    // ---------- the error the user actually saw ----------

    [Fact]
    public void AccessDenied_IsExplainedWithSomethingActionable()
    {
        var diskpartOutput = new ShellResult(1,
            "Microsoft DiskPart version 10.0.19041.3636\n\nDisk 3 is now the selected disk.\n\n" +
            "DiskPart has encountered an error: Access is denied.\nSee the System Event Log for more information.",
            "");

        var explained = FormatVolumeOperation.ExplainDiskPartFailure(diskpartOutput);

        // Keep diskpart's own words, but do not leave the user staring at a dead end.
        Assert.Contains("Access is denied", explained);
        Assert.Contains("Explorer", explained);
        Assert.Contains("unplug", explained);
    }

    /// <summary>
    /// The failure seen on a real USB stick on 2026-08-05: diskpart reported "The device is not ready"
    /// and the System log recorded VDS Basic Provider event 5, "Cannot zero sectors on disk
    /// \\?\PhysicalDrive3. Error code: 15". The drive was left with no partition table, so the message
    /// must not let the user assume their data survived a reported failure.
    /// </summary>
    [Fact]
    public void DeviceNotReady_SaysTheDiskWasProbablyAlreadyWiped()
    {
        var diskpartOutput = new ShellResult(1,
            "Microsoft DiskPart version 10.0.19041.3636\n\nDisk 3 is now the selected disk.\n\n" +
            "DiskPart has encountered an error: The device is not ready.\n" +
            "See the System Event Log for more information.",
            "");

        var explained = FormatVolumeOperation.ExplainDiskPartFailure(diskpartOutput);

        Assert.Contains("device is not ready", explained, StringComparison.OrdinalIgnoreCase);
        // The dangerous misreading is "it failed, so nothing happened".
        Assert.Contains("already", explained);
        // And it is not a permissions problem, which is what the other diskpart failure looks like.
        Assert.DoesNotContain("Access is denied", explained);
    }

    [Fact]
    public void OtherDiskPartFailures_ArePassedThroughUnchanged()
    {
        var other = new ShellResult(1, "There is no volume selected.", "");
        Assert.Equal("There is no volume selected.", FormatVolumeOperation.ExplainDiskPartFailure(other));
    }
}
