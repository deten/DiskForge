using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.Core.Planning;

/// <summary>
/// Applies a staged batch to a captured <see cref="SystemState"/> and returns what the disks *would*
/// look like afterwards, so the dashboard can show a queued delete as free space and let the user plan
/// a new partition into it before anything has been written.
///
/// Pure, allocation-only and side-effect free — it never touches a disk and is never on an execution
/// path. Operations still re-validate against a fresh real capture inside <c>ExecuteAsync</c>; this is
/// a drawing aid, not a source of truth.
/// </summary>
public static class LayoutProjector
{
    public static PlannedState Project(SystemState actual, IReadOnlyList<IDiskOperation> staged)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(staged);

        var plans = actual.Disks.ToDictionary(d => d.Number, d => new DiskPlan(d));

        foreach (var op in staged)
        foreach (var change in op.PlanLayoutChanges())
            if (plans.TryGetValue(change.DiskNumber, out var plan))
                plan.Apply(change, op);

        var planned = actual.Disks.Select(d => plans[d.Number].Build()).ToList();

        return new PlannedState
        {
            Actual = actual,
            Projected = actual with { Disks = planned.Select(p => p.Disk).ToList() },
            Disks = planned
        };
    }

    /// <summary>Mutable working copy of one disk's real partitions while changes are folded in.</summary>
    private sealed class DiskPlan
    {
        private readonly PhysicalDiskInfo _disk;

        /// <summary>Partitions that will exist after Apply — real survivors plus staged creations.</summary>
        private readonly List<Item> _items = new();

        /// <summary>Extents a staged delete/clean hands back; used to label the resulting free space.</summary>
        private readonly List<Freed> _freed = new();

        public DiskPlan(PhysicalDiskInfo disk)
        {
            _disk = disk;
            foreach (var p in disk.Partitions.Where(p => !p.IsUnallocated).OrderBy(p => p.OffsetBytes))
                _items.Add(new Item { Part = p });
        }

        public void Apply(LayoutChange c, IDiskOperation op)
        {
            switch (c.Kind)
            {
                case LayoutChangeKind.DeletePartition: Delete(c, op); break;
                case LayoutChangeKind.CreatePartition: Create(c, op); break;
                case LayoutChangeKind.ReformatPartition: Reformat(c, op); break;
                case LayoutChangeKind.ClearDisk: Clear(c, op); break;
                case LayoutChangeKind.Relabel:
                case LayoutChangeKind.Reletter: Modify(c, op); break;
                case LayoutChangeKind.OverwriteDisk: Overwrite(c, op); break;
                case LayoutChangeKind.ResizePartition: Resize(c, op); break;
            }
        }

        public PlannedDisk Build()
        {
            var map = DiskMap.Build(_items.Select(i => i.Part), _disk.SizeBytes, _disk.PartitionStyle);
            var regions = new List<PlannedRegion>(map.Count);

            foreach (var p in map)
            {
                if (!p.IsUnallocated)
                {
                    var item = _items.FirstOrDefault(i => ReferenceEquals(i.Part, p));
                    regions.Add(new PlannedRegion
                    {
                        Partition = p,
                        Pending = item?.Pending ?? PendingChange.None,
                        PendingNote = item?.Note,
                        PendingOperation = item?.Op
                    });
                    continue;
                }

                // Free space overlapping a staged delete only exists because of that delete — say so,
                // and keep a handle on the op so the user can take it back off the queue.
                var sources = _freed.Where(f => f.Offset < p.EndBytes && f.End > p.OffsetBytes).ToList();
                regions.Add(new PlannedRegion
                {
                    Partition = p,
                    Pending = sources.Count > 0 ? PendingChange.Delete : PendingChange.None,
                    PendingNote = sources.Count > 0
                        ? string.Join("  ·  ", sources.Select(s => s.Note).Distinct())
                        : null,
                    PendingOperation = sources.Count == 1 ? sources[0].Op : null
                });
            }

            return new PlannedDisk { Disk = _disk with { Partitions = map }, Regions = regions };
        }

        /// <summary>Offset preferred over partition number: an offset cannot silently retarget.</summary>
        private Item? Find(LayoutChange c)
        {
            if (c.TargetOffsetBytes is { } offset)
            {
                var byOffset = _items.FirstOrDefault(i => i.Part.OffsetBytes == offset);
                if (byOffset is not null) return byOffset;
            }
            return c.TargetPartitionNumber is { } number
                ? _items.FirstOrDefault(i => i.Part.PartitionNumber == number)
                : null;
        }

        private void Delete(LayoutChange c, IDiskOperation op)
        {
            var item = Find(c);
            if (item is null) return;
            _items.Remove(item);
            _freed.Add(new Freed(item.Part.OffsetBytes, item.Part.SizeBytes, c.Note, op));
        }

        private void Create(LayoutChange c, IDiskOperation op)
        {
            var size = c.SpanRestOfDisk
                ? Sub(DiskMap.UsableEnd(_disk.SizeBytes, _disk.PartitionStyle), c.NewOffsetBytes)
                : c.NewSizeBytes;
            if (size == 0) return;

            // The space may no longer be free — typically because the user took the delete that was
            // going to free it back off the queue. Drawing the new partition on top of the one that is
            // still there would be a lie; the pending list flags the operation as blocked instead.
            var end = c.NewOffsetBytes + size;
            if (_items.Any(i => c.NewOffsetBytes < i.Part.EndBytes && end > i.Part.OffsetBytes)) return;

            _items.Add(new Item
            {
                Part = new PartitionInfo
                {
                    PartitionNumber = null,     // it has no number until Windows assigns one
                    OffsetBytes = c.NewOffsetBytes,
                    SizeBytes = size,
                    Kind = c.NewKind,
                    DriveLetter = c.DriveLetter,
                    Volume = BuildVolume(c, size, null)
                },
                Pending = PendingChange.Create,
                Note = c.Note,
                Op = op
            });
        }

        private void Reformat(LayoutChange c, IDiskOperation op)
        {
            var item = Find(c);
            if (item is null) return;

            item.Part = item.Part with
            {
                Kind = c.NewKind,
                DriveLetter = c.ClearDriveLetter ? null : item.Part.DriveLetter,
                Volume = BuildVolume(c, item.Part.SizeBytes, item.Part.Volume)
            };
            item.Pending = PendingChange.Reformat;
            item.Note = c.Note;
            item.Op = op;
        }

        /// <summary>
        /// The partition keeps its start and changes length. A grow is refused if it would run into the
        /// next partition, for the same reason the operation itself refuses it: an extent is contiguous
        /// and nothing here relocates a neighbour. The free space after it is recomputed at Build time.
        /// </summary>
        private void Resize(LayoutChange c, IDiskOperation op)
        {
            var item = Find(c);
            if (item is null || c.NewSizeBytes == 0) return;

            var end = item.Part.OffsetBytes + c.NewSizeBytes;
            var collides = _items.Any(i => !ReferenceEquals(i, item) &&
                                           item.Part.OffsetBytes < i.Part.EndBytes && end > i.Part.OffsetBytes);
            if (collides || end > DiskMap.UsableEnd(_disk.SizeBytes, _disk.PartitionStyle)) return;

            var shrinking = c.NewSizeBytes < item.Part.SizeBytes;
            item.Part = item.Part with
            {
                SizeBytes = c.NewSizeBytes,
                // The volume moves with the extent; its free space changes by the same amount.
                Volume = item.Part.Volume is { } v && v.UsageKnown
                    ? v with
                    {
                        SizeBytes = c.NewSizeBytes,
                        FreeBytes = c.NewSizeBytes > v.UsedBytes ? c.NewSizeBytes - v.UsedBytes : 0
                    }
                    : item.Part.Volume
            };

            item.Pending = PendingChange.Resize;
            item.Note = shrinking
                ? $"{c.Note} (shrink)"
                : $"{c.Note} (extend)";
            item.Op = op;
        }

        private void Clear(LayoutChange c, IDiskOperation op)
        {
            foreach (var item in _items)
                _freed.Add(new Freed(item.Part.OffsetBytes, item.Part.SizeBytes, c.Note, op));
            _items.Clear();
            Create(c, op);
        }

        private void Modify(LayoutChange c, IDiskOperation op)
        {
            var item = Find(c);
            if (item is null) return;

            if (c.Kind == LayoutChangeKind.Relabel && item.Part.Volume is { } labelled)
                item.Part = item.Part with { Volume = labelled with { Label = c.Label ?? "" } };

            if (c.Kind == LayoutChangeKind.Reletter)
                item.Part = item.Part with
                {
                    DriveLetter = c.DriveLetter,
                    Volume = item.Part.Volume is { } v ? v with { DriveLetter = c.DriveLetter } : null
                };

            // A rename on top of a queued format is still a queued format — don't downgrade the badge.
            if (item.Pending == PendingChange.None)
            {
                item.Pending = PendingChange.Modify;
                item.Note = c.Note;
                item.Op = op;
            }
            else
            {
                item.Note = item.Note is { Length: > 0 } prior ? prior + "  ·  " + c.Note : c.Note;
            }
        }

        private void Overwrite(LayoutChange c, IDiskOperation op)
        {
            foreach (var item in _items.Where(i => i.Pending == PendingChange.None))
            {
                item.Pending = PendingChange.Overwrite;
                item.Note = c.Note;
                item.Op = op;
            }
        }

        /// <summary>
        /// The volume the projected partition will carry. Sizes are the partition's, not a real
        /// filesystem's — nothing has been written, so "free space" is not a measurement.
        /// </summary>
        private static VolumeInfo? BuildVolume(LayoutChange c, ulong sizeBytes, VolumeInfo? existing)
        {
            if (c.FileSystem is null) return existing;
            return new VolumeInfo
            {
                DriveLetter = c.ClearDriveLetter ? null : c.DriveLetter ?? existing?.DriveLetter,
                Label = c.Label ?? "",
                FileSystem = c.FileSystem,
                SizeBytes = sizeBytes,
                FreeBytes = sizeBytes,
                UniqueId = existing?.UniqueId
            };
        }

        private static ulong Sub(ulong a, ulong b) => a > b ? a - b : 0;

        private sealed class Item
        {
            public required PartitionInfo Part { get; set; }
            public PendingChange Pending { get; set; } = PendingChange.None;
            public string? Note { get; set; }
            public IDiskOperation? Op { get; set; }
        }

        private readonly record struct Freed(ulong Offset, ulong Size, string Note, IDiskOperation Op)
        {
            public ulong End => Offset + Size;
        }
    }
}
