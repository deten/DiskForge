using DiskForge.Engine;
using DiskForge.Engine.Tests.Harness;
using DiskForge.Engine.Virtual;

namespace DiskForge.Engine.Tests;

public class VirtualDiskTests
{
    /// <summary>Creating a VHDX file does not require elevation — validates the create interop everywhere.</summary>
    [Fact]
    public void CreateDynamicVhdx_ProducesNonEmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diskforge-create-{Guid.NewGuid():N}.vhdx");
        try
        {
            VirtualDisk.CreateDynamicVhdx(path, 64UL * 1024 * 1024);

            Assert.True(File.Exists(path), "VHDX file should exist after creation");
            Assert.True(new FileInfo(path).Length > 0, "VHDX file should not be empty");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [RequiresElevationFact]
    public void Attach_SurfacesDisk_AndDetachReleasesIt()
    {
        int diskNumber;
        using (var disk = new VhdxLoopbackDisk(128UL * 1024 * 1024))
        {
            diskNumber = disk.DiskNumber;
            Assert.True(diskNumber >= 0);
            Assert.Contains("PhysicalDrive", disk.PhysicalPath);

            // The freshly attached VHDX must show up in the real enumeration path.
            var state = new SystemInspector().Capture();
            Assert.Contains(state.Disks, d => d.Number == diskNumber);
        }

        // After dispose (detach) the disk must be gone.
        var after = new SystemInspector().Capture();
        Assert.DoesNotContain(after.Disks, d => d.Number == diskNumber);
    }
}
