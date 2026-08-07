using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

/// <summary>
/// Backs the Create-partition dialog for one unallocated gap. Every choice is live-validated against
/// the current disk state, so the user sees the exact plan (or the block) before staging.
/// </summary>
public partial class CreatePartitionDialogViewModel : ObservableObject
{
    private const ulong MB = 1024UL * 1024;

    private readonly PhysicalDiskInfo _disk;
    private readonly SystemState _state;
    private readonly ulong _gapOffset;
    private readonly ulong _gapSize;

    public CreatePartitionDialogViewModel(PhysicalDiskInfo disk, SystemState state, ulong gapOffset, ulong gapSize)
    {
        _disk = disk;
        _state = state;

        // Start on the next 1 MiB boundary inside the gap and never run past its end, so the
        // defaults are always a valid plan even before the user touches anything.
        _gapOffset = AlignUp(gapOffset);
        _gapSize = gapOffset + gapSize > _gapOffset ? gapOffset + gapSize - _gapOffset : 0;

        DiskHeader = $"Create partition on Disk {disk.Number} — {disk.FriendlyName}";
        DiskIdentity =
            $"{Bytes(disk.SizeBytes)} · {disk.Bus} · {(disk.IsRemovable ? "Removable" : "INTERNAL")}" +
            (disk.SerialNumber is { } sn ? $" · S/N {sn}" : "");
        DiskIsRemovable = disk.IsRemovable;
        FreeSpaceText = $"{Bytes(_gapSize)} of unallocated space at offset {Bytes(_gapOffset)}";

        // Slider/NumberBox both speak double, so the bound size is a double and is converted back to
        // exact bytes in BuildSettings. Gap sizes are far below double's exact-integer range.
        MaxSizeMb = _gapSize / MB;
        MinSizeMb = Math.Min(CreatePartitionOperation.MinSize / MB, MaxSizeMb);
        _sizeMb = MaxSizeMb; // default: fill the gap

        var used = state.Disks.SelectMany(d => d.Partitions)
            .Where(p => p.DriveLetter is not null)
            .Select(p => p.DriveLetter!.ToUpperInvariant())
            .ToHashSet();
        var letters = new List<string> { NoLetter };
        for (var c = 'D'; c <= 'Z'; c++)
            if (!used.Contains(c.ToString())) letters.Add(c.ToString());
        AvailableLetters = new ObservableCollection<string>(letters);
        _selectedLetter = AvailableLetters.Skip(1).FirstOrDefault() ?? NoLetter;

        FileSystemOptions = FsOptionCatalog.Build(state.LinuxToolchain);
        LinuxBackendText = FsOptionCatalog.DescribeBackend(state.LinuxToolchain);

        Recompute();
    }

    /// <summary>Sentinel for "create the partition without a drive letter".</summary>
    public const string NoLetter = "(none)";

    public string DiskHeader { get; }
    public string DiskIdentity { get; }
    public bool DiskIsRemovable { get; }
    public string FreeSpaceText { get; }

    public double MinSizeMb { get; }
    public double MaxSizeMb { get; }
    public ObservableCollection<string> AvailableLetters { get; }

    public IReadOnlyList<FsOption> FileSystemOptions { get; }

    /// <summary>Status line for the Linux backend, shown under the filesystem picker.</summary>
    public string LinuxBackendText { get; }

    /// <summary>Max characters the label box accepts — tracks the selected filesystem's own limit.</summary>
    public int LabelMaxLength => SelectedFileSystem.MaxLabelLength();

    public bool IsLinuxFileSystem => FormatNew && SelectedFileSystem.IsLinux();

    /// <summary>Windows cannot mount a Linux volume, so a drive letter is not offered for one.</summary>
    public bool CanChooseLetter => !IsLinuxFileSystem;

    public string FullFormatText => SelectedFileSystem.SupportsBadBlockScan()
        ? "Full format (scan for bad sectors — slower)"
        : $"Full format — not supported by {SelectedFileSystem.MkfsTool()}, a normal format will run";

    [ObservableProperty] private double _sizeMb;
    [ObservableProperty] private string _selectedLetter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinuxFileSystem))]
    [NotifyPropertyChangedFor(nameof(CanChooseLetter))]
    private bool _formatNew = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelMaxLength))]
    [NotifyPropertyChangedFor(nameof(IsLinuxFileSystem))]
    [NotifyPropertyChangedFor(nameof(CanChooseLetter))]
    [NotifyPropertyChangedFor(nameof(FullFormatText))]
    private FileSystemType _selectedFileSystem = FileSystemType.Exfat;

    /// <summary>
    /// Picking a Linux filesystem drops the drive letter automatically. Leaving a letter selected
    /// would only produce a validation error the user cannot act on from this dialog.
    /// </summary>
    partial void OnSelectedFileSystemChanged(FileSystemType value)
    {
        if (value.IsLinux() && SelectedLetter != NoLetter) SelectedLetter = NoLetter;
    }
    [ObservableProperty] private bool _fullFormat;
    [ObservableProperty] private string _label = "DISKFORGE";
    [ObservableProperty] private bool _allowNonRemovable;

    [ObservableProperty] private string _simulationText = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canConfirm;

    /// <summary>Populated when the user confirms; the dashboard stages this operation.</summary>
    public CreatePartitionSettings? Result { get; private set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(SimulationText) or nameof(ValidationText)
            or nameof(HasError) or nameof(CanConfirm)))
        {
            Recompute();
        }
    }

    public void Confirm() => Result = BuildSettings();

    private CreatePartitionSettings BuildSettings() => new()
    {
        DiskNumber = _disk.Number,
        OffsetBytes = _gapOffset,
        SizeBytes = (ulong)Math.Clamp(SizeMb, 0, MaxSizeMb) * MB,
        DriveLetter = SelectedLetter == NoLetter ? null : SelectedLetter,
        FormatNew = FormatNew,
        FileSystem = SelectedFileSystem,
        FullFormat = FullFormat,
        Label = Label ?? "",
        AllowNonRemovable = AllowNonRemovable
    };

    private void Recompute()
    {
        var op = new CreatePartitionOperation(BuildSettings());
        var validation = op.Validate(_state);
        var sim = op.Simulate(_state);

        HasError = !validation.IsValid;
        CanConfirm = validation.IsValid;
        ValidationText = validation.IsValid ? "" : string.Join("\n", validation.Errors);

        var lines = new List<string>();
        if (sim.Feasible)
        {
            lines.AddRange(sim.PlannedSteps.Select((s, i) => $"{i + 1}. {s}"));
            if (sim.Warnings.Count > 0)
            {
                lines.Add("");
                lines.AddRange(sim.Warnings.Select(w => "⚠ " + w));
            }
        }
        SimulationText = string.Join("\n", lines);
    }

    private static ulong AlignUp(ulong value)
    {
        var a = CreatePartitionOperation.Alignment;
        var rem = value % a;
        return rem == 0 ? value : value + (a - rem);
    }

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
