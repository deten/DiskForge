namespace DiskForge.Core.Model;

/// <summary>
/// Turns a set of real partitions into a contiguous disk map by inserting synthetic
/// <see cref="PartitionKind.Unallocated"/> regions for every usable gap, so the UI can draw a gap-free
/// proportional bar and offer "create partition" on free space.
///
/// This lives in Core rather than next to the WMI enumerator because the staged-change projector
/// (<see cref="Planning.LayoutProjector"/>) builds exactly the same map from a *planned* partition set.
/// The preview and the real thing have to agree on where free space is, or the user would stage a
/// create that Windows then refuses.
/// </summary>
public static class DiskMap
{
    /// <summary>
    /// Smallest gap worth rendering — matches <c>CreatePartitionOperation.MinSize</c>. Nothing can be
    /// created in less than this, so smaller slivers are alignment noise, not usable free space.
    /// </summary>
    public const ulong MinGap = 8UL * 1024 * 1024;

    /// <summary>
    /// Reserved at the very start of every disk — GPT header + partition array, or the MBR plus
    /// alignment slack. Free space never starts before this, so a "fill the gap" plan can never
    /// propose offset 0, which Windows would refuse.
    /// </summary>
    public const ulong HeadReserve = 1024UL * 1024;

    /// <summary>
    /// Reserved at the end of a GPT disk for the backup GPT. Free space stops short of it, so a
    /// partition sized to fill the last gap doesn't collide with the secondary header.
    /// </summary>
    public const ulong GptTailReserve = 1024UL * 1024;

    /// <summary>Highest byte a partition may extend to on this disk (before the backup GPT, if any).</summary>
    public static ulong UsableEnd(ulong diskSize, PartitionStyle style) =>
        style == PartitionStyle.Gpt && diskSize > GptTailReserve ? diskSize - GptTailReserve : diskSize;

    public static IReadOnlyList<PartitionInfo> Build(
        IEnumerable<PartitionInfo> real, ulong diskSize, PartitionStyle style = PartitionStyle.Unknown)
    {
        var ordered = real.Where(p => !p.IsUnallocated).OrderBy(p => p.OffsetBytes).ToList();
        var usableEnd = UsableEnd(diskSize, style);

        var result = new List<PartitionInfo>();
        var cursor = HeadReserve;

        foreach (var p in ordered)
        {
            if (p.OffsetBytes > cursor && p.OffsetBytes - cursor >= MinGap)
                result.Add(Unallocated(cursor, p.OffsetBytes - cursor));

            result.Add(p);
            cursor = Math.Max(cursor, p.EndBytes);
        }

        if (usableEnd > cursor && usableEnd - cursor >= MinGap)
            result.Add(Unallocated(cursor, usableEnd - cursor));

        return result;
    }

    private static PartitionInfo Unallocated(ulong offset, ulong size) => new()
    {
        Kind = PartitionKind.Unallocated,
        OffsetBytes = offset,
        SizeBytes = size
    };
}
