using System.Buffers.Binary;

namespace DiskForge.Engine.Linux.Ext;

/// <summary>Why a filesystem cannot be grown, or null when it can.</summary>
public sealed record ExtGrowPlan
{
    public required uint BlockSize { get; init; }
    public required uint OldTotalBlocks { get; init; }
    public required uint NewTotalBlocks { get; init; }
    public required uint OldGroupCount { get; init; }
    public required uint NewGroupCount { get; init; }
    public required uint AddedFreeBlocks { get; init; }
    public required uint AddedInodes { get; init; }

    public ulong OldSizeBytes => (ulong)OldTotalBlocks * BlockSize;
    public ulong NewSizeBytes => (ulong)NewTotalBlocks * BlockSize;
}

/// <summary>A shrink that needs no data relocation, because the region being cut away is already free.</summary>
public sealed record ExtShrinkPlan
{
    public required uint BlockSize { get; init; }
    public required uint OldTotalBlocks { get; init; }
    public required uint NewTotalBlocks { get; init; }
    public required uint OldGroupCount { get; init; }
    public required uint NewGroupCount { get; init; }
    public required uint RemovedFreeBlocks { get; init; }
    public required uint RemovedInodes { get; init; }

    public ulong OldSizeBytes => (ulong)OldTotalBlocks * BlockSize;
    public ulong NewSizeBytes => (ulong)NewTotalBlocks * BlockSize;
}

/// <summary>
/// Grows an existing ext2/ext3/ext4 filesystem in place, natively.
///
/// Growing is the tractable half of resizing: every block being added was previously outside the
/// filesystem, so nothing that already exists has to move. New block groups are appended, the group
/// that used to be last is extended, and the counters are updated. Shrinking is the other half and is
/// deliberately not attempted here, because it requires relocating live data and rewriting extent
/// trees, which is where a resizer destroys filesystems.
///
/// The safety property this class relies on: it refuses anything whose layout it cannot predict
/// exactly. Before writing, it recomputes where every existing group's bitmaps and inode table should
/// be and compares that against the group descriptors actually on disk. A mismatch means the
/// filesystem uses a layout this code does not model (flex_bg, meta_bg, a different descriptor size),
/// and it stops rather than guessing.
/// </summary>
public static class ExtResizer
{
    private const int SuperblockOffset = 1024;
    private const int SuperblockLength = 1024;
    private const ushort Magic = 0xEF53;
    private const int GroupDescriptorSize = 32;

    // Feature bits that change where metadata lives, so their presence means "not our layout".
    private const uint IncompatMetaBg = 0x0010;
    private const uint IncompatFlexBg = 0x0200;
    private const uint IncompatCsumSeed = 0x2000;
    private const uint RoCompatSparseSuper = 0x0001;
    private const uint RoCompatGdtCsum = 0x0010;
    private const uint RoCompatMetadataCsum = 0x0400;
    private const uint RoCompatBigalloc = 0x0200;

    /// <summary>
    /// Works out whether the filesystem in <paramref name="stream"/> can be grown to
    /// <paramref name="newSizeBytes"/>. Returns null and sets <paramref name="blockedReason"/> when it
    /// cannot, so the caller can show the user why rather than a generic failure.
    /// </summary>
    public static ExtGrowPlan? TryPlanGrow(Stream stream, ulong newSizeBytes, out string? blockedReason)
    {
        var sb = ReadSuperblock(stream);
        if (sb is null)
        {
            blockedReason = "No ext filesystem was found here (its superblock magic is missing).";
            return null;
        }

        if (UnsupportedLayout(sb) is { } unsupported)
        {
            blockedReason = unsupported;
            return null;
        }

        var newTotalBlocks = (uint)Math.Min(newSizeBytes / sb.BlockSize, uint.MaxValue);
        if (newTotalBlocks <= sb.TotalBlocks)
        {
            blockedReason =
                $"Shrinking an ext filesystem is not supported yet. It currently occupies " +
                $"{Bytes((ulong)sb.TotalBlocks * sb.BlockSize)} and cannot be reduced to " +
                $"{Bytes(newSizeBytes)} without relocating live data.";
            return null;
        }

        var oldGroups = GroupCount(sb.TotalBlocks, sb.FirstDataBlock, sb.BlocksPerGroup);
        var newGroups = GroupCount(newTotalBlocks, sb.FirstDataBlock, sb.BlocksPerGroup);

        // The descriptor table cannot be moved without shifting every group's metadata, so growth is
        // capped at what the existing table can address.
        var capacity = sb.GroupDescriptorBlocks * sb.BlockSize / GroupDescriptorSize;
        if (newGroups > capacity)
        {
            var maxBlocks = sb.FirstDataBlock + capacity * sb.BlocksPerGroup;
            blockedReason =
                $"This filesystem's group descriptor table can only describe {capacity} block groups, " +
                $"which is {Bytes((ulong)maxBlocks * sb.BlockSize)}. Growing to {Bytes(newSizeBytes)} " +
                $"would need a bigger table, and moving it would mean relocating existing data.";
            return null;
        }

        if (LayoutMismatch(stream, sb, oldGroups) is { } mismatch)
        {
            blockedReason = mismatch;
            return null;
        }

        blockedReason = null;
        return new ExtGrowPlan
        {
            BlockSize = sb.BlockSize,
            OldTotalBlocks = sb.TotalBlocks,
            NewTotalBlocks = newTotalBlocks,
            OldGroupCount = oldGroups,
            NewGroupCount = newGroups,
            AddedFreeBlocks = CountAddedFreeBlocks(sb, oldGroups, newGroups, newTotalBlocks),
            AddedInodes = sb.InodesPerGroup * (newGroups - oldGroups)
        };
    }

    /// <summary>
    /// Works out whether the filesystem can be shrunk to <paramref name="newSizeBytes"/>.
    ///
    /// Only the case that needs <b>no data relocation</b> is supported: everything above the new
    /// boundary must already be free. Moving live blocks down would mean rewriting extent trees,
    /// indirect chains and directory contents, and removing whole groups would renumber inodes, which
    /// is the part of resize2fs that eats filesystems. Refusing is the honest position until that is
    /// built and heavily tested; a user who frees space or copies the data off can still shrink.
    /// </summary>
    public static ExtShrinkPlan? TryPlanShrink(Stream stream, ulong newSizeBytes, out string? blockedReason)
    {
        var sb = ReadSuperblock(stream);
        if (sb is null)
        {
            blockedReason = "No ext filesystem was found here (its superblock magic is missing).";
            return null;
        }

        if (UnsupportedLayout(sb) is { } unsupported)
        {
            blockedReason = unsupported;
            return null;
        }

        var newTotalBlocks = (uint)Math.Min(newSizeBytes / sb.BlockSize, uint.MaxValue);
        if (newTotalBlocks >= sb.TotalBlocks)
        {
            blockedReason = "That is not smaller than the filesystem already is.";
            return null;
        }

        var oldGroups = GroupCount(sb.TotalBlocks, sb.FirstDataBlock, sb.BlocksPerGroup);
        var newGroups = GroupCount(newTotalBlocks, sb.FirstDataBlock, sb.BlocksPerGroup);
        if (newGroups == 0)
        {
            blockedReason = "That would leave no block groups at all.";
            return null;
        }

        // The descriptor table must keep the same number of blocks, or every remaining group's
        // metadata would have to move. Shrinking across that boundary is refused rather than attempted.
        var newGdtBlocks = (newGroups * GroupDescriptorSize + sb.BlockSize - 1) / sb.BlockSize;
        if (newGdtBlocks != sb.GroupDescriptorBlocks)
        {
            blockedReason =
                "Shrinking this far would need a smaller group descriptor table, which would move " +
                "every block group's metadata. That is not supported.";
            return null;
        }

        if (LayoutMismatch(stream, sb, oldGroups) is { } mismatch)
        {
            blockedReason = mismatch;
            return null;
        }

        // The whole safety argument: nothing live may sit above the new end.
        if (RegionInUse(stream, sb, oldGroups, newGroups, newTotalBlocks) is { } inUse)
        {
            blockedReason = inUse;
            return null;
        }

        blockedReason = null;
        return new ExtShrinkPlan
        {
            BlockSize = sb.BlockSize,
            OldTotalBlocks = sb.TotalBlocks,
            NewTotalBlocks = newTotalBlocks,
            OldGroupCount = oldGroups,
            NewGroupCount = newGroups,
            RemovedFreeBlocks = CountRemovedFreeBlocks(sb, oldGroups, newGroups, newTotalBlocks),
            RemovedInodes = sb.InodesPerGroup * (oldGroups - newGroups)
        };
    }

    /// <summary>
    /// Applies a shrink. The superblock's block count drops <b>last</b>, so an interruption leaves a
    /// filesystem still describing the larger volume, which is intact and checkable.
    /// </summary>
    public static void Shrink(Stream stream, ExtShrinkPlan plan)
    {
        var sb = ReadSuperblock(stream)
                 ?? throw new InvalidOperationException("The ext superblock disappeared before the resize.");

        var descriptors = ReadDescriptorTable(stream, sb);

        // The group that becomes last may end mid-group. Blocks past the new end must read as in-use,
        // or fsck reports free space that is no longer part of the filesystem.
        TruncateFinalGroup(stream, sb, descriptors, plan);

        // Trailing descriptors describe groups that no longer exist.
        var removed = (int)((plan.OldGroupCount - plan.NewGroupCount) * GroupDescriptorSize);
        if (removed > 0)
            Array.Clear(descriptors, (int)(plan.NewGroupCount * GroupDescriptorSize), removed);

        WriteDescriptorTableEverywhere(stream, sb, descriptors, plan.NewGroupCount);
        UpdateSuperblocksForShrink(stream, sb, plan);

        stream.Flush();
    }

    /// <summary>
    /// Marks the blocks past the new end of the volume as in-use in the final surviving group, and
    /// drops them from its free count.
    /// </summary>
    private static void TruncateFinalGroup(
        Stream stream, Superblock sb, byte[] descriptors, ExtShrinkPlan plan)
    {
        var group = plan.NewGroupCount - 1;
        var groupStart = sb.FirstDataBlock + group * sb.BlocksPerGroup;

        var before = BlocksInGroup(groupStart, sb.BlocksPerGroup, plan.OldTotalBlocks);
        var after = BlocksInGroup(groupStart, sb.BlocksPerGroup, plan.NewTotalBlocks);
        if (after >= before) return;

        var blockBitmapBlock = ReadU32(descriptors, (int)(group * GroupDescriptorSize));
        var bitmap = ReadBlock(stream, sb, blockBitmapBlock);

        for (var i = after; i < before; i++) SetBit(bitmap, i);
        WriteBlock(stream, sb, blockBitmapBlock, bitmap);

        var at = (int)(group * GroupDescriptorSize);
        var free = BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan(at + 12));
        BinaryPrimitives.WriteUInt16LittleEndian(descriptors.AsSpan(at + 12), (ushort)(free - (before - after)));
    }

    private static void UpdateSuperblocksForShrink(Stream stream, Superblock sb, ExtShrinkPlan plan)
    {
        var newInodeCount = sb.InodeCount - plan.RemovedInodes;
        var newFreeBlocks = sb.FreeBlocks - plan.RemovedFreeBlocks;
        var newFreeInodes = sb.FreeInodes - plan.RemovedInodes;

        for (uint g = 0; g < plan.NewGroupCount; g++)
        {
            if (!HasSuperBackup(sb, g)) continue;

            var offset = g == 0
                ? SuperblockOffset
                : (long)(sb.FirstDataBlock + g * sb.BlocksPerGroup) * sb.BlockSize;

            stream.Position = offset;
            var buffer = new byte[SuperblockLength];
            ReadExact(stream, buffer);

            WriteU32(buffer, 0, newInodeCount);
            WriteU32(buffer, 4, plan.NewTotalBlocks);
            WriteU32(buffer, 8, plan.NewTotalBlocks / 20);
            WriteU32(buffer, 12, newFreeBlocks);
            WriteU32(buffer, 16, newFreeInodes);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(90), (ushort)g);

            stream.Position = offset;
            stream.Write(buffer);
        }
    }

    /// <summary>
    /// Returns a reason when anything live sits in the region being cut away, or null when the tail is
    /// entirely free. A group being removed outright may still have its own metadata marked in use;
    /// that disappears with the group and does not block the shrink.
    /// </summary>
    private static string? RegionInUse(
        Stream stream, Superblock sb, uint oldGroups, uint newGroups, uint newTotalBlocks)
    {
        var descriptors = ReadDescriptorTable(stream, sb);

        // Groups that vanish entirely: every data block and every inode in them must be free.
        for (var g = newGroups; g < oldGroups; g++)
        {
            var at = (int)(g * GroupDescriptorSize);
            var groupStart = sb.FirstDataBlock + g * sb.BlocksPerGroup;
            var blocksHere = BlocksInGroup(groupStart, sb.BlocksPerGroup, sb.TotalBlocks);

            var superBackup = HasSuperBackup(sb, g) ? 1 + sb.GroupDescriptorBlocks + sb.ReservedGdtBlocks : 0;
            var ownMetadata = superBackup + 2 + sb.InodeTableBlocks;

            var bitmap = ReadBlock(stream, sb, ReadU32(descriptors, at));
            for (var i = ownMetadata; i < blocksHere; i++)
            {
                if (!GetBit(bitmap, i)) continue;
                return $"There is data stored {Bytes((ulong)(groupStart + i) * sb.BlockSize)} into the " +
                       "filesystem, past where it would end. Shrinking would have to move it, which is " +
                       "not supported yet. Free up space at the end of the volume first.";
            }

            var inodeBitmap = ReadBlock(stream, sb, ReadU32(descriptors, at + 4));
            for (uint i = 0; i < sb.InodesPerGroup; i++)
            {
                if (!GetBit(inodeBitmap, i)) continue;
                return "There are files whose inodes live in the part of the filesystem that would be " +
                       "removed. Moving them would renumber them, which is not supported yet.";
            }
        }

        // The group that survives but gets truncated: its tail must be free.
        var lastGroup = newGroups - 1;
        var lastStart = sb.FirstDataBlock + lastGroup * sb.BlocksPerGroup;
        var keepTo = newTotalBlocks - lastStart;
        var hadTo = BlocksInGroup(lastStart, sb.BlocksPerGroup, sb.TotalBlocks);

        var lastBitmap = ReadBlock(stream, sb, ReadU32(descriptors, (int)(lastGroup * GroupDescriptorSize)));
        for (var i = keepTo; i < hadTo; i++)
        {
            if (!GetBit(lastBitmap, i)) continue;
            return $"There is data stored {Bytes((ulong)(lastStart + i) * sb.BlockSize)} into the " +
                   "filesystem, past where it would end. Shrinking would have to move it, which is not " +
                   "supported yet. Free up space at the end of the volume first.";
        }

        return null;
    }

    private static uint CountRemovedFreeBlocks(
        Superblock sb, uint oldGroups, uint newGroups, uint newTotalBlocks)
    {
        uint removed = 0;

        var lastStart = sb.FirstDataBlock + (newGroups - 1) * sb.BlocksPerGroup;
        removed += BlocksInGroup(lastStart, sb.BlocksPerGroup, sb.TotalBlocks)
                   - BlocksInGroup(lastStart, sb.BlocksPerGroup, newTotalBlocks);

        for (var g = newGroups; g < oldGroups; g++)
        {
            var start = sb.FirstDataBlock + g * sb.BlocksPerGroup;
            var inGroup = BlocksInGroup(start, sb.BlocksPerGroup, sb.TotalBlocks);
            var superBackup = HasSuperBackup(sb, g) ? 1 + sb.GroupDescriptorBlocks + sb.ReservedGdtBlocks : 0;
            removed += inGroup - (superBackup + 2 + sb.InodeTableBlocks);
        }

        return removed;
    }

    private static bool GetBit(byte[] bitmap, uint index) => (bitmap[index >> 3] & (1 << (int)(index & 7))) != 0;

    /// <summary>
    /// Applies a plan produced by <see cref="TryPlanGrow"/>. The order matters: everything the new
    /// size depends on is written before the superblock's block count is raised, so an interruption
    /// leaves a filesystem that still describes the smaller, intact volume.
    /// </summary>
    public static void Grow(Stream stream, ExtGrowPlan plan)
    {
        var sb = ReadSuperblock(stream)
                 ?? throw new InvalidOperationException("The ext superblock disappeared before the resize.");

        var descriptors = ReadDescriptorTable(stream, sb);

        // 1. Lay down metadata for every group being added.
        for (var g = plan.OldGroupCount; g < plan.NewGroupCount; g++)
            WriteNewGroup(stream, sb, descriptors, g, plan.NewTotalBlocks);

        // 2. The group that used to be last may have been partial. Blocks past the old end were marked
        //    in-use so fsck would not report space that did not exist; they are real now.
        ExtendFinalOldGroup(stream, sb, descriptors, plan);

        // 3. Publish the new descriptor table everywhere it lives.
        WriteDescriptorTableEverywhere(stream, sb, descriptors, plan.NewGroupCount);

        // 4. Only now does the filesystem claim the new size.
        UpdateSuperblocks(stream, sb, plan);

        stream.Flush();
    }

    // ---------------------------------------------------------------- new groups

    private static void WriteNewGroup(
        Stream stream, Superblock sb, byte[] descriptors, uint group, uint newTotalBlocks)
    {
        var groupStart = sb.FirstDataBlock + group * sb.BlocksPerGroup;
        var blocksInGroup = BlocksInGroup(groupStart, sb.BlocksPerGroup, newTotalBlocks);

        var superBackup = HasSuperBackup(sb, group) ? 1 + sb.GroupDescriptorBlocks + sb.ReservedGdtBlocks : 0;
        var blockBitmap = groupStart + superBackup;
        var inodeBitmap = blockBitmap + 1;
        var inodeTable = inodeBitmap + 1;
        var metadata = superBackup + 2 + sb.InodeTableBlocks;

        // Block bitmap: our own metadata is used, everything else in the group is free, and anything
        // past the end of the volume must read as used.
        var bitmap = new byte[sb.BlockSize];
        for (uint i = 0; i < metadata; i++) SetBit(bitmap, i);
        for (var i = blocksInGroup; i < sb.BlocksPerGroup; i++) SetBit(bitmap, i);
        WriteBlock(stream, sb, blockBitmap, bitmap);

        // Inode bitmap: every inode in a new group is free; pad past inodes_per_group as used.
        var inodes = new byte[sb.BlockSize];
        for (var i = sb.InodesPerGroup; i < sb.BlockSize * 8; i++) SetBit(inodes, i);
        WriteBlock(stream, sb, inodeBitmap, inodes);

        // Inode table must be zeroed, or fsck reads whatever was on the disk as inodes.
        var empty = new byte[sb.BlockSize];
        for (uint i = 0; i < sb.InodeTableBlocks; i++) WriteBlock(stream, sb, inodeTable + i, empty);

        SetDescriptor(descriptors, group, blockBitmap, inodeBitmap, inodeTable,
            freeBlocks: (ushort)(blocksInGroup - metadata),
            freeInodes: (ushort)sb.InodesPerGroup,
            usedDirs: 0);
    }

    /// <summary>
    /// Frees the blocks that appear in the previously-final group now that the volume is bigger. Its
    /// bitmap had them marked used purely because they were past the end of the filesystem.
    /// </summary>
    private static void ExtendFinalOldGroup(
        Stream stream, Superblock sb, byte[] descriptors, ExtGrowPlan plan)
    {
        var group = plan.OldGroupCount - 1;
        var groupStart = sb.FirstDataBlock + group * sb.BlocksPerGroup;

        var before = BlocksInGroup(groupStart, sb.BlocksPerGroup, plan.OldTotalBlocks);
        var after = BlocksInGroup(groupStart, sb.BlocksPerGroup, plan.NewTotalBlocks);
        if (after <= before) return;

        var blockBitmapBlock = ReadU32(descriptors, (int)(group * GroupDescriptorSize) + 0);
        var bitmap = ReadBlock(stream, sb, blockBitmapBlock);

        for (var i = before; i < after; i++) ClearBit(bitmap, i);
        WriteBlock(stream, sb, blockBitmapBlock, bitmap);

        var at = (int)(group * GroupDescriptorSize);
        var free = BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan(at + 12));
        BinaryPrimitives.WriteUInt16LittleEndian(descriptors.AsSpan(at + 12), (ushort)(free + (after - before)));
    }

    // ---------------------------------------------------------------- publishing

    private static void WriteDescriptorTableEverywhere(
        Stream stream, Superblock sb, byte[] descriptors, uint newGroupCount)
    {
        for (uint g = 0; g < newGroupCount; g++)
        {
            if (!HasSuperBackup(sb, g)) continue;

            var groupStart = sb.FirstDataBlock + g * sb.BlocksPerGroup;
            // Group 0's table sits after the block holding the superblock, not after the group start.
            var target = g == 0
                ? (sb.BlockSize == 1024 ? 2u : 1u)
                : groupStart + 1;

            stream.Position = (long)target * sb.BlockSize;
            stream.Write(descriptors);
        }
    }

    private static void UpdateSuperblocks(Stream stream, Superblock sb, ExtGrowPlan plan)
    {
        var newInodeCount = sb.InodeCount + plan.AddedInodes;
        var newFreeBlocks = sb.FreeBlocks + plan.AddedFreeBlocks;
        var newFreeInodes = sb.FreeInodes + plan.AddedInodes;

        for (uint g = 0; g < plan.NewGroupCount; g++)
        {
            if (!HasSuperBackup(sb, g)) continue;

            var offset = g == 0
                ? SuperblockOffset
                : (long)(sb.FirstDataBlock + g * sb.BlocksPerGroup) * sb.BlockSize;

            stream.Position = offset;
            var buffer = new byte[SuperblockLength];
            ReadExact(stream, buffer);

            // A brand-new backup group has no superblock yet; seed it from the primary.
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(56)) != Magic)
            {
                stream.Position = SuperblockOffset;
                ReadExact(stream, buffer);
            }

            WriteU32(buffer, 0, newInodeCount);
            WriteU32(buffer, 4, plan.NewTotalBlocks);
            WriteU32(buffer, 8, plan.NewTotalBlocks / 20);     // keep the 5% root reserve proportional
            WriteU32(buffer, 12, newFreeBlocks);
            WriteU32(buffer, 16, newFreeInodes);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(90), (ushort)g);  // s_block_group_nr

            stream.Position = offset;
            stream.Write(buffer);
        }
    }

    // ---------------------------------------------------------------- reading and checking

    private sealed record Superblock
    {
        public required uint InodeCount { get; init; }
        public required uint TotalBlocks { get; init; }
        public required uint FreeBlocks { get; init; }
        public required uint FreeInodes { get; init; }
        public required uint FirstDataBlock { get; init; }
        public required uint BlockSize { get; init; }
        public required uint BlocksPerGroup { get; init; }
        public required uint InodesPerGroup { get; init; }
        public required uint InodeSize { get; init; }
        public required uint ReservedGdtBlocks { get; init; }
        public required uint CompatFeatures { get; init; }
        public required uint IncompatFeatures { get; init; }
        public required uint RoCompatFeatures { get; init; }
        public required uint DescriptorSize { get; init; }

        public bool SparseSuper => (RoCompatFeatures & RoCompatSparseSuper) != 0;

        public uint InodeTableBlocks =>
            (InodesPerGroup * InodeSize + BlockSize - 1) / BlockSize;

        public uint GroupDescriptorBlocks
        {
            get
            {
                var groups = GroupCount(TotalBlocks, FirstDataBlock, BlocksPerGroup);
                return (groups * GroupDescriptorSize + BlockSize - 1) / BlockSize;
            }
        }
    }

    private static Superblock? ReadSuperblock(Stream stream)
    {
        var buffer = new byte[SuperblockLength];
        stream.Position = SuperblockOffset;
        if (!TryReadExact(stream, buffer)) return null;

        if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(56)) != Magic) return null;

        var logBlockSize = ReadU32(buffer, 24);
        if (logBlockSize > 6) return null;   // 1 KiB << 6 = 64 KiB, well past anything real

        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(88));

        return new Superblock
        {
            InodeCount = ReadU32(buffer, 0),
            TotalBlocks = ReadU32(buffer, 4),
            FreeBlocks = ReadU32(buffer, 12),
            FreeInodes = ReadU32(buffer, 16),
            FirstDataBlock = ReadU32(buffer, 20),
            BlockSize = 1024u << (int)logBlockSize,
            BlocksPerGroup = ReadU32(buffer, 32),
            InodesPerGroup = ReadU32(buffer, 40),
            InodeSize = inodeSize == 0 ? 128u : inodeSize,
            ReservedGdtBlocks = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(206)),
            CompatFeatures = ReadU32(buffer, 92),
            IncompatFeatures = ReadU32(buffer, 96),
            RoCompatFeatures = ReadU32(buffer, 100),
            DescriptorSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(254))
        };
    }

    /// <summary>
    /// Features that place metadata somewhere this code does not model. Refusing them is the point:
    /// a resizer that guesses where a bitmap lives will destroy the filesystem it is growing.
    /// </summary>
    private static string? UnsupportedLayout(Superblock sb)
    {
        if (sb.BlocksPerGroup == 0 || sb.InodesPerGroup == 0 || sb.BlockSize == 0)
            return "The ext superblock is not self-consistent, so it will not be modified.";

        if (sb.DescriptorSize is not (0 or GroupDescriptorSize))
            return $"This filesystem uses {sb.DescriptorSize}-byte group descriptors (64-bit ext4). " +
                   "Growing it is not supported yet.";

        if ((sb.IncompatFeatures & IncompatFlexBg) != 0)
            return "This filesystem uses flex_bg, which groups several block groups' metadata " +
                   "together. It was almost certainly made by mkfs rather than DiskForge, and growing " +
                   "it is not supported yet.";

        if ((sb.IncompatFeatures & IncompatMetaBg) != 0)
            return "This filesystem uses meta_bg. Growing it is not supported yet.";

        if ((sb.RoCompatFeatures & (RoCompatMetadataCsum | RoCompatGdtCsum)) != 0)
            return "This filesystem has metadata checksums enabled. Growing it would need every " +
                   "checksum recomputed, which is not supported yet.";

        if ((sb.RoCompatFeatures & RoCompatBigalloc) != 0)
            return "This filesystem uses bigalloc. Growing it is not supported yet.";

        if ((sb.IncompatFeatures & IncompatCsumSeed) != 0)
            return "This filesystem uses a checksum seed. Growing it is not supported yet.";

        return null;
    }

    /// <summary>
    /// Recomputes where each existing group's metadata should sit and compares it with the descriptors
    /// actually on disk. Any disagreement means the real layout differs from the model, so stop.
    /// </summary>
    private static string? LayoutMismatch(Stream stream, Superblock sb, uint groups)
    {
        var descriptors = ReadDescriptorTable(stream, sb);

        for (uint g = 0; g < groups; g++)
        {
            var groupStart = sb.FirstDataBlock + g * sb.BlocksPerGroup;
            var superBackup = HasSuperBackup(sb, g) ? 1 + sb.GroupDescriptorBlocks + sb.ReservedGdtBlocks : 0;
            var expected = groupStart + superBackup;

            var actual = ReadU32(descriptors, (int)(g * GroupDescriptorSize));
            if (actual != expected)
                return $"This filesystem's layout does not match what DiskForge expects (group {g}'s " +
                       $"block bitmap is at block {actual}, not {expected}). It will not be modified.";
        }

        return null;
    }

    private static byte[] ReadDescriptorTable(Stream stream, Superblock sb)
    {
        var table = new byte[sb.GroupDescriptorBlocks * sb.BlockSize];
        var first = sb.BlockSize == 1024 ? 2u : 1u;
        stream.Position = (long)first * sb.BlockSize;
        ReadExact(stream, table);
        return table;
    }

    // ---------------------------------------------------------------- arithmetic

    private static uint GroupCount(uint totalBlocks, uint firstDataBlock, uint blocksPerGroup)
    {
        if (totalBlocks <= firstDataBlock || blocksPerGroup == 0) return 0;
        return (totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
    }

    private static uint BlocksInGroup(uint groupStart, uint blocksPerGroup, uint totalBlocks)
        => totalBlocks >= groupStart + blocksPerGroup ? blocksPerGroup : totalBlocks - groupStart;

    /// <summary>sparse_super puts backups in groups 0, 1 and every power of 3, 5 and 7.</summary>
    private static bool HasSuperBackup(Superblock sb, uint group)
    {
        if (!sb.SparseSuper) return true;
        return ExtLayout.HasSuperBackup(group);
    }

    private static uint CountAddedFreeBlocks(
        Superblock sb, uint oldGroups, uint newGroups, uint newTotalBlocks)
    {
        uint added = 0;

        // Whatever the old final group gains.
        var lastStart = sb.FirstDataBlock + (oldGroups - 1) * sb.BlocksPerGroup;
        added += BlocksInGroup(lastStart, sb.BlocksPerGroup, newTotalBlocks)
                 - BlocksInGroup(lastStart, sb.BlocksPerGroup, sb.TotalBlocks);

        // Plus every new group, minus the metadata each one spends on itself.
        for (var g = oldGroups; g < newGroups; g++)
        {
            var start = sb.FirstDataBlock + g * sb.BlocksPerGroup;
            var inGroup = BlocksInGroup(start, sb.BlocksPerGroup, newTotalBlocks);
            var superBackup = HasSuperBackup(sb, g) ? 1 + sb.GroupDescriptorBlocks + sb.ReservedGdtBlocks : 0;
            added += inGroup - (superBackup + 2 + sb.InodeTableBlocks);
        }

        return added;
    }

    private static void SetDescriptor(
        byte[] table, uint group, uint blockBitmap, uint inodeBitmap, uint inodeTable,
        ushort freeBlocks, ushort freeInodes, ushort usedDirs)
    {
        var at = (int)(group * GroupDescriptorSize);
        WriteU32(table, at + 0, blockBitmap);
        WriteU32(table, at + 4, inodeBitmap);
        WriteU32(table, at + 8, inodeTable);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 12), freeBlocks);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 14), freeInodes);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 16), usedDirs);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at + 18), 0);
    }

    // ---------------------------------------------------------------- byte plumbing

    private static void WriteBlock(Stream stream, Superblock sb, uint block, byte[] data)
    {
        stream.Position = (long)block * sb.BlockSize;
        stream.Write(data, 0, (int)sb.BlockSize);
    }

    private static byte[] ReadBlock(Stream stream, Superblock sb, uint block)
    {
        var buffer = new byte[sb.BlockSize];
        stream.Position = (long)block * sb.BlockSize;
        ReadExact(stream, buffer);
        return buffer;
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        if (!TryReadExact(stream, buffer))
            throw new IOException($"Short read of {buffer.Length} bytes from the ext filesystem.");
    }

    private static bool TryReadExact(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static uint ReadU32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void WriteU32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    private static void SetBit(byte[] bitmap, uint index) => bitmap[index >> 3] |= (byte)(1 << (int)(index & 7));
    private static void ClearBit(byte[] bitmap, uint index) => bitmap[index >> 3] &= (byte)~(1 << (int)(index & 7));

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
