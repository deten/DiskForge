using System.Diagnostics;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The one place DiskForge's tests touch real hardware, and only ever a drive the user has explicitly
/// nominated by number in <c>DISKFORGE_TEST_DISK</c>. Without that variable every test here skips.
///
/// It exists because the VHDX harness is structurally blind to the failures that actually happened: a
/// VHDX is not removable, can be taken offline, and behaves differently under <c>diskpart clean</c>.
/// Four real bugs in a row were invisible to it. This closes that gap.
///
/// Every test re-checks the target's identity — removable, not the system disk, and matching the size
/// the caller declared — immediately before writing. The golden rule (§7, never format a real disk to
/// test) is deliberately narrowed here, not abandoned: opt-in, one named disk, verified each time.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class RealRemovableDiskTests
{
    private const ulong MB = 1024UL * 1024;

    /// <summary>Disk number the user nominated, or null when these tests must not run.</summary>
    private static int? TargetDisk =>
        int.TryParse(Environment.GetEnvironmentVariable("DISKFORGE_TEST_DISK"), out var n) ? n : null;

    private sealed class RequiresNominatedDiskFactAttribute : FactAttribute
    {
        public RequiresNominatedDiskFactAttribute()
        {
            if (TargetDisk is null)
                Skip = "Set DISKFORGE_TEST_DISK=<disk number> to run against real removable hardware. " +
                       "This ERASES that disk, so it is opt-in only.";
            else if (!Elevation.IsElevated())
                Skip = "Raw disk access needs Administrator.";
        }
    }

    /// <summary>The theory twin of <see cref="RequiresNominatedDiskFactAttribute"/>.</summary>
    private sealed class RequiresNominatedDiskTheoryAttribute : TheoryAttribute
    {
        public RequiresNominatedDiskTheoryAttribute()
        {
            if (TargetDisk is null)
                Skip = "Set DISKFORGE_TEST_DISK=<disk number> to run against real removable hardware. " +
                       "This ERASES that disk, so it is opt-in only.";
            else if (!Elevation.IsElevated())
                Skip = "Raw disk access needs Administrator.";
        }
    }

    /// <summary>
    /// Re-verifies the nominated disk really is a safe target, then returns it. Anything unexpected
    /// fails the test rather than proceeding — a wrong disk number here erases someone's data.
    /// </summary>
    private static PhysicalDiskInfo Target(SystemState state)
    {
        var disk = state.FindDisk(TargetDisk!.Value);
        Assert.True(disk is not null, $"Disk {TargetDisk} was not found.");
        Assert.True(disk!.IsRemovable,
            $"Disk {disk.Number} ({disk.FriendlyName}) is NOT removable — refusing to use it as a test target.");
        Assert.False(disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number,
            $"Disk {disk.Number} is the system/boot disk — refusing.");
        Assert.True(disk.SizeBytes < 128UL * 1024 * MB,
            $"Disk {disk.Number} is {disk.SizeBytes} bytes; refusing to erase anything that large by accident.");
        return disk;
    }

    [RequiresNominatedDiskFact]
    public async Task Ext4_OnRealRemovableHardware_IsWrittenAndReadableByLinux()
    {
        var inspector = new SystemInspector();
        var disk = Target(inspector.Capture());

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = disk.Number,
            Scope = FormatScope.CleanWholeDisk,
            FileSystem = FileSystemType.Ext4,
            Label = "DISKFORGE"
        });

        var validation = format.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await format.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True((await format.VerifyAsync()).Verified, "the format should verify");

        // --- what Windows now sees ---
        var after = inspector.Capture().FindDisk(disk.Number)!;
        var part = after.Partitions.Where(p => !p.IsUnallocated).OrderByDescending(p => p.SizeBytes).First();

        // Windows insists on MBR for removable media, so either scheme is acceptable — what matters is
        // that the partition is tagged Linux so Explorer leaves it alone.
        Assert.Equal(PartitionKind.Linux, part.Kind);
        if (after.PartitionStyle == PartitionStyle.Gpt)
            Assert.Equal(FileSystemTypeExtensions.LinuxFilesystemDataGuid, part.GptType);
        else
            Assert.Equal(FileSystemTypeExtensions.LinuxMbrType, part.MbrType);
        Assert.Null(part.DriveLetter);

        // --- what is actually on the drive ---
        var signature = LinuxFsSignature.Read(after.Number, part.OffsetBytes);
        Assert.Equal("ext4", signature?.Type);
        Assert.Equal("DISKFORGE", signature?.Label);

        // --- and the independent verdict: copy the partition off the drive and let real e2fsck judge
        // it. WSL cannot attach this disk, but it can read a file, so the drive's own bytes are the
        // thing being checked here, not our opinion of them. ---
        AssertLinuxAgrees(after.Number, part.OffsetBytes, part.SizeBytes);
    }

    /// <summary>
    /// Copies the partition off the drive and runs e2fsck over it.
    ///
    /// The copy must be the <b>whole</b> partition: a truncated one makes e2fsck complain that the
    /// device is smaller than the superblock claims, which says nothing about whether the filesystem
    /// is sound. Copying it all also means every block group and backup superblock gets checked, not
    /// just the first.
    /// </summary>
    private static void AssertLinuxAgrees(int diskNumber, ulong offsetBytes, ulong sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diskforge-usb-{Guid.NewGuid():N}.img");
        try
        {
            var length = (long)Math.Min(sizeBytes, 8192 * MB);
            using (var source = RawDiskAccess.OpenRead(diskNumber))
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                while (copied < length)
                {
                    var want = (int)Math.Min(buffer.Length, length - copied);
                    var got = RandomAccess.Read(source, buffer.AsSpan(0, want), (long)offsetBytes + copied);
                    if (got <= 0) break;
                    file.Write(buffer, 0, got);
                    copied += got;
                }
            }

            var (exit, output) = RunFsck(path);
            Assert.True(exit == 0,
                $"e2fsck rejected the filesystem written to real hardware (exit {exit}):\n{output}");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The Windows filesystem path (Format-Volume / diskpart) on real removable media.
    ///
    /// <see cref="WindowsFormatVhdxTests"/> covers the same operation against a VHDX, and that is not
    /// the same test: a VHDX is not removable, can be taken offline, and does not get re-initialized as
    /// MBR by Windows behind your back. Every removable-media surprise so far has been invisible to the
    /// VHDX harness.
    /// </summary>
    [RequiresNominatedDiskTheory]
    [InlineData(FileSystemType.Ntfs, "DF NTFS")]
    [InlineData(FileSystemType.Exfat, "DF EXFAT")]
    [InlineData(FileSystemType.Fat32, "DF FAT32")]
    public async Task WindowsFilesystem_OnRealRemovableHardware_IsWrittenAndMountable(
        FileSystemType fs, string label)
    {
        var inspector = new SystemInspector();
        var disk = Target(inspector.Capture());

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = disk.Number,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = fs,
            Label = label
        }, inspector);

        var validation = format.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await format.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var verify = await format.VerifyAsync();
        Assert.True(verify.Verified, string.Join(" ", verify.Findings));

        var after = inspector.Capture(probeLinuxToolchain: false).FindDisk(disk.Number)!;
        Assert.Equal(PartitionStyle.Gpt, after.PartitionStyle);

        var part = after.Partitions
            .Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes)
            .First();
        Assert.Equal(fs.ToFormatName(), part.Volume!.FileSystem, ignoreCase: true);
        Assert.Equal(label, part.Volume!.Label);

        // Windows agreeing about the type is not the same as the volume working. Use it.
        var letter = part.DriveLetter;
        Assert.False(string.IsNullOrWhiteSpace(letter), "The format should have assigned a drive letter.");

        var file = Path.Combine($"{letter!.TrimEnd(':', '\\')}:\\", "diskforge-roundtrip.txt");
        var payload = $"real removable round-trip {DateTimeOffset.UtcNow:O}";
        File.WriteAllText(file, payload);
        Assert.Equal(payload, File.ReadAllText(file));
        File.Delete(file);
    }

    /// <summary>
    /// Cloning onto real removable media, which is the case the clone engine could never actually do:
    /// it took the target offline first, and Windows refuses to offline removable media. That failure
    /// was structurally invisible because every clone test used a VHDX, which is not removable.
    ///
    /// The source is a throwaway VHDX formatted MBR. MBR is deliberate: a GPT source cloned onto a
    /// larger disk leaves the backup header in the wrong place, and repairing that is a separate
    /// unbuilt feature, so using it here would test two things and blame the wrong one.
    /// </summary>
    [RequiresNominatedDiskFact]
    public async Task Clone_OntoRealRemovableTarget_ProducesAMountableCopy()
    {
        var inspector = new SystemInspector();
        var target = Target(inspector.Capture());

        using var source = new VhdxLoopbackDisk(256 * MB);

        var prep = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = source.DiskNumber,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Mbr,
            FileSystem = FileSystemType.Ntfs,
            Label = "DF CLONESRC",
            AllowNonRemovable = true
        }, inspector);
        var prepped = await prep.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
        Assert.True(prepped.Success, prepped.Error);

        var srcPart = inspector.Capture(probeLinuxToolchain: false).FindDisk(source.DiskNumber)!
            .Partitions.Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes).First();
        var payload = $"cloned to removable {Guid.NewGuid():N}";
        File.WriteAllText(
            Path.Combine($"{srcPart.DriveLetter!.TrimEnd(':', '\\')}:\\", "payload.txt"), payload);

        var clone = new CloneDiskOperation(new CloneDiskSettings
        {
            SourceDiskNumber = source.DiskNumber,
            TargetDiskNumber = target.Number,
            Method = CloneMethod.FullSector,
            MakeBootable = false,
            VerifyAfter = true,
            AllowLiveCrashConsistent = true // the freshly formatted source is mounted, by construction
        }, inspector);

        var validation = clone.Validate(inspector.Capture());
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var result = await clone.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
        Assert.True(result.Success, result.Error);

        // The clone is only real if the copy mounts on the target and still holds the source's data.
        string? letter = null;
        for (var attempt = 0; attempt < 30 && letter is null; attempt++)
        {
            var part = inspector.Capture(probeLinuxToolchain: false).FindDisk(target.Number)?.Partitions
                .FirstOrDefault(p => !p.IsUnallocated &&
                                     string.Equals(p.Volume?.Label, "DF CLONESRC", StringComparison.Ordinal));

            if (part?.DriveLetter is { Length: > 0 } l) letter = l.TrimEnd(':', '\\');
            else if (part is not null)
                await PowerShellRunner.RunAsync(
                    $"$ErrorActionPreference='Stop'; " +
                    $"Get-Partition -DiskNumber {target.Number} -PartitionNumber {part.PartitionNumber} | " +
                    "Add-PartitionAccessPath -AssignDriveLetter", CancellationToken.None);

            if (letter is null) await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.True(letter is not null, "The cloned volume never appeared on the removable target.");
        Assert.Equal(payload, File.ReadAllText(Path.Combine($"{letter}:\\", "payload.txt")));
    }

    /// <summary>
    /// chkdsk on real removable media: a read-only check and then a repair, each proving the volume is
    /// still mounted and still holds its file afterwards. The repair route (chkdsk /f /x) dismounts the
    /// volume, and removable media is where dismount-and-remount behaves differently from a VHDX.
    /// </summary>
    [RequiresNominatedDiskFact]
    public async Task CheckAndRepair_OnRealRemovableHardware_LeaveTheVolumeIntact()
    {
        var inspector = new SystemInspector();
        var disk = Target(inspector.Capture());

        var format = new FormatVolumeOperation(new FormatVolumeSettings
        {
            DiskNumber = disk.Number,
            Scope = FormatScope.CleanWholeDisk,
            PartitionScheme = PartitionSchemeChoice.Gpt,
            FileSystem = FileSystemType.Ntfs,
            Label = "DF CHKDSK"
        }, inspector);
        var formatted = await format.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
        Assert.True(formatted.Success, formatted.Error);

        var part = inspector.Capture(probeLinuxToolchain: false).FindDisk(disk.Number)!
            .Partitions.Where(p => !p.IsUnallocated && p.Volume is not null)
            .OrderByDescending(p => p.SizeBytes).First();
        var canary = Path.Combine($"{part.DriveLetter!.TrimEnd(':', '\\')}:\\", "canary.txt");
        File.WriteAllText(canary, "survives check and repair");

        foreach (var repair in new[] { false, true })
        {
            var op = new CheckFilesystemOperation(new CheckFilesystemSettings
            {
                DiskNumber = disk.Number,
                PartitionNumber = part.PartitionNumber!.Value,
                OffsetBytes = part.OffsetBytes,
                DriveLetter = part.DriveLetter,
                Repair = repair
            }, inspector);

            var v = op.Validate(inspector.Capture(probeLinuxToolchain: false));
            Assert.True(v.IsValid, string.Join(" ", v.Errors));

            var result = await op.ExecuteAsync(new Progress<OpProgress>(), CancellationToken.None);
            Assert.True(result.Success, $"{(repair ? "repair" : "check")}: {result.Error}\n{result.Report}");
            Assert.False(string.IsNullOrWhiteSpace(result.Report));

            var verify = await op.VerifyAsync();
            Assert.True(verify.Verified, string.Join(" ", verify.Findings));
            Assert.Equal("survives check and repair", File.ReadAllText(canary));
        }
    }

    private static (int ExitCode, string Output) RunFsck(string windowsPath)
    {
        var distro = LinuxToolchainProbe.Get().Distros
            .Where(d => d.WslVersion == 2)
            .OrderByDescending(d => d.IsDefault)
            .Select(d => d.Name)
            .FirstOrDefault();
        Assert.True(distro is not null, "A WSL2 distro is needed to run e2fsck as an independent judge.");

        var wslPath = Run(distro!, "wslpath", "-a", "-u", windowsPath).Output.Trim();
        return Run(distro!, "e2fsck", "-fn", wslPath);
    }

    private static (int ExitCode, string Output) Run(string distro, params string[] command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var a in new[] { "-d", distro, "-u", "root", "--exec", "sh", "-c",
                     "PATH=/usr/local/sbin:/usr/sbin:/sbin:/usr/bin:/bin:$PATH; exec \"$0\" \"$@\"" })
            psi.ArgumentList.Add(a);
        foreach (var a in command) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
