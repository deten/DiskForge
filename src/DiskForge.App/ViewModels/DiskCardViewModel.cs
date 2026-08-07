using System.Collections.ObjectModel;
using DiskForge.Core.Model;
using DiskForge.Core.Planning;
using DiskForge.Engine.Operations;

namespace DiskForge.App.ViewModels;

/// <summary>A single disk rendered as a card: header chips + proportional partition map.</summary>
public sealed class DiskCardViewModel
{
    public required string Title { get; init; }
    public required string SizeText { get; init; }
    public required string BusText { get; init; }
    public required string MediaText { get; init; }
    public required string SchemeText { get; init; }
    public required string HealthText { get; init; }
    public required string DetailText { get; init; }
    public required string ConnectionText { get; init; }
    public required string CapabilityText { get; init; }
    public required string EncryptionText { get; init; }

    public bool ShowLinkMismatch { get; init; }
    public string LinkMismatchHint { get; init; } = "";

    public int DiskNumber { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool IsReadOnly { get; init; }
    public bool ShowEncryptionWarning { get; init; }
    public bool CanFormat { get; init; }
    public string FormatBlockedReason { get; init; } = "";

    /// <summary>Whether "Create partition" should be enabled in the context menu for this disk.</summary>
    public bool CanCreatePartition { get; init; }

    /// <summary>Tooltip for the create menu item — the reason it's blocked, or a short description when
    /// enabled. Always populated so the grayed-out item explains itself (§ user request).</summary>
    public string CreatePartitionHint { get; init; } = "";

    /// <summary>Whether "Clone this disk…" should be enabled (needs an eligible target disk).</summary>
    public bool CanClone { get; init; }

    /// <summary>Tooltip for the clone menu item — reason blocked, or a short description when enabled.</summary>
    public string CloneHint { get; init; } = "";

    /// <summary>True when the map being drawn is a plan, not the drive's current contents.</summary>
    public bool HasPlannedChanges { get; init; }

    public ObservableCollection<PartitionSegmentViewModel> Segments { get; } = new();

    /// <summary>
    /// Builds the card from a *projected* disk, so every eligibility check ("no free space",
    /// "MBR is full") reflects the batch the user has staged rather than the drive's current state.
    /// </summary>
    public static DiskCardViewModel From(PlannedDisk planned, SystemState projected)
    {
        var d = planned.Disk;
        var (canFormat, blockedReason) = FormatEligibility(d);
        var (canCreate, createHint) = CreateEligibility(d);
        var (canClone, cloneHint) = CloneEligibility(d, projected);
        var vm = new DiskCardViewModel
        {
            HasPlannedChanges = planned.HasPendingChanges,
            DiskNumber = d.Number,
            CanFormat = canFormat,
            FormatBlockedReason = blockedReason,
            CanCreatePartition = canCreate,
            CreatePartitionHint = createHint,
            CanClone = canClone,
            CloneHint = cloneHint,
            Title = $"Disk {d.Number} — {d.FriendlyName}",
            SizeText = Display.Size(d.SizeBytes),
            BusText = d.Bus.ToString(),
            MediaText = d.IsSolidState ? "SSD" : d.Media.ToString(),
            SchemeText = d.PartitionStyle.ToString().ToUpperInvariant(),
            HealthText = d.Health.ToString(),
            DetailText = BuildDetail(d),
            ConnectionText = BuildConnection(d.Link),
            CapabilityText = BuildCapabilities(d.Capabilities),
            EncryptionText = BuildEncryption(d),
            ShowLinkMismatch = d.Link?.IsUnderNegotiated ?? false,
            LinkMismatchHint = d.Link?.MismatchHint ?? "",
            IsSystem = d.IsSystemDisk,
            IsBoot = d.IsBootDisk,
            IsReadOnly = d.IsReadOnly,
            ShowEncryptionWarning = d.HasEncryptedVolume
        };

        foreach (var region in planned.Regions)
            vm.Segments.Add(PartitionSegmentViewModel.From(region, d.SizeBytes, d.Number));

        return vm;
    }

    private static string BuildConnection(LinkInfo? link)
    {
        if (link is null) return "";
        var parts = new List<string> { "Connection: " + link.Interface };
        if (link.NegotiatedSpeed is { } n) parts.Add(n);
        if (link.IsUnderNegotiated && link.CapableSpeed is { } c) parts.Add($"capable of {c}");
        if (link.FormFactor is { } ff) parts.Add(ff);
        return string.Join("  ·  ", parts);
    }

    private static (bool canFormat, string reason) FormatEligibility(PhysicalDiskInfo d)
    {
        if (d.IsSystemDisk || d.IsBootDisk) return (false, "System/boot disk — protected");
        if (d.IsReadOnly) return (false, "Disk is read-only");
        return (true, "");
    }

    /// <summary>
    /// Mirrors CreatePartitionOperation.Validate's disk-level gates so the menu never offers a create
    /// that would then fail. Internal disks stay *enabled* — the dialog collects the acknowledgment.
    /// </summary>
    private static (bool can, string hint) CreateEligibility(PhysicalDiskInfo d)
    {
        if (d.IsSystemDisk || d.IsBootDisk)
            return (false, "System/boot disk — protected. Cannot create partitions here.");
        if (d.IsReadOnly)
            return (false, "Disk is read-only.");
        if (d.IsOffline)
            return (false, "Disk is offline — bring it online first.");
        if (d.PartitionStyle is PartitionStyle.Raw or PartitionStyle.Unknown)
            return (false, "Disk is not initialized — create a GPT or MBR partition table first.");

        var hasGap = d.Partitions.Any(p => p.IsUnallocated && p.SizeBytes >= CreatePartitionOperation.MinSize);
        if (!hasGap)
            return (false, "No unallocated free space — delete or shrink a partition first.");

        if (d.PartitionStyle == PartitionStyle.Mbr &&
            d.Partitions.Count(p => !p.IsUnallocated) >= CreatePartitionOperation.MbrMaxCreatablePartitions)
            return (false,
                $"This disk uses MBR, which has only {CreatePartitionOperation.MbrMaxPrimaryPartitions} table " +
                $"slots. DiskForge stops at {CreatePartitionOperation.MbrMaxCreatablePartitions} because Windows " +
                "turns the last slot into an extended container, which cannot hold a filesystem. " +
                "Erase the disk as GPT to get more partitions.");

        return (true, "Create a new partition in the largest block of unallocated space.");
    }

    /// <summary>Clone is offered on any disk as a source, provided at least one other disk is an eligible
    /// target (not this disk, not the system/boot disk). The dialog does the full validation.</summary>
    private static (bool can, string hint) CloneEligibility(PhysicalDiskInfo d, SystemState? state)
    {
        if (state is null) return (false, "Rescan to enable cloning.");
        var eligibleTarget = state.Disks.Any(t =>
            t.Number != d.Number && !t.IsSystemDisk && !t.IsBootDisk && state.SystemDiskNumber != t.Number);
        return eligibleTarget
            ? (true, "Clone this disk onto another disk (verified copy).")
            : (false, "No eligible target disk connected — attach another non-system disk to clone onto.");
    }

    private static string BuildDetail(PhysicalDiskInfo d)
    {
        var parts = new List<string>();
        if (d.Model is { } m) parts.Add(m);
        if (d.FirmwareVersion is { } fw) parts.Add($"fw {fw}");
        if (d.SerialNumber is { } sn) parts.Add($"S/N {sn}");
        if (d.LogicalSectorSize is { } ls)
            parts.Add($"{ls}/{d.PhysicalSectorSize?.ToString() ?? "?"} B sector");
        return string.Join("   ·   ", parts);
    }

    private static string BuildCapabilities(DriveCapabilities caps)
    {
        var have = Enum.GetValues<DriveCapability>()
            .Where(c => c != DriveCapability.None && caps.Has(c))
            .Select(c => c.ToString());
        var text = "Capabilities: " + string.Join(", ", have);
        if (caps.AtaSecurityFreeze == FreezeState.Frozen)
            text += "   ⚠ security-frozen (power-cycle needed for Secure Erase)";
        return text;
    }

    private static string BuildEncryption(PhysicalDiskInfo d)
    {
        var bl = d.Partitions
            .Select(p => p.Volume?.BitLocker)
            .FirstOrDefault(b => b is { } x && (x.IsProtected || x.IsConverting));
        if (bl is not null)
            return $"BitLocker {bl.Protection}" + (bl.IsConverting ? $" · converting {bl.ConversionPercent}%" : "");
        if (d.Sed.IsSelfEncrypting)
            return $"SED: {d.Sed.Type} ({d.Sed.Lock})";
        return "No active encryption detected";
    }
}
