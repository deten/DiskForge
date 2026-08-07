using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>Assigns or changes a partition's drive letter. Non-destructive.</summary>
public sealed class SetDriveLetterOperation : IDiskOperation
{
    private readonly SystemInspector _inspector;

    public SetDriveLetterOperation(SetDriveLetterSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public SetDriveLetterSettings Settings { get; }

    public OperationDescriptor Describe() => new(
        $"Assign drive letter {Settings.NewLetter}: to partition {Settings.PartitionNumber} (disk {Settings.DiskNumber})",
        "Changes the drive letter. No data is affected.", IsDestructive: false, Settings.DiskNumber);

    public DriveCapability RequiredCapabilities() => DriveCapability.None;

    public IReadOnlyList<LayoutChange> PlanLayoutChanges() => new[]
    {
        new LayoutChange
        {
            Kind = LayoutChangeKind.Reletter,
            DiskNumber = Settings.DiskNumber,
            TargetPartitionNumber = Settings.PartitionNumber,
            DriveLetter = Settings.NewLetter.ToUpperInvariant(),
            Note = $"Queued: drive letter {Settings.NewLetter.ToUpperInvariant()}:"
        }
    };

    public ValidationResult Validate(SystemState state)
    {
        var letter = Settings.NewLetter.ToUpperInvariant();
        if (letter.Length != 1 || letter[0] is < 'A' or > 'Z')
            return ValidationResult.Fail("Choose a drive letter A–Z.");

        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return ValidationResult.Fail($"Disk {Settings.DiskNumber} was not found.");

        var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);
        if (part is null) return ValidationResult.Fail($"Partition {Settings.PartitionNumber} was not found.");
        if (part.IsBoot || part.IsSystem || part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved)
            return ValidationResult.Fail("Refusing to change the drive letter of a system/boot/EFI partition.");

        // Letter must be free (unless it's already this partition's letter).
        var inUse = state.Disks
            .SelectMany(d => d.Partitions)
            .Any(p => p.DriveLetter is { } dl
                      && string.Equals(dl, letter, StringComparison.OrdinalIgnoreCase)
                      && p.PartitionNumber != part.PartitionNumber);
        if (inUse) return ValidationResult.Fail($"Drive letter {letter}: is already in use.");

        return ValidationResult.Ok();
    }

    public SimulationResult Simulate(SystemState state)
    {
        var v = Validate(state);
        return v.IsValid
            ? new SimulationResult { Feasible = true, PlannedSteps = new[] { Describe().Title } }
            : new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", v.Errors) };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated()) return OpResult.Failed("Administrator rights are required.");
        var started = DateTimeOffset.UtcNow;
        progress.Report(new OpProgress("Assigning drive letter…", 0.2));

        var letter = Settings.NewLetter.ToUpperInvariant();
        var script = $"$ErrorActionPreference='Stop'; " +
                     $"Set-Partition -DiskNumber {Settings.DiskNumber} -PartitionNumber {Settings.PartitionNumber} -NewDriveLetter {letter}";
        var result = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
        if (!result.Success)
            return OpResult.Failed($"Assign failed: {(result.Error.Length > 0 ? result.Error : result.Output)}");

        Log.Information("Assigned {Letter}: to disk {Disk} partition {Part}", letter, Settings.DiskNumber, Settings.PartitionNumber);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    public Task<VerifyResult> VerifyAsync()
    {
        var state = _inspector.Capture();
        var part = state.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);
        return Task.FromResult(string.Equals(part?.DriveLetter, Settings.NewLetter, StringComparison.OrdinalIgnoreCase)
            ? VerifyResult.Pass()
            : VerifyResult.Fail($"Drive letter is \"{part?.DriveLetter}\", expected \"{Settings.NewLetter}\"."));
    }
}

public sealed record SetDriveLetterSettings
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }
    public required string NewLetter { get; init; }
}
