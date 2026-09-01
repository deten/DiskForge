using System.Security.Cryptography;
using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// End-to-end resize against a throwaway VHDX loopback disk, never a real drive.
///
/// The unit tests cover the guard ladder; this covers the thing they cannot: that a shrink followed by
/// a grow actually moves the filesystem with the extent and leaves the files intact. A resize that
/// reports success but loses data is the failure mode worth spending an elevated test on, so the
/// assertion is a SHA-256 of a real file written before the first resize and re-checked after both.
///
/// Elevated-only: VHDX attach needs Administrator, so this auto-skips in an unelevated run.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class ResizeVhdxTests
{
    private const ulong MB = 1024UL * 1024;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    [RequiresElevationFact]
    public async Task Shrink_ThenGrow_KeepsTheFilesystemAndItsFiles()
    {
        // 2 GB, so NTFS has room to behave normally. On a very small volume its own metadata sits near
        // the end and Windows reports a shrink minimum close to the full size, which says nothing about
        // whether resizing works.
        using var vhdx = new VhdxLoopbackDisk(2048 * MB);
        await RunAsync($"Initialize-Disk -Number {vhdx.DiskNumber} -PartitionStyle GPT");

        var inspector = new SystemInspector();
        var gap = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.Where(p => p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

        // 1 GB partition, leaving ~1 GB free after it to grow back into.
        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 1024 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "RESIZE",
            AllowNonRemovable = true      // a VHDX presents as non-removable
        });
        var created = await create.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(created.Success, created.Error);

        var part = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.First(p => !p.IsUnallocated && p.SizeBytes >= 900 * MB);
        var letter = await AssignLetterAsync(vhdx.DiskNumber, part.PartitionNumber!.Value);

        // The canary: a real file, hashed, that must survive both moves.
        var canary = $@"{letter}:\canary.bin";
        var payload = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        await File.WriteAllBytesAsync(canary, payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload));

        // Ask Windows what it will actually allow rather than guessing. Hardcoding a target makes the
        // test a statement about NTFS internals instead of about the resize operation.
        var (min, _) = await SupportedSizeAsync(vhdx.DiskNumber, part.PartitionNumber.Value);
        var shrinkTo = RoundUpToAlignment(min + 64 * MB);
        Assert.True(shrinkTo < part.SizeBytes,
            $"NTFS will not shrink below {min} bytes on a {part.SizeBytes}-byte volume; nothing to test.");

        // --- shrink ---
        var shrink = await Resize(vhdx.DiskNumber, part, shrinkTo)
            .ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(shrink.Success, shrink.Error);
        await AssertCanaryAsync(canary, expected, "after shrinking");
        AssertSizeNear(inspector, vhdx.DiskNumber, part.OffsetBytes, shrinkTo);

        // --- grow back, past the original size ---
        var shrunk = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.First(p => p.OffsetBytes == part.OffsetBytes);
        var growTo = 1536 * MB;
        var grow = await Resize(vhdx.DiskNumber, shrunk, growTo)
            .ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(grow.Success, grow.Error);
        await AssertCanaryAsync(canary, expected, "after growing back");
        AssertSizeNear(inspector, vhdx.DiskNumber, part.OffsetBytes, growTo);
    }

    private static ulong RoundUpToAlignment(ulong value)
    {
        var a = ResizePartitionOperation.Alignment;
        var rem = value % a;
        return rem == 0 ? value : value + (a - rem);
    }

    private static async Task<(ulong Min, ulong Max)> SupportedSizeAsync(int diskNumber, int partitionNumber)
    {
        var result = await RunAsync(
            $"$s = Get-PartitionSupportedSize -DiskNumber {diskNumber} -PartitionNumber {partitionNumber}; " +
            "\"$($s.SizeMin) $($s.SizeMax)\"");

        var parts = result.Output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return (ulong.Parse(parts[0]), ulong.Parse(parts[1]));
    }

    /// <summary>
    /// Shrinking over live data must be refused and must change nothing. Two guards can catch it: the
    /// staging-time check against the volume's used bytes, and the <c>Get-PartitionSupportedSize</c>
    /// bound queried immediately before the write. The first one fires here, which is the better
    /// outcome, so the assertion accepts either and insists only that it was refused with a reason
    /// naming the shrink, and that the partition is untouched afterwards.
    /// </summary>
    [RequiresElevationFact]
    public async Task ShrinkBelowWhatTheFilesystemAllows_FailsWithoutChangingAnything()
    {
        using var vhdx = new VhdxLoopbackDisk(512 * MB);
        await RunAsync($"Initialize-Disk -Number {vhdx.DiskNumber} -PartitionStyle GPT");

        var inspector = new SystemInspector();
        var gap = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.Where(p => p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber, OffsetBytes = gap.OffsetBytes, SizeBytes = 256 * MB,
            FileSystem = FileSystemType.Ntfs, Label = "TIGHT", AllowNonRemovable = true
        });
        var created = await create.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(created.Success, created.Error);

        var part = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.First(p => !p.IsUnallocated && p.SizeBytes >= 200 * MB);
        var letter = await AssignLetterAsync(vhdx.DiskNumber, part.PartitionNumber!.Value);

        // Fill most of it, so anything near the minimum is impossible.
        await File.WriteAllBytesAsync($@"{letter}:\filler.bin", RandomNumberGenerator.GetBytes(180 * 1024 * 1024));

        var result = await Resize(vhdx.DiskNumber, part, 16 * MB)
            .ExecuteAsync(NoProgress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(
            result.Error!.Contains("shrink", StringComparison.OrdinalIgnoreCase) ||
            result.Error.Contains("Nothing was changed", StringComparison.Ordinal),
            $"the refusal should say why it would not shrink, but said: {result.Error}");

        // And the partition really is untouched.
        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.First(p => p.OffsetBytes == part.OffsetBytes);
        Assert.Equal(part.SizeBytes, after.SizeBytes);
    }

    private static ResizePartitionOperation Resize(
        int diskNumber, Core.Model.PartitionInfo part, ulong newSize)
        => new(new ResizePartitionSettings
        {
            DiskNumber = diskNumber,
            PartitionNumber = part.PartitionNumber!.Value,
            NewSizeBytes = newSize,
            OffsetBytes = part.OffsetBytes,
            CurrentSizeBytes = part.SizeBytes,
            AllowNonRemovable = true
        });

    private static async Task AssertCanaryAsync(string path, string expectedHash, string when)
    {
        Assert.True(File.Exists(path), $"the canary file vanished {when}");
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.True(expectedHash == actual, $"the canary file changed {when}");
    }

    private static void AssertSizeNear(SystemInspector inspector, int diskNumber, ulong offset, ulong expected)
    {
        var part = inspector.Capture(probeLinuxToolchain: false).FindDisk(diskNumber)!
            .Partitions.First(p => p.OffsetBytes == offset);
        var diff = part.SizeBytes > expected ? part.SizeBytes - expected : expected - part.SizeBytes;
        Assert.True(diff <= ResizePartitionOperation.Alignment,
            $"expected about {expected} bytes, got {part.SizeBytes}");
    }

    private static async Task<char> AssignLetterAsync(int diskNumber, int partitionNumber)
    {
        var result = await RunAsync(
            $"$p = Get-Partition -DiskNumber {diskNumber} -PartitionNumber {partitionNumber}; " +
            "$p | Add-PartitionAccessPath -AssignDriveLetter; " +
            $"(Get-Partition -DiskNumber {diskNumber} -PartitionNumber {partitionNumber}).DriveLetter");

        var letter = result.Output.Trim().LastOrDefault(char.IsLetter);
        Assert.True(char.IsLetter(letter), $"no drive letter was assigned: {result.Output}{result.Error}");
        return letter;
    }

    private static async Task<ShellResult> RunAsync(string script)
    {
        var result = await PowerShellRunner.RunAsync(
            "$ErrorActionPreference='Stop'; " + script, CancellationToken.None);
        Assert.True(result.Success, $"{script} failed: {result.Error}{result.Output}");
        return result;
    }
}
