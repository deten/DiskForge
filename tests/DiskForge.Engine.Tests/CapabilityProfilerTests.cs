using DiskForge.Core.Model;
using DiskForge.Engine.Capabilities;
using DiskForge.Engine.Native;

namespace DiskForge.Engine.Tests;

public class CapabilityProfilerTests
{
    private static NativeDriveProbe Probe(bool? trim, bool? seek = null)
        => new(trim, seek, Opened: true, Error: null);

    [Fact]
    public void NvmeSsd_HasTrim_NoSectorOverwrite_FreezeNotApplicable()
    {
        var caps = CapabilityProfiler.Profile(StorageBus.Nvme, DiskMediaType.Ssd, isRemovable: false, Probe(trim: true));

        Assert.True(caps.Has(DriveCapability.Trim));
        Assert.True(caps.Has(DriveCapability.PartitionEdit));
        Assert.True(caps.Has(DriveCapability.Smart));
        // Multi-pass overwrite must NOT be offered on solid-state media (§1A.5).
        Assert.False(caps.Has(DriveCapability.SectorOverwrite));
        Assert.Equal(FreezeState.NotApplicable, caps.AtaSecurityFreeze);
    }

    [Fact]
    public void SataHdd_OffersSectorOverwrite_FreezeUnknown()
    {
        var caps = CapabilityProfiler.Profile(StorageBus.Sata, DiskMediaType.Hdd, isRemovable: false, Probe(trim: false));

        Assert.True(caps.Has(DriveCapability.SectorOverwrite));
        Assert.False(caps.Has(DriveCapability.Trim));
        Assert.Equal(FreezeState.Unknown, caps.AtaSecurityFreeze);
        Assert.Contains("ATA", caps.ReasonUnavailable(DriveCapability.AtaSecureErase));
    }

    [Fact]
    public void UsbDrive_BlocksSmartAndHardwareErase_WithBridgeReason()
    {
        var caps = CapabilityProfiler.Profile(StorageBus.Usb, DiskMediaType.Ssd, isRemovable: true, Probe(trim: null));

        Assert.False(caps.Has(DriveCapability.Smart));
        Assert.Contains("USB", caps.ReasonUnavailable(DriveCapability.Smart));
        Assert.Contains("USB", caps.ReasonUnavailable(DriveCapability.TcgCryptoErase));
    }

    [Fact]
    public void NvmeErase_NotApplicable_OnSataBus()
    {
        var caps = CapabilityProfiler.Profile(StorageBus.Sata, DiskMediaType.Hdd, isRemovable: false, Probe(trim: false));
        Assert.Contains("not applicable", caps.ReasonUnavailable(DriveCapability.NvmeSanitize));
    }
}
