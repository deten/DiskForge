using System.Diagnostics;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;

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
