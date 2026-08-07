using System.Text;
using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Linux;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Creates a new partition in unallocated space and (optionally) formats it. Non-destructive: it only
/// ever consumes free space, and <see cref="Validate"/> refuses unless the requested extent lies wholly
/// inside a single unallocated region — that check is what stops this from overwriting a neighbour.
/// </summary>
public sealed class CreatePartitionOperation : IDiskOperation
{
    /// <summary>Partitions are aligned to 1 MiB (§1.7) — correct for 512e/4Kn and SSD erase blocks.</summary>
    public const ulong Alignment = 1024UL * 1024;

    /// <summary>Smallest partition we will create. Below this, no filesystem is usable.</summary>
    public const ulong MinSize = 8UL * 1024 * 1024;

    /// <summary>MBR cannot address beyond 2 TiB, and its table holds at most 4 primary partitions.</summary>
    public const ulong MbrMaxAddressable = 2UL * 1024 * 1024 * 1024 * 1024;
    public const int MbrMaxPrimaryPartitions = 4;

    /// <summary>
    /// How many partitions DiskForge will create on an MBR disk. Three, not four: when the table's
    /// last slot is filled, <c>New-Partition</c> creates an <b>extended</b> container (MBR type 0x05)
    /// rather than a primary, and an extended container is not a partition a filesystem can live in.
    /// Writing one there produces a partition Windows shows as empty and Linux cannot mount. Use GPT
    /// for more than three.
    /// </summary>
    public const int MbrMaxCreatablePartitions = 3;

    /// <summary>MBR partition types that are containers, not data extents: extended (CHS and LBA).</summary>
    private static readonly byte[] ExtendedMbrTypes = { 0x05, 0x0F };

    private readonly SystemInspector _inspector;
    private readonly ILinuxFormatBackend _linuxBackend;

    /// <summary>Read-back signature from a Linux format, reused by <see cref="VerifyAsync"/>.</summary>
    private LinuxFormatOutcome? _linuxResult;

    public CreatePartitionOperation(
        CreatePartitionSettings settings,
        SystemInspector? inspector = null,
        ILinuxFormatBackend? linuxBackend = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
        // Native first: ext2/3/4 are written by DiskForge itself with no external dependency.
        // WSL stays available purely as a fallback for btrfs/XFS/F2FS, which we do not write.
        _linuxBackend = linuxBackend ?? new NativeLinuxFormatBackend(new WslLinuxFormatBackend());
    }

    public CreatePartitionSettings Settings { get; }

    public OperationDescriptor Describe()
    {
        var s = Settings;
        var letter = s.DriveLetter is { } l ? $" as {l}:" : "";
        var title = s.FormatNew
            ? $"Create {Bytes(s.SizeBytes)} {s.FileSystem.ToFormatName()} partition{letter} on disk {s.DiskNumber}"
            : $"Create {Bytes(s.SizeBytes)} partition{letter} on disk {s.DiskNumber} (unformatted)";
        return new OperationDescriptor(title, DescribeDetail(), IsDestructive: false, s.DiskNumber);
    }

    private string DescribeDetail()
    {
        var s = Settings;
        var detail = $"Uses free space at offset {Bytes(s.OffsetBytes)}. Existing partitions are not touched.";
        return s.FormatNew ? detail : detail + " The new partition is left unformatted.";
    }

    public DriveCapability RequiredCapabilities() => Settings.FormatNew
        ? DriveCapability.PartitionEdit | DriveCapability.Format
        : DriveCapability.PartitionEdit;

    /// <summary>Preview: a new partition appears in the chosen free space.</summary>
    public IReadOnlyList<LayoutChange> PlanLayoutChanges()
    {
        var s = Settings;
        var linux = s.FormatNew && s.FileSystem.IsLinux();
        return new[]
        {
            new LayoutChange
            {
                Kind = LayoutChangeKind.CreatePartition,
                DiskNumber = s.DiskNumber,
                NewOffsetBytes = s.OffsetBytes,
                NewSizeBytes = s.SizeBytes,
                NewKind = linux ? PartitionKind.Linux : PartitionKind.Basic,
                FileSystem = s.FormatNew ? s.FileSystem.ToFormatName() : null,
                Label = s.Label,
                DriveLetter = s.DriveLetter,
                Note = s.FormatNew
                    ? $"Queued: new {Bytes(s.SizeBytes)} {s.FileSystem.ToFormatName()} partition"
                    : $"Queued: new {Bytes(s.SizeBytes)} partition (unformatted)"
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

        // --- Hard guards: never repartition the disk that runs Windows (§1.4) ---
        if (disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number)
            return ValidationResult.Fail(
                $"Refusing to create a partition on disk {disk.Number} — it is the system/boot disk that runs Windows.");

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
                    "Partitioning it is blocked. Enable 'Include internal disks' only if you are certain.");
            warnings.Add($"Disk {disk.Number} is an INTERNAL disk — double-check this is really the drive you mean.");
        }

        // --- Capability gate (§1A.7) ---
        var required = RequiredCapabilities();
        foreach (var cap in new[] { DriveCapability.PartitionEdit, DriveCapability.Format })
        {
            if (!required.HasFlag(cap)) continue;
            if (!disk.Capabilities.Has(cap))
                return ValidationResult.MissingCapability(cap,
                    disk.Capabilities.ReasonUnavailable(cap) ?? $"{cap} is not available on this disk");
        }

        // --- Geometry: alignment, minimum size, and "must fit a free gap" ---
        if (s.OffsetBytes % Alignment != 0)
            errors.Add($"Start offset must be aligned to {Bytes(Alignment)} — {Bytes(s.OffsetBytes)} is not.");
        if (s.SizeBytes < MinSize)
            errors.Add($"Partition is too small — the minimum is {Bytes(MinSize)}.");
        if (s.SizeBytes % Alignment != 0)
            warnings.Add($"Size is not a multiple of {Bytes(Alignment)}; Windows may round it down.");

        var end = s.OffsetBytes + s.SizeBytes;
        if (end > disk.SizeBytes)
            errors.Add($"The partition would extend past the end of the disk " +
                       $"({Bytes(end)} > {Bytes(disk.SizeBytes)}).");

        // This is the anti-clobber gate: the requested extent must sit inside ONE unallocated region.
        // Overlapping any real partition is refused outright rather than trimmed.
        var overlapped = disk.Partitions.FirstOrDefault(
            p => !p.IsUnallocated && s.OffsetBytes < p.EndBytes && end > p.OffsetBytes);
        if (overlapped is not null)
            errors.Add($"That space is already used by partition {overlapped.PartitionNumber} " +
                       $"({Bytes(overlapped.OffsetBytes)}–{Bytes(overlapped.EndBytes)}). Choose free space.");
        else
        {
            var gap = disk.Partitions.FirstOrDefault(
                p => p.IsUnallocated && s.OffsetBytes >= p.OffsetBytes && end <= p.EndBytes);
            if (gap is null)
                errors.Add("The requested space is not inside a single block of unallocated free space.");
        }

        // --- Partition-scheme constraints (§1.7) ---
        if (disk.PartitionStyle == PartitionStyle.Mbr)
        {
            var existing = disk.Partitions.Count(p => !p.IsUnallocated);
            if (existing >= MbrMaxCreatablePartitions)
                errors.Add(
                    $"This disk uses the MBR partition table, which has only {MbrMaxPrimaryPartitions} slots, " +
                    $"and it already has {existing} partition(s). DiskForge stops at {MbrMaxCreatablePartitions} " +
                    "because Windows turns the last MBR slot into an *extended* container rather than a real " +
                    "partition, and a filesystem cannot live in one. To get more partitions, erase the disk " +
                    "with Format → \"Clean whole disk\" on a drive Windows will initialize as GPT.");
            if (end > MbrMaxAddressable)
                errors.Add("MBR cannot address space beyond 2 TiB — convert the disk to GPT first.");
        }
        else if (disk.PartitionStyle is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            errors.Add($"Disk {disk.Number} has no partition table yet — initialize it as GPT or MBR first.");
        }

        // --- Filesystem + label + letter ---
        if (s.FormatNew)
        {
            if (s.FileSystem.ExceedsFat32Limit(s.SizeBytes))
                errors.Add("FAT32 cannot be created on a volume larger than 32 GB — choose exFAT or NTFS.");
            if (FormatVolumeOperation.LabelError(s.Label, s.FileSystem) is { } le)
                errors.Add(le);

            var minSize = s.FileSystem.MinimumSizeBytes();
            if (s.SizeBytes < minSize)
                errors.Add($"{s.FileSystem.ToFormatName()} needs at least {Bytes(minSize)} — " +
                           $"this partition would be {Bytes(s.SizeBytes)}.");

            // Linux toolchain gate: never offer a filesystem whose mkfs tool is not actually present.
            if (state.LinuxToolchain.BlockingReason(s.FileSystem) is { } linuxBlocked)
                errors.Add(linuxBlocked);

            if (s.FileSystem.IsLinux())
            {
                if (s.DriveLetter is not null)
                    errors.Add($"A drive letter cannot be assigned to a {s.FileSystem.ToFormatName()} partition — " +
                               "Windows has no driver for it. Choose \"(none)\".");
                if (disk.PartitionStyle == PartitionStyle.Mbr)
                    warnings.Add($"The new partition will be tagged MBR type 0x{s.FileSystem.PreferredMbrType():X2} " +
                                 "(Linux) so Windows leaves it alone.");
                warnings.Add($"Windows cannot read {s.FileSystem.ToFormatName()}: the new partition will show as " +
                             "unformatted/RAW in Explorer and Disk Management. That is expected — do NOT let " +
                             "Windows \"format\" it. Opening it in Explorer and trying to create a file there " +
                             "will fail with \"Error 0x8000FFFF: Catastrophic failure\" — that is Windows having " +
                             "no driver for the filesystem, not a damaged partition. Use it from Linux/WSL.");

                if (s.FullFormat && !s.FileSystem.SupportsBadBlockScan())
                    warnings.Add($"{s.FileSystem.MkfsTool()} has no bad-block scan, so \"full format\" cannot be " +
                                 "honoured here — a normal format will be performed instead.");
            }
        }

        if (s.DriveLetter is { } letter)
        {
            var norm = letter.ToUpperInvariant();
            if (norm.Length != 1 || norm[0] is < 'A' or > 'Z')
                errors.Add("Choose a drive letter A–Z.");
            else if (state.Disks.SelectMany(d => d.Partitions)
                     .Any(p => string.Equals(p.DriveLetter, norm, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Drive letter {norm}: is already in use.");
        }

        if (!state.IsElevated)
            warnings.Add("Administrator rights are required to apply — you will be prompted to elevate.");

        return errors.Count > 0 ? ValidationResult.Fail(errors.ToArray()) : ValidationResult.Ok(warnings.ToArray());
    }

    public SimulationResult Simulate(SystemState state)
    {
        var validation = Validate(state);
        if (!validation.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", validation.Errors) };

        var s = Settings;
        var steps = new List<string>
        {
            $"Create a {Bytes(s.SizeBytes)} partition on disk {s.DiskNumber} at offset {Bytes(s.OffsetBytes)}.",
        };
        if (s.DriveLetter is { } l) steps.Add($"Assign drive letter {l}:.");

        if (!s.FormatNew)
        {
            steps.Add("Leave the new partition unformatted (no filesystem).");
        }
        else if (s.FileSystem.IsLinux())
        {
            var distro = state.LinuxToolchain.ToolFor(s.FileSystem).Distro ?? "the WSL distribution";
            steps.Add($"Tag it as Linux filesystem data ({FileSystemTypeExtensions.LinuxFilesystemDataGuid}).");
            steps.Add($"Attach disk {s.DiskNumber} to the WSL2 kernel and confirm the device by disk size and " +
                      "partition offset before writing anything.");
            steps.Add($"Run {s.FileSystem.MkfsTool()} in {distro}" +
                      (s.Label.Length > 0 ? $" with label \"{s.Label}\"" : " with no label") + ".");
            steps.Add("Read the filesystem signature back with blkid, then detach the disk from WSL.");
        }
        else
        {
            steps.Add($"Format the new partition as {s.FileSystem.ToFormatName()} " +
                      $"({(s.FullFormat ? "full" : "quick")}), label \"{s.Label}\".");
        }

        steps.Add("No existing partition or file is modified.");

        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = validation.Warnings };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to create a partition. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before writing — never trust a stale plan.
        var fresh = _inspector.Capture();
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var started = DateTimeOffset.UtcNow;
        progress.Report(new OpProgress("Creating partition…", 0.1, Describe().Title));

        var style = fresh.FindDisk(Settings.DiskNumber)?.PartitionStyle ?? PartitionStyle.Gpt;
        var result = await PowerShellRunner.RunAsync(BuildScript(style), ct).ConfigureAwait(false);
        if (!result.Success)
            return OpResult.Failed($"Create partition failed: {(result.Error.Length > 0 ? result.Error : result.Output)}");

        Log.Information("Created partition on disk {Disk} at offset {Offset}", Settings.DiskNumber, Settings.OffsetBytes);

        // Confirm Windows made a real data partition and not an extended container. It silently does
        // the latter when it fills the last MBR slot, and a filesystem written into a container is
        // invisible to Windows and unmountable by Linux — so undo it rather than format into it.
        if (await RejectIfExtendedContainerAsync(ct).ConfigureAwait(false) is { } containerFailure)
            return containerFailure;

        // A Linux filesystem is written after the partition exists: Windows can create and tag the
        // extent, but only mkfs can put ext4/btrfs/xfs inside it.
        if (Settings.FormatNew && Settings.FileSystem.IsLinux())
        {
            var linuxFailure = await FormatNewPartitionWithLinuxAsync(progress, ct).ConfigureAwait(false);
            if (linuxFailure is not null) return linuxFailure;
        }

        progress.Report(new OpProgress("Partition created", 1.0));
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    /// <summary>
    /// Checks that what Windows actually created is a data partition. Filling the last slot of an MBR
    /// table makes <c>New-Partition</c> produce an <b>extended</b> container (type 0x05/0x0F) instead
    /// of a primary — it reports success, and the result looks like a partition in every listing, but
    /// it is a container for logical drives. A filesystem written into one is unreachable from both
    /// Windows and Linux.
    ///
    /// Returns null when the partition is sound, or the failure to report after removing it. The
    /// container is removed because leaving it would consume the last table slot for nothing.
    /// </summary>
    private async Task<OpResult?> RejectIfExtendedContainerAsync(CancellationToken ct)
    {
        var after = _inspector.Capture(probeLinuxToolchain: false);
        var created = after.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => !p.IsUnallocated && AbsDiff(p.OffsetBytes, Settings.OffsetBytes) <= Alignment);

        if (created?.MbrType is not { } mbrType || !ExtendedMbrTypes.Contains(mbrType))
            return null;

        Log.Warning("Disk {Disk}: Windows created an extended container (MBR type 0x{Type:X2}) at offset " +
                    "{Offset} instead of a primary partition; removing it", Settings.DiskNumber, mbrType,
            created.OffsetBytes);

        var cleanup = await PowerShellRunner.RunAsync(
            "$ErrorActionPreference='Stop'; " +
            $"Remove-Partition -DiskNumber {Settings.DiskNumber} -Offset {created.OffsetBytes} " +
            "-Confirm:$false; 'DISKFORGE_OK'", ct).ConfigureAwait(false);

        var undone = cleanup.Success
            ? "It has been removed, so the disk is unchanged."
            : "It could NOT be removed automatically — delete it in Disk Management.";

        return OpResult.Failed(
            $"Windows created an extended partition container on disk {Settings.DiskNumber} rather than a " +
            $"real partition, because this filled the last slot of the MBR partition table. No filesystem " +
            $"was written. {undone} " +
            "An MBR disk can hold at most three DiskForge-created partitions; use GPT for more.");
    }

    /// <summary>
    /// Hands the just-created extent to the Linux backend. Returns null on success, or the failure to
    /// report — the partition itself already exists at that point, so the message says so explicitly.
    /// </summary>
    private async Task<OpResult?> FormatNewPartitionWithLinuxAsync(
        IProgress<OpProgress> progress, CancellationToken ct)
    {
        progress.Report(new OpProgress($"Writing {Settings.FileSystem.ToFormatName()}…", 0.4));

        // Re-read the layout: New-Partition may nudge the start, and the backend matches on the real
        // on-disk offset rather than on what we asked for.
        var after = _inspector.Capture(probeLinuxToolchain: false);
        var disk = after.FindDisk(Settings.DiskNumber);
        var created = disk?.Partitions.FirstOrDefault(
            p => !p.IsUnallocated && AbsDiff(p.OffsetBytes, Settings.OffsetBytes) <= Alignment);

        if (disk is null || created is null)
            return OpResult.Failed(
                $"The partition was created on disk {Settings.DiskNumber} but could not be found again to " +
                $"format it. No filesystem was written — format it from the partition's details pane.");

        var request = new LinuxFormatRequest
        {
            DiskNumber = disk.Number,
            DiskSizeBytes = disk.SizeBytes,
            PartitionOffsetBytes = created.OffsetBytes,
            PartitionSizeBytes = created.SizeBytes,
            FileSystem = Settings.FileSystem,
            Label = Settings.Label,
            BadBlockScan = Settings.FullFormat && Settings.FileSystem.SupportsBadBlockScan(),
            VolumePaths = DiskVolumeReleaser.VolumePathsOn(disk),
            DiskIsRemovable = disk.IsRemovable
        };

        var outcome = await _linuxBackend.FormatAsync(request, progress, ct).ConfigureAwait(false);
        foreach (var line in outcome.Log) Log.Information("Linux format: {Step}", line);
        _linuxResult = outcome;

        if (!outcome.Success)
            return OpResult.Failed(
                $"The partition was created, but {Settings.FileSystem.MkfsTool()} could not format it: " +
                (outcome.Error ?? "the filesystem signature could not be read back."));

        Log.Information("Linux format succeeded on new partition ({Device} → {Type})",
            outcome.DeviceNode, outcome.DetectedType);
        return null;
    }

    public async Task<VerifyResult> VerifyAsync()
    {
        var state = _inspector.Capture(probeLinuxToolchain: false);
        var disk = state.FindDisk(Settings.DiskNumber);
        if (disk is null) return VerifyResult.Fail("Disk vanished after creating the partition.");

        // Windows may nudge the start slightly, so match on offset within one alignment unit
        // rather than demanding an exact hit.
        var created = disk.Partitions.FirstOrDefault(
            p => !p.IsUnallocated && AbsDiff(p.OffsetBytes, Settings.OffsetBytes) <= Alignment);
        if (created is null)
            return VerifyResult.Fail(
                $"No partition found at offset {Bytes(Settings.OffsetBytes)} on disk {Settings.DiskNumber}.");

        if (!Settings.FormatNew) return VerifyResult.Pass();

        var expected = Settings.FileSystem.ToFormatName();

        // Windows reports a Linux volume as RAW by design, so the signature has to come from blkid.
        if (Settings.FileSystem.IsLinux())
        {
            var outcome = _linuxResult;
            if (outcome?.DetectedType is null)
                outcome = await _linuxBackend.ProbeSignatureAsync(new LinuxFormatRequest
                {
                    DiskNumber = disk.Number,
                    DiskSizeBytes = disk.SizeBytes,
                    PartitionOffsetBytes = created.OffsetBytes,
                    PartitionSizeBytes = created.SizeBytes,
                    FileSystem = Settings.FileSystem,
                    Label = Settings.Label,
                    VolumePaths = DiskVolumeReleaser.VolumePathsOn(disk),
                    DiskIsRemovable = disk.IsRemovable
                }, CancellationToken.None).ConfigureAwait(false);

            if (outcome.DetectedType is null)
                return VerifyResult.Fail(
                    "The partition exists, but no filesystem signature could be read back from it" +
                    (outcome.Error is { Length: > 0 } e ? $": {e}" : "."));

            return string.Equals(outcome.DetectedType, expected, StringComparison.OrdinalIgnoreCase)
                ? VerifyResult.Pass()
                : VerifyResult.Fail($"New partition holds \"{outcome.DetectedType}\", expected {expected}.");
        }

        return string.Equals(created.Volume?.FileSystem, expected, StringComparison.OrdinalIgnoreCase)
            ? VerifyResult.Pass()
            : VerifyResult.Fail($"New partition's filesystem is \"{created.Volume?.FileSystem}\", expected {expected}.");
    }

    /// <summary>The script that will run, for preview/testing.</summary>
    public string PreviewScript() => BuildScript(PartitionStyle.Gpt);

    /// <summary>
    /// New-Partition takes an exact byte offset/size (diskpart only speaks whole MB), so it is the
    /// accurate tool here. The partition object is piped straight into Format-Volume so we never have
    /// to re-find it by number — that lookup is what races with Windows' automount.
    /// </summary>
    private string BuildScript(PartitionStyle style)
    {
        var s = Settings;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");

        var letter = s.DriveLetter is { } l ? $" -DriveLetter {l.ToUpperInvariant()}" : "";
        sb.AppendLine($"$p = New-Partition -DiskNumber {s.DiskNumber} -Offset {s.OffsetBytes} -Size {s.SizeBytes}{letter}");

        if (s.FormatNew && s.FileSystem.IsLinux())
        {
            // Tag the extent so Windows treats it as foreign and never offers to format it; the
            // filesystem itself is written by mkfs after this script returns.
            if (style == PartitionStyle.Mbr && s.FileSystem.PreferredMbrType() is { } mbrType)
                sb.AppendLine($"$p | Set-Partition -MbrType {mbrType}");
            else if (s.FileSystem.PreferredGptType() is { } gpt)
                sb.AppendLine($"$p | Set-Partition -GptType '{{{gpt:D}}}'");
        }
        else if (s.FormatNew)
        {
            var full = s.FullFormat ? "-Full " : "";
            sb.AppendLine($"$p | Format-Volume -FileSystem '{s.FileSystem.ToFormatName()}' " +
                          $"-NewFileSystemLabel '{EscapeSingleQuoted(s.Label)}' {full}-Force -Confirm:$false | Out-Null");
        }

        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    private static ulong AbsDiff(ulong a, ulong b) => a > b ? a - b : b - a;

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

/// <summary>Parameters for creating a partition in unallocated space.</summary>
public sealed record CreatePartitionSettings
{
    public required int DiskNumber { get; init; }

    /// <summary>Byte offset of the new partition's start; must be 1 MiB-aligned and inside a free gap.</summary>
    public required ulong OffsetBytes { get; init; }

    public required ulong SizeBytes { get; init; }

    /// <summary>Letter to assign, or null to create the partition without one.</summary>
    public string? DriveLetter { get; init; }

    public bool FormatNew { get; init; } = true;
    public FileSystemType FileSystem { get; init; } = FileSystemType.Exfat;
    public bool FullFormat { get; init; }
    public string Label { get; init; } = "";
    public bool AllowNonRemovable { get; init; }
}
