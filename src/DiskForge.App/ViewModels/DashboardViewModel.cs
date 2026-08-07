using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskForge.App.Services;
using DiskForge.App.Views;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.App.ViewModels;

public partial class PendingOpViewModel : ObservableObject
{
    public required IDiskOperation Operation { get; init; }
    public required string Title { get; init; }
    public required string Details { get; init; }
    public bool IsDestructive { get; init; }

    /// <summary>
    /// Why this operation can no longer run, checked against the layout the operations before it leave
    /// behind. Non-empty typically means an earlier op it depended on was removed from the batch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    private string _blockedReason = "";

    public bool IsBlocked => BlockedReason.Length > 0;
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly SystemInspector _inspector;

    /// <summary>The last real capture. Nothing staged ever mutates it.</summary>
    private SystemState? _lastState;

    /// <summary>The real capture with the staged batch folded in — what the map draws and dialogs plan against.</summary>
    private PlannedState? _planned;

    public DashboardViewModel(SystemInspector inspector)
    {
        _inspector = inspector;
        PendingOps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingSummary));
            // Every staging change redraws the map, so the plan is always what's on screen.
            RebuildDisks();
        };
    }

    public ObservableCollection<DiskCardViewModel> Disks { get; } = new();
    public ObservableCollection<PendingOpViewModel> PendingOps { get; } = new();

    public bool HasPending => PendingOps.Count > 0;
    public string PendingSummary => PendingOps.Count == 1 ? "1 pending operation" : $"{PendingOps.Count} pending operations";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _elevationText = "Checking privileges…";
    [ObservableProperty] private string _capturedText = "";
    [ObservableProperty] private string _summaryText = "";

    /// <summary>
    /// The state dialogs validate against: the *planned* layout while a batch is staged, so a create
    /// can be planned into space a queued delete will free. Every operation still re-validates against
    /// a fresh real capture inside ExecuteAsync — this only decides what the user may stage.
    /// </summary>
    private SystemState? PlanningState => _planned?.Projected ?? _lastState;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var state = await Task.Run(() => _inspector.Capture());
            _lastState = state;
            RebuildDisks();

            IsElevated = state.IsElevated;
            ElevationText = state.IsElevated
                ? "Administrator — disk operations are enabled."
                : "Read-only (not elevated). Restart as Administrator to apply disk operations.";
            CapturedText = $"Scanned {state.CapturedAt:yyyy-MM-dd HH:mm:ss}";
            SummaryText = $"{Disks.Count} disk(s)" +
                          (state.SystemDiskNumber is { } sd ? $" · system disk {sd}" : "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dashboard refresh failed");
            SummaryText = "Enumeration failed — see log.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Redraws every disk card from the real capture plus whatever is staged right now.</summary>
    private void RebuildDisks()
    {
        if (_lastState is null) return;

        var ops = PendingOps.Select(p => p.Operation).ToList();
        _planned = LayoutProjector.Project(_lastState, ops);
        RecheckPendingOps(ops);

        Disks.Clear();
        foreach (var disk in _planned.Disks)
            Disks.Add(DiskCardViewModel.From(disk, _planned.Projected));

        OnPropertyChanged(nameof(CanApplyBatch));
        OnPropertyChanged(nameof(BlockedSummary));
    }

    /// <summary>
    /// Re-validates every staged op against the layout its predecessors leave behind — the same order
    /// <see cref="OperationExecutor"/> will run them in. Removing a delete that a later create depended
    /// on is the case this catches, and it is much better caught here than half way through an Apply.
    /// </summary>
    private void RecheckPendingOps(IReadOnlyList<IDiskOperation> ops)
    {
        if (_lastState is null) return;

        for (var i = 0; i < PendingOps.Count; i++)
        {
            var before = LayoutProjector.Project(_lastState, ops.Take(i).ToList()).Projected;
            var validation = PendingOps[i].Operation.Validate(before);
            PendingOps[i].BlockedReason = validation.IsValid ? "" : string.Join(" ", validation.Errors);
        }
    }

    /// <summary>Apply stays disabled while any staged op is known to be unrunnable.</summary>
    public bool CanApplyBatch => HasPending && PendingOps.All(p => !p.IsBlocked);

    public string BlockedSummary
    {
        get
        {
            var blocked = PendingOps.Count(p => p.IsBlocked);
            return blocked == 0
                ? ""
                : $"{blocked} operation(s) can no longer run — remove them, or restore the operation they depended on.";
        }
    }

    [RelayCommand]
    private void RestartAsAdministrator() => ElevationService.TryRestartAsAdministrator();

    [RelayCommand]
    private void OpenFormat(int diskNumber) => OpenFormatFor(diskNumber, null);

    private void OpenFormatFor(int diskNumber, int? preselectPartition)
    {
        var state = PlanningState;
        if (state is null) return;
        var disk = state.FindDisk(diskNumber);
        if (disk is null) return;

        var dialogVm = new FormatDialogViewModel(disk, state, preselectPartition);
        var window = new FormatWindow(dialogVm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && dialogVm.Result is { } settings)
            StageOperation(new FormatVolumeOperation(settings));
    }

    [RelayCommand]
    private void OpenPartition(PartitionSegmentViewModel? segment)
    {
        var state = PlanningState;
        if (segment is null || state is null) return;

        // A staged partition doesn't exist yet, so there is nothing to inspect or act on — the only
        // useful thing to offer is taking it back off the queue.
        if (segment.IsPending && !segment.IsUnallocated)
        {
            OfferToUnqueue(segment);
            return;
        }

        // Clicking free space offers to fill it; this includes space a queued delete will release,
        // which is the whole point of planning against the projected layout.
        if (segment.IsUnallocated)
        {
            var gapDisk = state.FindDisk(segment.DiskNumber);
            if (gapDisk is not null)
                OpenCreatePartitionFor(gapDisk, segment.OffsetBytes, segment.SizeBytes);
            return;
        }

        if (segment.PartitionNumber is null) return;
        var disk = state.FindDisk(segment.DiskNumber);
        var part = disk?.Partitions.FirstOrDefault(p => p.PartitionNumber == segment.PartitionNumber);
        if (disk is null || part is null) return;

        var vm = new PartitionDetailsViewModel(disk, part, state);
        var window = new PartitionDetailsWindow(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;

        if (vm.RequestFormat)
        {
            OpenFormatFor(disk.Number, part.PartitionNumber);
            return;
        }
        if (vm.RequestDelete)
        {
            if (vm.BuildDeleteOperation() is { } del) StageOperation(del);
            return;
        }
        foreach (var op in vm.StagedOps)
            StageOperation(op);
    }

    /// <summary>Clicking a queued (not-yet-real) segment asks whether to drop that operation.</summary>
    private void OfferToUnqueue(PartitionSegmentViewModel segment)
    {
        if (segment.PendingOperation is not { } op) return;

        var entry = PendingOps.FirstOrDefault(p => ReferenceEquals(p.Operation, op));
        if (entry is null) return;

        var answer = MessageBox.Show(
            $"{entry.Title}\n\n{entry.Details}\n\n" +
            "This is still only queued — nothing has been written to the disk.\n\n" +
            "Remove it from the pending batch?",
            "Queued operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) PendingOps.Remove(entry);
    }

    /// <summary>Context-menu entry point: create into the largest free gap on the chosen disk.</summary>
    [RelayCommand]
    private void OpenCreatePartitionOnDisk(int diskNumber)
    {
        var state = PlanningState;
        if (state is null) return;
        var disk = state.FindDisk(diskNumber);
        var gap = disk?.Partitions
            .Where(p => p.IsUnallocated && p.SizeBytes >= CreatePartitionOperation.MinSize)
            .OrderByDescending(p => p.SizeBytes)
            .FirstOrDefault();
        if (disk is null || gap is null) return;

        OpenCreatePartitionFor(disk, gap.OffsetBytes, gap.SizeBytes);
    }

    private void OpenCreatePartitionFor(PhysicalDiskInfo disk, ulong gapOffset, ulong gapSize)
    {
        var state = PlanningState;
        if (state is null) return;

        var vm = new CreatePartitionDialogViewModel(disk, state, gapOffset, gapSize);
        var window = new CreatePartitionWindow(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && vm.Result is { } settings)
            StageOperation(new CreatePartitionOperation(settings));
    }

    /// <summary>Context-menu entry point: clone starting from a chosen source disk.</summary>
    [RelayCommand]
    private void OpenCloneFromDisk(int diskNumber)
    {
        var state = PlanningState;
        if (state is null) return;
        var disk = state.FindDisk(diskNumber);
        if (disk is null) return;
        ShowCloneDialog(disk);
    }

    /// <summary>Nav-rail entry point: open the clone flow with the source left for the user to pick.</summary>
    [RelayCommand]
    private void StartClone()
    {
        if (PlanningState is null) return;
        ShowCloneDialog(null);
    }

    private void ShowCloneDialog(PhysicalDiskInfo? source)
    {
        var state = PlanningState;
        if (state is null) return;
        var vm = new CloneDialogViewModel(state, source);
        var window = new CloneWindow(vm) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() == true && vm.Result is { } settings)
            StageOperation(new CloneDiskOperation(settings));
    }

    private void StageOperation(IDiskOperation op)
    {
        var d = op.Describe();
        PendingOps.Add(new PendingOpViewModel
        {
            Operation = op,
            Title = d.Title,
            Details = d.Details,
            IsDestructive = d.IsDestructive
        });
        Log.Information("Staged operation: {Title}", d.Title);
    }

    /// <summary>
    /// Removes one staged op. Later ops can depend on it (a create planned into a deleted partition's
    /// space), so the map is redrawn straight after and any orphaned plan simply stops being drawn.
    /// </summary>
    [RelayCommand]
    private void RemovePending(PendingOpViewModel item) => PendingOps.Remove(item);

    [RelayCommand]
    private void ClearPending() => PendingOps.Clear();

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanApplyBatch) return;

        var ops = PendingOps.Select(p => p.Operation).ToList();
        var applyVm = new ApplyViewModel(ops);
        var window = new ApplyWindow(applyVm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();

        // Nothing ran unless the user confirmed inside the dialog; if they cancelled, the batch is
        // still theirs to edit. Only a run (successful or not) invalidates the plan.
        if (!applyVm.HasRun) return;

        PendingOps.Clear();
        await RefreshAsync();
    }
}
