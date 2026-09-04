using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

/// <summary>Details + partition-specific tasks for one partition. Produces staged ops or a format request.</summary>
public partial class PartitionDetailsViewModel : ObservableObject
{
    private readonly PhysicalDiskInfo _disk;
    private readonly PartitionInfo _part;
    private readonly SystemState _state;
    private readonly string? _currentLetter;
    private readonly string _currentLabel;

    public PartitionDetailsViewModel(PhysicalDiskInfo disk, PartitionInfo part, SystemState state)
    {
        _disk = disk;
        _part = part;
        _state = state;
        _currentLetter = part.DriveLetter;
        _currentLabel = part.Volume?.Label ?? "";
        _label = _currentLabel;

        Header = $"Partition {part.PartitionNumber}" +
                 (part.DriveLetter is { } dl ? $" ({dl}:)" : "") +
                 $" — {part.Kind}";
        InfoText = BuildInfo(disk, part);

        var protectedPart = part.IsBoot || part.IsSystem ||
                            part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved or PartitionKind.Recovery;
        // A Linux filesystem now carries a VolumeInfo read from its superblock, but Windows has not
        // mounted it — so the Windows-side label/letter operations still do not apply to it.
        HasVolume = part.Volume is not null;
        var windowsMounted = part.Volume is { MountedByWindows: true };
        CanEdit = windowsMounted && !protectedPart;
        CanFormat = !disk.IsSystemDisk && !disk.IsBootDisk && !protectedPart
                    && part.Kind is PartitionKind.Basic or PartitionKind.Unknown or PartitionKind.Linux;
        // Same partition-level eligibility as Format; CanDelete additionally requires the
        // internal-disk acknowledgment. The engine re-checks every guard in Validate() regardless.
        DeletableKind = CanFormat;
        DiskIsRemovable = disk.IsRemovable;
        CannotEditReason = protectedPart
            ? "System / EFI / recovery partition — protected."
            : windowsMounted ? ""
            : part.Kind == PartitionKind.Linux
                ? "Linux filesystem — Windows has no driver for it, so the label and drive letter " +
                  "cannot be changed from here. Formatting and deleting still work."
                : "This partition has no formatted volume.";

        var used = state.Disks.SelectMany(d => d.Partitions)
            .Where(p => p.DriveLetter is not null)
            .Select(p => p.DriveLetter!.ToUpperInvariant())
            .ToHashSet();
        var letters = new List<string>();
        if (_currentLetter is not null) letters.Add(_currentLetter.ToUpperInvariant());
        for (var c = 'D'; c <= 'Z'; c++)
            if (!used.Contains(c.ToString())) letters.Add(c.ToString());
        AvailableLetters = new ObservableCollection<string>(letters.Distinct().OrderBy(x => x));
        _selectedLetter = _currentLetter?.ToUpperInvariant() ?? AvailableLetters.FirstOrDefault();

        ComputeResizeBounds();
        ComputeCheckEligibility();
    }

    public string Header { get; }
    public string InfoText { get; }
    public bool HasVolume { get; }
    public bool CanEdit { get; }
    public bool CanFormat { get; }
    public bool DeletableKind { get; }
    public bool DiskIsRemovable { get; }
    public string CannotEditReason { get; }

    /// <summary>Deleting an internal disk's partition stays disabled until the user acknowledges it.</summary>
    public bool CanDelete => DeletableKind && (DiskIsRemovable || AllowNonRemovable);

    /// <summary>Shows the internal-disk acknowledgment only where it is actually needed.</summary>
    public bool ShowInternalDiskGate => (DeletableKind || RepairKindOk) && !DiskIsRemovable;

    public ObservableCollection<string> AvailableLetters { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _selectedLetter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    private bool _allowNonRemovable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStageError))]
    private string _stageError = "";

    public bool HasStageError => !string.IsNullOrEmpty(StageError);

    public List<IDiskOperation> StagedOps { get; } = new();
    public bool RequestFormat { get; private set; }
    public bool RequestDelete { get; private set; }

    // ---------------------------------------------------------------- resize

    /// <summary>
    /// Resize is offered only where it can actually work. Windows resizes NTFS in place and nothing
    /// else, so showing the control for exFAT or ext4 would just be a button that always errors.
    /// </summary>
    public bool CanResize { get; private set; }

    public string ResizeBlockedReason { get; private set; } = "";

    /// <summary>Smallest the partition can be, in MB: what the filesystem is using, rounded up.</summary>
    public double MinResizeMb { get; private set; }

    /// <summary>Largest it can be: its own size plus any free space directly after it.</summary>
    public double MaxResizeMb { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResizeSummary))]
    private double _resizeMb;

    public string ResizeSummary
    {
        get
        {
            if (!CanResize) return "";
            var current = _part.SizeBytes / (1024.0 * 1024);
            var delta = ResizeMb - current;
            if (Math.Abs(delta) < 1) return "No change.";
            return delta > 0
                ? $"Extend by {delta:N0} MB, taking free space that follows this partition."
                : $"Shrink by {-delta:N0} MB, releasing it as free space.";
        }
    }

    /// <summary>Builds the resize op, or null with <see cref="StageError"/> set explaining the block.</summary>
    public ResizePartitionOperation? BuildResizeOperation()
    {
        var op = new ResizePartitionOperation(new ResizePartitionSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber!.Value,
            NewSizeBytes = (ulong)Math.Round(ResizeMb) * 1024 * 1024,
            OffsetBytes = _part.OffsetBytes,
            CurrentSizeBytes = _part.SizeBytes,
            DriveLetter = _currentLetter,
            AllowNonRemovable = AllowNonRemovable
        });

        var v = op.Validate(_state);
        if (v.IsValid) return op;
        StageError = string.Join(" ", v.Errors);
        return null;
    }

    /// <summary>
    /// Works out the resize bounds from the live layout. The maximum is this partition plus the
    /// unallocated region that starts where it ends; free space anywhere else cannot be reached,
    /// because a partition is one contiguous extent.
    /// </summary>
    private void ComputeResizeBounds()
    {
        var sizeMb = _part.SizeBytes / (1024.0 * 1024);
        ResizeMb = sizeMb;   // start at the current size, so opening the dialog changes nothing

        if (_part.PartitionNumber is null) { ResizeBlockedReason = "This region is not a partition."; return; }

        var probe = new ResizePartitionOperation(new ResizePartitionSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber.Value,
            // A deliberately impossible target, so only the non-geometry guards can reject it. This
            // asks "could this partition ever be resized?" without assuming a particular new size.
            NewSizeBytes = _part.SizeBytes + 1024 * 1024,
            OffsetBytes = _part.OffsetBytes,
            CurrentSizeBytes = _part.SizeBytes,
            AllowNonRemovable = true
        });

        var validation = probe.Validate(_state);
        var blocking = validation.Errors.FirstOrDefault(e =>
            e.Contains("resize", StringComparison.OrdinalIgnoreCase) ||
            e.Contains("filesystem", StringComparison.OrdinalIgnoreCase));

        if (blocking is not null) { ResizeBlockedReason = blocking; return; }

        var following = _disk.Partitions.FirstOrDefault(
            p => p.IsUnallocated && p.OffsetBytes >= _part.EndBytes &&
                 p.OffsetBytes - _part.EndBytes < 1024 * 1024);

        var used = _part.Volume is { UsageKnown: true } vol ? vol.UsedBytes : 0;

        MinResizeMb = Math.Max(8, Math.Ceiling(used / (1024.0 * 1024)));
        MaxResizeMb = sizeMb + (following?.SizeBytes ?? 0) / (1024.0 * 1024);

        // Nothing to drag if it cannot grow and cannot usefully shrink.
        if (MaxResizeMb - MinResizeMb < 1)
        {
            ResizeBlockedReason =
                "There is no free space directly after this partition to grow into, and it is too full " +
                "to shrink.";
            return;
        }

        CanResize = true;
    }

    /// <summary>Builds label/letter ops for whatever the user changed. Returns false if nothing changed or invalid.</summary>
    public bool TryStageChanges()
    {
        StagedOps.Clear();
        StageError = "";

        if (Label != _currentLabel)
        {
            var op = new SetVolumeLabelOperation(new SetVolumeLabelSettings
            {
                DiskNumber = _disk.Number,
                PartitionNumber = _part.PartitionNumber!.Value,
                DriveLetter = _currentLetter,
                NewLabel = Label
            });
            var v = op.Validate(_state);
            if (!v.IsValid) { StageError = string.Join(" ", v.Errors); return false; }
            StagedOps.Add(op);
        }

        if (SelectedLetter is { } sel && !string.Equals(sel, _currentLetter, StringComparison.OrdinalIgnoreCase))
        {
            var op = new SetDriveLetterOperation(new SetDriveLetterSettings
            {
                DiskNumber = _disk.Number,
                PartitionNumber = _part.PartitionNumber!.Value,
                NewLetter = sel
            });
            var v = op.Validate(_state);
            if (!v.IsValid) { StageError = string.Join(" ", v.Errors); return false; }
            StagedOps.Add(op);
        }

        if (StagedOps.Count == 0)
        {
            StageError = "No changes to stage.";
            return false;
        }
        return true;
    }

    public void RequestFormatNow() => RequestFormat = true;

    public void RequestDeleteNow() => RequestDelete = true;

    /// <summary>A read-only chkdsk is offered wherever Windows has the volume mounted, system disk included.</summary>
    public bool CanCheck { get; private set; }

    /// <summary>Why neither check nor repair is offered, or empty.</summary>
    public string CheckBlockedReason { get; private set; } = "";

    /// <summary>Repair is a write, so it follows the write ladder: never the system disk, removable-only
    /// unless acknowledged. Computed by asking the operation itself, not by duplicating its rules here.</summary>
    public bool CanRepair => RepairKindOk && (DiskIsRemovable || AllowNonRemovable);

    private bool RepairKindOk { get; set; }

    private void ComputeCheckEligibility()
    {
        if (_part.PartitionNumber is null) { CheckBlockedReason = "This region is not a partition."; return; }

        var check = new CheckFilesystemOperation(new CheckFilesystemSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber.Value,
            OffsetBytes = _part.OffsetBytes,
            DriveLetter = _currentLetter
        }).Validate(_state);
        CanCheck = check.IsValid;
        if (!check.IsValid) { CheckBlockedReason = string.Join(" ", check.Errors); return; }

        // Probe repair with the internal-disk acknowledgment granted, to learn whether anything *else*
        // blocks it; the checkbox then decides the final answer at click time.
        var repair = new CheckFilesystemOperation(new CheckFilesystemSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber.Value,
            OffsetBytes = _part.OffsetBytes,
            DriveLetter = _currentLetter,
            Repair = true,
            AllowNonRemovable = true
        }).Validate(_state);
        RepairKindOk = repair.IsValid;
    }

    /// <summary>Builds a check or repair op, or null with <see cref="StageError"/> set explaining the block.</summary>
    public CheckFilesystemOperation? BuildCheckOperation(bool repair)
    {
        var op = new CheckFilesystemOperation(new CheckFilesystemSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber!.Value,
            OffsetBytes = _part.OffsetBytes,
            DriveLetter = _currentLetter,
            Repair = repair,
            // Never auto-granted: the user must tick the internal-disk acknowledgment themselves.
            AllowNonRemovable = AllowNonRemovable
        });

        var v = op.Validate(_state);
        if (v.IsValid) return op;
        StageError = string.Join(" ", v.Errors);
        return null;
    }

    /// <summary>Builds the delete op for this partition, carrying the offset so a shifted partition
    /// number cannot silently retarget it at Apply time. Returns null when validation blocks it.</summary>
    public DeletePartitionOperation? BuildDeleteOperation()
    {
        var op = new DeletePartitionOperation(new DeletePartitionSettings
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _part.PartitionNumber!.Value,
            OffsetBytes = _part.OffsetBytes,
            DriveLetter = _currentLetter,
            // Never auto-granted: the user must tick the internal-disk acknowledgment themselves.
            AllowNonRemovable = AllowNonRemovable
        });

        var v = op.Validate(_state);
        if (v.IsValid) return op;
        StageError = string.Join(" ", v.Errors);
        return null;
    }

    private static string BuildInfo(PhysicalDiskInfo disk, PartitionInfo p)
    {
        string Size(ulong b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }

        var lines = new List<string>
        {
            $"Disk {disk.Number} — {disk.FriendlyName}",
            $"Partition #{p.PartitionNumber}   ·   Type: {p.Kind}",
            $"Size: {Size(p.SizeBytes)}   ·   Offset: {Size(p.OffsetBytes)}",
        };
        if (p.Volume is { } vol)
        {
            lines.Add($"File system: {vol.FileSystem}   ·   Label: {(vol.Label is { Length: > 0 } ? vol.Label : "(none)")}");
            lines.Add($"Drive letter: {(p.DriveLetter is { } dl ? dl + ":" : "(none)")}");
            if (vol.UsageKnown)
                lines.Add($"Used: {Size(vol.UsedBytes)} of {Size(vol.SizeBytes)}   ·   Free: {Size(vol.FreeBytes)}");
            else
                lines.Add("Used / free: not readable — Windows has no driver for this filesystem. " +
                          "The type and label above were read from the filesystem's own superblock.");
            if (vol.BitLocker.Protection is BitLockerProtection.On or BitLockerProtection.Suspended)
                lines.Add($"BitLocker: {vol.BitLocker.Protection}");
        }
        else if (p.Kind == PartitionKind.Linux)
        {
            // Windows reports these as RAW with no volume — that is the driver's absence, not an
            // empty partition, so don't imply the partition is unformatted.
            lines.Add("Linux filesystem (ext4/btrfs/xfs/swap). Windows cannot mount it, and its " +
                      "superblock could not be read (this needs Administrator).");
        }
        else
        {
            lines.Add("No formatted volume on this partition.");
        }
        lines.Add($"Flags: Boot={p.IsBoot}  System={p.IsSystem}  Active={p.IsActive}  Hidden={p.IsHidden}");
        if (p.GptType is { } g) lines.Add($"GPT type: {g}");
        else if (p.MbrType is { } m) lines.Add($"MBR type: 0x{m:X2}");
        return string.Join("\n", lines);
    }
}
