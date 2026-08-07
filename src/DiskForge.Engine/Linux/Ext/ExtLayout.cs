using DiskForge.Core.Operations;

namespace DiskForge.Engine.Linux.Ext;

/// <summary>
/// The complete geometry of an ext2/ext3/ext4 filesystem, computed before a single byte is written.
///
/// This is deliberately a pure calculation with no I/O: every number the formatter needs — where each
/// group starts, which groups carry superblock backups, how many blocks the inode tables take, how big
/// the journal is — is derived here and unit-tested on its own. A mistake in this arithmetic produces a
/// filesystem that mounts but is subtly wrong, so it is the part that most deserves to be testable
/// without touching a disk.
///
/// Defaults follow mke2fs where it matters and stay conservative where it does not: 4 KiB blocks,
/// 256-byte inodes, one inode per 16 KiB, non-flex_bg group layout, and <b>no metadata checksums</b>.
/// Leaving metadata_csum/gdt_csum off is a legitimate, fully-supported ext4 configuration and means the
/// image needs no crc32c/crc16 — far less to get wrong for no loss of correctness.
/// </summary>
public sealed class ExtLayout
{
    /// <summary>Inode numbers fixed by the ext specification.</summary>
    public const uint RootInode = 2;
    public const uint JournalInode = 8;
    public const uint LostFoundInode = 11;

    /// <summary>First inode available to users; also the number lost+found occupies.</summary>
    public const uint FirstInode = 11;

    public const int InodeSize = 256;
    private const ulong BytesPerInode = 16384;

    /// <summary>Group descriptors are 32 bytes without the 64bit feature.</summary>
    public const int GroupDescriptorSize = 32;

    private ExtLayout() { }

    public FileSystemType FileSystem { get; private init; }
    public uint BlockSize { get; private init; }
    public uint TotalBlocks { get; private init; }
    public uint FirstDataBlock { get; private init; }
    public uint BlocksPerGroup { get; private init; }
    public uint InodesPerGroup { get; private init; }
    public uint GroupCount { get; private init; }
    public uint InodeCount { get; private init; }
    public uint InodeTableBlocksPerGroup { get; private init; }
    public uint GroupDescriptorBlocks { get; private init; }

    /// <summary>Journal length in blocks; 0 when the filesystem has no journal (ext2).</summary>
    public uint JournalBlocks { get; private init; }

    /// <summary>Blocks occupied by lost+found (mke2fs grows it towards 16 KiB).</summary>
    public uint LostFoundBlocks { get; private init; }

    public bool HasJournal => JournalBlocks > 0;
    public bool UsesExtents => FileSystem == FileSystemType.Ext4;
    public ulong SizeBytes => (ulong)TotalBlocks * BlockSize;

    /// <summary>
    /// Works out the geometry for a volume of <paramref name="sizeBytes"/>. Throws when the volume is
    /// too small to hold a filesystem at all — callers gate on
    /// <see cref="FileSystemTypeExtensions.MinimumSizeBytes"/> long before this.
    /// </summary>
    public static ExtLayout Compute(ulong sizeBytes, FileSystemType fileSystem)
    {
        if (!IsExt(fileSystem))
            throw new ArgumentException($"{fileSystem} is not an ext filesystem.", nameof(fileSystem));

        // 1 KiB blocks for very small volumes, matching mke2fs; 4 KiB everywhere else.
        var blockSize = sizeBytes < 3UL * 1024 * 1024 ? 1024u : 4096u;
        var totalBlocks = (uint)Math.Min(sizeBytes / blockSize, uint.MaxValue - 1);

        // With 1 KiB blocks the superblock lives in block 1, so block 0 is not part of the data area.
        var firstDataBlock = blockSize == 1024 ? 1u : 0u;
        if (totalBlocks <= firstDataBlock + 8)
            throw new ArgumentException($"{Bytes(sizeBytes)} is too small for an ext filesystem.");

        var blocksPerGroup = blockSize * 8;
        var groupCount = CeilDiv(totalBlocks - firstDataBlock, blocksPerGroup);

        // One inode per 16 KiB, then rounded so every group holds the same whole number of inode-table
        // blocks — the inode table cannot straddle a block boundary.
        var inodesPerBlock = blockSize / InodeSize;
        var wanted = Math.Max(sizeBytes / BytesPerInode, 16);
        var inodesPerGroup = (uint)Math.Max(CeilDiv((uint)Math.Min(wanted, uint.MaxValue), groupCount), 16);
        inodesPerGroup = RoundUp(inodesPerGroup, inodesPerBlock);
        inodesPerGroup = Math.Min(inodesPerGroup, blockSize * 8); // the inode bitmap is one block

        var inodeTableBlocks = CeilDiv(inodesPerGroup * (uint)InodeSize, blockSize);
        var gdtBlocks = CeilDiv(groupCount * (uint)GroupDescriptorSize, blockSize);
        var lostFound = LostFoundSize(blockSize);

        // Everything this formatter allocates lives in group 0, so the journal has to fit in what is
        // left of it. Capping is a deliberate simplification: allocating the journal across groups
        // would complicate every free-block count for, at worst, a couple of percent less journal.
        var blocksInGroup0 = Math.Min(blocksPerGroup, totalBlocks - firstDataBlock);
        var metadata0 = 1 + gdtBlocks + 2 + inodeTableBlocks;   // group 0 always carries a superblock
        var spare = blocksInGroup0 > metadata0 + 1 + lostFound
            ? blocksInGroup0 - metadata0 - 1 - lostFound
            : 0;

        var desired = fileSystem == FileSystemType.Ext2 ? 0 : DefaultJournalBlocks(totalBlocks);
        var journal = FitJournal(desired, spare, blockSize, fileSystem == FileSystemType.Ext4);

        return new ExtLayout
        {
            FileSystem = fileSystem,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            FirstDataBlock = firstDataBlock,
            BlocksPerGroup = blocksPerGroup,
            InodesPerGroup = inodesPerGroup,
            GroupCount = groupCount,
            InodeCount = inodesPerGroup * groupCount,
            InodeTableBlocksPerGroup = inodeTableBlocks,
            GroupDescriptorBlocks = gdtBlocks,
            LostFoundBlocks = lostFound,
            JournalBlocks = journal
        };
    }

    /// <summary>
    /// Largest journal that fits in <paramref name="spare"/> blocks once its own block map is counted.
    /// JBD2 refuses a journal under 1024 blocks, so anything smaller becomes no journal at all — a
    /// valid configuration (ext4 without has_journal) and far better than an invalid one.
    /// </summary>
    internal static uint FitJournal(uint desired, uint spare, uint blockSize, bool usesExtents)
    {
        if (desired == 0) return 0;

        var journal = Math.Min(desired, spare);
        for (var i = 0; i < 4; i++)
        {
            var overhead = JournalMapBlocks(journal, blockSize, usesExtents);
            if (journal + overhead <= spare) break;
            journal = spare > overhead ? spare - overhead : 0;
        }

        return journal < 1024 ? 0 : journal;
    }

    /// <summary>
    /// Extra blocks needed to *map* a journal of <paramref name="blocks"/> blocks. Extents map any
    /// contiguous run from inside the inode, so they cost nothing; without them the classic
    /// direct/indirect/double-indirect chain is required.
    /// </summary>
    internal static uint JournalMapBlocks(uint blocks, uint blockSize, bool usesExtents)
    {
        if (usesExtents || blocks <= 12) return 0;

        var pointersPerBlock = blockSize / 4;
        var remaining = blocks - 12;

        uint overhead = 1;                                  // the single-indirect block
        if (remaining <= pointersPerBlock) return overhead;

        remaining -= pointersPerBlock;
        overhead += 1;                                      // the double-indirect block
        overhead += CeilDiv(remaining, pointersPerBlock);   // indirect blocks hanging off it
        return overhead;
    }

    public static bool IsExt(FileSystemType fs)
        => fs is FileSystemType.Ext2 or FileSystemType.Ext3 or FileSystemType.Ext4;

    /// <summary>First block of a group, in filesystem block numbers.</summary>
    public uint GroupStart(uint group) => FirstDataBlock + group * BlocksPerGroup;

    /// <summary>Blocks in a group — the last group is usually short.</summary>
    public uint BlocksInGroup(uint group)
    {
        var start = GroupStart(group);
        return Math.Min(BlocksPerGroup, TotalBlocks - start);
    }

    /// <summary>
    /// Whether a group carries a superblock + descriptor-table backup. With sparse_super that is
    /// groups 0 and 1 and every power of 3, 5 and 7 — the rule readers rely on to find the backups.
    /// </summary>
    public static bool HasSuperBackup(uint group)
    {
        if (group is 0 or 1) return true;
        return IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);
    }

    /// <summary>Metadata blocks a group spends on its own superblock/descriptor backup.</summary>
    public uint SuperBackupBlocks(uint group) => HasSuperBackup(group) ? 1 + GroupDescriptorBlocks : 0;

    public uint BlockBitmapBlock(uint group) => GroupStart(group) + SuperBackupBlocks(group);
    public uint InodeBitmapBlock(uint group) => BlockBitmapBlock(group) + 1;
    public uint InodeTableBlock(uint group) => InodeBitmapBlock(group) + 1;

    /// <summary>First block in a group available for file data.</summary>
    public uint FirstUsableBlock(uint group) => InodeTableBlock(group) + InodeTableBlocksPerGroup;

    /// <summary>Every block a group reserves for metadata before any data can be stored.</summary>
    public uint MetadataBlocks(uint group) => SuperBackupBlocks(group) + 2 + InodeTableBlocksPerGroup;

    /// <summary>Byte offset of a block from the start of the filesystem.</summary>
    public long BlockOffset(uint block) => (long)block * BlockSize;

    /// <summary>
    /// mke2fs' journal sizing: big enough to be useful, never so big it dominates a small volume.
    /// </summary>
    internal static uint DefaultJournalBlocks(uint totalBlocks) => totalBlocks switch
    {
        < 2048 => 0,           // too small to be worth a journal
        < 32768 => 1024,
        < 256 * 1024 => 4096,
        < 512 * 1024 => 8192,
        < 1024 * 1024 => 16384,
        _ => 32768
    };

    /// <summary>lost+found grows towards 16 KiB but never past the 12 direct blocks.</summary>
    internal static uint LostFoundSize(uint blockSize)
    {
        uint blocks = 1;
        while (blocks < 12 && (ulong)blocks * blockSize < 16 * 1024) blocks++;
        return blocks;
    }

    internal static bool IsPowerOf(uint value, uint radix)
    {
        if (value == 0) return false;
        while (value % radix == 0) value /= radix;
        return value == 1;
    }

    private static uint CeilDiv(uint a, uint b) => (a + b - 1) / b;
    private static uint RoundUp(uint value, uint multiple) => CeilDiv(value, multiple) * multiple;

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
