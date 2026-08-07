using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Removes a partition, turning its space back into unallocated free space. DESTRUCTIVE — everything on
/// the partition is lost. <see cref="Validate"/> is the anti-wrong-target gate: it refuses the
/// system/boot disk, EFI/MSR/recovery partitions, encrypted volumes, and (by default) internal disks.
/// </summary>
public sealed class DeletePartitionOperation : IDiskOperation
{
    private readonly SystemInspector _inspector;

    public DeletePartitionOperation(DeletePartitionSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public DeletePartitionSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var s = Settings;
        var letter = s.DriveLetter is { } l ? $" ({l}:)" : "";
        return new OperationDescriptor(
            $"Delete partition {s.PartitionNumber}{letter} on disk {s.DiskNumber}",
            "Permanently erases the partition and every file on it. The space becomes unallocated.",
            IsDestructive: true, s.DiskNumber);
    }

    public DriveCapability RequiredCapabilities() => DriveCapability.PartitionEdit;

    /// <summary>Preview: the extent goes back to free space, which is what makes it selectable for a
    /// follow-up create before this delete has actually run.</summary>
    public IReadOnlyList<LayoutChange> PlanLayoutChanges() => new[]
    {
        new LayoutChange
        {
            Kind = LayoutChangeKind.DeletePartition,
            DiskNumber = Settings.DiskNumber,
            TargetPartitionNumber = Settings.PartitionNumber,
            TargetOffsetBytes = Settings.OffsetBytes,
            Note = $"Queued for deletion: partition {Settings.PartitionNumber}" +
                   (Settings.DriveLetter is { } l ? $" ({l}:)" : "")
        }
    };

    public ValidationResult Validate(SystemState state)
    {
        var s = Settings;
        var errors = new List<string>();
        var warnings = new List<string>();

        var disk = state.FindDisk(s.DiskNumber);
        if (disk is null)
            return ValidationResult.Fail($"Target disk {s.DiskNumber} was not found. Rescan and try again.");

        // --- Hard guards: never the system/boot disk (§1.4) ---
        if (disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number)
            return ValidationResult.Fail(
                $"Refusing to delete a partition on disk {disk.Number} — it is the system/boot disk that runs Windows.");

        if (disk.IsReadOnly)
            errors.Add($"Disk {disk.Number} is read-only.");
        if (disk.IsOffline)
            errors.Add($"Disk {disk.Number} is offline — bring it online first.");

        // --- Removable-only safety default, mirroring Format ---
        if (!disk.IsRemovable)
        {
            if (!s.AllowNonRemovable)
                return ValidationResult.Fail(
                    $"Safety: disk {disk.Number} ({disk.FriendlyName}) is an INTERNAL, non-removable disk. " +
                    "Deleting its partitions is blocked. Enable 'Include internal disks' only if you are certain.");
            warnings.Add($"Disk {disk.Number} is an INTERNAL disk — double-check this is really the drive you mean.");
        }

        // --- Capability gate (§1A.7) ---
        if (!disk.Capabilities.Has(DriveCapability.PartitionEdit))
            return ValidationResult.MissingCapability(DriveCapability.PartitionEdit,
                disk.Capabilities.ReasonUnavailable(DriveCapability.PartitionEdit)
                ?? "partition editing is not available on this disk");

        var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == s.PartitionNumber);
        if (part is null)
            return ValidationResult.Fail($"Partition {s.PartitionNumber} on disk {disk.Number} was not found.");

        // --- Never remove the pieces Windows needs to boot, or a recovery image (§1.4) ---
        if (part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved
            or PartitionKind.Recovery or PartitionKind.System || part.IsSystem || part.IsBoot)
            return ValidationResult.Fail(
                $"Refusing to delete partition {s.PartitionNumber} — it is a system/EFI/recovery partition.");

        // --- Encryption gate (§1A.3) ---
        if (part.Volume?.BitLocker is { } bl && (bl.IsProtected || bl.IsConverting))
            return ValidationResult.Fail(
                "The target volume is BitLocker-protected or mid-conversion. Suspend/decrypt BitLocker first (§1A.3).");

        // The offset is captured when the op is staged; if it no longer matches, the layout changed
        // underneath us and the partition number may now point at a different partition entirely.
        if (s.OffsetBytes is { } expected && part.OffsetBytes != expected)
            return ValidationResult.Fail(
                $"Partition {s.PartitionNumber} is no longer at the offset it had when this operation was staged " +
                "— the disk layout changed. Rescan and stage it again.");

        var vol = part.Volume;
        warnings.Add(vol is not null
            ? $"All data on {(vol.DriveLetter is { } dl ? dl + ": " : "the partition")} " +
              $"(\"{vol.Label}\", {vol.FileSystem}, {Bytes(part.SizeBytes)}) will be permanently erased."
            : $"All data on partition {s.PartitionNumber} ({Bytes(part.SizeBytes)}) will be permanently erased.");

        if (!state.IsElevated)
            warnings.Add("Administrator rights are required to apply — you will be prompted to elevate.");

        return errors.Count > 0 ? ValidationResult.Fail(errors.ToArray()) : ValidationResult.Ok(warnings.ToArray());
    }

    public SimulationResult Simulate(SystemState state)
    {
        var validation = Validate(state);
        if (!validation.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", validation.Errors) };

        var part = state.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);

        var steps = new List<string>
        {
            $"Delete partition {Settings.PartitionNumber} on disk {Settings.DiskNumber}.",
            "All existing files on that partition become unrecoverable.",
        };
        if (part is not null)
            steps.Add($"{Bytes(part.SizeBytes)} at offset {Bytes(part.OffsetBytes)} becomes unallocated free space.");

        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = validation.Warnings };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to delete a partition. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before writing — never trust a stale plan.
        // This is also what catches a partition number that shifted after an earlier op in the batch.
        var fresh = _inspector.Capture();
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var started = DateTimeOffset.UtcNow;
        progress.Report(new OpProgress("Deleting partition…", 0.1, Describe().Title));

        var script = "$ErrorActionPreference='Stop'; " +
                     $"Remove-Partition -DiskNumber {Settings.DiskNumber} -PartitionNumber {Settings.PartitionNumber} " +
                     "-Confirm:$false; 'DISKFORGE_OK'";
        var result = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
        if (!result.Success)
            return OpResult.Failed($"Delete failed: {(result.Error.Length > 0 ? result.Error : result.Output)}");

        progress.Report(new OpProgress("Partition deleted", 1.0));
        Log.Information("Deleted partition {Part} on disk {Disk}", Settings.PartitionNumber, Settings.DiskNumber);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    public Task<VerifyResult> VerifyAsync()
    {
        var state = _inspector.Capture();
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return Task.FromResult(VerifyResult.Fail("Disk vanished after the delete."));

        // Partition numbers can be reused/shift after a delete, so verify by offset when we have one:
        // the authoritative post-condition is "nothing real occupies that space any more".
        if (Settings.OffsetBytes is { } offset)
        {
            var still = disk.Partitions.Any(p => !p.IsUnallocated && p.OffsetBytes == offset);
            return Task.FromResult(still
                ? VerifyResult.Fail($"A partition still occupies offset {Bytes(offset)} on disk {disk.Number}.")
                : VerifyResult.Pass());
        }

        var gone = disk.Partitions.All(p => p.PartitionNumber != Settings.PartitionNumber);
        return Task.FromResult(gone
            ? VerifyResult.Pass()
            : VerifyResult.Fail($"Partition {Settings.PartitionNumber} still exists on disk {disk.Number}."));
    }

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

/// <summary>Parameters for deleting a partition.</summary>
public sealed record DeletePartitionSettings
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }

    /// <summary>Offset the partition had when staged; re-checked before executing so a shifted
    /// partition number cannot silently retarget the delete. Also used to verify afterwards.</summary>
    public ulong? OffsetBytes { get; init; }

    /// <summary>Letter at staging time — for the description only.</summary>
    public string? DriveLetter { get; init; }

    public bool AllowNonRemovable { get; init; }
}
