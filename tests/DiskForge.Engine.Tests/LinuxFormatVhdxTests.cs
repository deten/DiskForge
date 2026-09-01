using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// End-to-end Linux-filesystem writes against a throwaway VHDX loopback disk — never a real drive (§7).
/// These are the only tests that exercise the whole chain for real: Windows-side preparation → WSL disk
/// attach → device identification → mkfs → blkid read-back → detach.
///
/// Elevated-only (VHDX attach and <c>wsl --mount</c> both need Administrator) and toolchain-gated, so
/// they skip with a reason rather than failing on a machine without the tools.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class LinuxFormatVhdxTests
{
    private const ulong MB = 1024UL * 1024;

    private static readonly IProgress<OpProgress> NoProgress = new Progress<OpProgress>();

    /// <summary>Big enough for every filesystem under test — mkfs.xfs alone refuses below 300 MB.</summary>
    private const ulong DiskSize = 1024 * MB;

    private static async Task InitializeGptAsync(int diskNumber)
    {
        var result = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Initialize-Disk -Number {diskNumber} -PartitionStyle GPT",
            CancellationToken.None);
        Assert.True(result.Success, $"Initialize-Disk failed: {result.Error}{result.Output}");
    }

    private static PartitionInfo LargestGap(PhysicalDiskInfo disk) =>
        disk.Partitions.Where(p => p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

    /// <summary>A drive letter nothing else is using, so the test can mount a real Windows volume.</summary>
    private static string FreeDriveLetter(SystemState state)
    {
        var used = state.Disks.SelectMany(d => d.Partitions)
            .Where(p => p.DriveLetter is not null)
            .Select(p => p.DriveLetter!.ToUpperInvariant())
            .ToHashSet();

        for (var c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c.ToString())) return c.ToString();

        throw new InvalidOperationException("No free drive letter available for the test.");
    }

    /// <summary>Reads the filesystem back through the backend, independently of the operation.</summary>
    private static async Task<string?> ReadTypeAsync(PhysicalDiskInfo disk, PartitionInfo part, FileSystemType fs)
    {
        var outcome = await new WslLinuxFormatBackend().ProbeSignatureAsync(new LinuxFormatRequest
        {
            DiskNumber = disk.Number,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = part.OffsetBytes,
            PartitionSizeBytes = part.SizeBytes,
            FileSystem = fs
        }, CancellationToken.None);
        return outcome.DetectedType;
    }

    [RequiresLinuxToolchainFact]
    public async Task CreatePartition_AsExt4_WritesARealFilesystem()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var gap = LargestGap(inspector.Capture().FindDisk(vhdx.DiskNumber)!);

        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 512 * MB,
            FileSystem = FileSystemType.Ext4,
            Label = "DFEXT4",
            DriveLetter = null, // Windows cannot mount ext4; the op refuses a letter outright.
            AllowNonRemovable = true // a VHDX presents as a fixed disk
        });

        var validation = create.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await create.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await create.VerifyAsync()).Verified, "blkid should report ext4 on the new partition.");

        // The partition must exist, be tagged Linux, and carry no drive letter.
        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var created = after.Partitions.Single(
            p => !p.IsUnallocated && p.OffsetBytes >= gap.OffsetBytes && p.SizeBytes == 512 * MB);
        Assert.Equal(FileSystemTypeExtensions.LinuxFilesystemDataGuid, created.GptType);
        Assert.Equal(PartitionKind.Linux, created.Kind);
        Assert.Null(created.DriveLetter);

        // Windows itself cannot mount this, but DiskForge synthesizes a volume from the superblock so
        // the partition does not display as a nameless RAW block (StorageEnumerator.AttachLinuxFilesystems).
        // The flag is the load-bearing part: without MountedByWindows = false the UI would offer a
        // Windows-side rename and drive letter that cannot possibly work.
        Assert.NotNull(created.Volume);
        Assert.Equal("ext4", created.Volume!.FileSystem);
        Assert.Equal("DFEXT4", created.Volume!.Label);
        Assert.False(created.Volume!.MountedByWindows);
        Assert.False(created.Volume!.UsageKnown);

        Assert.Equal("ext4", await ReadTypeAsync(after, created, FileSystemType.Ext4));
    }

    [RequiresLinuxToolchainFact]
    public async Task FormatVolume_ConvertsAnNtfsPartitionToExt4()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var initial = inspector.Capture();
        var gap = LargestGap(initial.FindDisk(vhdx.DiskNumber)!);
        var letter = FreeDriveLetter(initial);

        // Start from a normal Windows partition, drive letter and all — the realistic starting point,
        // and the case where Windows is actively holding the volume when WSL asks for the disk.
        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 512 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "DFTEST",
            DriveLetter = letter,
            AllowNonRemovable = true
        });
        Assert.True((await create.ExecuteAsync(NoProgress, CancellationToken.None)).Success);

        var ntfs = inspector.Capture().FindDisk(vhdx.DiskNumber)!
            .Partitions.Single(p => p.Volume?.Label == "DFTEST");
        Assert.Equal(letter, ntfs.DriveLetter);

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.ReformatPartition,
            PartitionNumber = ntfs.PartitionNumber,
            TargetDriveLetter = ntfs.DriveLetter,
            FileSystem = FileSystemType.Ext4,
            Label = "DFEXT4",
            AllowNonRemovable = true
        });

        var validation = format.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report ext4 after the reformat.");

        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var converted = after.Partitions.Single(p => p.OffsetBytes == ntfs.OffsetBytes);
        Assert.Equal(FileSystemTypeExtensions.LinuxFilesystemDataGuid, converted.GptType);
        Assert.Null(converted.DriveLetter); // the letter is dropped, as the plan said it would be
        Assert.Equal("ext4", await ReadTypeAsync(after, converted, FileSystemType.Ext4));

        // The letter must be free again system-wide, not merely absent from this partition record.
        Assert.DoesNotContain(inspector.Capture().Disks.SelectMany(d => d.Partitions),
            p => string.Equals(p.DriveLetter, letter, StringComparison.OrdinalIgnoreCase));
    }

    [RequiresLinuxToolchainFact]
    public async Task FormatVolume_CleanWholeDisk_AsExt4()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Ext4,
            Label = "WHOLEDISK",
            AllowNonRemovable = true
        });

        var validation = format.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report ext4 on the new whole-disk partition.");

        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var part = after.Partitions.Where(p => !p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();
        Assert.Equal(PartitionKind.Linux, part.Kind);
        Assert.Equal("ext4", await ReadTypeAsync(after, part, FileSystemType.Ext4));
    }

    /// <summary>
    /// The regression test for the reported failure: formatting a whole disk that still has a
    /// <b>mounted</b> volume on it. Windows refuses to let diskpart zero sectors underneath a mounted
    /// volume, which surfaced as <c>DiskPart has encountered an error: Access is denied</c> — the format
    /// only works if the volumes are dismounted first.
    /// </summary>
    [RequiresLinuxToolchainFact]
    public async Task FormatVolume_CleanWholeDisk_AsExt4_WithAMountedVolumeInTheWay()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var initial = inspector.Capture();
        var gap = LargestGap(initial.FindDisk(vhdx.DiskNumber)!);
        var letter = FreeDriveLetter(initial);

        // Put a real, mounted, lettered NTFS volume in the way — the state the user's USB was in.
        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 512 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "INTHEWAY",
            DriveLetter = letter,
            AllowNonRemovable = true
        });
        Assert.True((await create.ExecuteAsync(NoProgress, CancellationToken.None)).Success);
        Assert.True(Directory.Exists($"{letter}:\\"), "the volume should really be mounted before we start");

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Ext4,
            Label = "SURVIVED",
            AllowNonRemovable = true
        });

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report ext4.");

        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var part = after.Partitions.Where(p => !p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();
        Assert.Equal(PartitionKind.Linux, part.Kind);
        Assert.Equal("ext4", await ReadTypeAsync(after, part, FileSystemType.Ext4));
    }

    [RequiresLinuxToolchainFact(FileSystemType.Btrfs)]
    public async Task FormatVolume_CleanWholeDisk_AsBtrfs()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Btrfs,
            Label = "BTRFSVOL",
            AllowNonRemovable = true
        });

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report btrfs.");
    }

    [RequiresLinuxToolchainFact(FileSystemType.Xfs)]
    public async Task FormatVolume_CleanWholeDisk_AsXfs()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Xfs,
            Label = "XFSVOL",
            AllowNonRemovable = true
        });

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report xfs.");
    }

    /// <summary>
    /// Regression test for "Virtual Disk Service error: The operation is not allowed on a disk that is
    /// offline". diskpart refuses an offline disk outright, and DiskForge can itself leave a disk
    /// offline after a failed WSL attach — so the format has to bring it back online rather than
    /// stranding the user in a state the app created.
    /// </summary>
    [RequiresLinuxToolchainFact(verifiesWithBlkid: false)]
    public async Task FormatVolume_BringsAnOfflineDiskOnline_InsteadOfFailing()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var offline = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {vhdx.DiskNumber} -IsOffline $true; 'OK'",
            CancellationToken.None);
        Assert.True(offline.Success, $"could not stage an offline disk: {offline.Error}{offline.Output}");

        var inspector = new SystemInspector();
        Assert.True(inspector.Capture().FindDisk(vhdx.DiskNumber)!.IsOffline, "the disk should be offline");

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = vhdx.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Ext4,
            Label = "WASOFFLINE",
            AllowNonRemovable = true
        });

        // Offline must be a warning, not a block — the user should not be sent to Disk Management.
        var validation = format.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.Contains(validation.Warnings, w => w.Contains("offline"));

        var result = await format.ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "blkid should report ext4.");

        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        Assert.False(after.IsOffline, "the disk should have been left online");
    }

    /// <summary>
    /// The staged route — the one that makes ext4 work on a USB stick. WSL cannot attach removable
    /// media, so mkfs runs against a scratch VHDX and the finished filesystem image is copied onto the
    /// partition. Verified twice over: once with DiskForge's own superblock reader (the only verifier
    /// available for a real removable drive) and once with real <c>blkid</c>, which cross-checks that
    /// the reader agrees with Linux itself.
    /// </summary>
    [RequiresLinuxToolchainFact]
    public async Task StagedFormatter_WritesARealExt4FilesystemOntoAPartition()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var gap = LargestGap(inspector.Capture().FindDisk(vhdx.DiskNumber)!);

        // Plain unformatted partition — the state the Windows-side prep leaves behind.
        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 512 * MB,
            FormatNew = false,
            AllowNonRemovable = true
        });
        Assert.True((await create.ExecuteAsync(NoProgress, CancellationToken.None)).Success);

        var disk = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var target = disk.Partitions.Single(
            p => !p.IsUnallocated && p.OffsetBytes >= gap.OffsetBytes && p.SizeBytes == 512 * MB);

        var distro = LinuxToolchainProbe.DistroFor(FileSystemType.Ext4);
        Assert.NotNull(distro);

        var request = new LinuxFormatRequest
        {
            DiskNumber = disk.Number,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = target.OffsetBytes,
            PartitionSizeBytes = target.SizeBytes,
            FileSystem = FileSystemType.Ext4,
            Label = "STAGED",
            VolumePaths = DiskVolumeReleaser.VolumePathsOn(disk),
            DiskIsRemovable = true // force the staged route; a VHDX is not really removable
        };

        var outcome = await new VhdxStagedFormatter()
            .FormatAsync(request, distro!, LinuxToolchainProbe.Get().ToolFor(FileSystemType.Ext4).Path,
                NoProgress, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("ext4", outcome.DetectedType);
        Assert.Equal("STAGED", outcome.DetectedLabel);

        // Our own reader, straight off the drive.
        var signature = LinuxFsSignature.Read(disk.Number, target.OffsetBytes);
        Assert.Equal("ext4", signature?.Type);
        Assert.Equal("STAGED", signature?.Label);

        // …and Linux's own verdict, which must agree. This is the check that proves the superblock
        // parser is not quietly wrong on a drive we cannot ask blkid about.
        Assert.Equal("ext4", await ReadTypeAsync(disk, target, FileSystemType.Ext4));

        // The scratch image must not be left behind.
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "diskforge-mkfs-*.vhdx"));
    }

    /// <summary>
    /// The safety property that matters most: a plan whose partition offset no longer matches reality
    /// must not write anywhere. Here the staged offset is deliberately wrong, so the backend's
    /// identification step has to refuse before mkfs runs — and the real filesystem must survive.
    /// </summary>
    [RequiresLinuxToolchainFact]
    public async Task LinuxFormat_RefusesWhenTheStagedOffsetDoesNotMatchTheDisk()
    {
        using var vhdx = new VhdxLoopbackDisk(DiskSize);
        await InitializeGptAsync(vhdx.DiskNumber);

        var inspector = new SystemInspector();
        var gap = LargestGap(inspector.Capture().FindDisk(vhdx.DiskNumber)!);

        var create = new CreatePartitionOperation(new CreatePartitionSettings
        {
            DiskNumber = vhdx.DiskNumber,
            OffsetBytes = gap.OffsetBytes,
            SizeBytes = 512 * MB,
            FileSystem = FileSystemType.Ntfs,
            Label = "KEEPME",
            AllowNonRemovable = true
        });
        Assert.True((await create.ExecuteAsync(NoProgress, CancellationToken.None)).Success);

        var disk = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var real = disk.Partitions.Single(p => p.Volume?.Label == "KEEPME");

        // Ask the backend to format an extent that does not exist on this disk.
        var outcome = await new WslLinuxFormatBackend().FormatAsync(new LinuxFormatRequest
        {
            DiskNumber = disk.Number,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = real.OffsetBytes + 7 * MB,
            PartitionSizeBytes = real.SizeBytes,
            FileSystem = FileSystemType.Ext4,
            Label = "SHOULDNOTHAPPEN"
        }, NoProgress, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("Refusing to write to an unverified device", outcome.Error);

        // …and the partition that really is there must be untouched.
        var after = inspector.Capture().FindDisk(vhdx.DiskNumber)!;
        var survivor = after.Partitions.Single(p => p.OffsetBytes == real.OffsetBytes);
        Assert.Equal("NTFS", survivor.Volume?.FileSystem);
        Assert.Equal("KEEPME", survivor.Volume?.Label);
    }
}
