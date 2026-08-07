namespace DiskForge.Core.Model;

/// <summary>
/// Result of probing a single physical drive (§1A). Records which capabilities are
/// supported, which were actually testable, why unsupported ones are unavailable, and
/// safety-relevant state (freeze, HPA/DCO). Trust this live probe over the static
/// capability-support matrix (§1A.8).
/// </summary>
public sealed class DriveCapabilities
{
    /// <summary>Capabilities the drive is believed to support.</summary>
    public DriveCapability Supported { get; init; } = DriveCapability.None;

    /// <summary>Capabilities we were actually able to interrogate (vs assumed/blocked).</summary>
    public DriveCapability Probed { get; init; } = DriveCapability.None;

    public FreezeState AtaSecurityFreeze { get; init; } = FreezeState.Unknown;

    public bool SmartAvailable { get; init; }

    /// <summary>True when an HPA/DCO appears to be hiding capacity (must resolve before imaging).</summary>
    public bool HasHiddenCapacity { get; init; }

    /// <summary>Per-capability human reason it is unavailable (e.g. "blocked by USB bridge").</summary>
    public IReadOnlyDictionary<DriveCapability, string> UnavailableReasons { get; init; }
        = new Dictionary<DriveCapability, string>();

    /// <summary>Free-form probe notes surfaced to the user/log.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool Has(DriveCapability capability) => Supported.HasFlag(capability);

    /// <summary>
    /// Returns the reason a capability is unavailable, or null when it is supported.
    /// Falls back to a generic message when no specific reason was recorded.
    /// </summary>
    public string? ReasonUnavailable(DriveCapability capability)
    {
        if (Has(capability)) return null;
        return UnavailableReasons.TryGetValue(capability, out var reason)
            ? reason
            : "not supported by this drive";
    }
}
