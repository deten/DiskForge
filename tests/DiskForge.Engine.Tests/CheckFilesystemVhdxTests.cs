using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Drives <see cref="CheckFilesystemOperation"/> for real against a throwaway VHDX: a read-only chkdsk,
/// a repair, and a check on a volume that has no drive letter (chkdsk is handed the volume GUID path).
/// Elevated-only: chkdsk needs Administrator and so does attaching the VHDX.
///
/// What these cannot prove is the "errors found" branch, because producing a deterministically broken
/// NTFS volume is its own project. That branch is covered by the exit-code handling in the operation
/// and the guard tests; the transcript is real either way.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class CheckFilesystemVhdxTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong DiskSize = 256 * MB;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    private static async Task<PartitionInfo> FormatNtfsAsync(int diskNumber, SystemInspector inspector)
    {
        var init = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Initialize-Disk -Number {diskNumber} -PartitionStyle GPT",
            CancellationToken.None);
        Assert.True(init.Success, init.Error);

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = diskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = FileSystemType.Ntfs,
            Label = "DF CHK",
            AllowNonRemovable = true
        }, inspector);
        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        return inspector.Capture(probeLinuxToolchain: false).FindDisk(diskNumber)!
            .Partitions.Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes).First();
    }

    private static CheckFilesystemOperation Op(int disk, PartitionInfo part, bool repair, SystemInspector inspector)
        => new(new CheckFilesystemSettings
        {
            DiskNumber = disk,
            PartitionNumber = part.PartitionNumber!.Value,
            OffsetBytes = part.OffsetBytes,
            DriveLetter = part.DriveLetter,
            Repair = repair,
            AllowNonRemovable = true // a VHDX presents as fixed
        }, inspector);

    [RequiresElevationFact]
    public async Task Check_OnACleanNtfsVolume_SucceedsAndReturnsTheTranscript()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        var inspector = new SystemInspector();
        var part = await FormatNtfsAsync(vhdx.DiskNumber, inspector);

        var canary = Path.Combine($"{part.DriveLetter!.TrimEnd(':', '\\')}:\\", "canary.txt");
        File.WriteAllText(canary, "still here after the check");

        var op = Op(vhdx.DiskNumber, part, repair: false, inspector);
        var v = op.Validate(inspector.Capture(probeLinuxToolchain: false));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));

        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        // The whole point of the operation is the text chkdsk prints.
        Assert.False(string.IsNullOrWhiteSpace(result.Report), "chkdsk's output should come back as the report");
        Assert.Contains("NTFS", result.Report!, StringComparison.OrdinalIgnoreCase);

        var verify = await op.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));

        Assert.Equal("still here after the check", File.ReadAllText(canary));
    }

    [RequiresElevationFact]
    public async Task Repair_OnACleanNtfsVolume_SucceedsAndTheVolumeComesBack()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        var inspector = new SystemInspector();
        var part = await FormatNtfsAsync(vhdx.DiskNumber, inspector);

        var canary = Path.Combine($"{part.DriveLetter!.TrimEnd(':', '\\')}:\\", "canary.txt");
        File.WriteAllText(canary, "still here after the repair");

        var op = Op(vhdx.DiskNumber, part, repair: true, inspector);
        var v = op.Validate(inspector.Capture(probeLinuxToolchain: false));
        Assert.True(v.IsValid, string.Join(" ", v.Errors));
        Assert.Contains(v.Warnings, w => w.Contains("dismounted"));

        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error + "\n" + result.Report);
        Assert.False(string.IsNullOrWhiteSpace(result.Report));

        // /x dismounted it; it must be mounted again afterwards, with its contents intact.
        var verify = await op.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));
        Assert.Equal("still here after the repair", File.ReadAllText(canary));
    }

    [RequiresElevationFact]
    public async Task Check_OnAVolumeWithNoDriveLetter_UsesTheVolumePath()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        var inspector = new SystemInspector();
        var part = await FormatNtfsAsync(vhdx.DiskNumber, inspector);

        // Take the letter away. A volume with no letter is still mounted and still checkable.
        var drop = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; " +
            $"Get-Partition -DiskNumber {vhdx.DiskNumber} -PartitionNumber {part.PartitionNumber} | " +
            $"Remove-PartitionAccessPath -AccessPath '{part.DriveLetter!.TrimEnd(':', '\\')}:\\'",
            CancellationToken.None);
        Assert.True(drop.Success, drop.Error);

        var unlettered = inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!
            .Partitions.Single(p => p.PartitionNumber == part.PartitionNumber);
        Assert.Null(unlettered.DriveLetter);
        Assert.NotNull(unlettered.Volume?.UniqueId);

        var op = Op(vhdx.DiskNumber, unlettered, repair: false, inspector);
        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error + "\n" + result.Report);
        Assert.True((await op.VerifyAsync()).Verified);
    }
}
