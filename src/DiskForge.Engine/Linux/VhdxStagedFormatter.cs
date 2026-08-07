using DiskForge.Core.Operations;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;
using DiskForge.Engine.Virtual;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace DiskForge.Engine.Linux;

/// <summary>
/// Writes a Linux filesystem onto a disk that WSL cannot be handed directly.
///
/// <b>Why.</b> <c>wsl --mount \\.\PHYSICALDRIVEn</c> detaches the disk from Windows and re-attaches it
/// to the Hyper-V VM that WSL2 runs in — and that layer flatly refuses <b>removable media</b>
/// (<c>Wsl/Service/AttachDisk/MountDisk/HCS/0x8007000f</c>, whether the disk is online or offline).
/// USB sticks are DiskForge's default-allowed target, so the direct route cannot be the only one.
///
/// <b>How.</b> WSL <i>will</i> mount a VHDX file. So the real mkfs runs against a scratch VHDX sized
/// exactly to the destination partition, producing a genuine filesystem image, and that image is then
/// written onto the partition with the same raw-disk code the clone engine uses. The bytes that land on
/// the drive are exactly what mkfs produced — only the delivery route changes.
///
/// A filesystem image is position-independent (ext4/btrfs/xfs/f2fs all address from the start of their
/// own volume), which is what makes the copy legitimate rather than a trick.
/// </summary>
public sealed class VhdxStagedFormatter
{
    /// <summary>Copy granularity. Also the size of the head/tail region wiped before writing.</summary>
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>Images are sized to a whole MiB so the copy is always chunk-aligned.</summary>
    private const ulong ImageAlignment = 1024 * 1024;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);

    public async Task<LinuxFormatOutcome> FormatAsync(
        LinuxFormatRequest request, string distro, string? toolPath,
        IProgress<OpProgress> progress, CancellationToken ct)
    {
        var log = new List<string>();
        var fs = request.FileSystem;

        // Size the image DOWN to a whole MiB: a filesystem smaller than its partition is perfectly
        // valid, whereas one even slightly larger would overrun the partition when copied.
        var imageSize = request.PartitionSizeBytes / ImageAlignment * ImageAlignment;
        if (imageSize < fs.MinimumSizeBytes())
            return LinuxFormatOutcome.Failed(
                $"The partition is too small for {fs.ToFormatName()} once aligned " +
                $"({Bytes(imageSize)} usable, {Bytes(fs.MinimumSizeBytes())} required).", log);

        var vhdxPath = Path.Combine(Path.GetTempPath(), $"diskforge-mkfs-{Guid.NewGuid():N}.vhdx");
        if (FreeSpaceShortfall(vhdxPath, imageSize) is { } shortfall)
            return LinuxFormatOutcome.Failed(shortfall, log);

        log.Add($"Staging {fs.ToFormatName()} through a {Bytes(imageSize)} scratch image " +
                "(WSL cannot attach removable disks directly).");

        try
        {
            // ---- 1. build the filesystem inside a VHDX that WSL is willing to mount ----
            progress.Report(new OpProgress("Creating a scratch image…", 0.15));
            VirtualDisk.CreateDynamicVhdx(vhdxPath, imageSize);

            var built = await BuildImageAsync(vhdxPath, imageSize, request, distro, toolPath, log, progress, ct)
                .ConfigureAwait(false);
            if (!built.Success) return built with { Log = log };

            // ---- 2. copy it onto the real partition ----
            progress.Report(new OpProgress($"Writing the filesystem to disk {request.DiskNumber}…", 0.6));
            WriteImageToPartition(vhdxPath, imageSize, request, log, progress);

            // ---- 3. verify from the drive itself, not from the image ----
            progress.Report(new OpProgress("Verifying the filesystem on the drive…", 0.95));
            var onDisk = LinuxFsSignature.Read(request.DiskNumber, request.PartitionOffsetBytes);
            if (onDisk is null)
                return LinuxFormatOutcome.Failed(
                    "The filesystem image was written, but no Linux superblock could be read back from " +
                    "the partition. Do not use the drive until this is re-checked.", log);

            log.Add($"Read back from disk {request.DiskNumber}: TYPE={onDisk.Type} " +
                    $"LABEL={onDisk.Label ?? "(none)"} UUID={onDisk.Uuid ?? "(none)"}");

            return new LinuxFormatOutcome
            {
                Success = true,
                DeviceNode = built.DeviceNode,
                DetectedType = onDisk.Type,
                DetectedLabel = onDisk.Label,
                Uuid = onDisk.Uuid,
                Log = log
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Staged Linux format failed on disk {Disk}", request.DiskNumber);
            return LinuxFormatOutcome.Failed($"Staged format failed: {ex.Message}", log);
        }
        finally
        {
            TryDelete(vhdxPath, log);
        }
    }

    /// <summary>Runs the real mkfs against the scratch image inside WSL.</summary>
    private static async Task<LinuxFormatOutcome> BuildImageAsync(
        string vhdxPath, ulong imageSize, LinuxFormatRequest request, string distro, string? toolPath,
        List<string> log, IProgress<OpProgress> progress, CancellationToken ct)
    {
        var before = await EnumerateAsync(distro, ct).ConfigureAwait(false);
        if (before.Error is { } enumError) return LinuxFormatOutcome.Failed(enumError);

        var mount = await WslCli.RunAsync(
            new[] { "--mount", "--vhd", vhdxPath, "--bare" }, ct, ShortTimeout).ConfigureAwait(false);
        if (!mount.Success)
            return LinuxFormatOutcome.Failed($"Could not attach the scratch image to WSL: {Detail(mount)}");

        try
        {
            // Same discipline as the direct route: the device must be one our own attach produced and
            // must match the expected size. No guessing which /dev/sd* is ours.
            var match = await WaitForImageDeviceAsync(distro, before.Devices, imageSize, ct).ConfigureAwait(false);
            if (!match.Found) return LinuxFormatOutcome.Failed(match.Error!);

            var device = match.DeviceNode!;
            log.Add($"Scratch image is {device} inside WSL ({distro}).");

            progress.Report(new OpProgress($"Running {request.FileSystem.MkfsTool()}…", 0.35));
            var argv = WslLinuxFormatBackend.BuildMkfsArgv(request, device, toolPath);
            log.Add($"{distro}: {string.Join(' ', argv)}");

            var mkfs = await WslCli.RunToolAsync(distro, argv, ct).ConfigureAwait(false);
            if (!mkfs.Success)
                return LinuxFormatOutcome.Failed($"{request.FileSystem.MkfsTool()} failed: {Detail(mkfs)}");

            log.Add($"{request.FileSystem.MkfsTool()} completed on the scratch image.");
            return new LinuxFormatOutcome { Success = true, DeviceNode = device };
        }
        finally
        {
            var unmount = await WslCli.RunAsync(
                new[] { "--unmount", vhdxPath }, CancellationToken.None, ShortTimeout).ConfigureAwait(false);
            if (unmount.Success) log.Add("Detached the scratch image from WSL.");
            else Log.Warning("Could not detach scratch image {Path}: {Error}", vhdxPath, Detail(unmount));
        }
    }

    /// <summary>
    /// Copies the finished filesystem onto the partition, so the bytes on the drive are exactly what
    /// mkfs produced — including the regions mkfs deliberately zeroed.
    ///
    /// An all-zero source chunk may only be skipped when the destination is <b>already</b> zero. It is
    /// not enough that the image is "mostly zeros": a partition being reformatted still holds the
    /// previous filesystem, and skipping a zero chunk there leaves that old metadata sitting inside the
    /// new filesystem. mkfs.xfs zeroes its log and unused metadata areas and then checksums what it
    /// wrote, so stale bytes surface later as CRC failures on a filesystem that verified fine at
    /// creation. Reading the destination first costs a read but never writes the wrong bytes.
    /// </summary>
    private static void WriteImageToPartition(
        string vhdxPath, ulong imageSize, LinuxFormatRequest request,
        List<string> log, IProgress<OpProgress> progress)
    {
        using var attached = AttachWithRetry(vhdxPath);
        log.Add($"Attached the scratch image as disk {attached.DiskNumber} to copy it.");

        // Windows will not permit sector writes under a mounted volume.
        if (request.VolumePaths.Count > 0)
            log.AddRange(DiskVolumeReleaser.Release(request.VolumePaths, request.DiskNumber));

        using var source = RawDiskAccess.OpenRead(attached.DiskNumber);
        using var target = RawDiskAccess.OpenWrite(request.DiskNumber);

        var start = (long)request.PartitionOffsetBytes;
        var total = (long)imageSize;

        WipeEnds(target, start, total);

        var buffer = new byte[ChunkBytes];
        var existing = new byte[ChunkBytes];
        var zero = new byte[ChunkBytes];
        long written = 0, copied = 0, cleared = 0, skipped = 0;

        for (long offset = 0; offset < total; offset += ChunkBytes)
        {
            var want = (int)Math.Min(ChunkBytes, total - offset);
            ReadExact(source, buffer, offset, want);

            var sourceIsZero = buffer.AsSpan(0, want).SequenceEqual(zero.AsSpan(0, want));
            var destinationReadable = sourceIsZero &&
                                      TryReadExact(source: target, existing, start + offset, want);

            switch (DecideChunk(buffer.AsSpan(0, want), existing.AsSpan(0, want), destinationReadable))
            {
                case ChunkAction.Skip:
                    skipped++;
                    continue;

                case ChunkAction.ZeroDestination:
                    RandomAccess.Write(target, zero.AsSpan(0, want), start + offset);
                    cleared++;
                    break;

                default:
                    RandomAccess.Write(target, buffer.AsSpan(0, want), start + offset);
                    copied++;
                    break;
            }

            written += want;
            progress.Report(new OpProgress("Writing the filesystem…", 0.6 + 0.3 * offset / total));
        }

        RandomAccess.FlushToDisk(target);
        log.Add($"Wrote {Bytes((ulong)written)} ({copied} chunk(s) of filesystem data, " +
                $"{cleared} cleared of old data); {skipped} chunk(s) were already blank.");
    }

    /// <summary>
    /// WSL does not always release the VHDX the instant <c>--unmount</c> returns, so a sharing
    /// violation here is a timing artefact rather than a real failure — retry briefly before giving up.
    /// </summary>
    private static AttachedDisk AttachWithRetry(string vhdxPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return VirtualDisk.Attach(vhdxPath);
            }
            catch when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Zeroes the first and last chunk of the destination extent.</summary>
    private static void WipeEnds(SafeFileHandle target, long start, long total)
    {
        var zero = new byte[ChunkBytes];
        var head = (int)Math.Min(ChunkBytes, total);
        RandomAccess.Write(target, zero.AsSpan(0, head), start);

        if (total > ChunkBytes * 2L)
            RandomAccess.Write(target, zero.AsSpan(0, ChunkBytes), start + total - ChunkBytes);
    }

    private static async Task<DeviceMatch> WaitForImageDeviceAsync(
        string distro, IReadOnlyList<WslBlockDevice> before, ulong imageSize, CancellationToken ct)
    {
        DeviceMatch last = DeviceMatch.Fail("The scratch image did not appear inside WSL.");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var after = await EnumerateAsync(distro, ct).ConfigureAwait(false);
            if (after.Error is { } error) return DeviceMatch.Fail(error);

            last = WslBlockDevices.MatchWholeDisk(before, after.Devices, imageSize);
            if (last.Found) return last;

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }
        return last;
    }

    private sealed record EnumerateResult(IReadOnlyList<WslBlockDevice> Devices, string? Error);

    private static async Task<EnumerateResult> EnumerateAsync(string distro, CancellationToken ct)
    {
        var result = await WslCli.RunScriptAsync(
            distro, WslBlockDevices.EnumerateScript, ct, ShortTimeout).ConfigureAwait(false);
        return result.Success
            ? new EnumerateResult(WslBlockDevices.Parse(result.Output), null)
            : new EnumerateResult(Array.Empty<WslBlockDevice>(),
                $"Could not list block devices inside WSL ({distro}): {Detail(result)}");
    }

    /// <summary>
    /// A dynamic VHDX only grows to hold what mkfs writes — mostly inode tables and the journal — but
    /// that is still real space on the temp volume, so check before starting rather than failing halfway.
    /// </summary>
    private static string? FreeSpaceShortfall(string vhdxPath, ulong imageSize)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(vhdxPath));
            if (root is null) return null;

            // Metadata is a few percent of the volume; 1/8th plus a floor is a comfortable margin.
            var needed = Math.Min(imageSize, imageSize / 8 + 256UL * 1024 * 1024);
            var free = (ulong)new DriveInfo(root).AvailableFreeSpace;

            return free >= needed
                ? null
                : $"Not enough temporary space to stage the filesystem: {Bytes(free)} free on {root}, " +
                  $"about {Bytes(needed)} needed. Free up space on that drive and try again.";
        }
        catch
        {
            return null; // a failed check must not block the operation
        }
    }

    /// <summary>What to do with one chunk of the staged image.</summary>
    public enum ChunkAction
    {
        /// <summary>Write the image's bytes — it has real filesystem content here.</summary>
        CopyImage,

        /// <summary>The image is blank here but the drive is not: erase what was there.</summary>
        ZeroDestination,

        /// <summary>Both are blank; nothing to do.</summary>
        Skip
    }

    /// <summary>
    /// Decides what one chunk needs. Pure, and separated out because the safety of the whole staged
    /// copy rests on a single rule: <b>never skip unless the destination is known to be zero.</b>
    /// Skipping on "the image is zero" alone leaves the previous filesystem's metadata inside the new
    /// one, which then fails checksums later even though the format verified at creation time.
    /// </summary>
    public static ChunkAction DecideChunk(
        ReadOnlySpan<byte> image, ReadOnlySpan<byte> destination, bool destinationReadable)
    {
        if (image.IndexOfAnyExcept((byte)0) >= 0) return ChunkAction.CopyImage;

        // Image is blank. Unreadable destination means unknown, and unknown must not be assumed blank.
        if (!destinationReadable) return ChunkAction.ZeroDestination;

        return destination.IndexOfAnyExcept((byte)0) >= 0
            ? ChunkAction.ZeroDestination
            : ChunkAction.Skip;
    }

    /// <summary>
    /// Reads the destination so an all-zero source chunk can be skipped only when the drive is already
    /// blank there. Returns false if the region cannot be read, which is treated as "not known to be
    /// zero" — the caller then writes zeros rather than assuming.
    /// </summary>
    private static bool TryReadExact(SafeFileHandle source, byte[] buffer, long offset, int count)
    {
        try
        {
            ReadExact(source, buffer, offset, count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the destination at offset {Offset}; will zero it instead", offset);
            return false;
        }
    }

    private static void ReadExact(SafeFileHandle handle, byte[] buffer, long offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(total, count - total), offset + total);
            if (n == 0) throw new IOException($"Short read from the scratch image at offset {offset + total}.");
            total += n;
        }
    }

    private static void TryDelete(string path, List<string> log)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
        log.Add($"Note: the scratch image {path} could not be deleted; it is safe to remove manually.");
    }

    private static string Detail(ShellResult result)
    {
        var text = result.Error.Length > 0 ? result.Error : result.Output;
        return text.Length > 0 ? text.Replace("\r", "").Replace("\n", " ").Trim() : $"exit code {result.ExitCode}";
    }

    private static string Bytes(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
