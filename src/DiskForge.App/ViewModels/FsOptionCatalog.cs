using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.App.ViewModels;

/// <summary>
/// One filesystem choice in a dialog. Unavailable Linux filesystems are shown <b>disabled with the
/// reason attached</b> rather than hidden — a user who wants ext4 needs to be told that mkfs.ext4 is
/// missing and how to get it, not left wondering why the option does not exist.
/// </summary>
public sealed record FsOption(FileSystemType Value, string Display, bool IsEnabled = true, string? Reason = null);

/// <summary>Builds the filesystem list for the Format and Create-partition dialogs.</summary>
public static class FsOptionCatalog
{
    private static readonly (FileSystemType Fs, string Text)[] Windows =
    {
        (FileSystemType.Exfat, "exFAT (recommended for USB)"),
        (FileSystemType.Ntfs, "NTFS (Windows)"),
        (FileSystemType.Fat32, "FAT32 (max compatibility, ≤32 GB)")
    };

    private static readonly (FileSystemType Fs, string Text)[] Linux =
    {
        (FileSystemType.Ext4, "ext4 (Linux — default choice)"),
        (FileSystemType.Ext3, "ext3 (Linux — older, journalled)"),
        (FileSystemType.Ext2, "ext2 (Linux — no journal)"),
        (FileSystemType.Btrfs, "btrfs (Linux — copy-on-write)"),
        (FileSystemType.Xfs, "XFS (Linux — large files, ≥300 MB)"),
        (FileSystemType.F2fs, "F2FS (Linux — flash-optimised)"),
        (FileSystemType.LinuxSwap, "Linux swap (no filesystem)")
    };

    public static IReadOnlyList<FsOption> Build(LinuxToolchainInfo toolchain)
    {
        var options = Windows.Select(w => new FsOption(w.Fs, w.Text)).ToList();

        foreach (var (fs, text) in Linux)
        {
            var blocked = toolchain.BlockingReason(fs);
            options.Add(blocked is null
                ? new FsOption(fs, text)
                : new FsOption(fs, text + " — unavailable", IsEnabled: false, Reason: blocked));
        }

        return options;
    }

    /// <summary>One-line status of the Linux backend for the dialog footer.</summary>
    public static string DescribeBackend(LinuxToolchainInfo toolchain)
    {
        if (!toolchain.IsAvailable)
            return "Linux filesystems unavailable — " + (toolchain.Reason ?? "no toolchain found.");

        var usable = toolchain.UsableFilesystems()
            .OrderBy(f => f.ToFormatName(), StringComparer.Ordinal)
            .Select(f => f.ToFormatName())
            .ToList();

        return $"Linux filesystems via {toolchain.BackendName}: {string.Join(", ", usable)}. " +
               "Windows cannot read these — the volume will show as unformatted in Explorer.";
    }
}
