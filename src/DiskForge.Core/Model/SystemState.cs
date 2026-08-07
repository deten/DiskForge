namespace DiskForge.Core.Model;

/// <summary>An immutable snapshot of all disks at a point in time — the read-only view the
/// UI renders and every operation validates against.</summary>
public sealed record SystemState
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<PhysicalDiskInfo> Disks { get; init; } = Array.Empty<PhysicalDiskInfo>();

    /// <summary>Disk number hosting the running OS, if identified — hard-gated from destructive ops.</summary>
    public int? SystemDiskNumber { get; init; }

    /// <summary>True while running without Administrator; enumeration works but writes are blocked.</summary>
    public bool IsElevated { get; init; }

    /// <summary>
    /// Which Linux filesystems this machine can actually write (probed once per process). Carried on
    /// the snapshot so Linux-format gating happens in pure <c>Validate()</c> code, with no shell-outs.
    /// </summary>
    public LinuxToolchainInfo LinuxToolchain { get; init; } = LinuxToolchainInfo.NotProbed;

    public PhysicalDiskInfo? FindDisk(int number) => Disks.FirstOrDefault(d => d.Number == number);
}
