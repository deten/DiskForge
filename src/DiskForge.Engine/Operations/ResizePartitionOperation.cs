using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using DiskForge.Core.Planning;
using DiskForge.Engine.Linux.Ext;
using DiskForge.Engine.Native;
using Serilog;

namespace DiskForge.Engine.Operations;

/// <summary>
/// Grows or shrinks an existing partition, keeping its data.
///
/// The partition and the filesystem inside it have to move together. Windows does that for NTFS:
/// <c>Resize-Partition</c> resizes the volume and the extent as one step, and
/// <c>Get-PartitionSupportedSize</c> reports the real bounds the filesystem allows. It does <b>not</b>
/// do it for exFAT, FAT32 or any Linux filesystem, where changing the extent alone would leave a
/// filesystem that believes it is a different size than it is. Those are refused with a specific
/// reason rather than attempted, because the failure mode is silent data loss.
///
/// Growing is non-destructive. Shrinking is classified destructive: it is reversible only if nothing
/// needed the space, and the guard against shrinking over live data is the filesystem's own minimum,
/// which is queried fresh immediately before the write.
/// </summary>
public sealed class ResizePartitionOperation : IDiskOperation
{
    /// <summary>Matches CreatePartition: partitions stay on 1 MiB boundaries.</summary>
    public const ulong Alignment = CreatePartitionOperation.Alignment;

    /// <summary>Filesystems Windows can resize in place along with their partition.</summary>
    private static readonly string[] ResizableFileSystems = { "NTFS" };

    private readonly SystemInspector _inspector;

    public ResizePartitionOperation(ResizePartitionSettings settings, SystemInspector? inspector = null)
    {
        Settings = settings;
        _inspector = inspector ?? new SystemInspector();
    }

    public ResizePartitionSettings Settings { get; }

    public bool IsShrink(SystemState state) =>
        FindPartition(state) is { } p && Settings.NewSizeBytes < p.SizeBytes;

    public OperationDescriptor Describe()
    {
        var s = Settings;
        var letter = s.DriveLetter is { } l ? $" ({l}:)" : "";
        var direction = s.CurrentSizeBytes is { } cur
            ? (s.NewSizeBytes < cur ? "Shrink" : "Extend")
            : "Resize";

        return new OperationDescriptor(
            $"{direction} partition {s.PartitionNumber}{letter} on disk {s.DiskNumber} to {Bytes(s.NewSizeBytes)}",
            direction == "Shrink"
                ? "Shrinks the filesystem and then the partition. Files stay, but the freed space is " +
                  "released and anything relying on it is gone."
                : "Extends the partition into the free space immediately after it. Existing files are untouched.",
            // A shrink can lose data if the filesystem's own minimum is wrong; an extend cannot.
            IsDestructive: s.CurrentSizeBytes is { } c && s.NewSizeBytes < c,
            s.DiskNumber);
    }

    public DriveCapability RequiredCapabilities() => DriveCapability.PartitionEdit;

    /// <summary>Preview: the extent changes size in place; the free space after it shrinks or grows.</summary>
    public IReadOnlyList<LayoutChange> PlanLayoutChanges() => new[]
    {
        new LayoutChange
        {
            Kind = LayoutChangeKind.ResizePartition,
            DiskNumber = Settings.DiskNumber,
            TargetPartitionNumber = Settings.PartitionNumber,
            TargetOffsetBytes = Settings.OffsetBytes,
            NewSizeBytes = Settings.NewSizeBytes,
            Note = $"Queued: resize to {Bytes(Settings.NewSizeBytes)}"
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

        // --- Hard guards: never repartition the disk that runs Windows (§1.4) ---
        if (disk.IsSystemDisk || disk.IsBootDisk || state.SystemDiskNumber == disk.Number)
            return ValidationResult.Fail(
                $"Refusing to resize a partition on disk {disk.Number} — it is the system/boot disk that runs Windows.");

        if (disk.IsReadOnly) errors.Add($"Disk {disk.Number} is read-only.");
        if (disk.IsOffline) errors.Add($"Disk {disk.Number} is offline — bring it online first.");

        // --- Removable-only safety default ---
        if (!disk.IsRemovable)
        {
            if (!s.AllowNonRemovable)
                return ValidationResult.Fail(
                    $"Safety: disk {disk.Number} ({disk.FriendlyName}) is an INTERNAL, non-removable disk. " +
                    "Resizing its partitions is blocked. Enable 'Include internal disks' only if you are certain.");
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

        // The offset is captured when staged; a mismatch means the layout moved and the partition
        // number may now point somewhere else entirely.
        if (s.OffsetBytes is { } expected && part.OffsetBytes != expected)
            return ValidationResult.Fail(
                $"Partition {s.PartitionNumber} is no longer at the offset it had when this operation was " +
                "staged — the disk layout changed. Rescan and stage it again.");

        // --- Never touch the pieces Windows needs to boot, or a recovery image (§1.4) ---
        if (part.Kind is PartitionKind.Efi or PartitionKind.MicrosoftReserved
            or PartitionKind.Recovery or PartitionKind.System || part.IsSystem || part.IsBoot)
            return ValidationResult.Fail(
                $"Refusing to resize partition {s.PartitionNumber} — it is a system/EFI/recovery partition.");

        // --- Encryption gate (§1A.3) ---
        if (part.Volume?.BitLocker is { } bl && (bl.IsProtected || bl.IsConverting))
            return ValidationResult.Fail(
                "The target volume is BitLocker-protected or mid-conversion. Suspend/decrypt BitLocker first (§1A.3).");

        // --- The filesystem has to be able to move with the extent ---
        if (FileSystemBlock(part) is { } fsBlocked) return ValidationResult.Fail(fsBlocked);

        // --- Geometry ---
        if (s.NewSizeBytes == part.SizeBytes)
            errors.Add($"Partition {s.PartitionNumber} is already {Bytes(part.SizeBytes)}.");
        if (s.NewSizeBytes < CreatePartitionOperation.MinSize)
            errors.Add($"A partition cannot be smaller than {Bytes(CreatePartitionOperation.MinSize)}.");
        if (s.NewSizeBytes % Alignment != 0)
            warnings.Add($"Size is not a multiple of {Bytes(Alignment)}; Windows will round it.");

        if (s.NewSizeBytes > part.SizeBytes)
            ValidateGrowth(disk, part, s.NewSizeBytes, errors);
        else if (s.NewSizeBytes < part.SizeBytes)
            ValidateShrink(part, s.NewSizeBytes, errors, warnings);

        if (!state.IsElevated)
            warnings.Add("Administrator rights are required to apply — you will be prompted to elevate.");

        return errors.Count > 0 ? ValidationResult.Fail(errors.ToArray()) : ValidationResult.Ok(warnings.ToArray());
    }

    /// <summary>
    /// Growing consumes the free space immediately after the partition. A gap elsewhere on the disk is
    /// no use: a partition is one contiguous extent and Windows will not relocate it.
    /// </summary>
    private static void ValidateGrowth(
        PhysicalDiskInfo disk, PartitionInfo part, ulong newSize, List<string> errors)
    {
        var extra = newSize - part.SizeBytes;
        var newEnd = part.OffsetBytes + newSize;

        var blocker = disk.Partitions
            .Where(p => !p.IsUnallocated && p.PartitionNumber != part.PartitionNumber)
            .FirstOrDefault(p => p.OffsetBytes < newEnd && p.EndBytes > part.EndBytes);

        if (blocker is not null)
        {
            errors.Add(
                $"Cannot grow by {Bytes(extra)} — partition {blocker.PartitionNumber} starts at " +
                $"{Bytes(blocker.OffsetBytes)}, immediately after this one. Free space has to be directly " +
                "after a partition to extend into it.");
            return;
        }

        var usableEnd = DiskMap.UsableEnd(disk.SizeBytes, disk.PartitionStyle);
        if (newEnd > usableEnd)
            errors.Add(
                $"Cannot grow to {Bytes(newSize)} — it would run past the end of the usable disk " +
                $"({Bytes(usableEnd)}).");
    }

    /// <summary>
    /// Shrinking cannot go below what the filesystem is actually using. This is the staging-time
    /// estimate from the volume's own figures; the authoritative bound comes from
    /// <c>Get-PartitionSupportedSize</c> immediately before the write.
    /// </summary>
    private static void ValidateShrink(
        PartitionInfo part, ulong newSize, List<string> errors, List<string> warnings)
    {
        var vol = part.Volume;
        if (vol is null || !vol.UsageKnown)
        {
            warnings.Add(
                "How much of this partition is in use is not readable from Windows, so the shrink " +
                "limit will only be known at Apply. It will be refused there if it is too small.");
            return;
        }

        if (newSize < vol.UsedBytes)
        {
            errors.Add(
                $"Cannot shrink to {Bytes(newSize)} — {Bytes(vol.UsedBytes)} is in use on this volume. " +
                "Delete or move files first.");
            return;
        }

        // Filesystems need headroom beyond raw used bytes; NTFS in particular cannot always relocate
        // its own metadata. Flag a tight fit rather than letting Apply fail with a cryptic bound.
        var headroom = newSize - vol.UsedBytes;
        if (headroom < vol.SizeBytes / 10)
            warnings.Add(
                $"Only {Bytes(headroom)} would be free afterwards. Windows may refuse to shrink this far " +
                "if immovable files sit near the end of the volume; the exact limit is checked at Apply.");
    }

    /// <summary>Null when the filesystem can be resized, or the reason it cannot.</summary>
    private string? FileSystemBlock(PartitionInfo part)
    {
        var fs = part.Volume?.FileSystem;
        var growing = Settings.NewSizeBytes > part.SizeBytes;

        // ext is resized by DiskForge itself, in both directions. A shrink additionally requires the
        // space being cut away to be free already; that can only be known by reading the filesystem,
        // so it is checked at Apply rather than guessed at here.
        if (IsExtName(fs)) return null;

        if (part.Kind == PartitionKind.Linux || (fs is not null && IsLinuxName(fs)))
            return $"Resizing {fs ?? "this Linux filesystem"} is not supported yet. Windows cannot resize " +
                   "it, and doing it properly needs the filesystem's own resize logic. ext2, ext3 and " +
                   "ext4 can be grown. Otherwise back up, recreate the partition, and restore.";

        if (string.IsNullOrWhiteSpace(fs) || fs.Equals("RAW", StringComparison.OrdinalIgnoreCase))
            return "This partition has no filesystem Windows can read, so it cannot be resized safely. " +
                   "Delete and recreate it at the size you want instead.";

        if (!ResizableFileSystems.Contains(fs, StringComparer.OrdinalIgnoreCase))
            return $"Windows cannot resize {fs} in place. Only NTFS supports it. Moving the partition " +
                   $"boundary without moving the {fs} filesystem would corrupt it, so this is refused. " +
                   "Back up, recreate the partition at the size you want, and restore.";

        return null;
    }

    private static bool IsExtName(string? fs) =>
        fs is not null && fs.StartsWith("ext", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinuxName(string fs) =>
        fs.StartsWith("ext", StringComparison.OrdinalIgnoreCase) ||
        fs.Equals("btrfs", StringComparison.OrdinalIgnoreCase) ||
        fs.Equals("xfs", StringComparison.OrdinalIgnoreCase) ||
        fs.Equals("f2fs", StringComparison.OrdinalIgnoreCase) ||
        fs.Equals("swap", StringComparison.OrdinalIgnoreCase);

    public SimulationResult Simulate(SystemState state)
    {
        var validation = Validate(state);
        if (!validation.IsValid)
            return new SimulationResult { Feasible = false, BlockingReason = string.Join(" ", validation.Errors) };

        var part = FindPartition(state);
        var growing = part is not null && Settings.NewSizeBytes > part.SizeBytes;

        var steps = new List<string>
        {
            $"Query the supported size range for partition {Settings.PartitionNumber} on disk {Settings.DiskNumber}.",
            growing
                ? $"Extend the partition and its filesystem to {Bytes(Settings.NewSizeBytes)}, taking " +
                  $"{Bytes(Settings.NewSizeBytes - (part?.SizeBytes ?? 0))} from the free space after it."
                : $"Shrink the filesystem, then the partition, to {Bytes(Settings.NewSizeBytes)}, releasing " +
                  $"{Bytes((part?.SizeBytes ?? 0) - Settings.NewSizeBytes)} as free space.",
            "Re-read the partition afterwards and confirm the new size."
        };

        if (!growing)
            steps.Insert(1, "Refuse if the filesystem's own minimum is larger than the requested size.");

        return new SimulationResult { Feasible = true, PlannedSteps = steps, Warnings = validation.Warnings };
    }

    public async Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!Elevation.IsElevated())
            return OpResult.Failed("Administrator rights are required to resize a partition. Restart DiskForge as Administrator.");

        // Re-validate against a fresh snapshot immediately before writing — never trust a stale plan.
        var fresh = _inspector.Capture();
        var recheck = Validate(fresh);
        if (!recheck.IsValid)
            return OpResult.Failed("Preflight re-check failed: " + string.Join(" ", recheck.Errors));

        var started = DateTimeOffset.UtcNow;
        var target = FindPartition(fresh);
        var ext = IsExtName(target?.Volume?.FileSystem);

        // For ext, decide what the filesystem can do BEFORE the extent is touched, so a filesystem
        // that cannot be resized fails having changed nothing.
        var shrinking = target is not null && Settings.NewSizeBytes < target.SizeBytes;
        ExtGrowPlan? growPlan = null;
        ExtShrinkPlan? shrinkPlan = null;

        if (ext && target is not null)
        {
            progress.Report(new OpProgress("Checking the ext filesystem…", 0.1, Describe().Title));

            if (shrinking)
            {
                shrinkPlan = PlanExtShrink(fresh, target, out var blocked);
                if (shrinkPlan is null)
                    return OpResult.Failed($"The filesystem cannot be shrunk, so nothing was changed. {blocked}");

                // Shrink the filesystem FIRST. The reverse order would leave the partition smaller
                // than the filesystem inside it, which is exactly how a resize destroys data.
                progress.Report(new OpProgress("Shrinking the ext filesystem…", 0.3));
                if (ResizeExtFilesystem(target, shrinkPlan, null) is { } shrinkError)
                    return OpResult.Failed(
                        $"The ext filesystem could not be shrunk, so the partition was left alone: {shrinkError}");
            }
            else
            {
                growPlan = PlanExtGrow(fresh, target, out var blocked);
                if (growPlan is null)
                    return OpResult.Failed($"The filesystem cannot be grown, so nothing was changed. {blocked}");
            }
        }

        progress.Report(new OpProgress("Checking the supported size range…", 0.15, Describe().Title));

        // The filesystem's real bounds, not our estimate. Windows knows where its immovable files are.
        // It cannot answer for a filesystem it does not understand, so this is skipped for ext.
        var bounds = ext ? null : await QuerySupportedSizeAsync(ct).ConfigureAwait(false);
        if (bounds is { } b)
        {
            if (Settings.NewSizeBytes < b.Min)
                return OpResult.Failed(
                    $"Windows will not shrink this partition below {Bytes(b.Min)} — {Bytes(Settings.NewSizeBytes)} " +
                    "was requested. That limit is set by files the filesystem cannot relocate. Nothing was changed.");
            if (Settings.NewSizeBytes > b.Max)
                return OpResult.Failed(
                    $"Windows will not grow this partition beyond {Bytes(b.Max)} — {Bytes(Settings.NewSizeBytes)} " +
                    "was requested. Nothing was changed.");
        }

        progress.Report(new OpProgress("Resizing…", 0.4));

        var style = fresh.FindDisk(Settings.DiskNumber)?.PartitionStyle ?? PartitionStyle.Gpt;
        var result = await PowerShellRunner.RunAsync(BuildResizeScript(ext, style), ct).ConfigureAwait(false);
        if (!result.Success)
            return OpResult.Failed($"Resize failed: {(result.Error.Length > 0 ? result.Error : result.Output)}");

        // The extent is bigger now; make the filesystem claim the new space.
        if (growPlan is not null)
        {
            progress.Report(new OpProgress("Growing the ext filesystem…", 0.7));
            if (ResizeExtFilesystem(target!, null, growPlan) is { } growError)
                return OpResult.Failed(
                    $"The partition was resized to {Bytes(Settings.NewSizeBytes)}, but the ext filesystem " +
                    $"inside it could not be grown: {growError} The filesystem is still valid at its " +
                    "previous size; the extra space is simply unused.");
        }

        progress.Report(new OpProgress("Resize complete", 1.0));
        Log.Information("Resized partition {Part} on disk {Disk} to {Size} bytes",
            Settings.PartitionNumber, Settings.DiskNumber, Settings.NewSizeBytes);
        return OpResult.Ok(DateTimeOffset.UtcNow - started);
    }

    /// <summary>Basic data partition types, the only ones <c>Resize-Partition</c> will act on.</summary>
    private static readonly Guid BasicDataGpt = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private const byte BasicDataMbr = 0x07;

    /// <summary>
    /// The resize script.
    ///
    /// <c>Resize-Partition</c> refuses anything that is not a basic data partition: on a Linux-typed
    /// extent it fails with "This operation is only supported on data partitions", which is exactly
    /// what happened on a real USB stick. Windows is only being asked to move the extent boundary, not
    /// to understand the filesystem, so the partition is presented as basic data for the length of the
    /// call and put back afterwards. The restore sits in a PowerShell <c>finally</c> so it still runs
    /// if the resize throws — leaving a Linux partition tagged as basic data would invite Explorer to
    /// offer to format it.
    /// </summary>
    public string BuildResizeScript(bool retagAsBasicData, PartitionStyle style)
    {
        var s = Settings;
        var target = $"-DiskNumber {s.DiskNumber} -PartitionNumber {s.PartitionNumber}";
        var resize = $"Resize-Partition {target} -Size {s.NewSizeBytes}";

        if (!retagAsBasicData)
            return $"$ErrorActionPreference='Stop'; {resize}; 'DISKFORGE_OK'";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");

        if (style == PartitionStyle.Mbr)
        {
            sb.AppendLine($"$orig = (Get-Partition {target}).MbrType");
            sb.AppendLine("try {");
            sb.AppendLine($"  Set-Partition {target} -MbrType {BasicDataMbr}");
            sb.AppendLine($"  {resize}");
            sb.AppendLine("}");
            sb.AppendLine($"finally {{ Set-Partition {target} -MbrType $orig }}");
        }
        else
        {
            sb.AppendLine($"$orig = (Get-Partition {target}).GptType");
            sb.AppendLine("try {");
            sb.AppendLine($"  Set-Partition {target} -GptType '{{{BasicDataGpt:D}}}'");
            sb.AppendLine($"  {resize}");
            sb.AppendLine("}");
            sb.AppendLine($"finally {{ Set-Partition {target} -GptType $orig }}");
        }

        sb.AppendLine("'DISKFORGE_OK'");
        return sb.ToString();
    }

    /// <summary>
    /// Reads the ext superblock off the partition and works out whether it can be grown to the target
    /// size. Read-only: nothing is written here, so a refusal leaves the disk exactly as it was.
    /// </summary>
    private ExtGrowPlan? PlanExtGrow(SystemState state, PartitionInfo part, out string? blockedReason)
    {
        var disk = state.FindDisk(Settings.DiskNumber);
        var sector = (int)(disk?.LogicalSectorSize ?? 512);

        try
        {
            using var handle = RawDiskAccess.OpenRead(Settings.DiskNumber);
            using var stream = new RawPartitionStream(
                handle, (long)part.OffsetBytes, (long)part.SizeBytes, sector);

            return ExtResizer.TryPlanGrow(stream, Settings.NewSizeBytes, out blockedReason);
        }
        catch (Exception ex)
        {
            blockedReason = $"Its superblock could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Reads the ext superblock and works out whether the filesystem can be shrunk. Read-only, so a
    /// refusal leaves the disk untouched.
    /// </summary>
    private ExtShrinkPlan? PlanExtShrink(SystemState state, PartitionInfo part, out string? blockedReason)
    {
        var sector = (int)(state.FindDisk(Settings.DiskNumber)?.LogicalSectorSize ?? 512);

        try
        {
            using var handle = RawDiskAccess.OpenRead(Settings.DiskNumber);
            using var stream = new RawPartitionStream(
                handle, (long)part.OffsetBytes, (long)part.SizeBytes, sector);

            return ExtResizer.TryPlanShrink(stream, Settings.NewSizeBytes, out blockedReason);
        }
        catch (Exception ex)
        {
            blockedReason = $"Its superblock could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Applies a grow or a shrink to the ext filesystem. Exactly one plan is expected.
    ///
    /// The stream length differs by direction. A grow runs after the extent was enlarged, so it uses
    /// the partition's current (new) length. A shrink runs <i>before</i> the extent moves, so the
    /// partition is still the old, larger size, which is what the filesystem still occupies.
    /// </summary>
    private string? ResizeExtFilesystem(PartitionInfo staged, ExtShrinkPlan? shrink, ExtGrowPlan? grow)
    {
        try
        {
            var state = _inspector.Capture(probeLinuxToolchain: false);
            var disk = state.FindDisk(Settings.DiskNumber);
            var part = disk?.Partitions.FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber)
                       ?? staged;
            if (disk is null) return "the disk could not be found again.";

            // Windows refuses sector writes under a mounted volume, and it may have mounted the
            // partition in the moment after a resize.
            foreach (var line in DiskVolumeReleaser.Release(disk)) Log.Information("{Step}", line);

            using var handle = RawDiskAccess.OpenWrite(Settings.DiskNumber);
            using var stream = new RawPartitionStream(
                handle, (long)part.OffsetBytes, (long)part.SizeBytes,
                (int)(disk.LogicalSectorSize ?? 512));

            if (shrink is not null)
            {
                ExtResizer.Shrink(stream, shrink);
                Log.Information("Shrank ext filesystem on disk {Disk} partition {Part} to {Blocks} blocks",
                    Settings.DiskNumber, Settings.PartitionNumber, shrink.NewTotalBlocks);
            }
            else if (grow is not null)
            {
                ExtResizer.Grow(stream, grow);
                Log.Information("Grew ext filesystem on disk {Disk} partition {Part} to {Blocks} blocks",
                    Settings.DiskNumber, Settings.PartitionNumber, grow.NewTotalBlocks);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Resizing the ext filesystem failed on disk {Disk}", Settings.DiskNumber);
            return ex.Message;
        }
    }

    /// <summary>
    /// Asks Windows for the smallest and largest this partition can be. Returns null when the query
    /// fails, which is treated as "unknown" rather than "no limit" — the resize itself still enforces it.
    /// </summary>
    private async Task<(ulong Min, ulong Max)?> QuerySupportedSizeAsync(CancellationToken ct)
    {
        var script =
            "$ErrorActionPreference='Stop'; " +
            $"$s = Get-PartitionSupportedSize -DiskNumber {Settings.DiskNumber} " +
            $"-PartitionNumber {Settings.PartitionNumber}; " +
            "\"$($s.SizeMin) $($s.SizeMax)\"";

        var result = await PowerShellRunner.RunAsync(script, ct).ConfigureAwait(false);
        if (!result.Success) return null;

        var parts = result.Output.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && ulong.TryParse(parts[0], out var min) && ulong.TryParse(parts[1], out var max)
            ? (min, max)
            : null;
    }

    public Task<VerifyResult> VerifyAsync()
    {
        var state = _inspector.Capture(probeLinuxToolchain: false);
        var part = FindPartition(state);

        if (part is null)
            return Task.FromResult(VerifyResult.Fail(
                $"Partition {Settings.PartitionNumber} was not found on disk {Settings.DiskNumber} after the resize."));

        // Windows rounds to its own allocation granularity, so accept anything within one alignment unit.
        var diff = part.SizeBytes > Settings.NewSizeBytes
            ? part.SizeBytes - Settings.NewSizeBytes
            : Settings.NewSizeBytes - part.SizeBytes;

        return Task.FromResult(diff <= Alignment
            ? VerifyResult.Pass()
            : VerifyResult.Fail(
                $"Partition {Settings.PartitionNumber} is {Bytes(part.SizeBytes)}, expected " +
                $"{Bytes(Settings.NewSizeBytes)}."));
    }

    private PartitionInfo? FindPartition(SystemState state) =>
        state.FindDisk(Settings.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.PartitionNumber == Settings.PartitionNumber);

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

/// <summary>Parameters for resizing a partition in place.</summary>
public sealed record ResizePartitionSettings
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }

    /// <summary>Target size. Windows rounds to its own granularity.</summary>
    public required ulong NewSizeBytes { get; init; }

    /// <summary>Offset the partition had when staged; re-checked so a shifted number cannot retarget.</summary>
    public ulong? OffsetBytes { get; init; }

    /// <summary>Size at staging time, used to describe the operation as a grow or a shrink.</summary>
    public ulong? CurrentSizeBytes { get; init; }

    /// <summary>Letter at staging time — for the description only.</summary>
    public string? DriveLetter { get; init; }

    public bool AllowNonRemovable { get; init; }
}
