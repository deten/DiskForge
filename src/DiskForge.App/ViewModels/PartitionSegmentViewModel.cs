using System.Windows.Media;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;

namespace DiskForge.App.ViewModels;

/// <summary>One colored slice of the partition map bar.</summary>
public sealed class PartitionSegmentViewModel
{
    public required string Title { get; init; }
    public required string SubTitle { get; init; }
    public required string Tooltip { get; init; }
    public required double Fraction { get; init; }
    public required Brush Fill { get; init; }
    public bool IsUnallocated { get; init; }
    public int DiskNumber { get; init; }
    public int? PartitionNumber { get; init; }

    /// <summary>Where this region sits on the disk — lets a clicked gap seed a create-partition plan.</summary>
    public ulong OffsetBytes { get; init; }
    public ulong SizeBytes { get; init; }

    // --- Staged-change overlay -------------------------------------------------------------------

    /// <summary>How this region differs from the drive right now (<see cref="PendingChange.None"/> = it's real).</summary>
    public PendingChange Pending { get; init; } = PendingChange.None;

    /// <summary>The staged op that owns this region, when there is exactly one. Lets a click un-queue it.</summary>
    public IDiskOperation? PendingOperation { get; init; }

    public bool IsPending => Pending != PendingChange.None;

    /// <summary>Short all-caps overlay, e.g. "QUEUED · DELETE". Empty when the region is real.</summary>
    public string PendingBadge { get; init; } = "";

    /// <summary>Dashed outline color marking a segment as planned rather than present.</summary>
    public Brush OutlineBrush { get; init; } = Transparent;

    public static PartitionSegmentViewModel From(PlannedRegion region, ulong diskSize, int diskNumber)
    {
        var p = region.Partition;
        var fraction = diskSize == 0 ? 0 : (double)p.SizeBytes / diskSize;
        var note = region.PendingNote;

        if (p.IsUnallocated)
        {
            // Free space that a queued delete will produce is drawn as free space on purpose — that is
            // what makes it clickable to plan the next partition into, before anything has been erased.
            var queued = region.Pending == PendingChange.Delete;
            var tip = $"Unallocated free space — {Display.Size(p.SizeBytes)}";
            if (queued) tip += $"\n{note}\n(nothing has been erased yet — Clear takes it back)";
            tip += "\n(click to create a partition here)";

            return new PartitionSegmentViewModel
            {
                Title = queued ? "Unallocated (planned)" : "Unallocated",
                SubTitle = Display.Size(p.SizeBytes),
                Tooltip = tip,
                Fraction = fraction,
                Fill = Display.BrushFor(PartitionKind.Unallocated),
                IsUnallocated = true,
                DiskNumber = diskNumber,
                OffsetBytes = p.OffsetBytes,
                SizeBytes = p.SizeBytes,
                Pending = region.Pending,
                PendingOperation = null,   // clicking free space creates; un-queue from the pending list
                PendingBadge = queued ? BadgeFor(region.Pending) : "",
                OutlineBrush = queued ? DangerOutline : Transparent
            };
        }

        var vol = p.Volume;
        var letter = p.DriveLetter is { } dl ? $"{dl}: " : "";
        var label = vol?.Label is { Length: > 0 } ? vol.Label : p.Kind.ToString();
        var fs = vol?.FileSystem is { Length: > 0 } and not "RAW" ? vol.FileSystem : null;

        var tooltip = $"{letter}{label}\n{p.Kind}, {Display.Size(p.SizeBytes)}";
        if (fs is not null) tooltip += $"\n{fs}";
        if (region.Pending == PendingChange.Create)
        {
            tooltip += $"\n\n{note}\nNot created yet — Apply writes it, Clear takes it back.";
        }
        else
        {
            // A Linux volume is identified from its superblock, so its type and label are real but
            // its free space is unknown — don't render "0 B used" as though it were measured.
            if (vol is { UsageKnown: true })
                tooltip += $"\n{Display.Size(vol.UsedBytes)} used of {Display.Size(vol.SizeBytes)}";
            else if (vol is not null)
                tooltip += "\nSpace used is not readable from Windows for this filesystem.";
            if (vol?.BitLocker is { } bl && bl.Protection is BitLockerProtection.On or BitLockerProtection.Suspended)
                tooltip += $"\nBitLocker: {bl.Protection}" + (bl.IsConverting ? $" ({bl.ConversionPercent}%)" : "");
            tooltip += region.IsPending
                ? $"\n\n{note}\n(click to take this off the queue)"
                : "\n(click for details & tasks)";
        }

        return new PartitionSegmentViewModel
        {
            Title = $"{letter}{label}",
            SubTitle = fs is not null ? $"{fs} · {Display.Size(p.SizeBytes)}" : Display.Size(p.SizeBytes),
            Tooltip = tooltip,
            Fraction = fraction,
            Fill = Display.BrushFor(p.Kind),
            DiskNumber = diskNumber,
            PartitionNumber = p.PartitionNumber,
            OffsetBytes = p.OffsetBytes,
            SizeBytes = p.SizeBytes,
            Pending = region.Pending,
            PendingOperation = region.PendingOperation,
            PendingBadge = BadgeFor(region.Pending),
            OutlineBrush = region.Pending switch
            {
                PendingChange.None => Transparent,
                // A resize keeps the data, so it reads as a plan rather than a destructive act. The
                // shrink/extend distinction is carried in the note and the pending list.
                PendingChange.Create or PendingChange.Modify or PendingChange.Resize => PlanOutline,
                _ => DangerOutline
            }
        };
    }

    private static string BadgeFor(PendingChange pending) => pending switch
    {
        PendingChange.Delete => "QUEUED · DELETE",
        PendingChange.Create => "QUEUED · NEW",
        PendingChange.Reformat => "QUEUED · FORMAT",
        PendingChange.Modify => "QUEUED · CHANGE",
        PendingChange.Overwrite => "QUEUED · CLONE",
        PendingChange.Resize => "QUEUED · RESIZE",
        _ => ""
    };

    private static readonly Brush Transparent = Frozen(Colors.Transparent);
    private static readonly Brush DangerOutline = Frozen(Color.FromRgb(0xF5, 0xB3, 0x01));  // amber
    private static readonly Brush PlanOutline = Frozen(Color.FromRgb(0x6E, 0xE7, 0xB7));    // mint

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
