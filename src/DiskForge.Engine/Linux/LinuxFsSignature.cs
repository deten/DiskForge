using System.Buffers.Binary;
using System.Text;
using DiskForge.Core.Operations;
using DiskForge.Engine.Native;

namespace DiskForge.Engine.Linux;

/// <summary>What a Linux filesystem's on-disk superblock says about itself.</summary>
public sealed record LinuxFsInfo(string Type, string? Label, string? Uuid);

/// <summary>
/// Reads Linux filesystem superblocks directly from a disk, from Windows.
///
/// <b>Why this is needed.</b> The natural way to verify a Linux format is <c>blkid</c> inside WSL — but
/// WSL cannot attach removable media at all, so for a USB stick there is no Linux to ask. Rather than
/// leaving those formats unverified, DiskForge reads the superblock itself: the magic numbers are small,
/// stable, well-documented constants, and this is a read-only parse of bytes we just wrote.
///
/// All offsets below are from the start of the <i>filesystem</i> (i.e. the partition), not the disk.
/// </summary>
public static class LinuxFsSignature
{
    /// <summary>Enough to cover every superblock we look at — btrfs' lives at 64 KiB.</summary>
    private const int ProbeBytes = 128 * 1024;

    // ext2/3/4: superblock at 1024; magic 0xEF53 at +56.
    private const int ExtSuperOffset = 1024;
    private const ushort ExtMagic = 0xEF53;

    // btrfs: superblock at 64 KiB; magic "_BHRfS_M" at +64.
    private const int BtrfsSuperOffset = 0x10000;
    private static readonly byte[] BtrfsMagic = Encoding.ASCII.GetBytes("_BHRfS_M");

    // xfs: magic "XFSB" at offset 0.
    private static readonly byte[] XfsMagic = Encoding.ASCII.GetBytes("XFSB");

    // f2fs: superblock at 1024, magic 0xF2F52010. Offsets below are relative to the superblock and
    // come from struct f2fs_super_block: uuid[16] at 0x6C, then volume_name as UTF-16LE.
    private const uint F2fsMagic = 0xF2F52010;
    private const int F2fsUuidOffset = 0x6C;
    private const int F2fsVolumeNameOffset = 0x7C;

    /// <summary>f2fs' MAX_VOLUME_LEN is 512 UTF-16 code units.</summary>
    private const int F2fsVolumeNameChars = 512;

    // swap: "SWAPSPACE2" ends the first page.
    private static readonly byte[] SwapMagic = Encoding.ASCII.GetBytes("SWAPSPACE2");

    /// <summary>
    /// Reads the first <see cref="ProbeBytes"/> of a partition and identifies the filesystem in it.
    /// Returns null when nothing recognisable is there.
    /// </summary>
    public static LinuxFsInfo? Read(int diskNumber, ulong partitionOffsetBytes, int sectorSize = 512)
    {
        var length = (int)DiskCloneAlign(ProbeBytes, sectorSize);
        var buffer = new byte[length];

        using var handle = RawDiskAccess.OpenRead(diskNumber);
        var read = 0;
        while (read < length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read, length - read), (long)partitionOffsetBytes + read);
            if (n == 0) break;
            read += n;
        }

        return read < ExtSuperOffset + 128 ? null : Identify(buffer.AsSpan(0, read));
    }

    /// <summary>Identifies a filesystem from the head of a partition. Pure — unit-tested with fixtures.</summary>
    public static LinuxFsInfo? Identify(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual(XfsMagic))
            return new LinuxFsInfo("xfs", ReadString(head, 108, 12), null);

        if (head.Length >= BtrfsSuperOffset + 0x22B &&
            head.Slice(BtrfsSuperOffset + 64, 8).SequenceEqual(BtrfsMagic))
            return new LinuxFsInfo("btrfs",
                ReadString(head, BtrfsSuperOffset + 0x12B, 256),
                ReadGuid(head, BtrfsSuperOffset + 32));

        if (head.Length >= ExtSuperOffset + 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(ExtSuperOffset, 4)) == F2fsMagic)
            return new LinuxFsInfo(
                "f2fs",
                // f2fs stores its label as UTF-16LE, unlike every other filesystem here — reading it
                // as UTF-8 would yield "D\0F\0-\0…". This was returning null, so an f2fs partition
                // DiskForge had just labelled came back with no name.
                ReadUtf16String(head, ExtSuperOffset + F2fsVolumeNameOffset, F2fsVolumeNameChars),
                ReadGuid(head, ExtSuperOffset + F2fsUuidOffset));

        if (head.Length >= ExtSuperOffset + 160 &&
            BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(ExtSuperOffset + 56, 2)) == ExtMagic)
            return new LinuxFsInfo(
                ExtFlavour(head),
                ReadString(head, ExtSuperOffset + 120, 16),
                ReadGuid(head, ExtSuperOffset + 104));

        // swap's signature sits at the end of the first page; 4 KiB is the only size we create.
        if (head.Length >= 4096 && head.Slice(4096 - 10, 10).SequenceEqual(SwapMagic))
            return new LinuxFsInfo("swap", null, ReadGuid(head, 1024 + 4));

        return null;
    }

    /// <summary>
    /// ext2 vs ext3 vs ext4, decided from the feature flags the same way blkid does: any ext4-only
    /// feature means ext4, otherwise a journal means ext3, otherwise ext2.
    /// </summary>
    private static string ExtFlavour(ReadOnlySpan<byte> head)
    {
        var compat = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(ExtSuperOffset + 92, 4));
        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(ExtSuperOffset + 96, 4));
        var roCompat = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(ExtSuperOffset + 100, 4));

        const uint hasJournal = 0x0004;                       // COMPAT_HAS_JOURNAL
        const uint ext4Incompat = 0x0040 | 0x0080 | 0x0200;   // EXTENTS | 64BIT | FLEX_BG
        const uint ext4RoCompat = 0x0008 | 0x0010 | 0x0020    // HUGE_FILE | GDT_CSUM | DIR_NLINK
                                | 0x0040 | 0x0400;            // EXTRA_ISIZE | METADATA_CSUM

        if ((incompat & ext4Incompat) != 0 || (roCompat & ext4RoCompat) != 0) return "ext4";
        return (compat & hasJournal) != 0 ? "ext3" : "ext2";
    }

    private static string? ReadString(ReadOnlySpan<byte> head, int offset, int maxLength)
    {
        if (head.Length < offset + maxLength) return null;
        var slice = head.Slice(offset, maxLength);
        var end = slice.IndexOf((byte)0);
        if (end == 0) return null;
        if (end < 0) end = maxLength;
        return Encoding.UTF8.GetString(slice[..end]);
    }

    /// <summary>
    /// Reads a NUL-terminated UTF-16LE string of at most <paramref name="maxChars"/> code units.
    /// f2fs is the only filesystem here that stores its label this way.
    /// </summary>
    private static string? ReadUtf16String(ReadOnlySpan<byte> head, int offset, int maxChars)
    {
        if (head.Length <= offset) return null;

        var available = Math.Min(maxChars * 2, head.Length - offset);
        var slice = head.Slice(offset, available);

        var chars = 0;
        while ((chars + 1) * 2 <= slice.Length &&
               BinaryPrimitives.ReadUInt16LittleEndian(slice.Slice(chars * 2, 2)) != 0)
        {
            chars++;
        }

        return chars == 0 ? null : Encoding.Unicode.GetString(slice[..(chars * 2)]);
    }

    /// <summary>Linux stores UUIDs big-endian on disk, which is the RFC 4122 text order.</summary>
    private static string? ReadGuid(ReadOnlySpan<byte> head, int offset)
    {
        if (head.Length < offset + 16) return null;
        var bytes = head.Slice(offset, 16);
        if (bytes.IndexOfAnyExcept((byte)0) < 0) return null;

        var sb = new StringBuilder(36);
        for (var i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) sb.Append('-');
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private static long DiskCloneAlign(long bytes, int sectorSize)
    {
        var rem = bytes % sectorSize;
        return rem == 0 ? bytes : bytes + (sectorSize - rem);
    }
}
