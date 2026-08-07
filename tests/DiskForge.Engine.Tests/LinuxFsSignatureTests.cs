using System.Text;
using DiskForge.Engine.Linux;

namespace DiskForge.Engine.Tests;

/// <summary>
/// The superblock reader is how a Linux format gets verified on a <b>removable</b> drive: WSL cannot
/// attach one, so blkid is unreachable and DiskForge reads the on-disk signature itself. If this parser
/// is wrong, a failed format could be reported as a success — so it is tested against hand-built
/// superblocks here, and against real mkfs output in the elevated VHDX tests.
/// </summary>
public class LinuxFsSignatureTests
{
    private const int ExtSuper = 1024;

    /// <summary>Builds a buffer big enough for every superblock location we probe.</summary>
    private static byte[] Blank() => new byte[128 * 1024];

    private static void WriteU16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] b, int offset, uint value)
    {
        for (var i = 0; i < 4; i++) b[offset + i] = (byte)(value >> (8 * i));
    }

    private static void WriteAscii(byte[] b, int offset, string text)
        => Encoding.ASCII.GetBytes(text).CopyTo(b, offset);

    /// <summary>An ext superblock with the given feature flags and label.</summary>
    private static byte[] Ext(uint compat = 0, uint incompat = 0, uint roCompat = 0, string? label = null)
    {
        var b = Blank();
        WriteU16(b, ExtSuper + 56, 0xEF53);          // s_magic
        WriteU32(b, ExtSuper + 92, compat);
        WriteU32(b, ExtSuper + 96, incompat);
        WriteU32(b, ExtSuper + 100, roCompat);
        for (var i = 0; i < 16; i++) b[ExtSuper + 104 + i] = (byte)(i + 1); // s_uuid
        if (label is not null) WriteAscii(b, ExtSuper + 120, label);
        return b;
    }

    // ---------- ext2 / ext3 / ext4 ----------

    [Fact]
    public void Ext4_IsRecognised_FromItsIncompatFeatureFlags()
    {
        // EXTENTS (0x40) is the flag mke2fs sets for ext4 and never for ext2/ext3.
        var info = LinuxFsSignature.Identify(Ext(compat: 0x0004, incompat: 0x0040, label: "MYDATA"));

        Assert.NotNull(info);
        Assert.Equal("ext4", info!.Type);
        Assert.Equal("MYDATA", info.Label);
    }

    [Fact]
    public void Ext4_IsRecognised_FromARoCompatFeatureAlone()
        => Assert.Equal("ext4", LinuxFsSignature.Identify(Ext(compat: 0x0004, roCompat: 0x0040))!.Type);

    [Fact]
    public void Ext3_IsAJournalWithNoExt4Features()
        => Assert.Equal("ext3", LinuxFsSignature.Identify(Ext(compat: 0x0004))!.Type);

    [Fact]
    public void Ext2_HasNeitherJournalNorExt4Features()
        => Assert.Equal("ext2", LinuxFsSignature.Identify(Ext())!.Type);

    [Fact]
    public void ExtUuid_IsRenderedInTheStandardTextOrder()
    {
        // Linux stores the UUID big-endian on disk, which is already RFC 4122 text order — no byte
        // swapping (getting this wrong is the classic .NET Guid trap).
        var info = LinuxFsSignature.Identify(Ext());
        Assert.Equal("01020304-0506-0708-090a-0b0c0d0e0f10", info!.Uuid);
    }

    [Fact]
    public void AnEmptyLabelIsReportedAsNoLabel()
        => Assert.Null(LinuxFsSignature.Identify(Ext(incompat: 0x0040))!.Label);

    // ---------- the other filesystems ----------

    [Fact]
    public void Xfs_IsRecognisedByItsMagicAtOffsetZero()
    {
        var b = Blank();
        WriteAscii(b, 0, "XFSB");
        WriteAscii(b, 108, "XFSVOL");

        var info = LinuxFsSignature.Identify(b);
        Assert.Equal("xfs", info!.Type);
        Assert.Equal("XFSVOL", info.Label);
    }

    [Fact]
    public void Btrfs_IsRecognisedByItsMagicAt64Kib()
    {
        var b = Blank();
        WriteAscii(b, 0x10000 + 64, "_BHRfS_M");
        WriteAscii(b, 0x10000 + 0x12B, "BTRFSVOL");

        var info = LinuxFsSignature.Identify(b);
        Assert.Equal("btrfs", info!.Type);
        Assert.Equal("BTRFSVOL", info.Label);
    }

    [Fact]
    public void F2fs_IsRecognisedByItsMagic()
    {
        var b = Blank();
        WriteU32(b, ExtSuper, 0xF2F52010);
        Assert.Equal("f2fs", LinuxFsSignature.Identify(b)!.Type);
    }

    /// <summary>
    /// f2fs stores its label as UTF-16LE (struct f2fs_super_block.volume_name at 0x7C), unlike every
    /// other filesystem here. This came back null on a real drive, so a partition DiskForge had just
    /// labelled "DF-F2FS" was reported with no name at all.
    /// </summary>
    [Fact]
    public void F2fs_LabelIsReadAsUtf16()
    {
        var b = Blank();
        WriteU32(b, ExtSuper, 0xF2F52010);
        WriteUtf16(b, ExtSuper + 0x7C, "DF-F2FS");

        Assert.Equal("DF-F2FS", LinuxFsSignature.Identify(b)!.Label);
    }

    [Fact]
    public void F2fs_UuidIsRead()
    {
        var b = Blank();
        WriteU32(b, ExtSuper, 0xF2F52010);
        for (var i = 0; i < 16; i++) b[ExtSuper + 0x6C + i] = (byte)(i + 1);

        Assert.Equal("01020304-0506-0708-090a-0b0c0d0e0f10", LinuxFsSignature.Identify(b)!.Uuid);
    }

    [Fact]
    public void F2fs_WithNoLabel_ReportsNull()
    {
        var b = Blank();
        WriteU32(b, ExtSuper, 0xF2F52010);
        Assert.Null(LinuxFsSignature.Identify(b)!.Label);
    }

    /// <summary>A UTF-8 read of a UTF-16 label yields "D\0F\0…" — the bug this guards against.</summary>
    [Fact]
    public void F2fs_LabelDoesNotLeakInterleavedNulls()
    {
        var b = Blank();
        WriteU32(b, ExtSuper, 0xF2F52010);
        WriteUtf16(b, ExtSuper + 0x7C, "AB");

        Assert.Equal("AB", LinuxFsSignature.Identify(b)!.Label);
    }

    private static void WriteUtf16(byte[] buffer, int offset, string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        bytes.CopyTo(buffer, offset);
    }

    [Fact]
    public void Swap_IsRecognisedByTheSignatureEndingItsFirstPage()
    {
        var b = Blank();
        WriteAscii(b, 4096 - 10, "SWAPSPACE2");
        Assert.Equal("swap", LinuxFsSignature.Identify(b)!.Type);
    }

    // ---------- the negative cases that keep a bad format from passing ----------

    [Fact]
    public void AnEmptyPartitionIsNotAFilesystem()
        => Assert.Null(LinuxFsSignature.Identify(Blank()));

    [Fact]
    public void AWrongMagicIsNotAFilesystem()
    {
        var b = Blank();
        WriteU16(b, ExtSuper + 56, 0xEF52); // one bit off
        Assert.Null(LinuxFsSignature.Identify(b));
    }

    [Fact]
    public void AnNtfsVolumeIsNotMistakenForLinux()
    {
        // The exact case that matters: if a format silently failed, the old Windows filesystem is
        // still there and must NOT be reported as a successful ext4.
        var b = Blank();
        WriteAscii(b, 3, "NTFS    ");
        Assert.Null(LinuxFsSignature.Identify(b));
    }

    [Fact]
    public void ATruncatedReadIsNotAFilesystem()
        => Assert.Null(LinuxFsSignature.Identify(new byte[512]));
}
