using System.Diagnostics;
using System.Text;
using DiskForge.Core.Operations;
using DiskForge.Engine.Linux;
using DiskForge.Engine.Linux.Ext;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// Correctness tests for the native ext writer.
///
/// The important ones run the <b>real e2fsck</b> over the images we produce. That is the only honest
/// way to claim a hand-written filesystem is valid: our own reader agreeing with our own writer proves
/// nothing. e2fsck is used here purely as a test oracle against a <i>file</i> — no disk, no elevation,
/// and no dependency of the shipping product, which formats drives with no external tool at all.
/// </summary>
public class ExtFormatterTests
{
    private const ulong MB = 1024UL * 1024;

    // ---------------------------------------------------------------- geometry

    [Theory]
    [InlineData(FileSystemType.Ext4)]
    [InlineData(FileSystemType.Ext3)]
    [InlineData(FileSystemType.Ext2)]
    public void Layout_CoversTheWholeVolume_WithoutOverlappingGroups(FileSystemType fs)
    {
        var layout = ExtLayout.Compute(512 * MB, fs);

        Assert.Equal(4096u, layout.BlockSize);
        Assert.True(layout.GroupCount >= 1);

        // Every group's metadata must fit inside the group.
        for (uint g = 0; g < layout.GroupCount; g++)
        {
            var used = layout.MetadataBlocks(g);
            Assert.True(used < layout.BlocksInGroup(g),
                $"group {g} metadata ({used}) does not fit in {layout.BlocksInGroup(g)} blocks");
            Assert.True(layout.FirstUsableBlock(g) <= layout.GroupStart(g) + layout.BlocksInGroup(g));
        }

        // The groups must tile the volume exactly.
        var last = layout.GroupCount - 1;
        Assert.Equal(layout.TotalBlocks, layout.GroupStart(last) + layout.BlocksInGroup(last));
    }

    [Fact]
    public void Layout_PutsSuperblockBackupsWhereSparseSuperSaysTheyGo()
    {
        // Groups 0, 1 and every power of 3, 5 or 7 — the rule every ext reader relies on.
        Assert.True(ExtLayout.HasSuperBackup(0));
        Assert.True(ExtLayout.HasSuperBackup(1));
        Assert.True(ExtLayout.HasSuperBackup(3));
        Assert.True(ExtLayout.HasSuperBackup(5));
        Assert.True(ExtLayout.HasSuperBackup(7));
        Assert.True(ExtLayout.HasSuperBackup(9));   // 3^2
        Assert.True(ExtLayout.HasSuperBackup(25));  // 5^2
        Assert.True(ExtLayout.HasSuperBackup(49));  // 7^2
        Assert.True(ExtLayout.HasSuperBackup(81));  // 3^4

        Assert.False(ExtLayout.HasSuperBackup(2));
        Assert.False(ExtLayout.HasSuperBackup(4));
        Assert.False(ExtLayout.HasSuperBackup(6));
        Assert.False(ExtLayout.HasSuperBackup(15));
    }

    [Fact]
    public void Layout_GivesExt2NoJournal_AndExt3Ext4One()
    {
        Assert.False(ExtLayout.Compute(512 * MB, FileSystemType.Ext2).HasJournal);
        Assert.True(ExtLayout.Compute(512 * MB, FileSystemType.Ext3).HasJournal);
        Assert.True(ExtLayout.Compute(512 * MB, FileSystemType.Ext4).HasJournal);
    }

    [Fact]
    public void Layout_OnlyExt4UsesExtents()
    {
        Assert.True(ExtLayout.Compute(512 * MB, FileSystemType.Ext4).UsesExtents);
        Assert.False(ExtLayout.Compute(512 * MB, FileSystemType.Ext3).UsesExtents);
        Assert.False(ExtLayout.Compute(512 * MB, FileSystemType.Ext2).UsesExtents);
    }

    [Fact]
    public void Layout_RejectsAVolumeTooSmallToHoldAnything()
        => Assert.Throws<ArgumentException>(() => ExtLayout.Compute(4096, FileSystemType.Ext4));

    // ---------------------------------------------------------------- our own reader

    [Theory]
    [InlineData(FileSystemType.Ext4, "ext4")]
    [InlineData(FileSystemType.Ext3, "ext3")]
    [InlineData(FileSystemType.Ext2, "ext2")]
    public void WrittenImage_IsIdentifiedByOurOwnSignatureReader(FileSystemType fs, string expected)
    {
        var image = BuildImage(64 * MB, fs, "NATIVEFS");

        var info = LinuxFsSignature.Identify(image.AsSpan(0, 128 * 1024));

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Type);
        Assert.Equal("NATIVEFS", info.Label);
    }

    [Fact]
    public void TheUuidWeReportIsTheUuidWeWrote()
    {
        var uuid = Guid.NewGuid();
        var layout = ExtLayout.Compute(64 * MB, FileSystemType.Ext4);
        var buffer = new byte[layout.SizeBytes];

        using (var stream = new MemoryStream(buffer))
            new ExtFormatter(layout, "UUIDTEST", uuid).Write(stream);

        var info = LinuxFsSignature.Identify(buffer.AsSpan(0, 128 * 1024));
        Assert.Equal(uuid.ToString("D"), info!.Uuid);
    }

    // ---------------------------------------------------------------- the real oracle

    [RequiresWslOracleTheory]
    [InlineData(FileSystemType.Ext4)]
    [InlineData(FileSystemType.Ext3)]
    [InlineData(FileSystemType.Ext2)]
    public void RealE2fsck_ReportsTheImageAsClean(FileSystemType fs)
    {
        // 512 MiB exercises several block groups, so backup superblocks and per-group bitmaps are all
        // covered rather than just the degenerate single-group case.
        AssertFsckClean(64 * MB, fs);
        AssertFsckClean(512 * MB, fs);
    }

    [RequiresWslOracleFact]
    public void RealE2fsck_IsHappyWithAVolumeSpanningManyGroups()
        => AssertFsckClean(3072 * MB, FileSystemType.Ext4);

    [RequiresWslOracleFact]
    public void RealE2fsck_IsHappyWithASmallVolume()
        => AssertFsckClean(16 * MB, FileSystemType.Ext4);

    [RequiresWslOracleFact]
    public void RealDumpe2fs_ReportsTheLabelAndFeaturesWeAskedFor()
    {
        var path = WriteImageToDisk(128 * MB, FileSystemType.Ext4, "FEATURES");
        try
        {
            var output = RunInWsl("dumpe2fs", "-h", ToWslPath(path));
            Assert.Contains("FEATURES", output);
            Assert.Contains("has_journal", output);
            Assert.Contains("extent", output);
            Assert.Contains("sparse_super", output);
            // Checksums are deliberately not enabled — nothing should claim otherwise.
            Assert.DoesNotContain("metadata_csum", output);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------- formatting over an existing filesystem

    /// <summary>
    /// Formats onto a volume that is <b>not</b> blank. Every other test here formats into a sparse file
    /// that reads as zeros everywhere, so a region the writer fails to zero is indistinguishable from
    /// one it zeroed correctly — the whole class of "stale metadata shows through the new filesystem"
    /// is invisible to them.
    ///
    /// This is the shape of a real report: an ext2 volume on a reused USB stick came back with
    /// "Inode 1 has EXTENTS_FL flag set on filesystem without extents support" — an ext4 flag inside an
    /// ext2 inode table, i.e. an older filesystem's bytes surviving underneath the new one.
    /// </summary>
    [RequiresWslOracleTheory]
    [InlineData(FileSystemType.Ext2)]
    [InlineData(FileSystemType.Ext3)]
    [InlineData(FileSystemType.Ext4)]
    public void Format_OverGarbage_LeavesNoStaleMetadata(FileSystemType fs)
        // 512 MB spans several block groups, so every group's inode table and bitmaps are covered —
        // a single-group image would only prove the first one gets zeroed.
        => AssertFsckClean(512 * MB, fs, PreFill.Garbage);

    /// <summary>
    /// The reported case exactly: ext4 first, then ext2 over the top. ext4 metadata left behind in the
    /// inode table is what produced EXTENTS_FL on an ext2 volume.
    /// </summary>
    [RequiresWslOracleFact]
    public void Ext2_FormattedOverAnExistingExt4_IsClean()
        => AssertFsckClean(512 * MB, FileSystemType.Ext2, PreFill.Ext4);

    private enum PreFill { Blank, Garbage, Ext4 }

    /// <summary>Formats an image on disk and asserts real e2fsck finds nothing wrong with it.</summary>
    private static void AssertFsckClean(ulong sizeBytes, FileSystemType fs, PreFill preFill = PreFill.Blank)
    {
        var path = WriteImageToDisk(sizeBytes, fs, "FSCKTEST", preFill);
        try
        {
            // -f forces a full check even though the superblock says clean; -n answers "no" to every
            // repair prompt, so a non-zero exit means e2fsck found something it wanted to change.
            var (exit, output) = RunInWslRaw("e2fsck", "-fn", ToWslPath(path));
            Assert.True(exit == 0,
                $"e2fsck rejected the {fs} image ({Size(sizeBytes)}), exit {exit}:\n{output}");
        }
        finally { File.Delete(path); }
    }

    private static byte[] BuildImage(ulong sizeBytes, FileSystemType fs, string label)
    {
        var layout = ExtLayout.Compute(sizeBytes, fs);
        var buffer = new byte[layout.SizeBytes];
        using var stream = new MemoryStream(buffer);
        new ExtFormatter(layout, label).Write(stream);
        return buffer;
    }

    private static string WriteImageToDisk(
        ulong sizeBytes, FileSystemType fs, string label, PreFill preFill = PreFill.Blank)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diskforge-exttest-{Guid.NewGuid():N}.img");
        var layout = ExtLayout.Compute(sizeBytes, fs);

        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            file.SetLength((long)layout.SizeBytes); // sparse; unwritten regions read as zeros
            Prefill(file, layout.SizeBytes, preFill);
            file.Position = 0;
            new ExtFormatter(layout, label).Write(file);
        }
        return path;
    }

    /// <summary>Puts something other than zeros on the volume before it is formatted.</summary>
    private static void Prefill(FileStream file, ulong sizeBytes, PreFill preFill)
    {
        switch (preFill)
        {
            case PreFill.Blank:
                return;

            case PreFill.Ext4:
                // A real, complete ext4 filesystem of the same size — the previous occupant.
                new ExtFormatter(ExtLayout.Compute(sizeBytes, FileSystemType.Ext4), "OLDEXT4").Write(file);
                return;

            case PreFill.Garbage:
                // Deterministic non-zero fill, so a failure is reproducible. Avoids 0x00 entirely:
                // any surviving byte is then provably ours and not something the writer zeroed.
                var random = new Random(20260805);
                var block = new byte[64 * 1024];
                file.Position = 0;
                for (ulong written = 0; written < sizeBytes; written += (ulong)block.Length)
                {
                    random.NextBytes(block);
                    for (var i = 0; i < block.Length; i++) if (block[i] == 0) block[i] = 0xA5;

                    var count = (int)Math.Min((ulong)block.Length, sizeBytes - written);
                    file.Write(block, 0, count);
                }
                file.Flush();
                return;
        }
    }

    // ---------------------------------------------------------------- WSL oracle plumbing

    /// <summary>The distro used purely to run e2fsprogs against a file. Not needed by the product.</summary>
    private static string? OracleDistro => LinuxToolchainProbe.Get().Distros
        .Where(d => d.WslVersion == 2)
        .OrderByDescending(d => d.IsDefault)
        .Select(d => d.Name)
        .FirstOrDefault();

    private static string ToWslPath(string windowsPath)
        => RunInWsl("wslpath", "-a", "-u", windowsPath).Trim();

    private static string RunInWsl(params string[] command)
    {
        var (exit, output) = RunInWslRaw(command);
        return output;
    }

    private static (int ExitCode, string Output) RunInWslRaw(params string[] command)
    {
        if (OracleDistro is null) throw new InvalidOperationException("No WSL2 oracle distro.");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var a in new[] { "-d", OracleDistro!, "-u", "root", "--exec", "sh", "-c",
                     "PATH=/usr/local/sbin:/usr/sbin:/sbin:/usr/bin:/bin:$PATH; exec \"$0\" \"$@\"" })
            psi.ArgumentList.Add(a);
        foreach (var a in command) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, new StringBuilder(stdout).Append(stderr).ToString());
    }

    private static string Size(ulong bytes) => $"{bytes / MB} MiB";
}
