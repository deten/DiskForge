namespace DiskForge.Core.Model;

/// <summary>
/// How a drive is connected: interface, the speed it negotiated, the speed it's capable of, and
/// (best-effort) form factor. Populated by the link probe. Honest by design — fields Windows does not
/// expose are left null with a note rather than guessed.
/// </summary>
public sealed class LinkInfo
{
    /// <summary>e.g. "USB", "NVMe (PCIe)", "SATA".</summary>
    public string Interface { get; init; } = "";

    /// <summary>Human negotiated link speed, e.g. "5 Gbps (SuperSpeed / USB 3.x)".</summary>
    public string? NegotiatedSpeed { get; init; }

    /// <summary>Best-known speed the device itself is capable of.</summary>
    public string? CapableSpeed { get; init; }

    /// <summary>True when the device can go faster than the link it negotiated (e.g. USB 3 drive in a USB 2 port).</summary>
    public bool IsUnderNegotiated { get; init; }

    /// <summary>Actionable hint when <see cref="IsUnderNegotiated"/> is true.</summary>
    public string? MismatchHint { get; init; }

    /// <summary>Best-effort form factor, or null when Windows does not report it.</summary>
    public string? FormFactor { get; init; }

    /// <summary>Probe notes (what was inferred vs unavailable and why).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>One-line summary for compact display.</summary>
    public string Summary
    {
        get
        {
            var s = Interface;
            if (NegotiatedSpeed is { } n) s += $" · {n}";
            if (IsUnderNegotiated) s += " ⚠";
            return s;
        }
    }
}
