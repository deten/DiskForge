using DiskForge.Core.Operations;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.Engine.Linux;

/// <summary>
/// Writes real Linux filesystems by handing the physical disk to the WSL2 kernel
/// (<c>wsl --mount --bare</c>) and running the distro's own mkfs on it. No filesystem is
/// re-implemented and no third-party binary is bundled — the tools that Linux itself uses do the work.
///
/// Safety shape of this class:
/// <list type="number">
/// <item>The device list is captured <b>before</b> attaching, so only a disk our own attach produced
/// is a candidate (<see cref="WslBlockDevices.MatchPartition"/>).</item>
/// <item>The candidate must match the target disk's size <i>and</i> expose a partition starting at the
/// exact byte offset the plan recorded. Anything ambiguous aborts before any write.</item>
/// <item>The disk is always detached again in a <c>finally</c>, and a disk we took offline in Windows
/// is always brought back online.</item>
/// </list>
/// </summary>
public sealed class WslLinuxFormatBackend : ILinuxFormatBackend
{
    /// <summary>udev needs a moment after attach before partition nodes exist.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);
    private const int SettleAttempts = 8;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    public async Task<LinuxFormatOutcome> FormatAsync(
        LinuxFormatRequest request, IProgress<OpProgress> progress, CancellationToken ct)
    {
        var fs = request.FileSystem;
        var tool = fs.MkfsTool();
        if (tool is null)
            return LinuxFormatOutcome.Failed($"{fs.ToFormatName()} is not a Linux filesystem.");

        var toolchain = LinuxToolchainProbe.Get();
        if (toolchain.BlockingReason(fs) is { } blocked)
            return LinuxFormatOutcome.Failed(blocked);

        var available = toolchain.ToolFor(fs);
        var distro = available.Distro!;

        // Removable media cannot be handed to the WSL2 VM at all (Hyper-V refuses it), so those go the
        // staged route: mkfs into a scratch VHDX, then write that image onto the partition ourselves.
        if (request.DiskIsRemovable)
            return await new VhdxStagedFormatter()
                .FormatAsync(request, distro, available.Path, progress, ct).ConfigureAwait(false);

        return await WithAttachedDiskAsync(request, distro, progress, async (device, log) =>
        {
            progress.Report(new OpProgress($"Running {tool} on {device}…", 0.55));

            // Prefer the absolute path the probe resolved: mkfs lives in /sbin or /usr/sbin, and this
            // formats with exactly the binary that was detected rather than whatever PATH resolves to.
            var argv = BuildMkfsArgv(request, device, available.Path);
            log.Add($"{distro}: {string.Join(' ', argv)}");

            // No timeout: a bad-block scan on a large disk legitimately runs for a long time.
            var mkfs = await WslCli.RunToolAsync(distro, argv, ct).ConfigureAwait(false);
            if (!mkfs.Success)
            {
                log.Add($"{tool} failed ({mkfs.ExitCode}): {Detail(mkfs)}");
                return LinuxFormatOutcome.Failed($"{tool} failed: {Detail(mkfs)}", log);
            }
            log.Add($"{tool} completed: {FirstLine(mkfs.Output)}");

            progress.Report(new OpProgress("Reading back the filesystem signature…", 0.9));
            var signature = await ReadSignatureAsync(distro, device, ct).ConfigureAwait(false);
            log.AddRange(signature.Log);

            return signature with { Success = true, Log = log };
        }).ConfigureAwait(false);
    }

    public Task<LinuxFormatOutcome> ProbeSignatureAsync(LinuxFormatRequest request, CancellationToken ct)
    {
        // A removable disk can never be attached to WSL, so blkid is not reachable — read the
        // superblock from the drive directly instead. This is also simply faster.
        if (request.DiskIsRemovable)
        {
            var signature = LinuxFsSignature.Read(request.DiskNumber, request.PartitionOffsetBytes);
            return Task.FromResult(signature is null
                ? LinuxFormatOutcome.Failed(
                    $"No Linux filesystem superblock was found on disk {request.DiskNumber} at " +
                    $"offset {request.PartitionOffsetBytes}.")
                : new LinuxFormatOutcome
                {
                    Success = true,
                    DetectedType = signature.Type,
                    DetectedLabel = signature.Label,
                    Uuid = signature.Uuid
                });
        }

        var toolchain = LinuxToolchainProbe.Get();
        var distro = toolchain.ToolFor(request.FileSystem).Distro ?? LinuxToolchainProbe.AnyDistro();
        if (distro is null)
            return Task.FromResult(LinuxFormatOutcome.Failed(
                "No WSL distribution is available to read the filesystem signature."));

        return WithAttachedDiskAsync(request, distro, new Progress<OpProgress>(), async (device, log) =>
        {
            var signature = await ReadSignatureAsync(distro, device, ct).ConfigureAwait(false);
            log.AddRange(signature.Log);
            return signature with { Log = log };
        });
    }

    /// <summary>
    /// Attaches the disk, positively identifies the target partition, runs <paramref name="body"/>,
    /// and always detaches again. <paramref name="body"/> never runs unless identification succeeded.
    /// </summary>
    private async Task<LinuxFormatOutcome> WithAttachedDiskAsync(
        LinuxFormatRequest request,
        string distro,
        IProgress<OpProgress> progress,
        Func<string, List<string>, Task<LinuxFormatOutcome>> body)
    {
        var log = new List<string>();
        var drivePath = WslCli.PhysicalDrivePath(request.DiskNumber);
        var ct = CancellationToken.None;

        progress.Report(new OpProgress("Enumerating block devices inside WSL…", 0.15));
        var before = await EnumerateAsync(distro, ct).ConfigureAwait(false);
        if (before.Error is { } enumError)
            return LinuxFormatOutcome.Failed(enumError, log);
        log.Add($"WSL ({distro}) saw {before.Devices.Count(d => d.IsWholeDisk)} disk(s) before attaching.");

        progress.Report(new OpProgress($"Attaching disk {request.DiskNumber} to WSL…", 0.3));
        var attach = await AttachAsync(request, drivePath, log).ConfigureAwait(false);
        if (attach.Error is { } attachError)
            return LinuxFormatOutcome.Failed(attachError, log);

        try
        {
            var match = await WaitForDeviceAsync(distro, before.Devices, request, ct).ConfigureAwait(false);
            if (!match.Found)
                return LinuxFormatOutcome.Failed(match.Error!, log);

            log.Add($"Identified {match.DeviceNode} " +
                    $"(disk {match.DiskName}, offset {request.PartitionOffsetBytes}, size {request.PartitionSizeBytes}).");

            return await body(match.DeviceNode!, log).ConfigureAwait(false);
        }
        finally
        {
            await DetachAsync(drivePath, attach.TookOffline, request.DiskNumber, log).ConfigureAwait(false);
        }
    }

    private sealed record AttachResult(string? Error, bool TookOffline);

    /// <summary>
    /// Attaches the disk to the WSL2 kernel.
    ///
    /// Windows will not release a disk whose volumes it is still holding, so the volumes are dismounted
    /// first. If the attach still fails, a fixed disk gets one retry with <c>Set-Disk -IsOffline</c> —
    /// but <b>only</b> a fixed one: Windows rejects that outright for removable media ("Removable media
    /// cannot be set to offline"), which is most of what DiskForge targets, so trying it there would
    /// just replace the real error with a misleading one.
    /// </summary>
    private static async Task<AttachResult> AttachAsync(
        LinuxFormatRequest request, string drivePath, List<string> log)
    {
        var diskNumber = request.DiskNumber;

        if (request.VolumePaths.Count > 0)
            log.AddRange(DiskVolumeReleaser.Release(request.VolumePaths, diskNumber));

        var mount = await WslCli.RunAsync(
            new[] { "--mount", drivePath, "--bare" }, CancellationToken.None, ShortTimeout).ConfigureAwait(false);
        if (mount.Success)
        {
            log.Add($"Attached {drivePath} to the WSL2 kernel.");
            return new AttachResult(null, false);
        }

        var firstError = Detail(mount);

        if (request.DiskIsRemovable)
            return new AttachResult(
                $"Could not hand disk {diskNumber} to WSL: {firstError} " +
                "Close any Explorer window or program using this drive and try again.", false);

        log.Add($"wsl --mount failed: {firstError}; retrying with the disk offline in Windows.");

        var offline = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {diskNumber} -IsOffline $true; 'DISKFORGE_OK'",
            CancellationToken.None).ConfigureAwait(false);
        if (!offline.Success)
            return new AttachResult(
                $"Could not hand disk {diskNumber} to WSL: {firstError} " +
                $"Taking the disk offline also failed: {Detail(offline)}", false);

        var retry = await WslCli.RunAsync(
            new[] { "--mount", drivePath, "--bare" }, CancellationToken.None, ShortTimeout).ConfigureAwait(false);
        if (retry.Success)
        {
            log.Add($"Attached {drivePath} after taking disk {diskNumber} offline.");
            return new AttachResult(null, true);
        }

        // Put it back before reporting the failure — an offline disk left behind is a user-visible mess.
        await RestoreOnlineAsync(diskNumber, log).ConfigureAwait(false);
        return new AttachResult(
            $"Could not hand disk {diskNumber} to WSL. wsl --mount said: {Detail(retry)}", false);
    }

    private static async Task DetachAsync(string drivePath, bool tookOffline, int diskNumber, List<string> log)
    {
        var unmount = await WslCli.RunAsync(
            new[] { "--unmount", drivePath }, CancellationToken.None, ShortTimeout).ConfigureAwait(false);
        if (unmount.Success)
            log.Add($"Detached {drivePath} from WSL.");
        else
            Log.Warning("wsl --unmount {Path} failed: {Error}", drivePath, Detail(unmount));

        if (tookOffline) await RestoreOnlineAsync(diskNumber, log).ConfigureAwait(false);
    }

    private static async Task RestoreOnlineAsync(int diskNumber, List<string> log)
    {
        var online = await PowerShellRunner.RunAsync(
            $"$ErrorActionPreference='Stop'; Set-Disk -Number {diskNumber} -IsOffline $false; 'DISKFORGE_OK'",
            CancellationToken.None).ConfigureAwait(false);
        if (online.Success) log.Add($"Brought disk {diskNumber} back online in Windows.");
        else Log.Warning("Could not bring disk {Disk} back online: {Error}", diskNumber, Detail(online));
    }

    /// <summary>Polls until the attached disk's partition nodes exist, then identifies ours.</summary>
    private static async Task<DeviceMatch> WaitForDeviceAsync(
        string distro, IReadOnlyList<WslBlockDevice> before, LinuxFormatRequest request, CancellationToken ct)
    {
        DeviceMatch last = DeviceMatch.Fail("The disk did not appear inside WSL.");

        for (var attempt = 0; attempt < SettleAttempts; attempt++)
        {
            var after = await EnumerateAsync(distro, ct).ConfigureAwait(false);
            if (after.Error is { } error) return DeviceMatch.Fail(error);

            last = WslBlockDevices.MatchPartition(
                before, after.Devices,
                request.DiskSizeBytes, request.PartitionOffsetBytes, request.PartitionSizeBytes);
            if (last.Found) return last;

            await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
        }

        return last;
    }

    private sealed record EnumerateResult(IReadOnlyList<WslBlockDevice> Devices, string? Error);

    private static async Task<EnumerateResult> EnumerateAsync(string distro, CancellationToken ct)
    {
        var result = await WslCli.RunScriptAsync(
            distro, WslBlockDevices.EnumerateScript, ct, ShortTimeout).ConfigureAwait(false);
        if (!result.Success)
            return new EnumerateResult(
                Array.Empty<WslBlockDevice>(),
                $"Could not list block devices inside WSL ({distro}): {Detail(result)}");

        return new EnumerateResult(WslBlockDevices.Parse(result.Output), null);
    }

    /// <summary>
    /// Reads the on-disk signature back with blkid, in the same distro that ran mkfs.
    ///
    /// <c>blkid -p -o export</c> is preferred: <c>-p</c> is a low-level probe that bypasses the cache,
    /// so it reports what is really on the disk. Not every distro has that blkid though — busybox
    /// provides a cut-down one with neither flag — so a plain <c>blkid &lt;device&gt;</c> parse is the
    /// fallback. blkid is invoked by name, not by absolute path: the tool sweep may have resolved it in
    /// a different distro, and PATH is already fixed up by the runner.
    /// </summary>
    private static async Task<LinuxFormatOutcome> ReadSignatureAsync(
        string distro, string device, CancellationToken ct)
    {
        var log = new List<string>();

        var export = await WslCli.RunToolAsync(
            distro, new[] { "blkid", "-p", "-o", "export", device }, ct, ShortTimeout).ConfigureAwait(false);

        var fields = export.Success
            ? ParseExport(export.Output)
            : null;

        if (fields is null)
        {
            if (!export.Success) log.Add($"blkid -p unavailable ({Detail(export)}); falling back to plain blkid.");

            var plain = await WslCli.RunToolAsync(
                distro, new[] { "blkid", device }, ct, ShortTimeout).ConfigureAwait(false);
            if (!plain.Success)
            {
                log.Add($"blkid could not read {device}: {Detail(plain)}");
                return new LinuxFormatOutcome { Success = false, DeviceNode = device, Log = log };
            }
            fields = ParseTagged(plain.Output);
        }

        fields.TryGetValue("TYPE", out var type);
        fields.TryGetValue("LABEL", out var label);
        fields.TryGetValue("UUID", out var uuid);

        log.Add($"blkid {device}: TYPE={type ?? "(none)"} LABEL={label ?? "(none)"} UUID={uuid ?? "(none)"}");
        return new LinuxFormatOutcome
        {
            Success = type is not null,
            DeviceNode = device,
            DetectedType = type,
            DetectedLabel = label,
            Uuid = uuid,
            Log = log
        };
    }

    /// <summary><c>blkid -o export</c> output: one KEY=value per line. Null when no TYPE was reported.</summary>
    public static Dictionary<string, string>? ParseExport(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = line.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Length > 0) fields[kv[0]] = kv[1];
        }
        return fields.ContainsKey("TYPE") ? fields : null;
    }

    /// <summary>Default blkid output: <c>/dev/sdd1: LABEL="X" UUID="…" TYPE="ext4"</c>.</summary>
    public static Dictionary<string, string> ParseTagged(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(output, "([A-Z_]+)=\"([^\"]*)\""))
        {
            fields[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return fields;
    }

    /// <summary>
    /// Builds the mkfs command as argv. The label is a separate argument, never interpolated into a
    /// shell string, so no label can change the meaning of the command.
    /// </summary>
    public static IReadOnlyList<string> BuildMkfsArgv(
        LinuxFormatRequest request, string device, string? toolPath = null)
    {
        var fs = request.FileSystem;
        var argv = new List<string> { toolPath is { Length: > 0 } ? toolPath : fs.MkfsTool()! };

        // Always force: mkfs tools prompt interactively when they find an existing filesystem, and
        // this operation has already been explicitly confirmed by the user.
        if (fs.MkfsForceFlag() is { } force) argv.Add(force);

        if (request.Label.Length > 0 && fs.MkfsLabelFlag() is { } labelFlag)
        {
            argv.Add(labelFlag);
            argv.Add(request.Label);
        }

        // Only mke2fs has a bad-block scan; -c is the read-only pass (a read-write pass would double
        // the write load on flash for no extra safety here).
        if (request.BadBlockScan && fs.SupportsBadBlockScan()) argv.Add("-c");

        argv.Add(device);
        return argv;
    }

    private static string Detail(ShellResult result)
    {
        var text = result.Error.Length > 0 ? result.Error : result.Output;
        return text.Length > 0 ? text.Replace("\r", "").Replace("\n", " ").Trim() : $"exit code {result.ExitCode}";
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
