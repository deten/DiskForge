using DiskForge.Core.Operations;
using DiskForge.Engine.Linux.Ext;
using DiskForge.Engine.Native;
using DiskForge.Engine.Operations;
using Serilog;

namespace DiskForge.Engine.Linux;

/// <summary>
/// Formats ext2/ext3/ext4 with DiskForge's own filesystem writer — no WSL, no Hyper-V, no bundled
/// third-party binary, nothing outside this process.
///
/// This is the primary backend. It exists because every route through someone else's system failed on
/// the thing DiskForge exists to format: WSL's disk passthrough refuses removable media outright, and
/// bundling e2fsprogs would put GPL redistribution obligations on the product. Writing the filesystem
/// directly removes the whole class of problem — there is no external component to be missing, refuse,
/// or behave differently on someone else's machine.
///
/// Filesystems it cannot write (btrfs, XFS, F2FS) are delegated to <paramref name="fallback"/> if one
/// was supplied, and honestly reported as unavailable otherwise.
/// </summary>
public sealed class NativeLinuxFormatBackend : ILinuxFormatBackend
{
    private readonly ILinuxFormatBackend? _fallback;

    public NativeLinuxFormatBackend(ILinuxFormatBackend? fallback = null) => _fallback = fallback;

    /// <summary>Filesystems this backend writes itself, with no external dependency whatsoever.</summary>
    public static bool CanWriteNatively(FileSystemType fs) => ExtLayout.IsExt(fs);

    public async Task<LinuxFormatOutcome> FormatAsync(
        LinuxFormatRequest request, IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (!CanWriteNatively(request.FileSystem))
            return await DelegateAsync(request, progress, ct).ConfigureAwait(false);

        var log = new List<string>();
        try
        {
            var layout = ExtLayout.Compute(request.PartitionSizeBytes, request.FileSystem);
            log.Add($"{request.FileSystem.ToFormatName()}: {layout.BlockSize}-byte blocks, " +
                    $"{layout.GroupCount} group(s), {layout.InodeCount} inodes" +
                    (layout.HasJournal ? $", {layout.JournalBlocks}-block journal" : ", no journal") + ".");

            progress.Report(new OpProgress("Releasing the partition from Windows…", 0.1));
            if (request.VolumePaths.Count > 0)
                log.AddRange(DiskVolumeReleaser.Release(request.VolumePaths, request.DiskNumber));

            progress.Report(new OpProgress($"Writing {request.FileSystem.ToFormatName()}…", 0.3));

            var formatter = new ExtFormatter(layout, request.Label);
            using (var handle = RawDiskAccess.OpenWrite(request.DiskNumber))
            using (var partition = new RawPartitionStream(
                       handle, (long)request.PartitionOffsetBytes, (long)request.PartitionSizeBytes))
            {
                WipeForeignSignatures(partition, layout);
                formatter.Write(partition);
                partition.Flush();
            }

            log.Add($"Wrote {request.FileSystem.ToFormatName()} at offset {request.PartitionOffsetBytes} " +
                    $"on disk {request.DiskNumber}.");

            // Make Windows re-read the disk, or it keeps serving the volume that used to be here.
            DiskVolumeReleaser.Refresh(request.DiskNumber);

            // Read the superblock back off the drive — not out of our own buffers.
            progress.Report(new OpProgress("Verifying the filesystem on the drive…", 0.9));
            var onDisk = LinuxFsSignature.Read(request.DiskNumber, request.PartitionOffsetBytes);
            if (onDisk is null)
                return LinuxFormatOutcome.Failed(
                    "The filesystem was written but no superblock could be read back from the drive. " +
                    "Do not use it until this is re-checked.", log);

            log.Add($"Read back: TYPE={onDisk.Type} LABEL={onDisk.Label ?? "(none)"} " +
                    $"UUID={onDisk.Uuid ?? "(none)"}");
            Log.Information("Native {Fs} format complete on disk {Disk}",
                onDisk.Type, request.DiskNumber);

            return new LinuxFormatOutcome
            {
                Success = true,
                DeviceNode = $"disk {request.DiskNumber} @ {request.PartitionOffsetBytes}",
                DetectedType = onDisk.Type,
                DetectedLabel = onDisk.Label,
                Uuid = onDisk.Uuid,
                Log = log
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Native Linux format failed on disk {Disk}", request.DiskNumber);
            return LinuxFormatOutcome.Failed($"Could not write the filesystem: {ex.Message}", log);
        }
    }

    public Task<LinuxFormatOutcome> ProbeSignatureAsync(LinuxFormatRequest request, CancellationToken ct)
    {
        if (!CanWriteNatively(request.FileSystem) && _fallback is not null)
            return _fallback.ProbeSignatureAsync(request, ct);

        var signature = LinuxFsSignature.Read(request.DiskNumber, request.PartitionOffsetBytes);
        return Task.FromResult(signature is null
            ? LinuxFormatOutcome.Failed(
                $"No Linux filesystem was found on disk {request.DiskNumber} at " +
                $"offset {request.PartitionOffsetBytes}.")
            : new LinuxFormatOutcome
            {
                Success = true,
                DetectedType = signature.Type,
                DetectedLabel = signature.Label,
                Uuid = signature.Uuid
            });
    }

    /// <summary>
    /// Clears the areas a previous filesystem's signature could survive in. mke2fs (and this writer)
    /// leave the first 1024 bytes alone, so an old NTFS/exFAT boot sector would otherwise remain and
    /// leave the partition identifying as two filesystems at once.
    /// </summary>
    private static void WipeForeignSignatures(Stream partition, ExtLayout layout)
    {
        var zero = new byte[64 * 1024];

        partition.Position = 0;
        partition.Write(zero, 0, (int)Math.Min(zero.Length, partition.Length));

        // btrfs keeps its superblock at 64 KiB; clear up to the start of our own metadata.
        var upto = Math.Min(partition.Length, 128L * 1024);
        for (var at = (long)zero.Length; at < upto; at += zero.Length)
        {
            partition.Position = at;
            partition.Write(zero, 0, (int)Math.Min(zero.Length, upto - at));
        }
    }

    private Task<LinuxFormatOutcome> DelegateAsync(
        LinuxFormatRequest request, IProgress<OpProgress> progress, CancellationToken ct)
    {
        if (_fallback is not null) return _fallback.FormatAsync(request, progress, ct);

        var package = request.FileSystem.MkfsPackage();
        return Task.FromResult(LinuxFormatOutcome.Failed(
            $"{request.FileSystem.ToFormatName()} cannot be written by DiskForge itself — only " +
            "ext2, ext3 and ext4 are built in. " +
            (package is null ? "" : $"It needs {package} in a WSL2 distribution, which is not available here.")));
    }
}
