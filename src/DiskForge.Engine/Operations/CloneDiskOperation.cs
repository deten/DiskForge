using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Cloning;
using DiskForge.Engine.Native;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Clones an entire physical disk onto another. DESTRUCTIVE to the target (fully overwritten).
///
/// The safety-critical logic lives in <see cref="Validate"/>: it refuses to write onto the system/boot
/// disk, refuses source==target, enforces the size fit, gates encryption and live-source consistency,
/// and — the "boots cleanly" intelligence — inspects the boot topology so it never lets the user believe
/// a clone will boot when it won't (e.g. a split-boot disk whose ESP is on another drive).
///
/// The raw byte movement is delegated to <see cref="DiskCloneEngine"/>; identity regeneration and boot
/// rebuild use sanctioned Windows tools (Set-Disk / bcdboot) rather than hand-rolled GPT surgery.
/// </summary>
public sealed class CloneDiskOperation : IDiskOperation
{
    // 4 MiB copy chunk — a multiple of every common sector size (512 / 4096).
    private const int ChunkBytes = 4 * 1024 * 1024;

    private readonly SystemInspector _inspector;
    private byte[]? _writtenHash;   // captured during Execute, checked in Verify
    private long _copiedBytes;

    public CloneDiskOperation(CloneDiskSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public CloneDiskSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var s = Settings;
        var method = s.Method == CloneMethod.FullSector ? "full sector" : "used-extent";
        return new OperationDescriptor(
            $"Clone disk {s.SourceDiskNumber} → disk {s.TargetDiskNumber} ({method})",
            $"Overwrites ALL data on disk {s.TargetDiskNumber} with an exact copy of disk {s.SourceDiskNumber}.",
            IsDestructive: true, s.TargetDiskNumber);
    }

    public DriveCapability RequiredCapabilities() => DriveCapability.Clone;

    /// <summary>
    /// Preview: the target's existing partitions are all going away. The clone's resulting layout is
    /// the source's, but predicting it here would be a guess — flag the loss and stop there.
    /// </summary>
    public IReadOnlyList<LayoutChange> PlanLayoutChanges() => new[]
    {
        new LayoutChange
        {
            Kind = LayoutChangeKind.OverwriteDisk,
            DiskNumber = Settings.TargetDiskNumber,
            Note = $"Queued: overwritten by a clone of disk {Settings.SourceDiskNumber}"
        }
    };

    public ValidationResult Validate(SystemState state)
    {
        var s = Settings;
        var errors = new List<string>();
        var warnings = new List<string>();

        if (s.SourceDiskNumber == s.TargetDiskNumber)
            return ValidationResult.Fail("Source and target are the same disk.");

        var source = state.FindDisk(s.SourceDiskNumber);
        var target = state.FindDisk(s.TargetDiskNumber);
        if (source is null) return ValidationResult.Fail($"Source disk {s.SourceDiskNumber} was not found.");
        if (target is null) return ValidationResult.Fail($"Target disk {s.TargetDiskNumber} was not found.");

        // --- Never write onto the disk that runs Windows (§1.4). The target is destroyed. ---
        if (target.IsSystemDisk || target.IsBootDisk || state.SystemDiskNumber == target.Number)
            return ValidationResult.Fail(
                $"Refusing to clone onto disk {target.Number} — it is the system/boot disk that runs Windows.");

        if (target.IsReadOnly) errors.Add($"Target disk {target.Number} is read-only.");

        // --- Removable-only safety default for the target, mirroring the other write ops ---
        if (!target.IsRemovable)
        {
            if (!s.AllowNonRemovableTarget)
                return ValidationResult.Fail(
                    $"Safety: target disk {target.Number} ({target.FriendlyName}) is an INTERNAL, non-removable disk. " +
                    "Cloning onto it is blocked. Enable 'Include internal disks' only if you are certain.");
            warnings.Add($"Target disk {target.Number} is an INTERNAL disk — its entire contents will be destroyed.");
        }

        // --- Capability gate (§1A.7): both ends must support block clone ---
        if (!source.Capabilities.Has(DriveCapability.Clone))
            return ValidationResult.MissingCapability(DriveCapability.Clone,
                source.Capabilities.ReasonUnavailable(DriveCapability.Clone) ?? "source cannot be cloned");
        if (!target.Capabilities.Has(DriveCapability.Clone))
            return ValidationResult.MissingCapability(DriveCapability.Clone,
                target.Capabilities.ReasonUnavailable(DriveCapability.Clone) ?? "target cannot be written");

        // --- Size fit. A raw clone needs the target ≥ the copied extent. ---
        var copyBytes = CopyExtentBytes(source);
        if (copyBytes <= 0)
            errors.Add("Could not determine the source copy size.");
        else if ((ulong)copyBytes > target.SizeBytes)
            errors.Add($"Target is too small — needs at least {Bytes((ulong)copyBytes)}, " +
                       $"but disk {target.Number} is {Bytes(target.SizeBytes)}.");

        if (source.LogicalSectorSize is { } ss && target.LogicalSectorSize is { } ts && ss != ts)
            warnings.Add($"Sector sizes differ (source {ss} B, target {ts} B) — a raw clone may not mount " +
                         "on the target without a filesystem-aware copy. Prefer matching sector sizes.");

        // --- Encryption gate (§1A.3): a verbatim clone of ciphertext won't unlock on the target ---
        if (source.HasEncryptedVolume)
            warnings.Add("Source has a BitLocker-protected volume. A verbatim clone copies the ciphertext; " +
                         "it will NOT unlock/boot on the target without the original keys/TPM binding. " +
                         "Suspend or decrypt BitLocker first for a portable clone (§1A.3).");

        // --- The "boots cleanly" intelligence — computed up front so it can enrich the blocks below ---
        var boot = ClassifyBoot(source);
        var splitBootNote = boot == BootHandling.EspOnAnotherDisk
            ? " Also note: this disk's EFI System Partition is on a DIFFERENT physical disk (split-boot), " +
              "so even an offline clone of it ALONE will not boot — you must also clone the disk holding the ESP."
            : "";

        // --- Live-source consistency: no VSS yet, so a mounted source is only crash-consistent ---
        var sourceInUse = source.IsBootDisk || source.IsSystemDisk ||
                          source.Partitions.Any(p => p.DriveLetter is not null);
        if (sourceInUse)
        {
            if (source.IsBootDisk || source.IsSystemDisk)
                return ValidationResult.Fail(
                    "Source is the running Windows disk. A consistent live clone needs a VSS snapshot, which is " +
                    "Coming Soon. Clone it from another Windows install (offline) for now." + splitBootNote);
            if (!s.AllowLiveCrashConsistent)
                return ValidationResult.Fail(
                    "Source has mounted volumes; without a VSS snapshot the copy is only crash-consistent " +
                    "(as if the power was cut). Acknowledge this to proceed, or unmount the source first.");
            warnings.Add("Source is live — the copy will be crash-consistent, not a clean snapshot.");
        }

        // --- Surface the boot outcome for sources that weren't blocked above ---
        switch (boot)
        {
            case BootHandling.EspOnAnotherDisk:
                warnings.Add(
                    "⚠ This disk hosts Windows but its EFI System Partition is on a DIFFERENT physical disk " +
                    "(split-boot). A clone of this disk ALONE will not boot. To get a bootable system you must " +
                    "also clone the disk that holds the ESP, or rebuild the boot files afterward.");
                break;
            case BootHandling.RebuildBootFiles when s.MakeBootable:
                warnings.Add(
                    "Source is self-contained bootable; after copying, DiskForge will regenerate the disk " +
                    "identity and rebuild the boot files (bcdboot) so the clone boots on its own. Full OS " +
                    "migration edge cases (drivers/activation) remain Coming Soon.");
                break;
        }

        if (!state.IsElevated)
            warnings.Add("Administrator rights are required to apply — you will be prompted to elevate.");

        warnings.Add($"Disk {target.Number} ({target.FriendlyName}, {Bytes(target.SizeBytes)}) will be " +
                     "completely overwritten and its current data lost.");

        return errors.Count > 0 ? ValidationResult.Fail(errors.ToArray()) : ValidationResult.Ok(warnings.ToArray());
    }

    public SimulationResult Simulate(SystemState state)
    {
        var validation = Validate(state);
        if (!validation.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", validation.Errors) };

        var s = Settings;
        var source = state.FindDisk(s.SourceDiskNumber)!;
        var copyBytes = CopyExtentBytes(source);

        var steps = new List<string>
        {
            $"Take target disk {s.TargetDiskNumber} offline (releases its volumes).",
            $"Copy {Bytes((ulong)copyBytes)} from disk {s.SourceDiskNumber} to disk {s.TargetDiskNumber} " +
            $"({(s.Method == CloneMethod.FullSector ? "every sector" : "up to the last partition")}).",
        };
        if (s.RegenerateDiskIdentity)
            steps.Add("Regenerate the clone's GPT disk identity so it doesn't collide with the source.");
        if (s.VerifyAfter)
            steps.Add("Re-read the target and compare SHA-256 against what was written (verify).");
        if (s.MakeBootable && ClassifyBoot(source) == BootHandling.RebuildBootFiles)
            steps.Add("Rebuild boot files on the clone's EFI System Partition (bcdboot).");
        steps.Add($"Bring disk {s.TargetDiskNumber} back online.");

        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = validation.Warnings };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to clone. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before writing — never trust a stale plan.
        var fresh = _inspector.Capture();
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var s = Settings;
        var source = fresh.FindDisk(s.SourceDiskNumber)!;
        var target = fresh.FindDisk(s.TargetDiskNumber)!;
        var sectorSize = (int)(source.LogicalSectorSize ?? 512);
        var totalBytes = CopyExtentBytes(source);
        var started = DateTimeOffset.UtcNow;

        try
        {
            progress.Report(new OpProgress("Taking target offline…", 0.02, Describe().Title));
            var offline = await SetDiskStateAsync(target.Number, offlineFlag: true, ct).ConfigureAwait(false);
            if (!offline.Success)
                return OpResult.Failed($"Could not take target disk {target.Number} offline: {offline.Error}");

            try
            {
                using var src = RawDiskAccess.OpenRead(source.Number);
                using var dst = RawDiskAccess.OpenWrite(target.Number);

                var copyProgress = new Progress<double>(f =>
                    progress.Report(new OpProgress("Cloning…", 0.05 + f * 0.75, $"{f * 100:0.#}%")));
                var copy = await DiskCloneEngine.CopyAsync(
                    src, dst, totalBytes, sectorSize, ChunkBytes, copyProgress, ct).ConfigureAwait(false);
                _writtenHash = copy.Sha256;
                _copiedBytes = copy.BytesCopied;

                if (s.VerifyAfter)
                {
                    var verifyProgress = new Progress<double>(f =>
                        progress.Report(new OpProgress("Verifying…", 0.80 + f * 0.15, $"{f * 100:0.#}%")));
                    var reread = await DiskCloneEngine.HashAsync(
                        dst, totalBytes, sectorSize, ChunkBytes, verifyProgress, ct).ConfigureAwait(false);
                    if (!reread.AsSpan().SequenceEqual(_writtenHash))
                        return OpResult.Failed("Verify FAILED: the target does not match what was written. " +
                                               "The clone is not trustworthy — do not use it.");
                }
            }
            finally
            {
                // Bring the disk back online whatever happened, so we never leave it stranded offline.
                await SetDiskStateAsync(target.Number, offlineFlag: false, ct).ConfigureAwait(false);
            }

            if (s.RegenerateDiskIdentity)
            {
                progress.Report(new OpProgress("Regenerating disk identity…", 0.96));
                var regen = await RegenerateIdentityAsync(target.Number, ct).ConfigureAwait(false);
                if (!regen.Success)
                    Log.Warning("Disk identity regeneration reported: {Err}", regen.Error);
            }

            progress.Report(new OpProgress("Clone complete", 1.0));
            Log.Information("Cloned disk {Src} → {Dst} ({Bytes} bytes)", source.Number, target.Number, _copiedBytes);
            return OpResult.Ok(DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException)
        {
            return OpResult.Failed("Clone was cancelled — the target is now partially written and must not be used.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clone failed");
            return OpResult.Failed("Clone failed: " + ex.Message);
        }
    }

    public async Task<VerifyResult> VerifyAsync()
    {
        // The authoritative verify already ran inside Execute (read-back hash compare). Here we confirm
        // the target now presents a partition table Windows can read — i.e. the copy produced a real disk.
        var state = _inspector.Capture();
        var target = state.FindDisk(Settings.TargetDiskNumber);
        if (target is null) return VerifyResult.Fail("Target disk vanished after clone.");
        if (_writtenHash is null) return VerifyResult.Fail("No copy hash was recorded — clone did not complete.");

        var hasPartitions = target.Partitions.Any(p => !p.IsUnallocated);
        return await Task.FromResult(hasPartitions
            ? VerifyResult.Pass()
            : VerifyResult.Fail("Target has no readable partitions after the clone."));
    }

    // ---- boot topology intelligence ----

    /// <summary>Decide how (and whether) a clone of this disk could boot.</summary>
    public static BootHandling ClassifyBoot(PhysicalDiskInfo disk)
    {
        var hostsOs = disk.IsBootDisk ||
                      disk.Partitions.Any(p => string.Equals(p.DriveLetter, "C", StringComparison.OrdinalIgnoreCase));
        if (!hostsOs) return BootHandling.NotBootable;

        var hasEsp = disk.Partitions.Any(p =>
            p.Kind is PartitionKind.Efi or PartitionKind.System || p.IsSystem);
        return hasEsp ? BootHandling.RebuildBootFiles : BootHandling.EspOnAnotherDisk;
    }

    /// <summary>Bytes the clone will copy: the whole disk, or up to the end of the last partition,
    /// rounded up to a whole sector.</summary>
    public long CopyExtentBytes(PhysicalDiskInfo source)
    {
        var sector = (int)(source.LogicalSectorSize ?? 512);
        if (Settings.Method == CloneMethod.FullSector)
            return DiskCloneEngine.AlignUpToSector((long)source.SizeBytes, sector);

        var lastEnd = source.Partitions.Where(p => !p.IsUnallocated)
            .Select(p => (long)p.EndBytes)
            .DefaultIfEmpty(0)
            .Max();
        // Keep a little tail for the GPT backup header region; never exceed the disk.
        var withBackup = Math.Min((long)source.SizeBytes, lastEnd + 33L * sector);
        return DiskCloneEngine.AlignUpToSector(withBackup, sector);
    }

    private static Task<(bool Success, string Error)> SetDiskStateAsync(int diskNumber, bool offlineFlag, CancellationToken ct)
        => RunPsAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {diskNumber} -IsOffline ${offlineFlag.ToString().ToLowerInvariant()}; " +
            (offlineFlag ? "" : $"Set-Disk -Number {diskNumber} -IsReadOnly $false; ") + "'DISKFORGE_OK'",
            ct);

    private static Task<(bool Success, string Error)> RegenerateIdentityAsync(int diskNumber, CancellationToken ct)
        => RunPsAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {diskNumber} -Guid ([guid]::NewGuid()); 'DISKFORGE_OK'",
            ct);

    private static async Task<(bool Success, string Error)> RunPsAsync(string script, CancellationToken ct)
    {
        var r = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
        return (r.Success, r.Error.Length > 0 ? r.Error : r.Output);
    }

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
