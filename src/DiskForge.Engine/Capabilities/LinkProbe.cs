using DiskForge.Core.Model;
using static DiskForge.Engine.Native.UsbNative;

namespace DiskForge.Engine.Capabilities;

/// <summary>
/// Builds the <see cref="LinkInfo"/> for a disk: real USB negotiated-vs-capable speed (incl. the
/// "3.x drive in a 2.0 port" mismatch), and honest best-effort labels for SATA/NVMe/form-factor
/// where Windows does not expose the data.
/// </summary>
public static class LinkProbe
{
    public static LinkInfo Probe(int diskNumber, StorageBus bus, DiskMediaType media, bool isSolidState) => bus switch
    {
        StorageBus.Usb => BuildUsb(diskNumber),

        StorageBus.Nvme => new LinkInfo
        {
            Interface = "NVMe (PCIe)",
            FormFactor = "M.2 / add-in (not reported by Windows)",
            Notes = new[]
            {
                "PCIe link generation/width is not exposed by Windows — not shown.",
                "Physical form factor (M.2 vs U.2 vs add-in card) is not reported by Windows."
            }
        },

        StorageBus.Sata or StorageBus.Ata => new LinkInfo
        {
            Interface = "SATA",
            FormFactor = isSolidState
                ? "2.5\" or M.2 (not reported by Windows)"
                : "3.5\" / 2.5\" (not reported by Windows)",
            Notes = new[] { "SATA negotiated link speed needs an ATA IDENTIFY pass-through (Administrator) — not shown." }
        },

        _ => new LinkInfo { Interface = bus.ToString() }
    };

    private static LinkInfo BuildUsb(int diskNumber)
    {
        var port = UsbLinkProbe.Probe(diskNumber, out var stage);
        if (port is null)
        {
            return new LinkInfo
            {
                Interface = "USB",
                FormFactor = "USB enclosure",
                Notes = new[] { $"USB port speed unavailable (probe stage: {stage})." }
            };
        }

        // Prefer precise V2 SuperSpeed flags; fall back to V1 speed + device bcdUSB (works on USB 2.0 hubs).
        bool opSSP = port.HasV2 && (port.Flags & DeviceIsOperatingAtSuperSpeedPlusOrHigher) != 0;
        bool opSS = port.HasV2 && (port.Flags & DeviceIsOperatingAtSuperSpeedOrHigher) != 0;
        bool capSSP = port.HasV2 && (port.Flags & DeviceIsSuperSpeedPlusCapableOrHigher) != 0;
        bool capSS = port.HasV2 && (port.Flags & DeviceIsSuperSpeedCapableOrHigher) != 0;
        bool portIsUsb3 = port.HasV2 && (port.SupportedProtocols & Usb300Supported) != 0;
        bool deviceClaimsUsb3 = port.HasV1 && port.BcdUsb >= 0x0300;

        bool operatingSuper = opSSP || opSS || (port.HasV1 && port.V1Speed >= 3);

        string negotiated = opSSP ? "10 Gbps (SuperSpeed+ / USB 3.1 Gen 2)"
            : opSS ? "5 Gbps (SuperSpeed / USB 3.x Gen 1)"
            : port.HasV1
                ? port.V1Speed switch
                {
                    3 => "5 Gbps (SuperSpeed / USB 3.x)",
                    2 => "480 Mbps (High-Speed / USB 2.0)",
                    1 => "12 Mbps (Full-Speed / USB 1.1)",
                    0 => "1.5 Mbps (Low-Speed / USB 1.0)",
                    _ => "USB (speed unknown)"
                }
                : "480 Mbps (High-Speed / USB 2.0)";

        bool capableUsb3 = capSSP || capSS || deviceClaimsUsb3;
        string capable = capSSP ? "10 Gbps (USB 3.1 Gen 2)"
            : capSS ? "5 Gbps (USB 3.x)"
            : deviceClaimsUsb3 ? "5 Gbps+ (USB 3.x)"
            : "480 Mbps (USB 2.0)";

        bool under = capableUsb3 && !operatingSuper;
        string? hint = under
            ? portIsUsb3
                ? "This drive is USB 3-capable but negotiated a slower link — try a different cable or port."
                : "This drive is USB 3-capable but this port is running at USB 2.0 — plug it into a USB 3 (often blue) port for full speed."
            : null;

        var notes = new List<string>();
        if (port.HasV1 && port.BcdUsb > 0)
            notes.Add($"Device reports USB spec {port.BcdUsb >> 8}.{(port.BcdUsb & 0xFF) >> 4} (bcdUSB 0x{port.BcdUsb:X4}).");

        return new LinkInfo
        {
            Interface = "USB",
            NegotiatedSpeed = negotiated,
            CapableSpeed = capable,
            IsUnderNegotiated = under,
            MismatchHint = hint,
            FormFactor = "USB enclosure",
            Notes = notes
        };
    }
}
