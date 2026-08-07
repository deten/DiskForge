using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

public sealed record ScopeOption(bool IsCleanWholeDisk, string Display);
public sealed record PartitionChoice(int Number, string Display, string? Letter);
public sealed record SchemeOption(PartitionSchemeChoice Scheme, string Display);

/// <summary>Backs the Format dialog. Every choice is live-validated against the current disk state so
/// the user sees exactly what will happen (and any block) before staging the operation.</summary>
public partial class FormatDialogViewModel : ObservableObject
{
    private readonly PhysicalDiskInfo _disk;
    private readonly SystemState _state;

    public FormatDialogViewModel(PhysicalDiskInfo disk, SystemState state, int? preselectPartition = null)
    {
        _disk = disk;
        _state = state;

        DiskHeader = $"Format Disk {disk.Number} — {disk.FriendlyName}";
        DiskIdentity =
            $"{Bytes(disk.SizeBytes)} · {disk.Bus} · {(disk.IsRemovable ? "Removable" : "INTERNAL")}" +
            (disk.SerialNumber is { } sn ? $" · S/N {sn}" : "");
        DiskIsRemovable = disk.IsRemovable;

        Partitions = new ObservableCollection<PartitionChoice>(
            disk.Partitions
                .Where(p => !p.IsUnallocated && p.PartitionNumber is not null && IsEligible(p))
                .Select(p => new PartitionChoice(
                    p.PartitionNumber!.Value,
                    $"Partition {p.PartitionNumber}" +
                    (p.DriveLetter is { } dl ? $" ({dl}:)" : "") +
                    (p.Volume is { } v ? $" — {v.FileSystem}, {Bytes(p.SizeBytes)}" : $" — {Bytes(p.SizeBytes)}"),
                    p.DriveLetter)));

        FileSystemOptions = FsOptionCatalog.Build(state.LinuxToolchain);
        LinuxBackendText = FsOptionCatalog.DescribeBackend(state.LinuxToolchain);

        _selectedPartition = (preselectPartition is { } pn
                                 ? Partitions.FirstOrDefault(pc => pc.Number == pn)
                                 : null)
                             ?? Partitions.FirstOrDefault();
        _allowNonRemovable = false;
        Recompute();
    }

    public string DiskHeader { get; }
    public string DiskIdentity { get; }
    public bool DiskIsRemovable { get; }

    public ObservableCollection<PartitionChoice> Partitions { get; }

    public IReadOnlyList<FsOption> FileSystemOptions { get; }

    /// <summary>Status line for the Linux backend, shown under the filesystem picker.</summary>
    public string LinuxBackendText { get; }

    /// <summary>Max characters the label box accepts — tracks the selected filesystem's own limit.</summary>
    public int LabelMaxLength => SelectedFileSystem.MaxLabelLength();

    /// <summary>True while a Linux filesystem is selected, for the explanatory banner.</summary>
    public bool IsLinuxFileSystem => SelectedFileSystem.IsLinux();

    /// <summary>What a full format means for the current filesystem, or why it cannot be honoured.</summary>
    public string FullFormatText => SelectedFileSystem.SupportsBadBlockScan()
        ? "Full format (scan for bad sectors — slower)"
        : $"Full format — not supported by {SelectedFileSystem.MkfsTool()}, a normal format will run";

    public IReadOnlyList<ScopeOption> ScopeOptions { get; } = new[]
    {
        new ScopeOption(false, "Reformat the selected partition"),
        new ScopeOption(true, "Clean the whole disk + create one new partition")
    };

    /// <summary>
    /// Partition table for a clean-whole-disk format. Not called "convert": nothing here preserves
    /// data, and the choice only exists because the disk is being erased anyway.
    /// </summary>
    public IReadOnlyList<SchemeOption> SchemeOptions { get; } = new[]
    {
        new SchemeOption(PartitionSchemeChoice.Automatic, "Automatic — let Windows choose"),
        new SchemeOption(PartitionSchemeChoice.Gpt, "GPT — up to 128 partitions, needed above 2 TiB"),
        new SchemeOption(PartitionSchemeChoice.Mbr, "MBR — 3 usable partitions, best compatibility")
    };

    /// <summary>The scheme picker only applies when the whole disk is being re-partitioned.</summary>
    public bool CanChooseScheme => IsCleanWholeDisk;

    [ObservableProperty] private PartitionChoice? _selectedPartition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseScheme))]
    private bool _isCleanWholeDisk;

    [ObservableProperty] private PartitionSchemeChoice _selectedScheme = PartitionSchemeChoice.Automatic;

    /// <summary>Leaving a scheme selected after switching back to a reformat would only produce an
    /// error the user cannot act on from this dialog, so reset it.</summary>
    partial void OnIsCleanWholeDiskChanged(bool value)
    {
        if (!value) SelectedScheme = PartitionSchemeChoice.Automatic;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelMaxLength))]
    [NotifyPropertyChangedFor(nameof(IsLinuxFileSystem))]
    [NotifyPropertyChangedFor(nameof(FullFormatText))]
    private FileSystemType _selectedFileSystem = FileSystemType.Exfat;
    [ObservableProperty] private bool _fullFormat;
    [ObservableProperty] private string _label = "DISKFORGE";
    [ObservableProperty] private bool _allowNonRemovable;

    [ObservableProperty] private string _simulationText = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canConfirm;

    /// <summary>Populated when the user confirms; the dashboard stages this operation.</summary>
    public FormatVolumeSettings? Result { get; private set; }

    public bool NoEligiblePartitions => Partitions.Count == 0;

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

    private FormatVolumeSettings BuildSettings() => new()
    {
        DiskNumber = _disk.Number,
        Scope = IsCleanWholeDisk ? FormatScope.CleanWholeDisk : FormatScope.ReformatPartition,
        PartitionScheme = IsCleanWholeDisk ? SelectedScheme : PartitionSchemeChoice.Automatic,
        PartitionNumber = IsCleanWholeDisk ? null : SelectedPartition?.Number,
        TargetDriveLetter = IsCleanWholeDisk ? null : SelectedPartition?.Letter,
        FileSystem = SelectedFileSystem,
        FullFormat = FullFormat,
        Label = Label ?? "",
        AllowNonRemovable = AllowNonRemovable
    };

    private void Recompute()
    {
        if (!IsCleanWholeDisk && SelectedPartition is null && Partitions.Count > 0)
        {
            SelectedPartition = Partitions[0];
            return; // triggers OnPropertyChanged → Recompute again
        }

        var op = new FormatVolumeOperation(BuildSettings());
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

    private static bool IsEligible(PartitionInfo p) =>
        p.Kind is PartitionKind.Basic or PartitionKind.Unknown or PartitionKind.Linux
        && !p.IsSystem && !p.IsBoot;

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
