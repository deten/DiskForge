using DiskForge.Core.Model;
using DiskForge.Engine.Enumeration;
using DiskForge.Engine.Linux;
using Serilog;

namespace DiskForge.Engine;

/// <summary>
/// Phase-2 entry point: captures a read-only <see cref="SystemState"/> snapshot of every disk,
/// its partitions/volumes, encryption and capability profile. This is the harmless milestone —
/// it performs no writes.
/// </summary>
public sealed class SystemInspector
{
    private readonly StorageEnumerator _enumerator = new();

    /// <param name="probeLinuxToolchain">
    /// Include Linux-filesystem tool detection. The probe is cached process-wide, so only the first
    /// capture pays for it; pass false to keep a capture strictly disk-only.
    /// </param>
    public SystemState Capture(bool probeLinuxToolchain = true)
    {
        var elevated = Elevation.IsElevated();
        IReadOnlyList<PhysicalDiskInfo> disks;
        try
        {
            disks = _enumerator.EnumerateDisks();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Disk enumeration failed");
            disks = Array.Empty<PhysicalDiskInfo>();
        }

        var systemDisk = disks.FirstOrDefault(d => d.IsSystemDisk)
                         ?? disks.FirstOrDefault(d => d.IsBootDisk);

        var linux = LinuxToolchainInfo.NotProbed;
        if (probeLinuxToolchain)
        {
            try
            {
                linux = LinuxToolchainProbe.Get();
            }
            catch (Exception ex)
            {
                // A broken WSL install must degrade to "Linux formats unavailable", never break
                // disk enumeration — the same way the BitLocker probe degrades to Unknown.
                Log.Warning(ex, "Linux toolchain probe failed");
                linux = new LinuxToolchainInfo
                {
                    IsAvailable = false,
                    Reason = "Probing the Linux toolchain failed: " + ex.Message
                };
            }
        }

        Log.Information("Captured {Count} disk(s); elevated={Elevated}; systemDisk={System}; linuxFs={Linux}",
            disks.Count, elevated, systemDisk?.Number,
            linux.IsAvailable ? linux.BackendName : linux.Reason);

        return new SystemState
        {
            CapturedAt = DateTimeOffset.Now,
            Disks = disks,
            IsElevated = elevated,
            SystemDiskNumber = systemDisk?.Number,
            LinuxToolchain = linux
        };
    }
}
