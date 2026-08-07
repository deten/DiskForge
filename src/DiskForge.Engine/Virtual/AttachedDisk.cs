using Microsoft.Win32.SafeHandles;
using Serilog;
using static DiskForge.Engine.Virtual.VirtDiskNative;

namespace DiskForge.Engine.Virtual;

/// <summary>
/// An attached VHDX surfaced as a physical disk. Disposing detaches it (and, because the disk was
/// attached with a non-permanent lifetime, closing the handle also releases it).
/// </summary>
public sealed class AttachedDisk : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _disposed;

    internal AttachedDisk(SafeFileHandle handle, string physicalPath, int diskNumber)
    {
        _handle = handle;
        PhysicalPath = physicalPath;
        DiskNumber = diskNumber;
    }

    /// <summary>Windows physical drive number this VHDX is surfaced as (e.g. 4 for \\.\PhysicalDrive4).</summary>
    public int DiskNumber { get; }

    public string PhysicalPath { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_handle.IsInvalid)
            {
                var result = DetachVirtualDisk(_handle, DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
                if (result != 0)
                    Log.Warning("DetachVirtualDisk returned {Code} for disk {Number}", result, DiskNumber);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Detach failed for disk {Number}", DiskNumber);
        }
        finally
        {
            _handle.Dispose();
        }
    }
}
