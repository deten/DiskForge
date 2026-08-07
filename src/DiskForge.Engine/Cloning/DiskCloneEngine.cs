using System.Security.Cryptography;
using DiskForge.Engine.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Cloning;

/// <summary>Outcome of a raw copy: bytes moved and the SHA-256 of everything written.</summary>
public sealed record CopyResult(long BytesCopied, byte[] Sha256);

/// <summary>
/// Low-level, filesystem-agnostic block copier used by the clone/image engines. It reads sector-aligned
/// chunks from one physical drive and writes them to another, hashing as it goes so the copy can be
/// verified without a second full read during the write pass.
///
/// SAFETY: this class only ever moves bytes between the handles it is given. ALL target selection,
/// system-disk guarding, offline-ing and elevation checks happen in <c>CloneDiskOperation</c> before a
/// handle is ever opened. Never call this against a handle you have not guarded upstream.
/// </summary>
public static class DiskCloneEngine
{
    /// <summary>Copy <paramref name="totalBytes"/> from source→target in aligned chunks, hashing writes.
    /// <paramref name="totalBytes"/> and <paramref name="chunkBytes"/> must be multiples of the sector
    /// size; device I/O rejects unaligned counts/offsets.</summary>
    public static async Task<CopyResult> CopyAsync(
        SafeFileHandle source, SafeFileHandle target,
        long totalBytes, int sectorSize, int chunkBytes,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (totalBytes % sectorSize != 0)
            throw new ArgumentException($"totalBytes ({totalBytes}) must be a multiple of the sector size ({sectorSize}).");
        if (chunkBytes % sectorSize != 0 || chunkBytes <= 0)
            throw new ArgumentException($"chunkBytes ({chunkBytes}) must be a positive multiple of the sector size ({sectorSize}).");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunkBytes];
        long offset = 0;

        while (offset < totalBytes)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunkBytes, totalBytes - offset); // still a sector multiple
            var got = ReadExact(source, buffer, offset, want);
            if (got != want)
                throw new IOException($"Short read at offset {offset}: wanted {want}, got {got}.");

            RandomAccess.Write(target, buffer.AsSpan(0, want), offset);
            sha.AppendData(buffer, 0, want);

            offset += want;
            progress?.Report((double)offset / totalBytes);
            await Task.Yield();
        }

        RandomAccess.FlushToDisk(target);
        return new CopyResult(offset, sha.GetHashAndReset());
    }

    /// <summary>Re-read <paramref name="totalBytes"/> from a drive and return its SHA-256 — the verify
    /// pass. Compare against the <see cref="CopyResult.Sha256"/> from the copy to prove the clone is
    /// byte-identical to what was written.</summary>
    public static async Task<byte[]> HashAsync(
        SafeFileHandle handle, long totalBytes, int sectorSize, int chunkBytes,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunkBytes];
        long offset = 0;

        while (offset < totalBytes)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(chunkBytes, totalBytes - offset);
            var got = ReadExact(handle, buffer, offset, want);
            if (got != want)
                throw new IOException($"Short read at offset {offset}: wanted {want}, got {got}.");

            sha.AppendData(buffer, 0, want);
            offset += want;
            progress?.Report((double)offset / totalBytes);
            await Task.Yield();
        }
        return sha.GetHashAndReset();
    }

    /// <summary>Round a byte count up to the next whole sector.</summary>
    public static long AlignUpToSector(long bytes, int sectorSize)
    {
        var rem = bytes % sectorSize;
        return rem == 0 ? bytes : bytes + (sectorSize - rem);
    }

    private static int ReadExact(SafeFileHandle handle, byte[] buffer, long fileOffset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(total, count - total), fileOffset + total);
            if (n == 0) break; // end of device
            total += n;
        }
        return total;
    }
}
