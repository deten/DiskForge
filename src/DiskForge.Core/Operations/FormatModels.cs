namespace DiskForge.Core.Operations;

/// <summary>Filesystems DiskForge can format to.</summary>
public enum FileSystemType
{
    // --- Windows-native: written by Format-Volume / diskpart ---
    Exfat = 0,
    Ntfs = 1,
    Fat32 = 2,

    // --- Linux: written by the mkfs toolchain via ILinuxFormatBackend. Windows has no driver for
    // these, so the resulting volume shows as RAW/unformatted in Explorer. That is expected, not a
    // failure — see FileSystemTypeExtensions.WindowsCanRead. ---
    Ext4 = 10,
    Ext3 = 11,
    Ext2 = 12,
    Btrfs = 13,
    Xfs = 14,
    F2fs = 15,
    LinuxSwap = 16
}

/// <summary>Which toolchain writes a filesystem — this decides the whole execution path.</summary>
public enum FileSystemFamily
{
    Windows = 0,
    Linux = 1
}

/// <summary>How much of the target disk a format touches.</summary>
public enum FormatScope
{
    /// <summary>Reformat one existing partition in place.</summary>
    ReformatPartition = 0,
    /// <summary>Erase the partition table and create one fresh partition spanning the disk, then format.</summary>
    CleanWholeDisk = 1
}

/// <summary>
/// Partition table to write when a clean-whole-disk format re-initializes the drive. There is no
/// non-destructive conversion here and none is implied: the scheme is only selectable because the disk
/// is being erased anyway.
/// </summary>
public enum PartitionSchemeChoice
{
    /// <summary>Let Windows pick. It chooses MBR for removable media and GPT for most fixed disks.</summary>
    Automatic = 0,

    /// <summary>GPT — 128 partition entries, required above 2 TiB. Not all systems boot from a GPT stick.</summary>
    Gpt = 1,

    /// <summary>MBR — 4 table slots (DiskForge uses 3, see CreatePartitionOperation) and a 2 TiB ceiling.</summary>
    Mbr = 2
}

public static class FileSystemTypeExtensions
{
    /// <summary>Linux "filesystem data" GPT partition type GUID — what parted/fdisk assign to ext4/btrfs/xfs.</summary>
    public static readonly Guid LinuxFilesystemDataGuid = new("0fc63daf-8483-4772-8e79-3d69d8477de4");

    /// <summary>Linux swap GPT partition type GUID.</summary>
    public static readonly Guid LinuxSwapGuid = new("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");

    /// <summary>MBR partition type byte for a Linux filesystem (0x83) / Linux swap (0x82).</summary>
    public const byte LinuxMbrType = 0x83;
    public const byte LinuxSwapMbrType = 0x82;

    public static FileSystemFamily Family(this FileSystemType fs) => fs switch
    {
        FileSystemType.Exfat or FileSystemType.Ntfs or FileSystemType.Fat32 => FileSystemFamily.Windows,
        _ => FileSystemFamily.Linux
    };

    public static bool IsLinux(this FileSystemType fs) => fs.Family() == FileSystemFamily.Linux;

    /// <summary>
    /// True when DiskForge writes this filesystem itself, with no external tool, no WSL and no bundled
    /// binary. These are always available — nothing about the user's machine can make them unavailable.
    /// </summary>
    public static bool IsWrittenNatively(this FileSystemType fs)
        => fs is FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2;

    /// <summary>True when Windows can mount and read the filesystem it just wrote.</summary>
    public static bool WindowsCanRead(this FileSystemType fs) => !fs.IsLinux();

    /// <summary>
    /// Display name. For Windows filesystems this is also the exact token Format-Volume expects;
    /// for Linux ones it is the type name <c>blkid</c> reports, which is what VerifyAsync compares.
    /// </summary>
    public static string ToFormatName(this FileSystemType fs) => fs switch
    {
        FileSystemType.Exfat => "exFAT",
        FileSystemType.Ntfs => "NTFS",
        FileSystemType.Fat32 => "FAT32",
        FileSystemType.Ext4 => "ext4",
        FileSystemType.Ext3 => "ext3",
        FileSystemType.Ext2 => "ext2",
        FileSystemType.Btrfs => "btrfs",
        FileSystemType.Xfs => "xfs",
        FileSystemType.F2fs => "f2fs",
        FileSystemType.LinuxSwap => "swap",
        _ => "exFAT"
    };

    /// <summary>The mkfs binary that writes this filesystem. Windows filesystems have none.</summary>
    public static string? MkfsTool(this FileSystemType fs) => fs switch
    {
        FileSystemType.Ext4 => "mkfs.ext4",
        FileSystemType.Ext3 => "mkfs.ext3",
        FileSystemType.Ext2 => "mkfs.ext2",
        FileSystemType.Btrfs => "mkfs.btrfs",
        FileSystemType.Xfs => "mkfs.xfs",
        FileSystemType.F2fs => "mkfs.f2fs",
        FileSystemType.LinuxSwap => "mkswap",
        _ => null
    };

    /// <summary>Distro package that provides <see cref="MkfsTool"/>, for a fix-it hint when it is missing.</summary>
    public static string? MkfsPackage(this FileSystemType fs) => fs switch
    {
        FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2 => "e2fsprogs",
        FileSystemType.Btrfs => "btrfs-progs",
        FileSystemType.Xfs => "xfsprogs",
        FileSystemType.F2fs => "f2fs-tools",
        FileSystemType.LinuxSwap => "util-linux",
        _ => null
    };

    /// <summary>The mkfs flag that forces a write over an existing filesystem instead of prompting.</summary>
    public static string? MkfsForceFlag(this FileSystemType fs) => fs switch
    {
        FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2 => "-F",
        FileSystemType.Btrfs or FileSystemType.Xfs or FileSystemType.F2fs => "-f",
        FileSystemType.LinuxSwap => "-f",
        _ => null
    };

    /// <summary>The mkfs flag that sets the volume label, when the tool supports one.</summary>
    public static string? MkfsLabelFlag(this FileSystemType fs) => fs switch
    {
        FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2 => "-L",
        FileSystemType.Btrfs => "-L",
        FileSystemType.Xfs => "-L",
        FileSystemType.F2fs => "-l",
        FileSystemType.LinuxSwap => "-L",
        _ => null
    };

    /// <summary>
    /// Max volume label length allowed by the filesystem. The Linux numbers are the mkfs tools'
    /// own limits (mke2fs 16 bytes, mkfs.xfs 12, mkfs.btrfs 255, mkfs.f2fs 512, mkswap 15).
    /// </summary>
    public static int MaxLabelLength(this FileSystemType fs) => fs switch
    {
        FileSystemType.Fat32 => 11,
        FileSystemType.Exfat or FileSystemType.Ntfs => 32,
        FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2 => 16,
        FileSystemType.Xfs => 12,
        FileSystemType.Btrfs => 255,
        FileSystemType.F2fs => 512,
        FileSystemType.LinuxSwap => 15,
        _ => 32
    };

    /// <summary>
    /// Smallest volume the filesystem's mkfs tool will accept. mkfs.xfs hard-refuses below 300 MB and
    /// mkfs.btrfs below ~109 MiB, so catching this in Validate beats a cryptic tool error at Apply.
    /// </summary>
    public static ulong MinimumSizeBytes(this FileSystemType fs) => fs switch
    {
        FileSystemType.Xfs => 300UL * 1024 * 1024,
        FileSystemType.Btrfs => 128UL * 1024 * 1024,
        FileSystemType.F2fs => 64UL * 1024 * 1024,
        FileSystemType.LinuxSwap => 64UL * 1024,
        _ => 8UL * 1024 * 1024
    };

    /// <summary>
    /// True when a "full format" maps onto a real bad-block scan. Only mke2fs has one (<c>-c</c>);
    /// mkfs.xfs/btrfs/f2fs have no equivalent, so a full format there is honestly downgraded to quick.
    /// </summary>
    public static bool SupportsBadBlockScan(this FileSystemType fs) => fs switch
    {
        FileSystemType.Ext4 or FileSystemType.Ext3 or FileSystemType.Ext2 => true,
        FileSystemType.Exfat or FileSystemType.Ntfs or FileSystemType.Fat32 => true,
        _ => false
    };

    /// <summary>GPT partition type this filesystem's partition should carry, or null to leave it alone.</summary>
    public static Guid? PreferredGptType(this FileSystemType fs) => fs switch
    {
        FileSystemType.LinuxSwap => LinuxSwapGuid,
        _ when fs.IsLinux() => LinuxFilesystemDataGuid,
        _ => null
    };

    /// <summary>MBR partition type byte this filesystem's partition should carry, or null to leave it alone.</summary>
    public static byte? PreferredMbrType(this FileSystemType fs) => fs switch
    {
        FileSystemType.LinuxSwap => LinuxSwapMbrType,
        _ when fs.IsLinux() => LinuxMbrType,
        _ => null
    };

    /// <summary>Windows' Format-Volume refuses to create FAT32 volumes larger than 32 GB.</summary>
    public static bool ExceedsFat32Limit(this FileSystemType fs, ulong sizeBytes)
        => fs == FileSystemType.Fat32 && sizeBytes > 32UL * 1024 * 1024 * 1024;
}
