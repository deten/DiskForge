namespace DiskForge.Core.Model;

/// <summary>
/// A physical drive plus its partition layout, encryption and capability profile. A record so the
/// staged-change projector can produce a copy with a *projected* partition list without having to
/// restate every property (and silently drop any added later).
/// </summary>
public sealed record PhysicalDiskInfo
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = "";
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareVersion { get; init; }
    public ulong SizeBytes { get; init; }

    public StorageBus Bus { get; init; } = StorageBus.Unknown;
    public DiskMediaType Media { get; init; } = DiskMediaType.Unknown;
    public PartitionStyle PartitionStyle { get; init; } = PartitionStyle.Unknown;
    public HealthStatus Health { get; init; } = HealthStatus.Unknown;

    public uint? LogicalSectorSize { get; init; }
    public uint? PhysicalSectorSize { get; init; }

    public bool IsBootDisk { get; init; }
    public bool IsSystemDisk { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsOffline { get; init; }
    public bool IsRemovable { get; init; }

    public SedInfo Sed { get; init; } = SedInfo.NotDetected;
    public DriveCapabilities Capabilities { get; init; } = new();
    public LinkInfo? Link { get; init; }

    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = Array.Empty<PartitionInfo>();

    /// <summary>True for SSD/NVMe/SCM media — wipe must route to hardware erase, never multi-pass (§1A.5).</summary>
    public bool IsSolidState => Media is DiskMediaType.Ssd or DiskMediaType.Scm || Bus == StorageBus.Nvme;

    /// <summary>Any volume on this disk that is BitLocker-protected or mid-conversion.</summary>
    public bool HasEncryptedVolume =>
        Partitions.Any(p => p.Volume?.BitLocker is { } bl && (bl.IsProtected || bl.IsConverting));
}
