using DiskForge.Engine.Linux;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The anti-wrong-target gate for Linux formats. Once a disk is inside the WSL VM there is no
/// Windows-side safety net left — mkfs will happily erase whatever device node it is handed — so the
/// logic that picks that node is pure and tested exhaustively here, with no WSL or hardware involved.
///
/// The scenario that matters most: the WSL VM always already has its own disks (/dev/sda is the
/// distro's root filesystem). A candidate must never be one of those, even when sizes or partition
/// offsets happen to coincide.
/// </summary>
public class WslBlockDevicesTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong GB = 1024UL * MB;
    private const ulong Sector = 512;

    /// <summary>Renders the same line format the on-device enumeration script emits.</summary>
    private static string Line(char kind, string name, ulong sizeBytes, ulong startBytes, string parent)
        => $"{kind} {name} {sizeBytes / Sector} {startBytes / Sector} {parent}";

    /// <summary>The WSL VM's own disks — always present, never a legal target.</summary>
    private static string WslOwnDisks() =>
        Line('D', "sda", 256 * GB, 0, "-") + "\n" +
        Line('P', "sda1", 256 * GB - MB, MB, "sda") + "\n" +
        Line('D', "sdb", 4 * GB, 0, "-");

    private static IReadOnlyList<WslBlockDevice> Parse(string text) => WslBlockDevices.Parse(text);

    // ---------- parsing ----------

    [Fact]
    public void Parse_ConvertsSysfsSectorsToBytes_AndLinksChildren()
    {
        var devices = Parse(
            Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
            Line('P', "sdc1", 1 * GB, MB, "sdc"));

        var disk = Assert.Single(devices.Where(d => d.IsWholeDisk));
        Assert.Equal("sdc", disk.Name);
        Assert.Equal(2 * GB, disk.SizeBytes);
        Assert.Equal("/dev/sdc", disk.DeviceNode);

        var part = Assert.Single(devices.Where(d => !d.IsWholeDisk));
        Assert.Equal(MB, part.StartBytes);
        Assert.Equal(1 * GB, part.SizeBytes);
        Assert.Equal("sdc", part.ParentName);
        Assert.Equal("/dev/sdc1", part.DeviceNode);
    }

    [Fact]
    public void Parse_IgnoresGarbageLines()
    {
        var devices = Parse("nonsense\nD sdc notanumber 0 -\n\n" + Line('D', "sdd", 1 * GB, 0, "-"));
        Assert.Equal("sdd", Assert.Single(devices).Name);
    }

    // ---------- the happy path ----------

    [Fact]
    public void MatchPartition_FindsTheAttachedDisksPartition()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 1 * GB, 100 * MB, "sdc"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 100 * MB, 1 * GB);

        Assert.True(match.Found);
        Assert.Equal("/dev/sdc1", match.DeviceNode);
        Assert.Equal("sdc", match.DiskName);
    }

    [Fact]
    public void MatchPartition_PicksTheRightPartitionAmongSeveral()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 100 * MB, MB, "sdc") + "\n" +
                          Line('P', "sdc2", 500 * MB, 101 * MB, "sdc") + "\n" +
                          Line('P', "sdc3", 1 * GB, 601 * MB, "sdc"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 101 * MB, 500 * MB);

        Assert.True(match.Found);
        Assert.Equal("/dev/sdc2", match.DeviceNode);
    }

    // ---------- the refusals ----------

    [Fact]
    public void MatchPartition_NeverTargetsAPreExistingDisk_EvenWhenTheLayoutMatches()
    {
        // /dev/sda was already there and happens to hold a partition at exactly the offset and size
        // we are looking for. Attaching produced nothing new — this must refuse, not fall back to sda.
        var same = WslOwnDisks();
        var before = Parse(same);
        var after = Parse(same);

        var match = WslBlockDevices.MatchPartition(before, after, 256 * GB, MB, 256 * GB - MB);

        Assert.False(match.Found);
        Assert.Null(match.DeviceNode);
        Assert.Contains("did not appear", match.Error);
    }

    [Fact]
    public void MatchPartition_RefusesWhenDiskSizeDisagrees()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 8 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 1 * GB, MB, "sdc"));

        // Target disk is 2 GB but a 8 GB disk showed up: wrong device, refuse.
        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, MB, 1 * GB);

        Assert.False(match.Found);
        Assert.Contains("unverified device", match.Error);
    }

    [Fact]
    public void MatchPartition_RefusesWhenNoPartitionStartsAtTheExpectedOffset()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 1 * GB, 600 * MB, "sdc"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 100 * MB, 1 * GB);

        Assert.False(match.Found);
        Assert.Contains("No partition on /dev/sdc starts at", match.Error);
        // The error must show what WAS there, so the user can tell a stale plan from a real problem.
        Assert.Contains("/dev/sdc1", match.Error);
    }

    [Fact]
    public void MatchPartition_RefusesWhenPartitionSizeIsWayOff()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 2 * MB, 100 * MB, "sdc"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 100 * MB, 1 * GB);

        Assert.False(match.Found);
    }

    [Fact]
    public void MatchPartition_RefusesWhenTwoIdenticalDisksAppear()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" +
                          Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdc1", 1 * GB, 100 * MB, "sdc") + "\n" +
                          Line('D', "sdd", 2 * GB, 0, "-") + "\n" +
                          Line('P', "sdd1", 1 * GB, 100 * MB, "sdd"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 100 * MB, 1 * GB);

        Assert.False(match.Found);
        Assert.Contains("ambiguous", match.Error);
    }

    [Fact]
    public void MatchPartition_RefusesWhenTheDiskHasNoPartitionsAtAll()
    {
        var before = Parse(WslOwnDisks());
        var after = Parse(WslOwnDisks() + "\n" + Line('D', "sdc", 2 * GB, 0, "-"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, MB, 1 * GB);

        Assert.False(match.Found);
        Assert.Contains("no partitions were visible", match.Error);
    }

    [Fact]
    public void MatchPartition_IgnoresPartitionsOnOtherDisks()
    {
        // A pre-existing disk carries a partition at the exact offset/size we want. The match must
        // stay scoped to the disk that appeared.
        var before = Parse(
            Line('D', "sda", 2 * GB, 0, "-") + "\n" +
            Line('P', "sda1", 1 * GB, 100 * MB, "sda"));
        var after = Parse(
            Line('D', "sda", 2 * GB, 0, "-") + "\n" +
            Line('P', "sda1", 1 * GB, 100 * MB, "sda") + "\n" +
            Line('D', "sdc", 2 * GB, 0, "-") + "\n" +
            Line('P', "sdc1", 1 * GB, 100 * MB, "sdc"));

        var match = WslBlockDevices.MatchPartition(before, after, 2 * GB, 100 * MB, 1 * GB);

        Assert.True(match.Found);
        Assert.Equal("/dev/sdc1", match.DeviceNode);
    }
}
