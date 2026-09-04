using System.Text;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Checks a Windows filesystem with <c>chkdsk</c>, and optionally repairs it (<c>/f</c>).
///
/// The check is read-only and is allowed on any mounted volume, including the one running Windows.
/// The repair is a write: it dismounts the volume for the duration, so it follows the same guard
/// ladder as the other write operations (never the system disk, removable-only by default), with one
/// deliberate extra refusal. <c>chkdsk /f</c> on the running Windows volume cannot run now, only offer
/// to schedule itself for the next boot, and DiskForge will not queue work it cannot watch or verify.
///
/// Linux filesystems are refused with the reason: chkdsk knows nothing about them, a native ext check
/// is not built, and btrfs/XFS/F2FS would need WSL, which cannot attach removable media.
///
/// The whole point of this operation is the text chkdsk prints, so it comes back in
/// <see cref="OpResult.Report"/>. A read-only check that <i>finds</i> errors is a check that worked,
/// so it succeeds and <see cref="VerifyAsync"/> is what carries the bad news.
/// </summary>
public sealed class CheckFilesystemOperation : IDiskOperation
{
    /// <summary>chkdsk found errors but was not asked to fix them (read-only run).</summary>
    private const int ExitErrorsNotFixed = 3;

    private readonly SystemInspector _inspector;
    private bool _ran;
    private bool _foundUnfixedErrors;

    public CheckFilesystemOperation(CheckFilesystemSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public CheckFilesystemSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var verb = Settings.Repair ? "Repair" : "Check";
        var where = Settings.DriveLetter is { } l
            ? $"{l.TrimEnd(':')}:"
            : $"partition {Settings.PartitionNumber} on disk {Settings.DiskNumber}";
        var details = Settings.Repair
            ? "Runs chkdsk /f. The volume is dismounted for the duration and errors found are corrected in place. " +
              "This modifies the volume's metadata, not your files, but a repair on a badly damaged volume can " +
              "still move files to FOUND.000."
            : "Runs a read-only chkdsk scan and reports what it finds. Nothing is changed.";
        return new OperationDescriptor($"{verb} filesystem on {where}", details, IsDestructive: false, Settings.DiskNumber);
    }

    public DriveCapability RequiredCapabilities() => DriveCapability.None;

    public ValidationResult Validate(SystemState state)
    {
        var s = Settings;
        var warnings = new List<string>();

        var disk = state.FindDisk(s.DiskNumber);
        if (disk is null)
            return ValidationResult.Fail($"Disk {s.DiskNumber} was not found. Rescan and try again.");

        if (disk.IsOffline)
            return ValidationResult.Fail($"Disk {disk.Number} is offline — bring it online first.");

        var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == s.PartitionNumber);
        if (part is null)
            return ValidationResult.Fail($"Partition {s.PartitionNumber} on disk {disk.Number} was not found.");

        // Staged offset guard, as for delete: a partition number can silently retarget after an earlier
        // op in the batch shifts the layout.
        if (s.OffsetBytes is { } expected && part.OffsetBytes != expected)
            return ValidationResult.Fail(
                $"Partition {s.PartitionNumber} is no longer at the offset it had when this operation was staged " +
                "— the disk layout changed. Rescan and stage it again.");

        var vol = part.Volume;
        if (vol is null)
            return ValidationResult.Fail("That partition has no formatted volume to check.");

        if (!vol.MountedByWindows)
            return ValidationResult.Fail(
                $"chkdsk cannot check a {vol.FileSystem} volume. A native check for Linux filesystems is not " +
                "built yet, and the WSL route cannot attach removable media.");

        if (!IsCheckable(vol.FileSystem))
            return ValidationResult.Fail($"chkdsk does not support {vol.FileSystem} volumes.");

        // A BitLocker volume that is still locked has no readable filesystem: Windows reports it as
        // Unknown/RAW, so IsCheckable above already refuses it. An unlocked one checks normally.

        if (!s.Repair)
        {
            // Read-only. Safe anywhere, but a live volume can produce findings that are not real.
            if (disk.IsSystemDisk || disk.IsBootDisk || part.IsBoot || part.IsSystem)
                warnings.Add("This volume is in use by Windows. A read-only scan of a live volume can report " +
                             "problems that are only in-flight writes, so treat findings as a reason to repair " +
                             "from outside Windows, not as proof of damage.");
            return ValidationResult.Ok(warnings.ToArray());
        }

        // --- Repair is a write. Same ladder as the other write ops. ---
        if (disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number || part.IsBoot || part.IsSystem)
            return ValidationResult.Fail(
                $"Refusing to repair a volume on disk {disk.Number} — it is the system/boot disk that runs Windows. " +
                "chkdsk /f could only schedule itself for the next boot, and DiskForge does not queue work it " +
                "cannot watch or verify. Run 'chkdsk /f' yourself and reboot if you want that.");

        if (disk.IsReadOnly)
            return ValidationResult.Fail($"Disk {disk.Number} is read-only.");

        if (part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved or PartitionKind.Recovery or PartitionKind.System)
            return ValidationResult.Fail(
                $"Refusing to repair partition {s.PartitionNumber} — it is a system/EFI/recovery partition.");

        if (!disk.IsRemovable)
        {
            if (!s.AllowNonRemovable)
                return ValidationResult.Fail(
                    $"Safety: disk {disk.Number} ({disk.FriendlyName}) is an INTERNAL, non-removable disk. " +
                    "Repairing its volumes is blocked. Enable 'Include internal disks' only if you are certain.");
            warnings.Add($"Disk {disk.Number} is an INTERNAL disk — double-check this is really the drive you mean.");
        }

        if (vol.BitLocker is { } bl && (bl.IsProtected || bl.IsConverting))
            return ValidationResult.Fail(
                "The volume is BitLocker-protected or mid-conversion. Suspend/decrypt BitLocker first (§1A.3).");

        warnings.Add($"{Where(vol, part)} will be dismounted while chkdsk runs. Anything with a file open on it " +
                     "will lose that handle.");
        return ValidationResult.Ok(warnings.ToArray());
    }

    public SimulationResult Simulate(SystemState state)
    {
        var v = Validate(state);
        if (!v.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", v.Errors) };

        var steps = new List<string>();
        if (Settings.Repair)
        {
            steps.Add("Dismount the volume (chkdsk /x).");
            steps.Add("Scan the filesystem metadata and fix the errors found (chkdsk /f).");
            steps.Add("Remount the volume and report what was changed.");
        }
        else
        {
            steps.Add("Scan the filesystem metadata read-only (chkdsk).");
            steps.Add("Report the findings. Nothing on the volume changes.");
        }
        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = v.Warnings };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to run chkdsk. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before running — never trust a stale plan.
        var fresh = _inspector.Capture(probeLinuxToolchain: false);
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var part = fresh.FindDisk(Settings.DiskNumber)!.Partitions.First(p => p.PartitionNumber == Settings.PartitionNumber);
        var target = ChkdskTarget(part);
        if (target is null)
            return OpResult.Failed("The volume has neither a drive letter nor a volume path chkdsk can be pointed at.");

        var started = DateTimeOffset.UtcNow;
        progress.Report(new OpProgress(Settings.Repair ? "Repairing filesystem…" : "Checking filesystem…", 0.1, Describe().Title));

        var args = new List<string> { target };
        if (Settings.Repair) { args.Add("/f"); args.Add("/x"); }

        var result = await ExternalProcess.RunAsync("chkdsk.exe", args, ct).ConfigureAwait(false);
        var report = FormatReport(result);
        _ran = true;

        // chkdsk's exit codes: 0 clean, 1 errors fixed, 2 cleanup done, 3 errors found and NOT fixed.
        switch (result.ExitCode)
        {
            case 0:
            case 1:
            case 2:
                _foundUnfixedErrors = false;
                break;
            case ExitErrorsNotFixed:
                // A read-only check that finds something is a check that worked. VerifyAsync tells the user.
                _foundUnfixedErrors = true;
                break;
            default:
                return OpResult.Failed(ExplainFailure(result), report);
        }

        if (Settings.Repair) DiskVolumeReleaser.Refresh(Settings.DiskNumber);

        progress.Report(new OpProgress("chkdsk finished", 1.0));
        Log.Information("chkdsk on disk {Disk} partition {Part} exited {Code}",
            Settings.DiskNumber, Settings.PartitionNumber, result.ExitCode);
        return OpResult.Ok(DateTimeOffset.UtcNow - started, report);
    }

    public Task<VerifyResult> VerifyAsync()
    {
        if (!_ran) return Task.FromResult(VerifyResult.Fail("chkdsk did not run."));

        var state = _inspector.Capture(probeLinuxToolchain: false);
        var part = state.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);

        if (part?.Volume is not { MountedByWindows: true })
            return Task.FromResult(VerifyResult.Fail("The volume is no longer mounted after chkdsk."));

        if (_foundUnfixedErrors)
            return Task.FromResult(VerifyResult.Fail(
                "chkdsk found errors and, running read-only, did not fix them. Stage a Repair to correct them."));

        return Task.FromResult(VerifyResult.Pass());
    }

    /// <summary>chkdsk takes a drive letter or a volume GUID path; a volume with no letter is still checkable.</summary>
    private static string? ChkdskTarget(PartitionInfo part)
    {
        if (part.DriveLetter is { Length: > 0 } letter) return $"{letter.TrimEnd(':', '\\')}:";
        if (part.Volume?.UniqueId is { Length: > 0 } id) return id.TrimEnd('\\');
        return null;
    }

    private static bool IsCheckable(string fs) =>
        fs.ToUpperInvariant() is "NTFS" or "EXFAT" or "FAT32" or "FAT" or "FAT16" or "REFS";

    private static string Where(VolumeInfo vol, PartitionInfo part) =>
        vol.DriveLetter is { Length: > 0 } dl ? $"{dl.TrimEnd(':')}:" : $"partition {part.PartitionNumber}";

    private static string FormatReport(ShellResult result)
    {
        var sb = new StringBuilder();
        if (result.Output.Length > 0) sb.AppendLine(result.Output);
        if (result.Error.Length > 0) sb.AppendLine(result.Error);
        return sb.ToString().TrimEnd();
    }

    private static string ExplainFailure(ShellResult result)
    {
        var text = result.Output + "\n" + result.Error;
        if (text.Contains("schedule", StringComparison.OrdinalIgnoreCase))
            return "chkdsk could not take the volume and offered to schedule itself for the next boot; DiskForge " +
                   "does not accept that. Close whatever is using the volume and try again.";
        if (text.Contains("in use by another process", StringComparison.OrdinalIgnoreCase))
            return "chkdsk could not dismount the volume because something is using it. Close Explorer windows and " +
                   "any program with files open on it, then try again.";
        if (text.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return "chkdsk was denied access to the volume. It needs Administrator rights.";
        return $"chkdsk exited with code {result.ExitCode}.";
    }
}

public sealed record CheckFilesystemSettings
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }

    /// <summary>Partition offset when staged; refused at Apply if it no longer matches.</summary>
    public ulong? OffsetBytes { get; init; }

    public string? DriveLetter { get; init; }

    /// <summary>False: read-only scan. True: chkdsk /f, which dismounts the volume and corrects errors.</summary>
    public bool Repair { get; init; }

    /// <summary>Required to repair a volume on an INTERNAL (non-removable) disk, mirroring the other write ops.</summary>
    public bool AllowNonRemovable { get; init; }
}
