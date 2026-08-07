using System.Text;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Linux;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Formats either an existing partition or a whole disk (clean + one fresh partition). DESTRUCTIVE.
/// The <see cref="Validate"/> method is the anti-wrong-disk gate: it refuses the system/boot disk,
/// protected partitions, encrypted volumes, and (by default) any non-removable disk.
///
/// Two execution families share that one gate: Windows filesystems go through diskpart /
/// Format-Volume, and Linux filesystems (ext4/btrfs/xfs/…) go through <see cref="ILinuxFormatBackend"/>,
/// which drives the distro's real mkfs. Which family applies is decided solely by
/// <see cref="FileSystemTypeExtensions.IsLinux"/>.
/// </summary>
public sealed class FormatVolumeOperation : IDiskOperation
{
    private readonly SystemInspector _inspector;
    private readonly ILinuxFormatBackend _linuxBackend;

    /// <summary>Read-back signature from the Linux write, reused by <see cref="VerifyAsync"/>.</summary>
    private LinuxFormatOutcome? _linuxResult;
    private LinuxFormatRequest? _linuxRequest;

    public FormatVolumeOperation(
        FormatVolumeSettings settings,
        SystemInspector? inspector = null,
        ILinuxFormatBackend? linuxBackend = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
        // Native first: ext2/3/4 are written by DiskForge itself with no external dependency.
        // WSL stays available purely as a fallback for btrfs/XFS/F2FS, which we do not write.
        _linuxBackend = linuxBackend ?? new NativeLinuxFormatBackend(new WslLinuxFormatBackend());
    }

    public FormatVolumeSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var s = Settings;
        var where = s.Scope == FormatScope.CleanWholeDisk
            ? $"Disk {s.DiskNumber} (entire disk)"
            : $"{(s.TargetDriveLetter is { } l ? l + ": " : "")}partition {s.PartitionNumber} on disk {s.DiskNumber}";
        var title = $"Format {where} as {s.FileSystem.ToFormatName()}"
                    + (EffectiveFullFormat ? " (full)" : " (quick)");
        return new OperationDescriptor(title, DescribeDetail(), IsDestructive: true, s.DiskNumber);
    }

    private string DescribeDetail()
    {
        var s = Settings;
        var detail = s.Scope == FormatScope.CleanWholeDisk
            ? "Erases the partition table and ALL data on the disk, then creates one new partition."
            : "Erases ALL data on the selected partition.";
        return s.FileSystem.IsLinux()
            ? detail + $" Written by {s.FileSystem.MkfsTool()} in WSL2; Windows cannot read the result."
            : detail;
    }

    /// <summary>A full format only means something where the tool actually has a bad-block scan.</summary>
    private bool EffectiveFullFormat => Settings.FullFormat && Settings.FileSystem.SupportsBadBlockScan();

    public DriveCapability RequiredCapabilities() => DriveCapability.Format;

    /// <summary>
    /// Preview: reformatting keeps the extent and swaps the filesystem; a clean-whole-disk replaces the
    /// entire map with one partition spanning the drive.
    /// </summary>
    public IReadOnlyList<LayoutChange> PlanLayoutChanges()
    {
        var s = Settings;
        var linux = s.FileSystem.IsLinux();
        var fs = s.FileSystem.ToFormatName();

        if (s.Scope == FormatScope.CleanWholeDisk)
            return new[]
            {
                new LayoutChange
                {
                    Kind = LayoutChangeKind.ClearDisk,
                    DiskNumber = s.DiskNumber,
                    NewOffsetBytes = DiskMap.HeadReserve,
                    SpanRestOfDisk = true,
                    NewKind = linux ? PartitionKind.Linux : PartitionKind.Basic,
                    FileSystem = fs,
                    Label = s.Label,
                    ClearDriveLetter = linux,
                    Note = $"Queued: erase disk {s.DiskNumber} and create one {fs} partition"
                }
            };

        return new[]
        {
            new LayoutChange
            {
                Kind = LayoutChangeKind.ReformatPartition,
                DiskNumber = s.DiskNumber,
                TargetPartitionNumber = s.PartitionNumber,
                NewKind = linux ? PartitionKind.Linux : PartitionKind.Basic,
                FileSystem = fs,
                Label = s.Label,
                // Windows cannot mount a Linux volume, so the format drops the letter (§ Linux prep).
                ClearDriveLetter = linux,
                Note = $"Queued for formatting as {fs}"
            }
        };
    }

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
                $"Refusing to format disk {disk.Number} — it is the system/boot disk that runs Windows.");

        if (disk.IsReadOnly)
            errors.Add($"Disk {disk.Number} is read-only.");

        // Offline is fixable and gets fixed at Apply — say so rather than blocking, because diskpart
        // would otherwise fail with a cryptic "not allowed on a disk that is offline".
        if (disk.IsOffline)
            warnings.Add($"Disk {disk.Number} is currently offline in Windows — it will be brought " +
                         "online first (nothing else is required from you).");

        // --- Removable-only safety default (§ user's anti-wrong-disk requirement) ---
        if (!disk.IsRemovable)
        {
            if (!s.AllowNonRemovable)
                return ValidationResult.Fail(
                    $"Safety: disk {disk.Number} ({disk.FriendlyName}) is an INTERNAL, non-removable disk. " +
                    "Formatting it is blocked. Enable 'Include internal disks' only if you are certain.");
            warnings.Add($"Disk {disk.Number} is an INTERNAL disk — double-check this is really the drive you mean.");
        }

        // --- Capability gate (§1A.7) ---
        if (!disk.Capabilities.Has(DriveCapability.Format))
            return ValidationResult.MissingCapability(DriveCapability.Format,
                disk.Capabilities.ReasonUnavailable(DriveCapability.Format) ?? "formatting is not available on this disk");

        // --- Linux toolchain gate: no ext4/btrfs/xfs offered unless the mkfs tool really exists ---
        if (state.LinuxToolchain.BlockingReason(s.FileSystem) is { } linuxBlocked)
            errors.Add(linuxBlocked);

        // --- Label sanity ---
        if (LabelError(s.Label, s.FileSystem) is { } le) errors.Add(le);

        // Changing the partition table means rewriting it, which cannot happen while the disk's other
        // partitions are meant to survive. Say so rather than silently ignoring the choice.
        if (s.Scope == FormatScope.ReformatPartition && s.PartitionScheme != PartitionSchemeChoice.Automatic)
            errors.Add("The partition scheme can only be chosen when erasing the whole disk. " +
                       "Reformatting one partition leaves the disk's partition table untouched.");

        if (s.Scope == FormatScope.ReformatPartition)
        {
            var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == s.PartitionNumber);
            if (part is null)
                return ValidationResult.Fail($"Partition {s.PartitionNumber} on disk {disk.Number} was not found.");

            if (part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved
                or PartitionKind.Recovery or PartitionKind.System || part.IsSystem || part.IsBoot)
                return ValidationResult.Fail(
                    $"Refusing to format partition {s.PartitionNumber} — it is a system/EFI/recovery partition.");

            if (part.Volume?.BitLocker is { } bl && (bl.IsProtected || bl.IsConverting))
                return ValidationResult.Fail(
                    "The target volume is BitLocker-protected or mid-conversion. Suspend/decrypt BitLocker first (§1A.3).");

            if (s.FileSystem.ExceedsFat32Limit(part.SizeBytes))
                errors.Add("FAT32 cannot be created on a volume larger than 32 GB — choose exFAT or NTFS.");

            if (MinimumSizeError(part.SizeBytes) is { } tooSmall) errors.Add(tooSmall);

            var vol = part.Volume;
            warnings.Add(vol is not null
                ? $"All data on {(vol.DriveLetter is { } dl ? dl + ": " : "the partition")} " +
                  $"(\"{vol.Label}\", {vol.FileSystem}, {Bytes(part.SizeBytes)}) will be permanently erased."
                : $"All data on partition {s.PartitionNumber} ({Bytes(part.SizeBytes)}) will be permanently erased.");

            if (s.FileSystem.IsLinux() && part.DriveLetter is { } letter)
                warnings.Add($"Drive letter {letter}: will be removed — Windows cannot mount " +
                             $"{s.FileSystem.ToFormatName()}, so keeping a letter would only make Explorer " +
                             "offer to reformat it.");
        }
        else // CleanWholeDisk
        {
            if (s.FileSystem.ExceedsFat32Limit(disk.SizeBytes))
                errors.Add("FAT32 cannot be created on a disk larger than 32 GB — choose exFAT or NTFS.");

            if (MinimumSizeError(disk.SizeBytes) is { } tooSmall) errors.Add(tooSmall);

            var count = disk.Partitions.Count(p => !p.IsUnallocated);
            warnings.Add($"The entire disk {disk.Number} ({disk.FriendlyName}, {Bytes(disk.SizeBytes)}) " +
                         $"and all {count} partition(s) will be permanently erased.");

            foreach (var schemeIssue in SchemeIssues(disk, errors)) warnings.Add(schemeIssue);
        }

        // --- Linux-specific expectations, stated up front rather than discovered afterwards ---
        if (s.FileSystem.IsLinux())
        {
            warnings.Add($"Windows has no {s.FileSystem.ToFormatName()} driver: after this completes, Explorer and " +
                         "Disk Management will show the volume as unformatted/RAW. That is expected — do NOT let " +
                         "Windows \"format\" it. The partition will be tagged as Linux filesystem data so Windows " +
                         "leaves it alone. Trying to create a file on it from Explorer will fail with " +
                         "\"Error 0x8000FFFF: Catastrophic failure\"; that is the missing driver, not a bad " +
                         "format. Read and write it from Linux/WSL.");

            if (state.LinuxToolchain.IsAvailable && state.LinuxToolchain.ToolFor(s.FileSystem) is { Available: true } tool)
                warnings.Add($"Will be written by {tool.Path ?? s.FileSystem.MkfsTool()} in WSL2 ({tool.Distro})" +
                             (disk.IsRemovable
                                 ? ", built in a scratch image and copied onto the drive (Windows cannot " +
                                   "attach a removable disk to WSL)."
                                 : "; the disk is handed to the WSL kernel for the duration."));

            if (s.FullFormat && !s.FileSystem.SupportsBadBlockScan())
                warnings.Add($"{s.FileSystem.MkfsTool()} has no bad-block scan, so \"full format\" cannot be " +
                             "honoured here — a normal format will be performed instead.");

            // WSL takes the whole physical disk, not one partition, so every volume on it goes away for
            // the duration. Worth saying out loud when the disk carries data the user still wants.
            var others = disk.Partitions
                .Where(p => !p.IsUnallocated && p.Volume is not null
                            && (s.Scope == FormatScope.CleanWholeDisk || p.PartitionNumber != s.PartitionNumber))
                .Select(p => p.DriveLetter is { } dl ? dl + ":" : $"partition {p.PartitionNumber}")
                .ToList();
            // Only the direct route takes the whole disk away from Windows; the staged route used for
            // removable media writes just the one partition.
            if (others.Count > 0 && s.Scope == FormatScope.ReformatPartition && !disk.IsRemovable)
                warnings.Add($"The whole of disk {disk.Number} is handed to WSL for the duration, so " +
                             $"{string.Join(", ", others)} on this disk will be temporarily unavailable in " +
                             "Windows. Close anything using them first.");
        }

        if (!state.IsElevated)
            warnings.Add("Administrator rights are required to apply — you will be prompted to elevate.");

        return errors.Count > 0 ? ValidationResult.Fail(errors.ToArray()) : ValidationResult.Ok(warnings.ToArray());
    }

    /// <summary>
    /// Rules for the requested partition table. MBR's 2 TiB ceiling is a hard error — the disk's tail
    /// would simply be unaddressable. Everything else is a warning, because the choice is legitimate
    /// but has consequences the user should see before erasing anything.
    /// </summary>
    private IEnumerable<string> SchemeIssues(PhysicalDiskInfo disk, List<string> errors)
    {
        switch (Settings.PartitionScheme)
        {
            case PartitionSchemeChoice.Mbr:
                if (disk.SizeBytes > CreatePartitionOperation.MbrMaxAddressable)
                {
                    errors.Add($"MBR cannot address a {Bytes(disk.SizeBytes)} disk — its limit is 2 TiB. " +
                               "Choose GPT.");
                    break;
                }
                yield return "MBR has four partition-table slots, and Windows turns the last one into an " +
                             $"extended container, so DiskForge will create at most " +
                             $"{CreatePartitionOperation.MbrMaxCreatablePartitions} partitions on this disk. " +
                             "Choose GPT if you want more.";
                break;

            case PartitionSchemeChoice.Gpt:
                if (disk.IsRemovable)
                    yield return "This is removable media, and Windows re-initializes a freshly cleaned " +
                                 "removable disk as MBR on its own — sometimes faster than it can be set to " +
                                 "GPT. DiskForge will retry, then report the scheme the disk actually ended " +
                                 "up with rather than assume it worked.";
                yield return "A GPT stick will not boot on older BIOS-only machines, and some appliances " +
                             "(car stereos, TVs, cameras) only read MBR.";
                break;

            case PartitionSchemeChoice.Automatic:
                if (disk.IsRemovable)
                    yield return "Windows will almost certainly choose MBR for removable media, which caps " +
                                 $"this disk at {CreatePartitionOperation.MbrMaxCreatablePartitions} " +
                                 "DiskForge-created partitions. Choose GPT explicitly if you need more.";
                break;
        }
    }

    public SimulationResult Simulate(SystemState state)
    {
        var validation = Validate(state);
        if (!validation.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", validation.Errors) };

        var s = Settings;
        var steps = new List<string>();
        var quality = EffectiveFullFormat ? "full" : "quick";

        if (state.FindDisk(s.DiskNumber) is { IsOffline: true })
            steps.Add($"Bring disk {s.DiskNumber} online in Windows (it is currently offline).");

        if (s.FileSystem.IsLinux())
        {
            if (s.Scope == FormatScope.CleanWholeDisk)
            {
                steps.Add($"Clear the partition table on disk {s.DiskNumber} (destroys all partitions & data).");
                steps.Add(DescribeSchemeStep(state));
                steps.Add($"Create one partition spanning disk {s.DiskNumber}.");
                steps.Add("Tag the new partition as Linux filesystem data " +
                          $"({FileSystemTypeExtensions.LinuxFilesystemDataGuid}) and assign no drive letter.");
            }
            else
            {
                // Take the letter from live state rather than the staged plan — the plan's copy can be
                // stale, and the prep step removes whatever access paths are actually there.
                var letter = state.FindDisk(s.DiskNumber)?.Partitions
                                 .FirstOrDefault(p => p.PartitionNumber == s.PartitionNumber)?.DriveLetter
                             ?? s.TargetDriveLetter;
                if (letter is not null) steps.Add($"Remove drive letter {letter}: from the partition.");
                steps.Add($"Tag partition {s.PartitionNumber} as Linux filesystem data " +
                          $"({FileSystemTypeExtensions.LinuxFilesystemDataGuid}).");
            }

            var distro = state.LinuxToolchain.ToolFor(s.FileSystem).Distro ?? "the WSL distribution";
            var removable = state.FindDisk(s.DiskNumber)?.IsRemovable ?? false;
            var mkfs = $"Run {s.FileSystem.MkfsTool()} in {distro}" +
                       (s.Label.Length > 0 ? $" with label \"{s.Label}\"" : " with no label") +
                       (EffectiveFullFormat ? " and a bad-block scan" : "");

            if (removable)
            {
                // Hyper-V will not attach removable media to the WSL VM, so the filesystem is built in a
                // scratch image and copied in. Say so — the user should not be surprised by the extra step.
                steps.Add("Create a scratch disk image the size of the target partition (Windows cannot " +
                          "hand a removable drive to WSL, so the filesystem is built in an image first).");
                steps.Add(mkfs + ", against that image.");
                steps.Add($"Write the finished filesystem onto disk {s.DiskNumber}, then delete the image.");
                steps.Add("Read the superblock back from the drive to confirm the filesystem is really there.");
            }
            else
            {
                steps.Add($"Attach disk {s.DiskNumber} to the WSL2 kernel (wsl --mount --bare) and confirm the " +
                          "device by disk size and partition offset before writing anything.");
                steps.Add(mkfs + ".");
                steps.Add("Read the filesystem signature back with blkid, then detach the disk from WSL.");
            }
        }
        else if (s.Scope == FormatScope.CleanWholeDisk)
        {
            steps.Add($"Clear the partition table on disk {s.DiskNumber} (destroys all partitions & data).");
            steps.Add(DescribeSchemeStep(state));
            steps.Add("Create one partition spanning the disk and assign a drive letter.");
            steps.Add($"Format it as {s.FileSystem.ToFormatName()} ({quality}), label \"{s.Label}\".");
        }
        else
        {
            steps.Add($"Format partition {s.PartitionNumber} on disk {s.DiskNumber} as " +
                      $"{s.FileSystem.ToFormatName()} ({quality}), label \"{s.Label}\".");
            steps.Add("All existing files on that partition become unrecoverable.");
        }

        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = validation.Warnings };
    }

    /// <summary>The Simulate line describing which partition table the disk will be given.</summary>
    private string DescribeSchemeStep(SystemState state)
    {
        var size = state.FindDisk(Settings.DiskNumber)?.SizeBytes ?? 0;
        return RequestedStyle(size) switch
        {
            PartitionStyle.Gpt when Settings.PartitionScheme == PartitionSchemeChoice.Automatic =>
                $"Initialize disk {Settings.DiskNumber} as GPT (required — MBR cannot address a disk this large).",
            PartitionStyle.Gpt =>
                $"Initialize disk {Settings.DiskNumber} as GPT, retrying if Windows re-initializes it as MBR, " +
                "and abort if it will not hold GPT.",
            PartitionStyle.Mbr =>
                $"Initialize disk {Settings.DiskNumber} as MBR, retrying if Windows re-initializes it as GPT, " +
                "and abort if it will not hold MBR.",
            _ =>
                $"Initialize disk {Settings.DiskNumber} with whichever partition table Windows chooses " +
                "(MBR for removable media, usually GPT otherwise)."
        };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to format. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before writing — never trust a stale plan.
        var fresh = _inspector.Capture();
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var started = DateTimeOffset.UtcNow;

        // diskpart and the Storage cmdlets both refuse an offline disk outright ("The operation is not
        // allowed on a disk that is offline"), so clear that before anything else. It matters because
        // DiskForge itself can leave a disk offline after a failed WSL attach — refusing to work
        // because of a state we created would just trap the user.
        if (await EnsureOnlineAsync(fresh, ct).ConfigureAwait(false) is { } onlineError)
            return OpResult.Failed(onlineError);

        if (Settings.FileSystem.IsLinux())
            return await ExecuteLinuxAsync(fresh, started, progress, ct).ConfigureAwait(false);

        progress.Report(new OpProgress("Formatting…", 0.1, Describe().Title));

        // Clean-whole-disk is a two-stage prep (diskpart wipes, PowerShell decides the scheme and
        // partitions) for the same reason the Linux path is: diskpart cannot branch on the disk's
        // resulting state, and `convert gpt` only accepts an empty MBR disk.
        // Reformat-in-place uses Format-Volume, which is a single reliable cmdlet.
        ShellResult result;
        if (Settings.Scope == FormatScope.CleanWholeDisk)
        {
            // diskpart's `clean` zeroes sectors, which Windows denies while a volume on the disk is
            // still mounted. Release them first or the whole operation dies on "Access is denied".
            ReleaseVolumes(fresh);

            var wipe = await CleanWithRetryAsync(fresh, progress, ct).ConfigureAwait(false);
            if (!wipe.Success)
                return OpResult.Failed($"Clearing disk {Settings.DiskNumber} failed: {ExplainDiskPartFailure(wipe)}");

            // The partition table just changed; make Windows re-read it before we look at the result.
            DiskVolumeReleaser.Refresh(Settings.DiskNumber);
            progress.Report(new OpProgress("Writing the partition table…", 0.3, Describe().Title));

            var diskSize = fresh.FindDisk(Settings.DiskNumber)?.SizeBytes ?? 0;
            result = await PowerShellRunner.RunAsync(BuildWindowsCleanScript(diskSize), ct).ConfigureAwait(false);
        }
        else
        {
            result = await PowerShellRunner.RunAsync(BuildFormatVolumeScript(), ct).ConfigureAwait(false);
        }

        if (!result.Success)
            return OpResult.Failed($"Format failed: {ExplainDiskPartFailure(result)}");

        if (await ReportSchemeMismatchAsync() is { } schemeWarning)
            Log.Warning("{Warning}", schemeWarning);

        progress.Report(new OpProgress("Format complete", 1.0));
        Log.Information("Format succeeded on disk {Disk}", Settings.DiskNumber);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    /// <summary>
    /// Linux path: Windows prepares the partition (drops the drive letter, tags the partition type),
    /// then the disk is handed to WSL2 where the real mkfs writes the filesystem. The partition extent
    /// is taken from the fresh capture, never from the staged plan, so the backend's offset check is
    /// comparing against what is on the disk right now.
    /// </summary>
    private async Task<OpResult> ExecuteLinuxAsync(
        SystemState fresh, DateTimeOffset started, IProgress<OpProgress> progress, CancellationToken ct)
    {
        var s = Settings;
        var disk = fresh.FindDisk(s.DiskNumber);
        if (disk is null) return OpResult.Failed($"Disk {s.DiskNumber} disappeared before the format started.");

        PartitionInfo target;

        if (s.Scope == FormatScope.CleanWholeDisk)
        {
            progress.Report(new OpProgress("Releasing the disk from Windows…", 0.03, Describe().Title));
            ReleaseVolumes(fresh);

            progress.Report(new OpProgress("Clearing the partition table…", 0.05, Describe().Title));
            var dp = await DiskPartRunner.RunAsync(BuildCleanScript(), ct).ConfigureAwait(false);
            if (!dp.Success)
                return OpResult.Failed(
                    $"Preparing disk {s.DiskNumber} failed: {ExplainDiskPartFailure(dp)}");

            // Partitioning is done from PowerShell, where the disk's actual state can be queried —
            // diskpart's `convert gpt` only accepts an empty MBR disk and fails on the RAW disk that
            // `clean` leaves behind on removable media.
            progress.Report(new OpProgress("Creating the Linux partition…", 0.15, Describe().Title));
            var init = await PowerShellRunner.RunAsync(BuildLinuxPartitionScript(disk.SizeBytes), ct)
                .ConfigureAwait(false);
            if (!init.Success)
                return OpResult.Failed(
                    $"Preparing disk {s.DiskNumber} failed: " +
                    (init.Error.Length > 0 ? init.Error : init.Output));

            // The partition table just changed; make Windows re-read it before we look for the result.
            DiskVolumeReleaser.Refresh(s.DiskNumber);

            var created = await WaitForCleanPartitionAsync(ct).ConfigureAwait(false);
            if (created is null)
                return OpResult.Failed(
                    $"Disk {s.DiskNumber} was cleared, but the new partition did not appear. " +
                    "Rescan the disk and try again — no filesystem was written.");

            (disk, target) = created.Value;
        }
        else
        {
            var part = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == s.PartitionNumber);
            if (part is null)
                return OpResult.Failed($"Partition {s.PartitionNumber} on disk {s.DiskNumber} was not found.");
            target = part;

            progress.Report(new OpProgress("Preparing the partition for a Linux filesystem…", 0.08, Describe().Title));
            var prep = await PowerShellRunner.RunAsync(BuildLinuxPrepScript(disk, target), ct).ConfigureAwait(false);
            if (!prep.Success)
                return OpResult.Failed(
                    "Could not release the partition from Windows: " +
                    (prep.Error.Length > 0 ? prep.Error : prep.Output));
        }

        var request = new LinuxFormatRequest
        {
            DiskNumber = s.DiskNumber,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = target.OffsetBytes,
            PartitionSizeBytes = target.SizeBytes,
            FileSystem = s.FileSystem,
            Label = s.Label,
            BadBlockScan = EffectiveFullFormat,
            VolumePaths = DiskVolumeReleaser.VolumePathsOn(disk),
            DiskIsRemovable = disk.IsRemovable
        };

        var outcome = await _linuxBackend.FormatAsync(request, progress, ct).ConfigureAwait(false);
        foreach (var line in outcome.Log) Log.Information("Linux format: {Step}", line);

        _linuxRequest = request;
        _linuxResult = outcome;

        if (!outcome.Success)
            return OpResult.Failed($"{s.FileSystem.MkfsTool()} could not format the partition: " +
                                   (outcome.Error ?? "the filesystem signature could not be read back."));

        progress.Report(new OpProgress("Format complete", 1.0));
        Log.Information("Linux format succeeded on disk {Disk} ({Device} → {Type})",
            s.DiskNumber, outcome.DeviceNode, outcome.DetectedType);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    /// <summary>
    /// Brings the target disk online if Windows has it offline. Returns null on success, or the error
    /// to report. Onlining is non-destructive and reversible, and it is a precondition of an operation
    /// the user has already confirmed — so it is done rather than refused.
    /// </summary>
    private async Task<string?> EnsureOnlineAsync(SystemState state, CancellationToken ct)
    {
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null || !disk.IsOffline) return null;

        Log.Information("Disk {Disk} is offline; bringing it online before formatting", disk.Number);
        var result = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {disk.Number} -IsOffline $false; 'DISKFORGE_OK'",
            ct).ConfigureAwait(false);

        if (result.Success) return null;

        return $"Disk {disk.Number} is offline in Windows and could not be brought online: " +
               $"{(result.Error.Length > 0 ? result.Error : result.Output)} " +
               "Bring it online in Disk Management and try again.";
    }

    /// <summary>
    /// "Access is denied" from diskpart is almost never a permissions problem — the process is already
    /// elevated by this point. It means Windows refused to zero sectors under a volume it is still
    /// holding, so say what to actually do about it instead of echoing diskpart's dead end.
    /// </summary>
    public static string ExplainDiskPartFailure(ShellResult result)
    {
        var text = result.Error.Length > 0 ? result.Error : result.Output;

        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return text.TrimEnd() + Environment.NewLine + Environment.NewLine +
                   "This usually means another program still has the drive open, so Windows would not " +
                   "let the partition table be rewritten. Close any Explorer window showing the drive " +
                   "(and any app reading from it), then try again — or unplug and reconnect the drive.";

        // "The device is not ready" during `clean` means the drive stopped responding to writes
        // part-way through zeroing. The System log records it as VDS Basic Provider event 5,
        // "Cannot zero sectors on disk \\?\PhysicalDriveN". Critically, `clean` zeroes the partition
        // table FIRST, so by the time this appears the old layout is usually already gone — saying
        // only "failed" would imply the disk was left untouched.
        if (text.Contains("device is not ready", StringComparison.OrdinalIgnoreCase))
            return text.TrimEnd() + Environment.NewLine + Environment.NewLine +
                   "The drive stopped responding while its sectors were being zeroed. The partition " +
                   "table is written first, so the disk's previous partitions are most likely already " +
                   "gone even though this reported a failure — check the disk before assuming your " +
                   "data survived." + Environment.NewLine + Environment.NewLine +
                   "This is the drive or its connection, not permissions. Unplug it, plug it back in " +
                   "(ideally into a rear/motherboard USB port, not a hub or front panel), and try " +
                   "again. If it keeps happening, the flash controller is failing and the drive should " +
                   "be replaced — cheap USB sticks fail this way once their spare blocks run out.";

        return text;
    }

    /// <summary>
    /// Runs diskpart <c>clean</c>, retrying once on "the device is not ready".
    ///
    /// That error means the drive stopped responding to writes part-way through zeroing — commonly
    /// because it dropped off the USB bus and re-enumerated the moment its partition table vanished.
    /// The second attempt then finds a settled device and usually succeeds. Retrying is safe because
    /// <c>clean</c> is idempotent and the user has already confirmed a destructive operation; it is
    /// deliberately <b>not</b> retried for any other error, where repeating it would just be noise.
    /// </summary>
    private async Task<ShellResult> CleanWithRetryAsync(
        SystemState state, IProgress<OpProgress> progress, CancellationToken ct)
    {
        var result = await DiskPartRunner.RunAsync(BuildCleanScript(), ct).ConfigureAwait(false);
        if (result.Success) return result;

        var text = result.Error.Length > 0 ? result.Error : result.Output;
        if (!text.Contains("device is not ready", StringComparison.OrdinalIgnoreCase)) return result;

        Log.Warning("Disk {Disk}: clean reported \"device is not ready\"; letting the device settle and " +
                    "retrying once", Settings.DiskNumber);
        progress.Report(new OpProgress("The drive stopped responding — retrying…", 0.2, Describe().Title));

        // Give the device time to re-enumerate, then release whatever Windows re-mounted in the interim.
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        DiskVolumeReleaser.Refresh(Settings.DiskNumber);
        ReleaseVolumes(_inspector.Capture(probeLinuxToolchain: false));

        return await DiskPartRunner.RunAsync(BuildCleanScript(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dismounts the disk's volumes so the sector writes that follow are not refused. Best-effort by
    /// design: if a volume cannot be released, the operation still runs and fails with its own, more
    /// specific error rather than being blocked here.
    /// </summary>
    private void ReleaseVolumes(SystemState state)
    {
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return;
        foreach (var line in DiskVolumeReleaser.Release(disk)) Log.Information("{Step}", line);
    }

    /// <summary>
    /// Waits for Windows to surface the partition diskpart just created, then returns it. The largest
    /// partition is the one we made — a GPT disk can also carry small reserved partitions.
    /// </summary>
    private async Task<(PhysicalDiskInfo Disk, PartitionInfo Partition)?> WaitForCleanPartitionAsync(
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var state = _inspector.Capture(probeLinuxToolchain: false);
            var disk = state.FindDisk(Settings.DiskNumber);
            var biggest = disk?.Partitions
                .Where(p => !p.IsUnallocated)
                .OrderByDescending(p => p.SizeBytes)
                .FirstOrDefault();

            if (disk is not null && biggest is not null && biggest.SizeBytes >= Settings.FileSystem.MinimumSizeBytes())
                return (disk, biggest);

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<VerifyResult> VerifyAsync()
    {
        var result = Settings.FileSystem.IsLinux()
            ? await VerifyLinuxAsync().ConfigureAwait(false)
            : VerifyWindowsFilesystem();

        // A filesystem can be perfectly written onto a disk that refused the requested partition table.
        // Surface that as a finding rather than letting the plan and the disk quietly disagree.
        if (await ReportSchemeMismatchAsync().ConfigureAwait(false) is { } mismatch)
            return VerifyResult.Fail(result.Findings.Append(mismatch).ToArray());

        return result;
    }

    private VerifyResult VerifyWindowsFilesystem()
    {
        var state = _inspector.Capture(probeLinuxToolchain: false);
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return VerifyResult.Fail("Disk vanished after format.");

        var expected = Settings.FileSystem.ToFormatName();
        var match = disk.Partitions
            .Where(p => Settings.Scope == FormatScope.CleanWholeDisk || p.PartitionNumber == Settings.PartitionNumber)
            .Any(p => string.Equals(p.Volume?.FileSystem, expected, StringComparison.OrdinalIgnoreCase));

        return match
            ? VerifyResult.Pass()
            : VerifyResult.Fail($"Post-format volume is not {expected} as expected.");
    }

    /// <summary>
    /// Verifies a Linux format from the on-disk signature. Windows reports these volumes as RAW by
    /// design, so asking Windows would always "fail" — the truth comes from blkid. The write path
    /// already read it back while the disk was attached; that result is reused, and only a Verify with
    /// no preceding Execute re-attaches the disk to look again.
    /// </summary>
    private async Task<VerifyResult> VerifyLinuxAsync()
    {
        var expected = Settings.FileSystem.ToFormatName();
        var outcome = _linuxResult;

        if (outcome is null || outcome.DetectedType is null)
        {
            var request = _linuxRequest ?? BuildVerifyRequest();
            if (request is null)
                return VerifyResult.Fail(
                    "Could not locate the formatted partition to verify it — rescan and check the disk manually.");
            outcome = await _linuxBackend.ProbeSignatureAsync(request, CancellationToken.None).ConfigureAwait(false);
        }

        if (outcome.DetectedType is null)
            return VerifyResult.Fail(
                $"Could not read a filesystem signature back from {outcome.DeviceNode ?? "the partition"}" +
                (outcome.Error is { Length: > 0 } e ? $": {e}" : "."));

        if (!string.Equals(outcome.DetectedType, expected, StringComparison.OrdinalIgnoreCase))
            return VerifyResult.Fail(
                $"The partition now holds \"{outcome.DetectedType}\", not {expected} as expected.");

        var findings = new List<string>();
        if (Settings.Label.Length > 0 && !string.Equals(outcome.DetectedLabel, Settings.Label, StringComparison.Ordinal))
            findings.Add($"Label reads \"{outcome.DetectedLabel}\" rather than \"{Settings.Label}\".");

        // A label mismatch is worth reporting but is not a failed format.
        if (findings.Count > 0)
            Log.Warning("Linux format verify note: {Findings}", string.Join(" ", findings));

        return VerifyResult.Pass();
    }

    /// <summary>Rebuilds a verify request from current state, for a Verify with no Execute behind it.</summary>
    private LinuxFormatRequest? BuildVerifyRequest()
    {
        var state = _inspector.Capture(probeLinuxToolchain: false);
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return null;

        var part = Settings.Scope == FormatScope.CleanWholeDisk
            ? disk.Partitions.Where(p => !p.IsUnallocated).OrderByDescending(p => p.SizeBytes).FirstOrDefault()
            : disk.Partitions.FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);
        if (part is null) return null;

        return new LinuxFormatRequest
        {
            DiskNumber = disk.Number,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = part.OffsetBytes,
            PartitionSizeBytes = part.SizeBytes,
            FileSystem = Settings.FileSystem,
            Label = Settings.Label,
            VolumePaths = DiskVolumeReleaser.VolumePathsOn(disk),
            DiskIsRemovable = disk.IsRemovable
        };
    }

    /// <summary>
    /// The script/command that will run, for preview/testing. <paramref name="diskSizeBytes"/> only
    /// affects whether "Automatic" is forced to GPT for a disk MBR cannot address.
    /// </summary>
    public string PreviewScript(ulong diskSizeBytes = 0)
    {
        if (!Settings.FileSystem.IsLinux())
            return Settings.Scope == FormatScope.CleanWholeDisk
                ? BuildCleanScript() + Environment.NewLine + BuildWindowsCleanScript(diskSizeBytes)
                : BuildFormatVolumeScript();

        // Linux formats are two-stage: Windows-side preparation, then mkfs inside WSL. The mkfs line is
        // shown with a placeholder device because the real node is only known after identification.
        var prep = Settings.Scope == FormatScope.CleanWholeDisk
            ? BuildCleanScript() + Environment.NewLine + BuildLinuxPartitionScript(diskSizeBytes)
            : BuildLinuxPrepScript(null, null);
        var mkfs = string.Join(' ', WslLinuxFormatBackend.BuildMkfsArgv(
            new LinuxFormatRequest
            {
                DiskNumber = Settings.DiskNumber,
                DiskSizeBytes = 0,
                PartitionOffsetBytes = 0,
                PartitionSizeBytes = 0,
                FileSystem = Settings.FileSystem,
                Label = Settings.Label,
                BadBlockScan = EffectiveFullFormat
            },
            "<identified device>"));

        return prep + Environment.NewLine + "# then, inside WSL2:" + Environment.NewLine + mkfs;
    }

    private string BuildFormatVolumeScript()
    {
        var s = Settings;
        var fs = s.FileSystem.ToFormatName();
        var label = EscapeSingleQuoted(s.Label);
        var full = s.FullFormat ? "-Full " : "";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"Get-Partition -DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber} | " +
                      $"Format-Volume -FileSystem '{fs}' -NewFileSystemLabel '{label}' {full}-Force -Confirm:$false | Out-Null");
        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    /// <summary>
    /// Reports the partition scheme the disk actually ended up with when it differs from what was
    /// asked for, or null when it matches (or nothing specific was asked for). Windows can re-initialize
    /// a cleaned removable disk to MBR on its own, so a GPT request is not a guarantee — and claiming
    /// success on a disk that is not GPT would be exactly the kind of optimism this codebase refuses.
    /// </summary>
    private async Task<string?> ReportSchemeMismatchAsync()
    {
        if (Settings.Scope != FormatScope.CleanWholeDisk) return null;
        if (Settings.PartitionScheme is PartitionSchemeChoice.Automatic) return null;

        var wanted = Settings.PartitionScheme == PartitionSchemeChoice.Gpt
            ? PartitionStyle.Gpt
            : PartitionStyle.Mbr;

        var state = await Task.Run(() => _inspector.Capture(probeLinuxToolchain: false)).ConfigureAwait(false);
        var actual = state.FindDisk(Settings.DiskNumber)?.PartitionStyle;
        if (actual is null || actual == wanted) return null;

        return $"Disk {Settings.DiskNumber} was formatted, but its partition table is {actual}, not the " +
               $"{wanted} that was requested. Windows re-initializes removable disks to MBR on its own.";
    }

    /// <summary>
    /// Clean-disk preparation, stage 1: wipe the partition table. Nothing else — no <c>convert</c>, no
    /// <c>format</c>, no <c>assign</c>. diskpart is kept for the wipe because it is the reliable way to
    /// do it, but everything after depends on the disk's resulting state, which diskpart cannot branch on.
    /// </summary>
    private string BuildCleanScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {Settings.DiskNumber}");
        sb.AppendLine("clean");
        sb.AppendLine("exit");
        return sb.ToString();
    }

    /// <summary>
    /// The partition style to insist on, or null for "whatever Windows settles on". Automatic still
    /// forces GPT above 2 TiB, because MBR simply cannot address the disk.
    /// </summary>
    private PartitionStyle? RequestedStyle(ulong diskSizeBytes) => Settings.PartitionScheme switch
    {
        PartitionSchemeChoice.Gpt => PartitionStyle.Gpt,
        PartitionSchemeChoice.Mbr => PartitionStyle.Mbr,
        _ => diskSizeBytes > CreatePartitionOperation.MbrMaxAddressable ? PartitionStyle.Gpt : null
    };

    /// <summary>
    /// Stage 2a: get the disk onto the requested partition table.
    ///
    /// This is a retry loop rather than one <c>Initialize-Disk</c> because Windows re-initializes a
    /// freshly cleaned <b>removable</b> disk as MBR by itself, sometimes before we can set GPT — that
    /// race is what defeated an earlier attempt on a real USB stick. <c>Initialize-Disk</c> only accepts
    /// a RAW disk, so each attempt clears the disk back to RAW first. The loop ends by verifying the
    /// style really is what was asked for and throwing a specific message if Windows won, rather than
    /// carrying on and reporting a success the disk does not reflect.
    /// </summary>
    private string BuildSchemeScript(ulong diskSizeBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$n = {Settings.DiskNumber}");
        sb.AppendLine("Update-HostStorageCache -ErrorAction SilentlyContinue");

        if (RequestedStyle(diskSizeBytes) is not { } want)
        {
            // Automatic: initialize only if the disk is still RAW, and accept whatever Windows picks.
            sb.AppendLine("if ((Get-Disk -Number $n).PartitionStyle -eq 'RAW') {");
            sb.AppendLine("  try { Initialize-Disk -Number $n -PartitionStyle GPT -ErrorAction Stop | Out-Null }");
            sb.AppendLine("  catch { if ($_.Exception.Message -notmatch 'already been initialized') { throw } }");
            sb.AppendLine("  Start-Sleep -Milliseconds 300");
            sb.AppendLine("}");
            sb.AppendLine("if ((Get-Disk -Number $n).PartitionStyle -eq 'RAW') { throw \"Disk $n could not be initialized.\" }");
            return sb.ToString();
        }

        var style = want == PartitionStyle.Gpt ? "GPT" : "MBR";
        sb.AppendLine($"$want = '{style}'");
        sb.AppendLine($"$convert = '{style.ToLowerInvariant()}'");
        sb.AppendLine("for ($i = 0; $i -lt 6; $i++) {");
        sb.AppendLine("  $style = (Get-Disk -Number $n).PartitionStyle");
        sb.AppendLine("  if ($style -eq $want) { break }");
        // RAW is the only state Initialize-Disk accepts. On removable media Windows often beats us to
        // it and initializes as MBR — that is fine, the convert below handles it.
        sb.AppendLine("  if ($style -eq 'RAW') {");
        sb.AppendLine("    try { Initialize-Disk -Number $n -PartitionStyle $want -ErrorAction Stop | Out-Null }");
        sb.AppendLine("    catch { if ($_.Exception.Message -notmatch 'already been initialized') { throw } }");
        sb.AppendLine("    Start-Sleep -Milliseconds 400");
        sb.AppendLine("    continue");
        sb.AppendLine("  }");
        // An EMPTY disk of the wrong style is exactly what diskpart's `convert` wants. This is the step
        // that wins the race Initialize-Disk keeps losing: verified on a real USB stick that Windows
        // forced back to MBR on all five Initialize-Disk attempts, then converted first time.
        sb.AppendLine("  $dp = [System.IO.Path]::GetTempFileName()");
        sb.AppendLine("  Set-Content -Path $dp -Encoding ASCII -Value \"select disk $n`r`nconvert $convert\"");
        sb.AppendLine("  & diskpart.exe /s $dp | Out-Null");
        sb.AppendLine("  Remove-Item $dp -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  Start-Sleep -Milliseconds 600");
        sb.AppendLine("  Update-HostStorageCache -ErrorAction SilentlyContinue");
        sb.AppendLine("}");
        sb.AppendLine("$style = (Get-Disk -Number $n).PartitionStyle");
        sb.AppendLine("if ($style -ne $want) { throw \"Disk $n came back as $style, not $want. " +
                      "diskpart can only convert an EMPTY disk, so this usually means a partition " +
                      "reappeared on it mid-operation.\" }");
        return sb.ToString();
    }

    /// <summary>
    /// Clean-disk preparation, stage 2: GPT + one partition spanning the disk, tagged as Linux
    /// filesystem data and given no drive letter (Windows can't read the filesystem, and a letter would
    /// only invite Explorer to "fix" it).
    ///
    /// This is PowerShell rather than diskpart for one specific reason: <c>clean</c> leaves a RAW disk
    /// on removable media but an empty MBR disk on some fixed/virtual disks, and diskpart's
    /// <c>convert gpt</c> accepts only the latter ("The disk you specified is not MBR formatted").
    /// Normalising to RAW and initializing explicitly removes that state-dependence entirely.
    /// </summary>
    private string BuildLinuxPartitionScript(ulong diskSizeBytes)
    {
        var s = Settings;
        var gpt = s.FileSystem.PreferredGptType() ?? FileSystemTypeExtensions.LinuxFilesystemDataGuid;
        var mbr = s.FileSystem.PreferredMbrType() ?? FileSystemTypeExtensions.LinuxMbrType;

        var sb = new StringBuilder();
        sb.Append(BuildSchemeScript(diskSizeBytes));

        // With no explicit choice, take whichever scheme Windows settled on and tag the partition to
        // match — MBR with type 0x83 is a perfectly normal, universally readable Linux disk.
        sb.AppendLine("$style = (Get-Disk -Number $n).PartitionStyle");
        sb.Append(BuildCreateMaxPartitionScript());

        // Tag it Linux either way, so Windows leaves the partition alone instead of offering to format it.
        sb.AppendLine($"if ($style -eq 'GPT') {{ $p | Set-Partition -GptType '{{{gpt:D}}}' }}");
        sb.AppendLine($"else {{ $p | Set-Partition -MbrType {mbr} }}");
        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    /// <summary>
    /// Stage 2b for a Windows filesystem: one partition spanning the disk, formatted and given a
    /// letter. This replaced diskpart's <c>create partition</c>/<c>format</c>/<c>assign</c> so the
    /// scheme choice above is honoured — diskpart's <c>convert gpt</c> accepts only an empty MBR disk
    /// and fails on the RAW disk that <c>clean</c> leaves on removable media.
    /// </summary>
    private string BuildWindowsCleanScript(ulong diskSizeBytes)
    {
        var s = Settings;
        var sb = new StringBuilder();
        sb.Append(BuildSchemeScript(diskSizeBytes));
        sb.Append(BuildCreateMaxPartitionScript());

        var full = s.FullFormat ? "-Full " : "";
        sb.AppendLine($"$p | Format-Volume -FileSystem '{s.FileSystem.ToFormatName()}' " +
                      $"-NewFileSystemLabel '{EscapeSingleQuoted(s.Label)}' {full}-Force -Confirm:$false | Out-Null");
        // A data partition is no use without a way to reach it; let Windows pick the next free letter.
        sb.AppendLine("try { $p | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction Stop } catch { }");
        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    /// <summary>
    /// One partition spanning the disk. Right after a clean, New-Partition can briefly report "Not
    /// enough available capacity" while Windows catches up with the new layout — retry rather than
    /// failing the whole operation.
    /// </summary>
    private static string BuildCreateMaxPartitionScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$p = $null");
        sb.AppendLine("for ($i = 0; $i -lt 12 -and -not $p; $i++) {");
        sb.AppendLine("  try { $p = New-Partition -DiskNumber $n -UseMaximumSize } catch { Start-Sleep -Milliseconds 500 }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $p) { throw \"Could not create a partition on disk $n after clearing it.\" }");
        return sb.ToString();
    }

    /// <summary>
    /// Reformat-in-place preparation: drop any drive letter and re-tag the partition type, so Windows
    /// stops treating the extent as a basic-data volume before Linux writes to it. Access paths are
    /// enumerated rather than read from DriveLetter — a partition can carry mount-point paths too.
    /// </summary>
    private string BuildLinuxPrepScript(PhysicalDiskInfo? disk, PartitionInfo? partition)
    {
        var s = Settings;
        var mbr = disk?.PartitionStyle == PartitionStyle.Mbr;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$p = Get-Partition -DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber}");

        // Offset is re-asserted here as well: if an earlier op in the batch shifted the layout, the
        // partition number alone could point somewhere else by now.
        if (partition is not null)
            sb.AppendLine($"if ($p.Offset -ne {partition.OffsetBytes}) {{ " +
                          $"throw \"Partition {s.PartitionNumber} is at offset $($p.Offset), expected {partition.OffsetBytes} — aborting.\" }}");

        sb.AppendLine("foreach ($ap in @($p.AccessPaths)) { if ($ap -match '^[A-Za-z]:\\\\$') { " +
                      $"Remove-PartitionAccessPath -DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber} " +
                      "-AccessPath $ap } }");

        if (mbr && s.FileSystem.PreferredMbrType() is { } mbrType)
            sb.AppendLine($"Set-Partition -DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber} " +
                          $"-MbrType {mbrType}");
        else if (s.FileSystem.PreferredGptType() is { } gpt)
            sb.AppendLine($"Set-Partition -DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber} " +
                          $"-GptType '{{{gpt:D}}}'");

        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    /// <summary>
    /// Refuses a label the target filesystem cannot hold, before anything is erased. Public because the
    /// rules differ per filesystem family and are worth testing directly (the engine has no
    /// InternalsVisibleTo).
    /// </summary>
    public static string? LabelError(string label, FileSystemType fs)
    {
        var max = fs.MaxLabelLength();

        if (fs.IsLinux())
        {
            // mke2fs and friends measure labels in bytes, so a UTF-8 label can overflow well before
            // it looks too long on screen.
            var bytes = Encoding.UTF8.GetByteCount(label);
            if (bytes > max)
                return $"Label is too long — {fs.ToFormatName()} allows at most {max} bytes" +
                       (bytes != label.Length ? $" and this label needs {bytes}." : ".");
            if (label.Any(char.IsControl))
                return "Label contains control characters that are not allowed.";
            if (label.Contains('/'))
                return "Label cannot contain '/'.";
            return null;
        }

        if (label.Length > max)
            return $"Label is too long — {fs.ToFormatName()} allows at most {max} characters.";
        if (label.Any(c => c is '\'' or '"' or '`' or '\\' or '/' or '*' or '?' or '<' or '>' or '|' or ':'))
            return "Label contains characters that are not allowed.";
        return null;
    }

    /// <summary>mkfs.xfs/btrfs/f2fs refuse small volumes outright — say so before erasing anything.</summary>
    private string? MinimumSizeError(ulong sizeBytes)
    {
        var min = Settings.FileSystem.MinimumSizeBytes();
        return sizeBytes < min
            ? $"{Settings.FileSystem.ToFormatName()} needs at least {Bytes(min)} — this target is {Bytes(sizeBytes)}."
            : null;
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

/// <summary>Parameters for a format operation, all user-selectable in the UI.</summary>
public sealed record FormatVolumeSettings
{
    public required int DiskNumber { get; init; }
    public FormatScope Scope { get; init; } = FormatScope.ReformatPartition;

    /// <summary>
    /// Partition table to write when <see cref="Scope"/> is <see cref="FormatScope.CleanWholeDisk"/>.
    /// Ignored for a reformat, which never touches the partition table.
    /// </summary>
    public PartitionSchemeChoice PartitionScheme { get; init; } = PartitionSchemeChoice.Automatic;

    public int? PartitionNumber { get; init; }
    public string? TargetDriveLetter { get; init; }
    public FileSystemType FileSystem { get; init; } = FileSystemType.Exfat;
    public bool FullFormat { get; init; }
    public string Label { get; init; } = "";
    public bool AllowNonRemovable { get; init; }
}
