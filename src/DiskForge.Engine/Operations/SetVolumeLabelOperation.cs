using System.Text;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>Renames a volume's label. Non-destructive.</summary>
public sealed class SetVolumeLabelOperation : IDiskOperation
{
    private readonly SystemInspector _inspector;

    public SetVolumeLabelOperation(SetVolumeLabelSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public SetVolumeLabelSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var where = Settings.DriveLetter is { } l ? $"{l}:" : $"partition {Settings.PartitionNumber} (disk {Settings.DiskNumber})";
        return new OperationDescriptor($"Rename {where} to \"{Settings.NewLabel}\"",
            "Changes the volume label. No data is affected.", IsDestructive: false, Settings.DiskNumber);
    }

    public DriveCapability RequiredCapabilities() => DriveCapability.None;

    public IReadOnlyList<LayoutChange> PlanLayoutChanges() => new[]
    {
        new LayoutChange
        {
            Kind = LayoutChangeKind.Relabel,
            DiskNumber = Settings.DiskNumber,
            TargetPartitionNumber = Settings.PartitionNumber,
            Label = Settings.NewLabel,
            Note = $"Queued: rename to \"{Settings.NewLabel}\""
        }
    };

    public ValidationResult Validate(SystemState state)
    {
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return ValidationResult.Fail($"Disk {Settings.DiskNumber} was not found.");

        var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);
        if (part is null) return ValidationResult.Fail($"Partition {Settings.PartitionNumber} was not found.");
        if (part.Volume is null) return ValidationResult.Fail("That partition has no formatted volume to rename.");

        // A Linux filesystem is identified by reading its superblock, not by Windows mounting it, so
        // Set-Volume has nothing to act on. Reformat it to change the label.
        if (!part.Volume.MountedByWindows)
            return ValidationResult.Fail(
                $"Windows cannot mount this {part.Volume.FileSystem} volume, so its label cannot be " +
                "changed from Windows. Reformat the partition to give it a new label.");

        var fs = ParseFs(part.Volume.FileSystem);
        var max = fs?.MaxLabelLength() ?? 32;
        if (Settings.NewLabel.Length > max)
            return ValidationResult.Fail($"Label is too long — {part.Volume.FileSystem} allows at most {max} characters.");
        if (Settings.NewLabel.Any(c => c is '\'' or '"' or '\\' or '/' or ':' or '*' or '?' or '<' or '>' or '|'))
            return ValidationResult.Fail("Label contains characters that are not allowed.");

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
        progress.Report(new OpProgress("Renaming volume…", 0.2));

        var label = Settings.NewLabel.Replace("'", "''");
        var script = Settings.DriveLetter is { } l
            ? $"$ErrorActionPreference='Stop'; Set-Volume -DriveLetter {l} -NewFileSystemLabel '{label}'"
            : $"$ErrorActionPreference='Stop'; Get-Partition -DiskNumber {Settings.DiskNumber} -PartitionNumber {Settings.PartitionNumber} | Get-Volume | Set-Volume -NewFileSystemLabel '{label}'";

        var result = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
        if (!result.Success)
            return OpResult.Failed($"Rename failed: {(result.Error.Length > 0 ? result.Error : result.Output)}");

        Log.Information("Renamed volume on disk {Disk} partition {Part}", Settings.DiskNumber, Settings.PartitionNumber);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    public Task<VerifyResult> VerifyAsync()
    {
        var state = _inspector.Capture();
        var part = state.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);
        var actual = part?.Volume?.Label ?? "";
        return Task.FromResult(string.Equals(actual, Settings.NewLabel, StringComparison.Ordinal)
            ? VerifyResult.Pass()
            : VerifyResult.Fail($"Label is \"{actual}\", expected \"{Settings.NewLabel}\"."));
    }

    private static FileSystemType? ParseFs(string fs) => fs.ToUpperInvariant() switch
    {
        "NTFS" => FileSystemType.Ntfs,
        "EXFAT" => FileSystemType.Exfat,
        "FAT32" => FileSystemType.Fat32,
        _ => null
    };
}

public sealed record SetVolumeLabelSettings
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }
    public string? DriveLetter { get; init; }
    public string NewLabel { get; init; } = "";
}
