using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

public sealed record CloneDiskChoice(int Number, string Display, bool IsRemovable);
public sealed record CloneMethodChoice(CloneMethod Value, string Display);

/// <summary>
/// Backs the Clone dialog. Both source and target are chosen here (the disk context-menu entry
/// pre-selects the source; the nav entry leaves it open). Every change is live-validated by the real
/// <see cref="CloneDiskOperation"/> so the plan and any block — including the boot-cleanliness warnings —
/// are shown before staging.
/// </summary>
public partial class CloneDialogViewModel : ObservableObject
{
    private readonly SystemState _state;

    public CloneDialogViewModel(SystemState state, PhysicalDiskInfo? source = null)
    {
        _state = state;

        // Any disk can be a source; the validator handles the running-OS case.
        Sources = new ObservableCollection<CloneDiskChoice>(
            state.Disks.Select(d => new CloneDiskChoice(d.Number, Describe(d), d.IsRemovable)));

        _selectedSource = (source is not null
                              ? Sources.FirstOrDefault(s => s.Number == source.Number)
                              : null)
                          ?? Sources.FirstOrDefault();

        Targets = new ObservableCollection<CloneDiskChoice>();
        RebuildTargets();
        Recompute();
    }

    public ObservableCollection<CloneDiskChoice> Sources { get; }
    public ObservableCollection<CloneDiskChoice> Targets { get; }

    public bool NoTargets => Targets.Count == 0;

    public IReadOnlyList<CloneMethodChoice> Methods { get; } = new[]
    {
        new CloneMethodChoice(CloneMethod.FullSector, "Full sector-by-sector (every byte)"),
        new CloneMethodChoice(CloneMethod.UsedExtent, "Up to the last partition (skip trailing free space)")
    };

    [ObservableProperty] private CloneDiskChoice? _selectedSource;
    [ObservableProperty] private CloneDiskChoice? _selectedTarget;
    [ObservableProperty] private CloneMethod _selectedMethod = CloneMethod.FullSector;
    [ObservableProperty] private bool _regenerateIdentity = true;
    [ObservableProperty] private bool _makeBootable = true;
    [ObservableProperty] private bool _verifyAfter = true;
    [ObservableProperty] private bool _allowNonRemovableTarget;
    [ObservableProperty] private bool _allowLiveCrashConsistent;

    [ObservableProperty] private string _simulationText = "";
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canConfirm;

    /// <summary>True when the chosen target is internal — surfaces the acknowledgment checkbox.</summary>
    public bool TargetIsInternal => SelectedTarget is { IsRemovable: false };

    public CloneDiskSettings? Result { get; private set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Changing the source changes which disks are valid targets.
        if (e.PropertyName is nameof(SelectedSource))
        {
            RebuildTargets();
            OnPropertyChanged(nameof(NoTargets));
        }
        if (e.PropertyName is nameof(SelectedTarget))
            OnPropertyChanged(nameof(TargetIsInternal));

        if (e.PropertyName is not (nameof(SimulationText) or nameof(ValidationText)
            or nameof(HasError) or nameof(CanConfirm)))
        {
            Recompute();
        }
    }

    public void Confirm() => Result = BuildSettings();

    private void RebuildTargets()
    {
        Targets.Clear();
        if (SelectedSource is not { } src) return;

        foreach (var d in _state.Disks)
        {
            if (d.Number == src.Number) continue;
            if (d.IsSystemDisk || d.IsBootDisk || _state.SystemDiskNumber == d.Number) continue;
            Targets.Add(new CloneDiskChoice(d.Number, Describe(d), d.IsRemovable));
        }
        SelectedTarget = Targets.FirstOrDefault();
    }

    private CloneDiskSettings? BuildSettings()
    {
        if (SelectedSource is not { } src || SelectedTarget is not { } dst) return null;
        return new CloneDiskSettings
        {
            SourceDiskNumber = src.Number,
            TargetDiskNumber = dst.Number,
            Method = SelectedMethod,
            RegenerateDiskIdentity = RegenerateIdentity,
            MakeBootable = MakeBootable,
            VerifyAfter = VerifyAfter,
            AllowNonRemovableTarget = AllowNonRemovableTarget,
            AllowLiveCrashConsistent = AllowLiveCrashConsistent
        };
    }

    private void Recompute()
    {
        if (BuildSettings() is not { } settings)
        {
            CanConfirm = false;
            HasError = NoTargets;
            ValidationText = NoTargets ? "No eligible target disk is connected for this source." : "";
            SimulationText = "";
            return;
        }

        var op = new CloneDiskOperation(settings);
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

    private static string Describe(PhysicalDiskInfo d)
        => $"Disk {d.Number} — {d.FriendlyName} ({Bytes(d.SizeBytes)}, {(d.IsRemovable ? "removable" : "INTERNAL")})";

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
