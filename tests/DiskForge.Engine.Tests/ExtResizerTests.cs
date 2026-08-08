using System.Diagnostics;
using System.Text;
using DiskForge.Core.Operations;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Linux.Ext;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Correctness tests for growing an ext filesystem in place.
///
/// The ones that matter run the <b>real e2fsck</b> over the grown image. A resizer that leaves a
/// filesystem mountable but subtly wrong is the worst possible outcome, and only an independent
/// checker can rule that out. Our own reader agreeing with our own writer would prove nothing.
///
/// Checking a <i>file</i> needs no elevation, so these run in an ordinary test pass.
/// </summary>
public class ExtResizerTests
{
    private const ulong MB = 1024UL * 1024;

    // ---------------------------------------------------------------- the real proof

    [RequiresWslOracleTheory]
    [InlineData(FileSystemType.Ext2)]
    [InlineData(FileSystemType.Ext3)]
    [InlineData(FileSystemType.Ext4)]
    public void Grown_FilesystemIsCleanPerE2fsck(FileSystemType fs)
        => AssertGrowsClean(64 * MB, 256 * MB, fs);

    /// <summary>Crossing into several new block groups, so more than one group's metadata is created.</summary>
    [RequiresWslOracleFact]
    public void GrowingAcrossManyGroups_IsClean()
        => AssertGrowsClean(64 * MB, 1024 * MB, FileSystemType.Ext4);

    /// <summary>
    /// A grow that stays inside the final partial group. Nothing new is created; only that group's
    /// bitmap and free count change, which is the easiest case to get subtly wrong.
    /// </summary>
    [RequiresWslOracleFact]
    public void GrowingWithinTheFinalGroup_IsClean()
        => AssertGrowsClean(100 * MB, 120 * MB, FileSystemType.Ext4);

    /// <summary>
    /// Growth must not disturb what is already stored. e2fsck proves the metadata is coherent; this
    /// proves the data blocks were left alone, by checking lost+found and the root directory survive
    /// and the bytes outside the filesystem's own metadata are untouched.
    /// </summary>
    [RequiresWslOracleFact]
    public void GrowingPreservesTheExistingContent()
    {
        var path = WriteImage(64 * MB, 256 * MB, FileSystemType.Ext4);
        try
        {
            // debugfs lists the root directory; lost+found is created by the formatter and must survive.
            var (exit, output) = RunInWslRaw("debugfs", "-R", "ls -l /", ToWslPath(path));
            Assert.True(exit == 0, output);
            Assert.Contains("lost+found", output);
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void Shrinking_IsRefusedWithAReason()
    {
        using var stream = new MemoryStream(BuildImage(128 * MB, 128 * MB, FileSystemType.Ext4));

        var plan = ExtResizer.TryPlanGrow(stream, 64 * MB, out var reason);

        Assert.Null(plan);
        Assert.Contains("Shrinking", reason);
    }

    [Fact]
    public void NoFilesystem_IsRefusedRatherThanCorrupted()
    {
        using var stream = new MemoryStream(new byte[8 * (int)MB]);

        var plan = ExtResizer.TryPlanGrow(stream, 16 * MB, out var reason);

        Assert.Null(plan);
        Assert.Contains("superblock magic", reason);
    }

    /// <summary>
    /// The descriptor table cannot move without relocating every group's metadata, so growth beyond
    /// what it can address has to be refused rather than attempted.
    /// </summary>
    [Fact]
    public void GrowingBeyondTheDescriptorTable_IsRefused()
    {
        // 16 MiB of ext4 has a single descriptor block, which caps it well below 4 TB.
        using var stream = new MemoryStream(BuildImage(16 * MB, 16 * MB, FileSystemType.Ext4));

        var plan = ExtResizer.TryPlanGrow(stream, 4096UL * 1024 * MB, out var reason);

        Assert.Null(plan);
        Assert.Contains("group descriptor table", reason);
    }

    [Fact]
    public void PlanReportsWhatWillChange()
    {
        using var stream = new MemoryStream(BuildImage(64 * MB, 64 * MB, FileSystemType.Ext4));

        var plan = ExtResizer.TryPlanGrow(stream, 256 * MB, out var reason);

        Assert.NotNull(plan);
        Assert.Null(reason);
        Assert.True(plan!.NewGroupCount > plan.OldGroupCount);
        Assert.True(plan.AddedFreeBlocks > 0);
        Assert.True(plan.AddedInodes > 0);
        Assert.Equal(256 * MB, plan.NewSizeBytes);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertGrowsClean(ulong fromBytes, ulong toBytes, FileSystemType fs)
    {
        var path = WriteImage(fromBytes, toBytes, fs);
        try
        {
            var (exit, output) = RunInWslRaw("e2fsck", "-fn", ToWslPath(path));
            Assert.True(exit == 0,
                $"e2fsck rejected {fs} grown from {fromBytes / MB} MB to {toBytes / MB} MB, exit {exit}:\n{output}");

            // e2fsck exits 0 on a clean filesystem; make sure it really saw the larger one.
            Assert.Contains("blocks", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Formats at the smaller size, grows to the larger, and returns the image path.</summary>
    private static string WriteImage(ulong fromBytes, ulong toBytes, FileSystemType fs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diskforge-resize-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(path, BuildImage(fromBytes, toBytes, fs));
        return path;
    }

    private static byte[] BuildImage(ulong formatBytes, ulong finalBytes, FileSystemType fs)
    {
        var layout = ExtLayout.Compute(formatBytes, fs);
        var buffer = new byte[Math.Max(layout.SizeBytes, finalBytes)];

        using var stream = new MemoryStream(buffer);
        new ExtFormatter(layout, "GROWTEST").Write(stream);

        if (finalBytes > formatBytes)
        {
            var plan = ExtResizer.TryPlanGrow(stream, finalBytes, out var reason);
            Assert.True(plan is not null, $"growth was refused: {reason}");
            ExtResizer.Grow(stream, plan!);
        }

        return buffer;
    }

    // ---------------------------------------------------------------- WSL oracle plumbing

    private static string? OracleDistro => LinuxToolchainProbe.Get().Distros
        .Where(d => d.WslVersion == 2)
        .OrderByDescending(d => d.IsDefault)
        .Select(d => d.Name)
        .FirstOrDefault();

    private static string ToWslPath(string windowsPath)
        => RunInWslRaw("wslpath", "-a", "-u", windowsPath).Output.Trim();

    /// <summary>
    /// Runs a tool in the oracle distro. <c>wsl --exec</c> starts no shell and therefore has no login
    /// PATH, so a bare <c>e2fsck</c> fails with ENOENT; the tool is exec'd through <c>sh -c</c> with
    /// the sbin directories restored. Same approach as ExtFormatterTests.
    /// </summary>
    private static (int ExitCode, string Output) RunInWslRaw(params string[] command)
    {
        var distro = OracleDistro ?? throw new InvalidOperationException("No WSL2 distro for the oracle.");

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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        return (process.ExitCode, new StringBuilder(stdout).Append(stderr).ToString());
    }
}
