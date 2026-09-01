using DiskForge.Engine.Cloning;
using DiskForge.Engine.Native;
using DiskForge.Engine.Tests.Harness;

namespace DiskForge.Engine.Tests;

/// <summary>
/// End-to-end test of the raw clone engine against two throwaway VHDX loopback disks — never a real
/// drive (§7). Writes a known pattern to a source disk, copies it to a target with
/// <see cref="DiskCloneEngine"/>, and proves the target is byte-identical via the same hash the verify
/// pass uses. Elevated-only: VHDX attach needs Administrator, so it auto-skips otherwise.
/// </summary>
[Collection(RealDiskCollection.Name)]
public class CloneVhdxTests
{
    private const ulong MB = 1024UL * 1024;
    private const int Sector = 512;

    [RequiresElevationFact]
    public async Task RawCopy_ProducesByteIdenticalTarget_AndVerifies()
    {
        // Two blank VHDX disks of equal size. Source gets a deterministic pattern across several MB.
        using var source = new VhdxLoopbackDisk(64 * MB);
        using var target = new VhdxLoopbackDisk(64 * MB);

        const long copyBytes = 16 * (long)MB;   // copy a 16 MB region (sector-aligned)
        const int chunk = 4 * (int)MB;

        // ---- write a known pattern to the source ----
        var writtenHash = await WritePatternAsync(source.DiskNumber, copyBytes, chunk);

        // ---- clone source → target ----
        CopyResult copy;
        using (var src = RawDiskAccess.OpenRead(source.DiskNumber))
        using (var dst = RawDiskAccess.OpenWrite(target.DiskNumber))
        {
            copy = await DiskCloneEngine.CopyAsync(src, dst, copyBytes, Sector, chunk, null, CancellationToken.None);
        }

        Assert.Equal(copyBytes, copy.BytesCopied);
        // What the copier hashed while writing must equal what we wrote to the source.
        Assert.Equal(writtenHash, copy.Sha256);

        // ---- independent verify: re-read the TARGET and confirm it matches ----
        byte[] targetHash;
        using (var dst = RawDiskAccess.OpenRead(target.DiskNumber))
        {
            targetHash = await DiskCloneEngine.HashAsync(dst, copyBytes, Sector, chunk, null, CancellationToken.None);
        }

        Assert.Equal(copy.Sha256, targetHash);
    }

    [RequiresElevationFact]
    public async Task RawCopy_DetectsMismatch_WhenTargetDiffers()
    {
        using var source = new VhdxLoopbackDisk(64 * MB);
        using var target = new VhdxLoopbackDisk(64 * MB);

        const long copyBytes = 8 * (long)MB;
        const int chunk = 4 * (int)MB;

        var srcHash = await WritePatternAsync(source.DiskNumber, copyBytes, chunk, seed: 1);
        // Give the target a DIFFERENT pattern so a naive "assume success" would be caught.
        await WritePatternAsync(target.DiskNumber, copyBytes, chunk, seed: 2);

        using var dst = RawDiskAccess.OpenRead(target.DiskNumber);
        var targetHash = await DiskCloneEngine.HashAsync(dst, copyBytes, Sector, chunk, null, CancellationToken.None);

        Assert.NotEqual(srcHash, targetHash);
    }

    /// <summary>Writes a deterministic byte pattern to a disk and returns its SHA-256, using the exact
    /// aligned-write path the clone engine relies on.</summary>
    private static async Task<byte[]> WritePatternAsync(int diskNumber, long bytes, int chunk, byte seed = 7)
    {
        var buffer = new byte[chunk];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)((i * 31 + seed * 131) & 0xFF);

        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var h = RawDiskAccess.OpenWrite(diskNumber);
        long offset = 0;
        while (offset < bytes)
        {
            var want = (int)Math.Min(chunk, bytes - offset);
            RandomAccess.Write(h, buffer.AsSpan(0, want), offset);
            sha.AppendData(buffer, 0, want);
            offset += want;
        }
        RandomAccess.FlushToDisk(h);
        return await Task.FromResult(sha.GetHashAndReset());
    }
}
