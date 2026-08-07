using System.Buffers.Binary;
using System.Text;
using DiskForge.Core.Operations;

namespace DiskForge.Engine.Linux.Ext;

/// <summary>
/// Writes an empty ext2/ext3/ext4 filesystem, natively, with no external tool and no WSL.
///
/// This exists because everything Windows can be asked to do for Linux filesystems goes through a
/// system we do not control — WSL's Hyper-V passthrough refuses removable media outright — and because
/// bundling e2fsprogs would put GPL redistribution obligations on the product. A fresh, empty
/// filesystem is a fully specified, deterministic structure, so writing it directly is both tractable
/// and far more predictable than orchestrating someone else's tooling.
///
/// Scope is deliberately narrow: it creates a <b>new, empty</b> filesystem. It does not read, repair,
/// resize or modify one. Correctness is checked against the real <c>e2fsck</c> in the test suite —
/// WSL is a test oracle here, never a runtime dependency.
///
/// Feature set: sparse_super, filetype, dir_index, resize_inode off, and (for ext3/ext4) a journal;
/// ext4 additionally uses extents. Metadata checksums are deliberately off — a valid, mountable
/// configuration that removes every checksum from the write path.
/// </summary>
public sealed class ExtFormatter
{
    // --- superblock ---
    private const ushort Magic = 0xEF53;
    private const ushort StateClean = 1;
    private const ushort ErrorsContinue = 1;
    private const uint RevisionDynamic = 1;
    private const ushort ResUid = 0;

    // --- feature flags ---
    private const uint CompatDirIndex = 0x0020;
    private const uint CompatHasJournal = 0x0004;
    private const uint IncompatFiletype = 0x0002;
    private const uint IncompatExtents = 0x0040;
    private const uint RoCompatSparseSuper = 0x0001;
    private const uint RoCompatLargeFile = 0x0002;

    // --- inode ---
    private const ushort ModeDirectory = 0x4000;
    private const ushort ModeRootPerms = 0x01ED;   // 0755
    private const ushort ModeLostFoundPerms = 0x01C0; // 0700
    private const uint InodeFlagExtents = 0x00080000;

    // --- directory entry file types ---
    private const byte FileTypeDirectory = 2;

    // --- journal (JBD2) ---
    private const uint JournalMagic = 0xC03B3998;
    private const uint JournalSuperblockV2 = 4;

    private readonly ExtLayout _layout;
    private readonly string _label;
    private readonly Guid _uuid;
    private readonly DateTimeOffset _now;

    public ExtFormatter(ExtLayout layout, string label = "", Guid? uuid = null, DateTimeOffset? now = null)
    {
        _layout = layout;
        _label = label ?? "";
        _uuid = uuid ?? Guid.NewGuid();
        _now = now ?? DateTimeOffset.UtcNow;
    }

    public ExtLayout Layout => _layout;
    public Guid Uuid => _uuid;

    /// <summary>Convenience: compute the geometry and write in one step.</summary>
    public static void Format(Stream target, ulong sizeBytes, FileSystemType fileSystem, string label = "")
        => new ExtFormatter(ExtLayout.Compute(sizeBytes, fileSystem), label).Write(target);

    /// <summary>
    /// Writes the filesystem to <paramref name="target"/>, which must be at least
    /// <see cref="ExtLayout.SizeBytes"/> long and positioned at the start of the volume.
    ///
    /// Only the blocks that actually hold structure are written. Everything else is left untouched, so
    /// the caller is responsible for the destination being zeroed where it matters (the block/inode
    /// bitmaps below explicitly mark every unused block free, so stale data is never referenced).
    /// </summary>
    public void Write(Stream target)
    {
        var l = _layout;

        // Block 0 holds the 1024-byte boot area then the primary superblock; with 1 KiB blocks the
        // superblock is block 1 instead. Either way it starts at byte 1024.
        WriteSuperblockAndDescriptors(target);
        WriteGroupMetadata(target);
        WriteInodeTable(target);
        WriteRootDirectory(target);
        WriteLostFound(target);
        if (l.HasJournal) WriteJournal(target);

        target.Flush();
    }

    // ------------------------------------------------------------------ superblock

    /// <summary>Primary superblock + descriptor table, and a backup copy in every sparse_super group.</summary>
    private void WriteSuperblockAndDescriptors(Stream target)
    {
        var l = _layout;
        var descriptors = BuildGroupDescriptors();

        for (uint group = 0; group < l.GroupCount; group++)
        {
            if (!ExtLayout.HasSuperBackup(group)) continue;

            var superblock = BuildSuperblock(group);
            var groupStart = l.GroupStart(group);

            if (group == 0)
            {
                // Primary: 1024 bytes of boot area first, then the superblock at byte 1024.
                target.Position = 1024;
                target.Write(superblock);

                // Descriptors begin in the block after the one holding the superblock.
                var descBlock = l.BlockSize == 1024 ? 2u : 1u;
                target.Position = l.BlockOffset(descBlock);
                target.Write(descriptors);
            }
            else
            {
                target.Position = l.BlockOffset(groupStart);
                target.Write(superblock);

                target.Position = l.BlockOffset(groupStart + 1);
                target.Write(descriptors);
            }
        }
    }

    /// <summary>
    /// The superblock. <paramref name="group"/> is stamped into s_block_group_nr so a backup knows
    /// which group it came from — e2fsck checks this.
    /// </summary>
    private byte[] BuildSuperblock(uint group)
    {
        var l = _layout;
        var sb = new byte[1024];

        var freeBlocks = CountFreeBlocks();
        var freeInodes = l.InodeCount - ExtLayout.FirstInode; // 1..10 reserved, 11 = lost+found
        var seconds = (uint)_now.ToUnixTimeSeconds();

        U32(sb, 0, l.InodeCount);                     // s_inodes_count
        U32(sb, 4, l.TotalBlocks);                    // s_blocks_count
        U32(sb, 8, l.TotalBlocks / 20);               // s_r_blocks_count (5% reserved for root)
        U32(sb, 12, freeBlocks);                      // s_free_blocks_count
        U32(sb, 16, freeInodes);                      // s_free_inodes_count
        U32(sb, 20, l.FirstDataBlock);                // s_first_data_block
        U32(sb, 24, Log2(l.BlockSize) - 10);          // s_log_block_size
        U32(sb, 28, Log2(l.BlockSize) - 10);          // s_log_cluster_size
        U32(sb, 32, l.BlocksPerGroup);                // s_blocks_per_group
        U32(sb, 36, l.BlocksPerGroup);                // s_clusters_per_group
        U32(sb, 40, l.InodesPerGroup);                // s_inodes_per_group
        U32(sb, 44, seconds);                         // s_mtime
        U32(sb, 48, seconds);                         // s_wtime
        U16(sb, 52, 0);                               // s_mnt_count
        U16(sb, 54, 0xFFFF);                          // s_max_mnt_count (-1 = no forced checks)
        U16(sb, 56, Magic);
        U16(sb, 58, StateClean);
        U16(sb, 60, ErrorsContinue);
        U16(sb, 62, 0);                               // s_minor_rev_level
        U32(sb, 64, seconds);                         // s_lastcheck
        U32(sb, 68, 0);                               // s_checkinterval
        U32(sb, 72, 0);                               // s_creator_os (0 = Linux)
        U32(sb, 76, RevisionDynamic);
        U16(sb, 80, ResUid);
        U16(sb, 82, ResUid);                          // s_def_resgid
        U32(sb, 84, ExtLayout.FirstInode);            // s_first_ino
        U16(sb, 88, ExtLayout.InodeSize);             // s_inode_size
        U16(sb, 90, (ushort)group);                   // s_block_group_nr
        U32(sb, 92, CompatFeatures());
        U32(sb, 96, IncompatFeatures());
        U32(sb, 100, RoCompatFeatures());
        WriteUuidBigEndian(sb, 104, _uuid);           // s_uuid
        WriteFixedAscii(sb, 120, 16, _label);         // s_volume_name
        U16(sb, 254, 0);                              // s_desc_size (0 => 32 bytes)
        U32(sb, 256, 0);                              // s_default_mount_opts
        U32(sb, 260, 0);                              // s_first_meta_bg

        // s_journal_inum alone means "the journal is inode 8 on this filesystem". s_journal_uuid at 208
        // must stay zero: setting it declares the journal to be on a *separate device*, which makes
        // e2fsck fail with "Can't find external journal".
        if (l.HasJournal) U32(sb, 224, ExtLayout.JournalInode);

        // s_min_extra_isize / s_want_extra_isize: 32 bytes of extra inode fields, matching mke2fs
        // for a 256-byte inode.
        U16(sb, 348, 32);
        U16(sb, 350, 32);

        return sb;
    }

    private uint CompatFeatures()
    {
        var f = CompatDirIndex;
        if (_layout.HasJournal) f |= CompatHasJournal;
        return f;
    }

    private uint IncompatFeatures()
    {
        var f = IncompatFiletype;
        if (_layout.UsesExtents) f |= IncompatExtents;
        return f;
    }

    private static uint RoCompatFeatures() => RoCompatSparseSuper | RoCompatLargeFile;

    /// <summary>The group descriptor table: 32 bytes per group, repeated in every backup location.</summary>
    private byte[] BuildGroupDescriptors()
    {
        var l = _layout;
        var table = new byte[l.GroupDescriptorBlocks * l.BlockSize];

        for (uint g = 0; g < l.GroupCount; g++)
        {
            var at = (int)(g * ExtLayout.GroupDescriptorSize);
            U32(table, at + 0, l.BlockBitmapBlock(g));
            U32(table, at + 4, l.InodeBitmapBlock(g));
            U32(table, at + 8, l.InodeTableBlock(g));
            U16(table, at + 12, (ushort)FreeBlocksInGroup(g));
            U16(table, at + 14, (ushort)FreeInodesInGroup(g));
            U16(table, at + 16, (ushort)(g == 0 ? 2 : 0)); // bg_used_dirs_count: / and lost+found
            U16(table, at + 18, 0);                        // bg_flags
        }

        return table;
    }

    // ------------------------------------------------------------------ bitmaps

    /// <summary>Writes each group's block and inode bitmaps.</summary>
    private void WriteGroupMetadata(Stream target)
    {
        var l = _layout;

        for (uint g = 0; g < l.GroupCount; g++)
        {
            var blockBitmap = new byte[l.BlockSize];
            var used = UsedBlocksInGroup(g);
            for (uint i = 0; i < used; i++) SetBit(blockBitmap, i);

            // Blocks past the end of the volume must read as in-use, or fsck reports them as free
            // space that does not exist.
            var inGroup = l.BlocksInGroup(g);
            for (var i = inGroup; i < l.BlocksPerGroup; i++) SetBit(blockBitmap, i);

            target.Position = l.BlockOffset(l.BlockBitmapBlock(g));
            target.Write(blockBitmap);

            var inodeBitmap = new byte[l.BlockSize];
            if (g == 0) for (uint i = 0; i < ExtLayout.FirstInode; i++) SetBit(inodeBitmap, i);

            // Same for inodes: the bitmap is a whole block, so pad past inodes_per_group as used.
            for (var i = l.InodesPerGroup; i < l.BlockSize * 8; i++) SetBit(inodeBitmap, i);

            target.Position = l.BlockOffset(l.InodeBitmapBlock(g));
            target.Write(inodeBitmap);
        }
    }

    /// <summary>Zeroes every inode table; the few live inodes are written afterwards.</summary>
    private void WriteInodeTable(Stream target)
    {
        var l = _layout;
        var empty = new byte[l.BlockSize];

        for (uint g = 0; g < l.GroupCount; g++)
        {
            target.Position = l.BlockOffset(l.InodeTableBlock(g));
            for (uint b = 0; b < l.InodeTableBlocksPerGroup; b++) target.Write(empty);
        }
    }

    // ------------------------------------------------------------------ directories

    private void WriteRootDirectory(Stream target)
    {
        var l = _layout;
        var block = RootDirectoryBlock();

        var data = new byte[l.BlockSize];
        var pos = WriteDirEntry(data, 0, ExtLayout.RootInode, ".", 12);
        pos = WriteDirEntry(data, pos, ExtLayout.RootInode, "..", 12);
        // The final entry's record length runs to the end of the block, as ext requires.
        WriteDirEntry(data, pos, ExtLayout.LostFoundInode, "lost+found", (ushort)(l.BlockSize - pos));

        target.Position = l.BlockOffset(block);
        target.Write(data);

        WriteInode(target, ExtLayout.RootInode, BuildDirectoryInode(
            (ushort)(ModeDirectory | ModeRootPerms), links: 3, firstBlock: block, blockCount: 1));
    }

    private void WriteLostFound(Stream target)
    {
        var l = _layout;
        var first = LostFoundFirstBlock();

        for (uint i = 0; i < l.LostFoundBlocks; i++)
        {
            var data = new byte[l.BlockSize];
            if (i == 0)
            {
                var pos = WriteDirEntry(data, 0, ExtLayout.LostFoundInode, ".", 12);
                WriteDirEntry(data, pos, ExtLayout.RootInode, "..", (ushort)(l.BlockSize - pos));
            }
            else
            {
                // An empty directory block is one unused entry spanning the whole block.
                WriteDirEntry(data, 0, 0, "", (ushort)l.BlockSize);
            }

            target.Position = l.BlockOffset(first + i);
            target.Write(data);
        }

        WriteInode(target, ExtLayout.LostFoundInode, BuildDirectoryInode(
            (ushort)(ModeDirectory | ModeLostFoundPerms), links: 2, firstBlock: first,
            blockCount: l.LostFoundBlocks));
    }

    /// <summary>
    /// A directory inode. ext4 maps its blocks with an extent tree (a single extent is enough for a
    /// contiguous run); ext2/ext3 use plain direct block pointers.
    /// </summary>
    private byte[] BuildDirectoryInode(ushort mode, ushort links, uint firstBlock, uint blockCount)
    {
        var l = _layout;
        var inode = new byte[ExtLayout.InodeSize];
        var seconds = (uint)_now.ToUnixTimeSeconds();

        U16(inode, 0, mode);
        U16(inode, 2, 0);                                   // i_uid
        U32(inode, 4, blockCount * l.BlockSize);            // i_size
        U32(inode, 8, seconds);                             // i_atime
        U32(inode, 12, seconds);                            // i_ctime
        U32(inode, 16, seconds);                            // i_mtime
        U32(inode, 20, 0);                                  // i_dtime
        U16(inode, 24, 0);                                  // i_gid
        U16(inode, 26, links);                              // i_links_count

        // i_blocks is counted in 512-byte sectors regardless of the filesystem's block size.
        U32(inode, 28, blockCount * (l.BlockSize / 512));

        if (l.UsesExtents)
        {
            U32(inode, 32, InodeFlagExtents);               // i_flags
            WriteSingleExtent(inode, 40, firstBlock, blockCount);
        }
        else
        {
            U32(inode, 32, 0);
            for (uint i = 0; i < blockCount && i < 12; i++)
                U32(inode, 40 + (int)i * 4, firstBlock + i); // i_block[0..11] direct pointers
        }

        U16(inode, 128, 32);                                // i_extra_isize
        return inode;
    }

    /// <summary>
    /// An extent header plus one extent covering a contiguous run — the whole tree fits in the
    /// inode's 60-byte i_block area, so there is no index block to write.
    /// </summary>
    private static void WriteSingleExtent(byte[] inode, int at, uint firstBlock, uint blockCount)
    {
        U16(inode, at + 0, 0xF30A);            // eh_magic
        U16(inode, at + 2, 1);                 // eh_entries
        U16(inode, at + 4, 4);                 // eh_max (fits in i_block)
        U16(inode, at + 6, 0);                 // eh_depth (0 = leaf)
        U32(inode, at + 8, 0);                 // eh_generation

        U32(inode, at + 12, 0);                // ee_block  (logical start)
        U16(inode, at + 16, (ushort)blockCount); // ee_len
        U16(inode, at + 18, 0);                // ee_start_hi
        U32(inode, at + 20, firstBlock);       // ee_start_lo
    }

    private void WriteInode(Stream target, uint inodeNumber, byte[] inode)
    {
        var l = _layout;
        var index = inodeNumber - 1;
        var group = index / l.InodesPerGroup;
        var indexInGroup = index % l.InodesPerGroup;

        target.Position = l.BlockOffset(l.InodeTableBlock(group)) + indexInGroup * ExtLayout.InodeSize;
        target.Write(inode);
    }

    /// <summary>Writes one directory entry, returning the offset of the next.</summary>
    private static int WriteDirEntry(byte[] block, int at, uint inode, string name, ushort recordLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        U32(block, at + 0, inode);
        U16(block, at + 4, recordLength);
        block[at + 6] = (byte)nameBytes.Length;
        block[at + 7] = inode == 0 ? (byte)0 : FileTypeDirectory;
        nameBytes.CopyTo(block, at + 8);
        return at + recordLength;
    }

    // ------------------------------------------------------------------ journal

    /// <summary>
    /// The journal is inode 8: a plain file of <see cref="ExtLayout.JournalBlocks"/> blocks whose first
    /// block is the JBD2 superblock and whose remainder is zeroed. An empty journal needs nothing else.
    /// Note JBD2 is <b>big-endian</b> on disk, unlike everything else in ext.
    /// </summary>
    private void WriteJournal(Stream target)
    {
        var l = _layout;
        var first = JournalFirstBlock();

        var superblock = new byte[l.BlockSize];
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(0, 4), JournalMagic);
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(4, 4), JournalSuperblockV2);
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(12, 4), l.BlockSize);      // s_blocksize
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(16, 4), l.JournalBlocks);  // s_maxlen
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(20, 4), 1);                // s_first
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(24, 4), 1);                // s_sequence
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(28, 4), 0);                // s_start
        // journal_superblock_t: s_uuid is at 48 (after the three feature words), s_nr_users at 64.
        WriteUuidBigEndian(superblock, 48, _uuid);                                         // s_uuid
        BinaryPrimitives.WriteUInt32BigEndian(superblock.AsSpan(64, 4), 1);                // s_nr_users

        target.Position = l.BlockOffset(first);
        target.Write(superblock);

        var empty = new byte[l.BlockSize];
        for (uint i = 1; i < l.JournalBlocks; i++)
        {
            target.Position = l.BlockOffset(first + i);
            target.Write(empty);
        }

        // The journal file itself, as inode 8.
        var inode = new byte[ExtLayout.InodeSize];
        var seconds = (uint)_now.ToUnixTimeSeconds();
        U16(inode, 0, 0x8180);                              // regular file, 0600
        U32(inode, 4, l.JournalBlocks * l.BlockSize);        // i_size
        U32(inode, 8, seconds);
        U32(inode, 12, seconds);
        U32(inode, 16, seconds);
        U16(inode, 26, 1);                                  // i_links_count
        U32(inode, 28, l.JournalBlocks * (l.BlockSize / 512));

        if (l.UsesExtents)
        {
            U32(inode, 32, InodeFlagExtents);
            WriteSingleExtent(inode, 40, first, l.JournalBlocks);
        }
        else
        {
            WriteIndirectBlockMap(target, inode, first, l.JournalBlocks);
        }

        U16(inode, 128, 32);
        WriteInode(target, ExtLayout.JournalInode, inode);
    }

    /// <summary>
    /// ext2/ext3 have no extents, so anything past 12 blocks needs the classic indirect chain:
    /// i_block[12] points at a block of pointers, and i_block[13] at a block of pointers to blocks of
    /// pointers. A single indirect block only reaches <c>blockSize/4</c> blocks (1024 at 4 KiB), which
    /// is nowhere near a 4096- or 32768-block journal — hence the double-indirect level.
    ///
    /// The map blocks are allocated immediately after the data they describe, and are counted by
    /// <see cref="ExtLayout.JournalMapBlocks"/> so the free-block totals stay honest.
    /// </summary>
    private void WriteIndirectBlockMap(Stream target, byte[] inode, uint firstBlock, uint blockCount)
    {
        var l = _layout;
        var pointersPerBlock = l.BlockSize / 4;

        var direct = Math.Min(blockCount, 12u);
        for (uint i = 0; i < direct; i++) U32(inode, 40 + (int)i * 4, firstBlock + i);

        var mapBlocks = ExtLayout.JournalMapBlocks(blockCount, l.BlockSize, usesExtents: false);
        U32(inode, 28, (blockCount + mapBlocks) * (l.BlockSize / 512)); // i_blocks counts the map too
        if (blockCount <= 12) return;

        var nextMapBlock = firstBlock + blockCount;
        var mapped = 12u;

        // --- single indirect: i_block[12] ---
        var singleIndirect = nextMapBlock++;
        var singleCount = Math.Min(blockCount - mapped, pointersPerBlock);
        WritePointerBlock(target, singleIndirect, firstBlock + mapped, singleCount);
        U32(inode, 40 + 12 * 4, singleIndirect);
        mapped += singleCount;
        if (mapped >= blockCount) return;

        // --- double indirect: i_block[13] ---
        var doubleIndirect = nextMapBlock++;
        var doubleMap = new byte[l.BlockSize];
        uint slot = 0;

        while (mapped < blockCount)
        {
            var indirect = nextMapBlock++;
            var count = Math.Min(blockCount - mapped, pointersPerBlock);
            WritePointerBlock(target, indirect, firstBlock + mapped, count);

            U32(doubleMap, (int)slot * 4, indirect);
            slot++;
            mapped += count;
        }

        target.Position = l.BlockOffset(doubleIndirect);
        target.Write(doubleMap);
        U32(inode, 40 + 13 * 4, doubleIndirect);
    }

    /// <summary>Writes a block of consecutive block pointers starting at <paramref name="from"/>.</summary>
    private void WritePointerBlock(Stream target, uint block, uint from, uint count)
    {
        var map = new byte[_layout.BlockSize];
        for (uint i = 0; i < count; i++) U32(map, (int)i * 4, from + i);

        target.Position = _layout.BlockOffset(block);
        target.Write(map);
    }

    // ------------------------------------------------------------------ allocation

    /// <summary>Root directory block: the first usable block of group 0.</summary>
    private uint RootDirectoryBlock() => _layout.FirstUsableBlock(0);

    private uint LostFoundFirstBlock() => RootDirectoryBlock() + 1;

    private uint JournalFirstBlock() => LostFoundFirstBlock() + _layout.LostFoundBlocks;

    /// <summary>Blocks group 0 hands to the root directory, lost+found and the journal (map included).</summary>
    private uint DataBlocksInGroupZero()
    {
        var l = _layout;
        var used = 1 + l.LostFoundBlocks;
        if (!l.HasJournal) return used;

        return used + l.JournalBlocks
               + ExtLayout.JournalMapBlocks(l.JournalBlocks, l.BlockSize, l.UsesExtents);
    }

    private uint UsedBlocksInGroup(uint group)
        => _layout.MetadataBlocks(group) + (group == 0 ? DataBlocksInGroupZero() : 0);

    private uint FreeBlocksInGroup(uint group)
        => _layout.BlocksInGroup(group) - UsedBlocksInGroup(group);

    private uint FreeInodesInGroup(uint group)
        => group == 0 ? _layout.InodesPerGroup - ExtLayout.FirstInode : _layout.InodesPerGroup;

    private uint CountFreeBlocks()
    {
        uint free = 0;
        for (uint g = 0; g < _layout.GroupCount; g++) free += FreeBlocksInGroup(g);
        return free;
    }

    // ------------------------------------------------------------------ primitives

    private static void U16(byte[] buffer, int at, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at, 2), value);

    private static void U32(byte[] buffer, int at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at, 4), value);

    /// <summary>
    /// Linux writes UUIDs in RFC 4122 byte order. .NET's <see cref="Guid.ToByteArray"/> emits the first
    /// three fields little-endian, so they have to be flipped back or every tool reports a different
    /// UUID than we intended.
    /// </summary>
    internal static void WriteUuidBigEndian(byte[] buffer, int at, Guid uuid)
    {
        var b = uuid.ToByteArray();
        buffer[at + 0] = b[3]; buffer[at + 1] = b[2]; buffer[at + 2] = b[1]; buffer[at + 3] = b[0];
        buffer[at + 4] = b[5]; buffer[at + 5] = b[4];
        buffer[at + 6] = b[7]; buffer[at + 7] = b[6];
        for (var i = 8; i < 16; i++) buffer[at + i] = b[i];
    }

    private static void WriteFixedAscii(byte[] buffer, int at, int length, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var n = Math.Min(bytes.Length, length);
        Array.Copy(bytes, 0, buffer, at, n);
    }

    private static void SetBit(byte[] bitmap, uint bit) => bitmap[bit / 8] |= (byte)(1 << (int)(bit % 8));

    private static uint Log2(uint value)
    {
        uint n = 0;
        while (value > 1) { value >>= 1; n++; }
        return n;
    }
}
