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
    public bool ShowInternalDiskGate => DeletableKind && !DiskIsRemovable;

    public ObservableCollection<string> AvailableLetters { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _selectedLetter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _allowNonRemovable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStageError))]
    private string _stageError = "";

    public bool HasStageError => !string.IsNullOrEmpty(StageError);

    public List<IDiskOperation> StagedOps { get; } = new();
    public bool RequestFormat { get; private set; }
    public bool RequestDelete { get; private set; }

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
