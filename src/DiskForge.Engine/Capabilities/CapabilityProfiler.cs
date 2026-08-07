using DiskForge.Core.Model;
using DiskForge.Engine.Native;

namespace DiskForge.Engine.Capabilities;

/// <summary>
/// Synthesizes a <see cref="DriveCapabilities"/> from what we can actually observe read-only in
/// Phase 2 (bus, media, TRIM via IOCTL) plus the §1A.8 baseline matrix as bus-aware expectation.
/// It is deliberately conservative and honest: hardware-erase / SED / freeze state are reported as
/// <b>pending</b> (not falsely "supported") until the real pass-through probes land, and every
/// unsupported capability carries a specific reason (§1A.7).
/// </summary>
public static class CapabilityProfiler
{
    public static DriveCapabilities Profile(StorageBus bus, DiskMediaType media, bool isRemovable, NativeDriveProbe probe)
    {
        var supported = DriveCapability.PartitionEdit | DriveCapability.Format
                        | DriveCapability.Clone | DriveCapability.Image;
        var probed = DriveCapability.None;
        var reasons = new Dictionary<DriveCapability, string>();
        var notes = new List<string>();

        bool isUsb = bus == StorageBus.Usb;
        bool isNvme = bus == StorageBus.Nvme;
        bool solidState = media is DiskMediaType.Ssd or DiskMediaType.Scm || isNvme || probe.IncursSeekPenalty == false;

        // --- TRIM (really probed) ---
        if (probe.Opened && probe.TrimEnabled is { } trim)
        {
            probed |= DriveCapability.Trim;
            if (trim) supported |= DriveCapability.Trim;
            else reasons[DriveCapability.Trim] = "drive reports TRIM not enabled";
        }
        else
        {
            reasons[DriveCapability.Trim] = probe.Error is { } e ? $"TRIM query failed: {e}" : "TRIM not reported";
        }

        // --- SMART / temperature (inferred from bus per §1A.8; not yet hardware-read) ---
        bool smart = !isUsb && !isRemovable;
        if (smart)
        {
            supported |= DriveCapability.Smart | DriveCapability.Temperature;
        }
        else
        {
            reasons[DriveCapability.Smart] = isUsb
                ? "SMART/health typically blocked over USB bridge (§1A.8)"
                : "SMART/health not available on removable media";
            reasons[DriveCapability.Temperature] = reasons[DriveCapability.Smart];
        }

        // --- Overwrite fallback: meaningful for HDD; discouraged on solid-state (§1A.5) ---
        if (!solidState)
            supported |= DriveCapability.SectorOverwrite;
        else
            reasons[DriveCapability.SectorOverwrite] =
                "avoid multi-pass overwrite on solid-state media; route to hardware secure-erase (§1A.5)";

        // --- Hardware secure-erase & SED families: bus-aware reason, PENDING real probe (§1A.6) ---
        const string usbBlocked =
            "typically blocked over USB bridge — connect via native SATA/NVMe (§1A.8)";
        bool isAta = bus is StorageBus.Sata or StorageBus.Ata or StorageBus.Atapi;
        bool isScsi = bus is StorageBus.Sas or StorageBus.Scsi;

        string AtaReason() => isUsb ? usbBlocked
            : isAta ? "ATA Secure-Erase/Sanitize detection pending hardware probe (§1A.6, Coming Soon)"
            : $"ATA security model not applicable on {bus} bus (use the bus-native erase)";
        string NvmeReason() => isNvme
            ? "NVMe Format/Sanitize detection pending hardware probe (§1A.6, Coming Soon)"
            : $"NVMe erase not applicable on {bus} bus";
        string ScsiReason() => isScsi
            ? "SCSI SANITIZE detection pending hardware probe (§1A.6, Coming Soon)"
            : $"SCSI SANITIZE not applicable on {bus} bus";
        string TcgReason() => isUsb ? usbBlocked
            : "TCG/SED detection pending hardware probe (§1A.6, Coming Soon)";

        reasons[DriveCapability.AtaSecurity] = AtaReason();
        reasons[DriveCapability.AtaSecureErase] = AtaReason();
        reasons[DriveCapability.AtaSanitize] = AtaReason();
        reasons[DriveCapability.NvmeFormat] = NvmeReason();
        reasons[DriveCapability.NvmeSanitize] = NvmeReason();
        reasons[DriveCapability.ScsiSanitize] = ScsiReason();
        reasons[DriveCapability.TcgOpal] = TcgReason();
        reasons[DriveCapability.TcgCryptoErase] = TcgReason();
        reasons[DriveCapability.Edrive] = TcgReason();

        reasons[DriveCapability.HpaDco] = "HPA/DCO detection pending hardware probe (§1A.1)";

        var freeze = isNvme ? FreezeState.NotApplicable : FreezeState.Unknown;
        if (freeze == FreezeState.Unknown)
            notes.Add("ATA security-freeze state not yet probed; required before Secure Erase (§1A.6).");
        if (isUsb)
            notes.Add("Drive is behind a USB bridge — assume hardware erase/SED unavailable until proven otherwise (§1A.8).");

        return new DriveCapabilities
        {
            Supported = supported,
            Probed = probed,
            AtaSecurityFreeze = freeze,
            SmartAvailable = smart,
            HasHiddenCapacity = false,
            UnavailableReasons = reasons,
            Notes = notes
        };
    }
}
