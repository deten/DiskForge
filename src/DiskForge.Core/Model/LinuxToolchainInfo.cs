using DiskForge.Core.Operations;

namespace DiskForge.Core.Model;

/// <summary>Availability of one mkfs tool, and where it was found.</summary>
public sealed record LinuxToolAvailability(bool Available, string? Distro, string? Path, string? Reason);

/// <summary>One WSL distribution the probe found.</summary>
public sealed record LinuxDistroInfo(string Name, int WslVersion, bool IsDefault, bool IsRunning);

/// <summary>
/// What Linux-filesystem support is actually present on this machine (§1A honesty rule: no
/// filesystem is offered unless the tool that writes it has been seen). Captured read-only as part
/// of <see cref="SystemState"/> so every <c>Validate()</c> stays pure and testable — no operation
/// shells out to WSL just to decide whether it is allowed to run.
/// </summary>
public sealed class LinuxToolchainInfo
{
    /// <summary>A machine with no WSL at all — every Linux filesystem is unavailable, with a reason.</summary>
    public static LinuxToolchainInfo NotProbed { get; } = new()
    {
        Reason = "The Linux toolchain has not been probed yet."
    };

    /// <summary>True when a usable WSL2 distribution with at least one mkfs tool was found.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Why Linux formatting is unavailable, when <see cref="IsAvailable"/> is false. Always specific.</summary>
    public string? Reason { get; init; }

    /// <summary>Backend label for the UI, e.g. "WSL2 (Ubuntu)".</summary>
    public string? BackendName { get; init; }

    /// <summary>Output of <c>wsl --version</c>, for logs and the details pane.</summary>
    public string? VersionText { get; init; }

    public IReadOnlyList<LinuxDistroInfo> Distros { get; init; } = Array.Empty<LinuxDistroInfo>();

    /// <summary>Per-filesystem tool availability, keyed by the filesystem the tool writes.</summary>
    public IReadOnlyDictionary<FileSystemType, LinuxToolAvailability> Tools { get; init; }
        = new Dictionary<FileSystemType, LinuxToolAvailability>();

    /// <summary>
    /// Absolute paths of the non-mkfs helpers (blkid, …), keyed by binary name. These live in
    /// <c>/sbin</c> on most distros, which is not on the PATH of a shell-less <c>wsl --exec</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> SupportToolPaths { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Absolute path of a helper binary if it was found, else the bare name to try anyway.</summary>
    public string SupportTool(string name)
        => SupportToolPaths.TryGetValue(name, out var path) ? path : name;

    /// <summary>
    /// True when <c>wsl --mount</c> is usable at all on this WSL build. Attaching a physical disk
    /// additionally needs Administrator, which is gated separately (like every other write path).
    /// </summary>
    public bool SupportsDiskMount { get; init; }

    /// <summary>Availability of the tool that writes <paramref name="fs"/>, never null.</summary>
    public LinuxToolAvailability ToolFor(FileSystemType fs)
    {
        if (Tools.TryGetValue(fs, out var tool)) return tool;
        return new LinuxToolAvailability(false, null, null,
            Reason ?? $"{fs.MkfsTool() ?? "the mkfs tool"} was not found.");
    }

    /// <summary>
    /// The single blocking reason a Linux format cannot run, or null when it can. This is the one
    /// gate every Linux-filesystem operation consults.
    /// </summary>
    public string? BlockingReason(FileSystemType fs)
    {
        if (!fs.IsLinux()) return null;

        // ext2/3/4 are written by DiskForge itself, so no external toolchain can gate them. This is
        // the whole point of the native writer: nothing about the host can make them unavailable.
        if (fs.IsWrittenNatively()) return null;

        if (!IsAvailable)
            return (Reason ?? "No Linux filesystem toolchain is available.") +
                   " DiskForge writes ext4/btrfs/xfs with the real mkfs tools through WSL2 — " +
                   "install WSL2 (`wsl --install`) to enable Linux formats.";

        if (!SupportsDiskMount)
            return "This WSL build cannot attach a physical disk (`wsl --mount` is unavailable). " +
                   "Update WSL with `wsl --update`.";

        var tool = ToolFor(fs);
        if (!tool.Available)
        {
            var pkg = fs.MkfsPackage();
            return $"{fs.MkfsTool()} is not installed in any WSL distribution" +
                   (tool.Reason is { Length: > 0 } r ? $" ({r})" : "") +
                   (pkg is null ? "." : $". Install it in your distro, e.g. `sudo apt install {pkg}`.");
        }

        return null;
    }

    /// <summary>Filesystems that can be written right now — the native ones always count.</summary>
    public IEnumerable<FileSystemType> UsableFilesystems()
        => Tools.Where(kv => kv.Value.Available || kv.Key.IsWrittenNatively()).Select(kv => kv.Key);
}
