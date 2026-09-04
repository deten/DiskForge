using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskForge.App.Services;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

public enum ApplyPhase { Confirm, Running, Done }

/// <summary>
/// Drives the Apply dialog: shows the plan, spells out what will be destroyed, hard-gates on elevation,
/// then runs the batch via <see cref="OperationExecutor"/> with progress.
///
/// This dialog *is* the confirmation step — it only appears because the user pressed Apply, and nothing
/// runs until they press the danger button in it. There is deliberately no typed phrase: the real
/// anti-wrong-target protections are the per-operation <c>Validate</c> guards and the fresh re-capture
/// each operation makes immediately before writing, not a string the user retypes from a prompt.
///
/// In <b>simulation</b> mode the same dialog shows the same plan and then runs the batch through
/// <see cref="BatchSimulator"/> instead of the executor: every operation's Validate and Simulate, in
/// order, each against the layout the ones before it would leave, and not one sector written. That is
/// the dry-run half of the Validate → Simulate → Execute → Verify contract, reached either from the
/// Simulate button or from Apply while the global dry-run switch is on. Simulation needs no elevation,
/// has no confirm step, and never counts as having run, so the staged batch stays exactly as it was.
/// </summary>
public partial class ApplyViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDiskOperation> _ops;
    private readonly SystemState? _stateForSimulation;

    /// <summary>Real Apply: confirm, then execute.</summary>
    public ApplyViewModel(IReadOnlyList<IDiskOperation> ops) : this(ops, simulateAgainst: null) { }

    /// <summary>
    /// Pass a captured state to open in simulation mode: the plan is simulated against it immediately
    /// and the dialog opens on the results. Nothing is written and <see cref="HasRun"/> stays false.
    /// </summary>
    public ApplyViewModel(IReadOnlyList<IDiskOperation> ops, SystemState? simulateAgainst)
    {
        _ops = ops;
        _stateForSimulation = simulateAgainst;
        IsSimulation = simulateAgainst is not null;
        IsElevated = Elevation.IsElevated();

        var destructive = ops.Where(o => o.Describe().IsDestructive).ToArray();
        RequiresConfirmation = !IsSimulation && destructive.Length > 0;

        var targetDisks = destructive
            .Select(o => o.Describe().TargetDiskNumber).Distinct().OrderBy(n => n).ToArray();

        // Name the action being confirmed, and the disks it lands on. Calling a pure delete batch a
        // "format" would misdescribe what is about to happen (§1.10).
        var verb = destructive.All(o => o is DeletePartitionOperation) ? "erase" : "erase and reformat";
        var disks = targetDisks.Length == 1
            ? $"disk {targetDisks[0]}"
            : "disks " + string.Join(", ", targetDisks);

        DestructiveWarning = RequiresConfirmation
            ? $"{destructive.Length} of these {(destructive.Length == 1 ? "operations is" : "operations are")} " +
              $"DESTRUCTIVE and will {verb} data on {disks}. This cannot be undone."
            : "";

        ConfirmButtonText = RequiresConfirmation ? "Confirm & Apply" : "Apply";
        WindowTitle = IsSimulation ? "Simulate operations (dry run)" : "Apply operations";

        var sb = new StringBuilder();
        foreach (var op in ops)
        {
            var d = op.Describe();
            sb.AppendLine((d.IsDestructive ? "⚠ DESTRUCTIVE — " : "") + d.Title);
            sb.AppendLine("    " + d.Details);
        }
        PlanText = sb.ToString().TrimEnd();

        if (IsSimulation) RunSimulation();
    }

    public string PlanText { get; }
    public string WindowTitle { get; }
    public bool RequiresConfirmation { get; }

    /// <summary>True when this dialog is a dry run: it shows what would happen and writes nothing.</summary>
    public bool IsSimulation { get; }

    /// <summary>Plain statement of what is about to be destroyed, or empty for a non-destructive batch.</summary>
    public string DestructiveWarning { get; }

    public string ConfirmButtonText { get; }

    /// <summary>
    /// True once the batch has actually been started. The dashboard uses this to decide whether to
    /// discard the staged plan — cancelling out of this dialog must leave the batch intact, and so must
    /// a simulation.
    /// </summary>
    public bool HasRun { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(ShowElevationGate))]
    private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsConfirm))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private ApplyPhase _phase = ApplyPhase.Confirm;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _resultsHeading = "Results";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _succeeded;

    public bool IsConfirm => Phase == ApplyPhase.Confirm;
    public bool IsRunning => Phase == ApplyPhase.Running;
    public bool IsDone => Phase == ApplyPhase.Done;
    public bool ShowElevationGate => !IsElevated && !IsSimulation;

    public bool CanApply => Phase == ApplyPhase.Confirm && IsElevated && !IsSimulation;

    [RelayCommand]
    private void RestartAsAdministrator() => ElevationService.TryRestartAsAdministrator();

    /// <summary>
    /// The dry run. Pure: <see cref="BatchSimulator"/> touches no disk, and this never sets
    /// <see cref="HasRun"/>, so the dashboard keeps the batch afterwards.
    /// </summary>
    private void RunSimulation()
    {
        var simulated = BatchSimulator.Simulate(_stateForSimulation!, _ops);
        Succeeded = BatchSimulator.AllFeasible(simulated);
        ProgressValue = 100;

        ResultsHeading = Succeeded
            ? "Simulation: every operation is feasible"
            : "Simulation: the batch would stop";

        var sb = new StringBuilder();
        sb.AppendLine($"Simulated {_ops.Count} {(_ops.Count == 1 ? "operation" : "operations")} against the " +
                      "current disk layout, each one against the layout the ones before it would leave. " +
                      "Nothing was written.");
        sb.AppendLine();

        foreach (var s in simulated)
        {
            var d = s.Descriptor;
            var mark = s.Result.Feasible ? "✓" : "✗";
            sb.AppendLine($"{mark} {s.Index + 1}. {(d.IsDestructive ? "DESTRUCTIVE — " : "")}{d.Title}");

            if (!s.Result.Feasible)
            {
                sb.AppendLine($"      Blocked: {s.Result.BlockingReason}");
                sb.AppendLine("      Apply would stop here and leave the remaining operations unrun.");
                continue;
            }

            foreach (var step in s.Result.PlannedSteps) sb.AppendLine($"      • {step}");
            foreach (var w in s.Result.Warnings) sb.AppendLine($"      ⚠ {w}");
        }

        StatusText = sb.ToString().TrimEnd();
        Phase = ApplyPhase.Done;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        HasRun = true;
        Phase = ApplyPhase.Running;
        StatusText = "Starting…";

        var progress = new Progress<ApplyProgress>(p =>
        {
            StatusText = $"[{p.OperationIndex + 1}/{p.OperationCount}] {p.Title} — {p.Step.Step}";
            ProgressValue = (p.OperationIndex + p.Step.Fraction) / Math.Max(1, p.OperationCount) * 100.0;
        });

        IReadOnlyList<OperationRunResult> results;
        try
        {
            results = await new OperationExecutor().ApplyAsync(_ops, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Succeeded = false;
            StatusText = "Failed: " + ex.Message;
            Phase = ApplyPhase.Done;
            return;
        }

        Succeeded = results.Count > 0 && results.All(r => r.Success);
        ProgressValue = 100;

        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var mark = r.Success ? "✓" : "✗";
            sb.AppendLine($"{mark} {r.Operation.Describe().Title}");
            if (r.Error is { } err) sb.AppendLine("    " + err);

            // An operation whose whole purpose is its output (a filesystem check) hands it back here.
            if (r.Report is { Length: > 0 } report)
            {
                sb.AppendLine();
                foreach (var line in report.Split('\n'))
                    sb.AppendLine("    " + line.TrimEnd('\r'));
                sb.AppendLine();
            }
        }
        StatusText = sb.ToString().TrimEnd();
        Phase = ApplyPhase.Done;
    }
}
