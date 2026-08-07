using System.Globalization;

namespace DiskForge.Engine.Linux;

/// <summary>A block device as Linux sees it. Sizes are already converted from 512-byte sysfs units.</summary>
public sealed record WslBlockDevice(
    string Name, bool IsWholeDisk, ulong SizeBytes, ulong StartBytes, string? ParentName)
{
    public string DeviceNode => "/dev/" + Name;
}

/// <summary>Outcome of trying to identify the caller's partition among the devices WSL exposes.</summary>
public sealed record DeviceMatch(string? DeviceNode, string? DiskName, string? Error)
{
    public bool Found => DeviceNode is not null;

    public static DeviceMatch Fail(string error) => new(null, null, error);
}

/// <summary>
/// Turns "what block devices does the WSL VM have?" into "which node is the partition the user
/// picked?" — the anti-wrong-target gate for Linux formats, and the reason this logic is a pure
/// function: it is unit-tested without WSL or hardware.
///
/// A device only qualifies when three independent facts line up: it appeared as a result of *our*
/// attach, its whole-disk size matches the Windows disk, and it has a child partition starting at
/// exactly the byte offset the staged plan recorded. Anything ambiguous is refused, never guessed.
/// </summary>
public static class WslBlockDevices
{
    /// <summary>sysfs reports sizes in fixed 512-byte units regardless of the drive's real sector size.</summary>
    private const ulong SysfsSectorSize = 512;

    /// <summary>Slack allowed when comparing sizes; offsets must still match to the byte.</summary>
    private const ulong SizeToleranceBytes = 1024 * 1024;

    /// <summary>
    /// Fixed, data-free enumeration script. Emits one line per device:
    /// <c>D name size_sectors 0</c> for whole disks and <c>P name size_sectors start_sectors parent</c>
    /// for partitions.
    /// </summary>
    public const string EnumerateScript =
        "for d in /sys/block/sd* /sys/block/vd* /sys/block/nvme*; do " +
        "[ -d \"$d\" ] || continue; n=${d##*/}; " +
        "printf 'D %s %s 0 -\\n' \"$n\" \"$(cat \"$d/size\")\"; " +
        "for p in \"$d\"/\"$n\"*; do [ -f \"$p/start\" ] || continue; " +
        "printf 'P %s %s %s %s\\n' \"${p##*/}\" \"$(cat \"$p/size\")\" \"$(cat \"$p/start\")\" \"$n\"; " +
        "done; done";

    public static IReadOnlyList<WslBlockDevice> Parse(string output)
    {
        var devices = new List<WslBlockDevice>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (parts[0] is not ("D" or "P")) continue;
            if (!ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeSectors))
                continue;
            ulong.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startSectors);

            var isDisk = parts[0] == "D";
            devices.Add(new WslBlockDevice(
                Name: parts[1],
                IsWholeDisk: isDisk,
                SizeBytes: sizeSectors * SysfsSectorSize,
                StartBytes: startSectors * SysfsSectorSize,
                ParentName: isDisk ? null : parts.Length > 4 ? parts[4] : null));
        }
        return devices;
    }

    /// <summary>
    /// Identifies the device node for one partition. <paramref name="before"/> is the device list from
    /// before the attach, so the candidate set is limited to disks our own mount produced.
    /// </summary>
    public static DeviceMatch MatchPartition(
        IReadOnlyList<WslBlockDevice> before,
        IReadOnlyList<WslBlockDevice> after,
        ulong diskSizeBytes,
        ulong partitionOffsetBytes,
        ulong partitionSizeBytes)
    {
        var known = before.Where(d => d.IsWholeDisk).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var appeared = after.Where(d => d.IsWholeDisk && !known.Contains(d.Name)).ToList();

        if (appeared.Count == 0)
            return DeviceMatch.Fail(
                "The disk did not appear as a block device inside WSL after attaching it. " +
                "Nothing was written.");

        // Size is the second, independent identity check. If more than one disk appeared (another
        // process attaching something concurrently), only a size match keeps us in the game.
        var candidates = appeared
            .Where(d => AbsDiff(d.SizeBytes, diskSizeBytes) <= SizeToleranceBytes)
            .ToList();

        if (candidates.Count == 0)
            return DeviceMatch.Fail(
                $"The disk that appeared inside WSL is {Bytes(appeared[0].SizeBytes)}, but the target " +
                $"disk is {Bytes(diskSizeBytes)}. Refusing to write to an unverified device.");

        if (candidates.Count > 1)
            return DeviceMatch.Fail(
                $"{candidates.Count} same-size disks appeared inside WSL ({string.Join(", ", candidates.Select(c => c.DeviceNode))}); " +
                "the target is ambiguous. Nothing was written.");

        var disk = candidates[0];

        // Third check: the partition table Linux read must contain our exact extent. The start offset
        // is the identity — both sides read the same GPT/MBR, so it matches to the byte or not at all.
        var matches = after
            .Where(d => !d.IsWholeDisk
                        && string.Equals(d.ParentName, disk.Name, StringComparison.Ordinal)
                        && d.StartBytes == partitionOffsetBytes
                        && AbsDiff(d.SizeBytes, partitionSizeBytes) <= SizeToleranceBytes)
            .ToList();

        if (matches.Count == 1)
            return new DeviceMatch(matches[0].DeviceNode, disk.Name, null);

        if (matches.Count > 1)
            return DeviceMatch.Fail(
                $"Found {matches.Count} partitions at offset {Bytes(partitionOffsetBytes)} on {disk.DeviceNode}; " +
                "the target is ambiguous. Nothing was written.");

        var seen = after.Where(d => !d.IsWholeDisk && d.ParentName == disk.Name).ToList();
        var listing = seen.Count == 0
            ? "no partitions were visible on it"
            : "it has " + string.Join(", ", seen.Select(
                s => $"{s.DeviceNode} at {Bytes(s.StartBytes)} ({Bytes(s.SizeBytes)})"));

        return DeviceMatch.Fail(
            $"No partition on {disk.DeviceNode} starts at {Bytes(partitionOffsetBytes)} with size " +
            $"{Bytes(partitionSizeBytes)} — {listing}. Refusing to write to an unverified device.");
    }

    /// <summary>
    /// Identifies a whole-disk device that our attach produced — the staged-VHDX route formats a bare
    /// device with no partition table, so there is no partition offset to match on. The same two locks
    /// still apply: it must be new since <paramref name="before"/>, and its size must match.
    /// </summary>
    public static DeviceMatch MatchWholeDisk(
        IReadOnlyList<WslBlockDevice> before,
        IReadOnlyList<WslBlockDevice> after,
        ulong expectedSizeBytes)
    {
        var known = before.Where(d => d.IsWholeDisk).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var appeared = after.Where(d => d.IsWholeDisk && !known.Contains(d.Name)).ToList();

        if (appeared.Count == 0)
            return DeviceMatch.Fail("The staging image did not appear as a block device inside WSL. " +
                                    "Nothing was written.");

        var candidates = appeared
            .Where(d => AbsDiff(d.SizeBytes, expectedSizeBytes) <= SizeToleranceBytes)
            .ToList();

        if (candidates.Count == 0)
            return DeviceMatch.Fail(
                $"The device that appeared inside WSL is {Bytes(appeared[0].SizeBytes)}, but the staging " +
                $"image is {Bytes(expectedSizeBytes)}. Refusing to write to an unverified device.");

        if (candidates.Count > 1)
            return DeviceMatch.Fail(
                $"{candidates.Count} same-size devices appeared inside WSL " +
                $"({string.Join(", ", candidates.Select(c => c.DeviceNode))}); the target is ambiguous.");

        return new DeviceMatch(candidates[0].DeviceNode, candidates[0].Name, null);
    }

    private static ulong AbsDiff(ulong a, ulong b) => a > b ? a - b : b - a;

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
