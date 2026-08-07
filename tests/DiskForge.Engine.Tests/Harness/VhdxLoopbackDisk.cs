using DiskForge.Engine.Virtual;

namespace DiskForge.Engine.Tests.Harness;

/// <summary>
/// A throwaway VHDX attached as a real physical disk for the duration of a test. On dispose it
/// detaches and deletes the backing file. This is the ONLY surface future write-operation tests
/// (resize/format/clone/wipe) should target — never a real drive.
/// </summary>
public sealed class VhdxLoopbackDisk : IDisposable
{
    private readonly AttachedDisk _attached;

    public VhdxLoopbackDisk(ulong sizeBytes = 128UL * 1024 * 1024)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"diskforge-test-{Guid.NewGuid():N}.vhdx");

        VirtualDisk.CreateDynamicVhdx(Path, sizeBytes);
        try
        {
            _attached = VirtualDisk.Attach(Path);
        }
        catch
        {
            TryDeleteFile();
            throw;
        }
    }

    public string Path { get; }
    public int DiskNumber => _attached.DiskNumber;
    public string PhysicalPath => _attached.PhysicalPath;

    public void Dispose()
    {
        _attached.Dispose();
        // Give the OS a moment to release the file after detach.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (TryDeleteFile()) return;
            Thread.Sleep(100);
        }
    }

    private bool TryDeleteFile()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
