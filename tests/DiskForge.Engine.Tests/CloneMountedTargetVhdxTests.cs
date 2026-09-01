using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Covers cloning onto a target that Windows has volumes mounted on, which every previous clone test
/// avoided by using a blank VHDX.
///
/// Windows denies raw sector writes landing inside a mounted volume extent (win32 5,
/// ERROR_ACCESS_DENIED) no matter how elevated the caller is. <see cref="CloneDiskOperation"/> takes
/// the target offline to get around that, but Windows refuses to offline removable media, which is
/// DiskForge's default-allowed target, so removable disks hold each volume's lock for the duration of
/// the copy instead.
///
/// A VHDX always presents as fixed, so these tests cover the offline route end to end and then test
/// the lock-holding mechanism directly. The removable route as a whole is only provable on real
/// hardware (see <see cref="RealRemovableDiskTests"/>).
/// </summary>
[Collection(RealDiskCollection.Name)]
public class CloneMountedTargetVhdxTests
{
    private const ulong MB = 1024UL * 1024;
    private const ulong DiskSize = 128 * MB;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    private static async Task InitializeGptAsync(int diskNumber)
    {
        var result = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Initialize-Disk -Number {diskNumber} -PartitionStyle GPT",
            CancellationToken.None);
        Assert.True(result.Success, $"Initialize-Disk failed: {result.Error}{result.Output}");
    }

    /// <summary>Gives a disk one NTFS volume with a drive letter, i.e. exactly the state that blocks
    /// raw writes.</summary>
    private static async Task<PartitionInfo> GiveItAMountedVolumeAsync(
        int diskNumber, string label, SystemInspector inspector)
    {
        await InitializeGptAsync(diskNumber);

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = diskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = FileSystemType.Ntfs,
            Label = label,
            AllowNonRemovable = true
        }, inspector);

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var disk = inspector.Capture(probeLinuxToolchain: false).FindDisk(diskNumber)!;
        var part = disk.Partitions
            .Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes)
            .First();

        Assert.False(string.IsNullOrWhiteSpace(part.DriveLetter),
            "The setup format should have produced a lettered, mounted volume.");
        return part;
    }

    [RequiresElevationFact]
    public async Task Clone_OntoATargetWithAMountedVolume_Succeeds()
    {
        using var source = new VhdxLoopbackDisk(DiskSize);
        using var target = new VhdxLoopbackDisk(DiskSize);

        var inspector = new SystemInspector();

        var srcPart = await GiveItAMountedVolumeAsync(source.DiskNumber, "DF SRC", inspector);
        var payload = $"clone payload {Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine($"{srcPart.DriveLetter!.TrimEnd(':', '\\')}:\\", "payload.txt"), payload);

        // The target is not blank: it carries its own mounted, lettered NTFS volume, which is what
        // Windows would otherwise refuse to let us write underneath.
        await GiveItAMountedVolumeAsync(target.DiskNumber, "DF DST", inspector);

        var clone = new CloneDiskOperation(new CloneDiskSettings
        {
            SourceDiskNumber = source.DiskNumber,
            TargetDiskNumber = target.DiskNumber,
            Method = CloneMethod.FullSector,
            MakeBootable = false,
            VerifyAfter = true,               // the byte-exactness check happens inside Execute
            AllowNonRemovableTarget = true,   // a VHDX presents as fixed
            AllowLiveCrashConsistent = true   // the source has a mounted volume, by construction
        }, inspector);

        var validation = clone.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await clone.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        // What this test is for is the write: the target had a mounted, lettered volume on it and the
        // copy still landed. Execute proved the bytes by re-reading them, and VerifyAsync confirms the
        // target now presents a partition table Windows can read.
        var verify = await clone.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));

        // It deliberately stops short of mounting the clone and reading the file back. Two attached GPT
        // disks sharing partition GUIDs make Windows keep the second one offline, and the clone only
        // regenerates the *disk* GUID, so whether the volume appears depends on an unbuilt piece of
        // identity handling rather than on anything this test is about (RUNNING_NOTES section 5).
        // RealRemovableDiskTests.Clone_OntoRealRemovableTarget_ProducesAMountableCopy makes that claim
        // instead, on MBR, where there is no collision to fight.
    }

    /// <summary>
    /// The mechanism the removable route depends on, tested on its own because a VHDX can never be
    /// removable. Without holding the volume, a raw write inside its extent is refused; with the volume
    /// held, the same write lands. If this ever stops being true, cloning onto a USB stick breaks and
    /// nothing else would catch it.
    /// </summary>
    [RequiresElevationFact]
    public async Task Hold_LetsRawSectorWritesLandUnderAMountedVolume()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        var inspector = new SystemInspector();

        var part = await GiveItAMountedVolumeAsync(vhdx.DiskNumber, "DF HOLD", inspector);

        // Well inside the volume, past any slack at its start.
        var offset = (long)part.OffsetBytes + 8 * (long)MB;
        var buffer = new byte[4096];
        Array.Fill(buffer, (byte)0xA5);

        var disk = inspector.Capture(probeLinuxToolchain: false).FindDisk(vhdx.DiskNumber)!;

        // ---- mounted: Windows must refuse ----
        var refused = Record.Exception(() =>
        {
            using var h = RawDiskAccess.OpenWrite(vhdx.DiskNumber);
            RandomAccess.Write(h, buffer, offset);
            RandomAccess.FlushToDisk(h);
        });
        Assert.NotNull(refused);

        // ---- held: the same write must succeed ----
        using (var held = DiskVolumeReleaser.Hold(disk))
        {
            Assert.True(held.AllHeld, string.Join(" ", held.Log));

            using var h = RawDiskAccess.OpenWrite(vhdx.DiskNumber);
            RandomAccess.Write(h, buffer, offset);
            RandomAccess.FlushToDisk(h);

            // Read it straight back while still holding, to prove it actually landed.
            var readBack = new byte[buffer.Length];
            RandomAccess.Read(h, readBack, offset);
            Assert.Equal(buffer, readBack);
        }

        DiskVolumeReleaser.Refresh(vhdx.DiskNumber);
    }
}
