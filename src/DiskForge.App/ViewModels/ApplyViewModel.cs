using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskForge.App.Services;
using DiskForge.Core.Operations;
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
/// </summary>
public partial class ApplyViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDiskOperation> _ops;

    public ApplyViewModel(IReadOnlyList<IDiskOperation> ops)
    {
        _ops = ops;
        IsElevated = Elevation.IsElevated();

        var destructive = ops.Where(o => o.Describe().IsDestructive).ToArray();
        RequiresConfirmation = destructive.Length > 0;

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

        var sb = new StringBuilder();
        foreach (var op in ops)
        {
            var d = op.Describe();
            sb.AppendLine((d.IsDestructive ? "⚠ DESTRUCTIVE — " : "") + d.Title);
            sb.AppendLine("    " + d.Details);
        }
        PlanText = sb.ToString().TrimEnd();
    }

    public string PlanText { get; }
    public bool RequiresConfirmation { get; }

    /// <summary>Plain statement of what is about to be destroyed, or empty for a non-destructive batch.</summary>
    public string DestructiveWarning { get; }

    public string ConfirmButtonText { get; }

    /// <summary>
    /// True once the batch has actually been started. The dashboard uses this to decide whether to
    /// discard the staged plan — cancelling out of this dialog must leave the batch intact.
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
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _succeeded;

    public bool IsConfirm => Phase == ApplyPhase.Confirm;
    public bool IsRunning => Phase == ApplyPhase.Running;
    public bool IsDone => Phase == ApplyPhase.Done;
    public bool ShowElevationGate => !IsElevated;

    public bool CanApply => Phase == ApplyPhase.Confirm && IsElevated;

    [RelayCommand]
    private void RestartAsAdministrator() => ElevationService.TryRestartAsAdministrator();

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
        }
        StatusText = sb.ToString().TrimEnd();
        Phase = ApplyPhase.Done;
    }
}
