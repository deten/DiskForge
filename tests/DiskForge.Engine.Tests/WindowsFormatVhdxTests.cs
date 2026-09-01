using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// End-to-end coverage of the <b>Windows</b> filesystem write path (Format-Volume and diskpart for
/// NTFS/exFAT/FAT32) against a throwaway VHDX loopback disk, never a real drive (section 7).
///
/// This was the wrong gap to have: the Linux paths were driven end to end by
/// <see cref="LinuxFormatVhdxTests"/> while the branch most users actually reach had nothing driving
/// its <c>ExecuteAsync</c> at all. The guard-ladder tests in <see cref="FormatVolumeOperationTests"/>
/// prove what the operation <i>refuses</i>; these prove what it <i>does</i>.
///
/// Every case ends by writing a real file through the resulting drive letter and reading it back.
/// Asserting that Windows reports "NTFS" only proves the volume object says so; a round-tripped file
/// proves a mountable filesystem was actually written.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class WindowsFormatVhdxTests
{
    private const ulong MB = 1024UL * 1024;

    /// <summary>FAT32 wants elbow room and NTFS/exFAT are happy here, so 1 GB suits all three.</summary>
    private const ulong DiskSize = 1024 * MB;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    private static async Task InitializeGptAsync(int diskNumber)
    {
        var result = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Initialize-Disk -Number {diskNumber} -PartitionStyle GPT",
            CancellationToken.None);
        Assert.True(result.Success, $"Initialize-Disk failed: {result.Error}{result.Output}");
    }

    /// <summary>The data partition the format produced, i.e. not MSR/EFI/reserved and not a gap.</summary>
    private static PartitionInfo DataPartition(PhysicalDiskInfo disk) =>
        disk.Partitions
            .Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes)
            .First();

    /// <summary>
    /// Proves the volume is real by using it. A filesystem that reports its type but cannot hold a file
    /// is exactly the failure a type-only assertion misses.
    /// </summary>
    private static void AssertFileRoundTrips(string driveLetter)
    {
        var path = Path.Combine($"{driveLetter}:\\", $"diskforge-{Guid.NewGuid():N}.txt");
        var payload = $"DiskForge round-trip {DateTimeOffset.UtcNow:O}";

        File.WriteAllText(path, payload);
        Assert.Equal(payload, File.ReadAllText(path));
        File.Delete(path);
    }

    private static string LetterOf(PartitionInfo part)
    {
        Assert.False(string.IsNullOrWhiteSpace(part.DriveLetter),
            "The format should have assigned a drive letter to the new volume.");
        return part.DriveLetter!.TrimEnd(':', '\\');
    }

    [RequiresElevationTheory]
    [InlineData(FileSystemType.Ntfs, "DF NTFS")]
    [InlineData(FileSystemType.Exfat, "DF EXFAT")]
    [InlineData(FileSystemType.Fat32, "DF FAT32")]
    public async Task CleanWholeDisk_WritesAMountableWindowsFilesystem(FileSystemType fs, string label)
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var op = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = fs,
            Label = label,
            AllowNonRemovable = true // a VHDX presents as a fixed disk
        }, inspector);

        var validation = op.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var verify = await op.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));

        // Independently of the operation own verify: ask Windows what is on the disk now.
        var disk = inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!;
        Assert.Equal(PartitionStyle.Gpt, disk.PartitionStyle);

        var part = DataPartition(disk);
        Assert.Equal(fs.ToFormatName(), part.Volume!.FileSystem, ignoreCase: true);
        Assert.Equal(label, part.Volume!.Label);

        AssertFileRoundTrips(LetterOf(part));
    }

    /// <summary>
    /// The scheme choice is only offered when the disk is being erased anyway, so this is the only
    /// place it can be proven. MBR is the interesting direction: Windows re-initializes a freshly
    /// cleaned disk on its own, and <c>BuildSchemeScript</c> has to convert what it finds rather than
    /// race it.
    /// </summary>
    [RequiresElevationFact]
    public async Task CleanWholeDisk_AsMbr_HonoursTheRequestedScheme()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber); // start as GPT so MBR is a real conversion

        var inspector = new SystemInspector();
        var op = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Mbr,
            FileSystem = FileSystemType.Ntfs,
            Label = "DF MBR",
            AllowNonRemovable = true
        }, inspector);

        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        // VerifyAsync folds in the scheme cross-check, so a filesystem written onto a disk that refused
        // the requested table fails here rather than passing quietly.
        var verify = await op.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));

        var disk = inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!;
        Assert.Equal(PartitionStyle.Mbr, disk.PartitionStyle);
        AssertFileRoundTrips(LetterOf(DataPartition(disk)));
    }

    /// <summary>
    /// Reformat-in-place is the other Windows branch and takes a completely different route
    /// (Format-Volume against an existing partition, no diskpart, no partition table change). The
    /// canary file proves the reformat actually happened rather than the old volume surviving.
    /// </summary>
    [RequiresElevationFact]
    public async Task ReformatPartition_NtfsToExfat_ReplacesTheFilesystemInPlace()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();

        // Arrange: a real NTFS volume with a file on it.
        var setup = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = FileSystemType.Ntfs,
            Label = "DF BEFORE",
            AllowNonRemovable = true
        }, inspector);
        var setupResult = await setup.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(setupResult.Success, setupResult.Error);

        var before = DataPartition(inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!);
        var letter = LetterOf(before);
        var canary = Path.Combine($"{letter}:\\", "canary.txt");
        File.WriteAllText(canary, "this must not survive the reformat");
        Assert.True(File.Exists(canary));

        var offsetBefore = before.OffsetBytes;
        var sizeBefore = before.SizeBytes;

        // Act: reformat that same partition as exFAT.
        var op = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.ReformatPartition,
            PartitionNumber = before.PartitionNumber,
            TargetDriveLetter = letter,
            FileSystem = FileSystemType.Exfat,
            Label = "DF AFTER",
            AllowNonRemovable = true
        }, inspector);

        var validation = op.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await op.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await op.VerifyAsync()).Verified);

        var after = DataPartition(inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!);
        Assert.Equal("exFAT", after.Volume!.FileSystem, ignoreCase: true);
        Assert.Equal("DF AFTER", after.Volume!.Label);

        // A reformat must not move or resize the partition it was handed.
        Assert.Equal(offsetBefore, after.OffsetBytes);
        Assert.Equal(sizeBefore, after.SizeBytes);

        Assert.False(File.Exists(canary), "The old NTFS contents should be gone after the reformat.");
        AssertFileRoundTrips(LetterOf(after));
    }
}
