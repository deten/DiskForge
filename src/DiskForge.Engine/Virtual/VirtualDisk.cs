using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using static DiskForge.Engine.Virtual.VirtDiskNative;

namespace DiskForge.Engine.Virtual;

/// <summary>
/// Managed wrapper over the Windows Virtual Disk API for creating and attaching VHDX files.
/// Creating a VHDX works unelevated; <b>attaching</b> (surfacing it as a physical disk) requires
/// Administrator.
/// </summary>
public static class VirtualDisk
{
    private const uint ErrorSuccess = 0;

    private static VIRTUAL_STORAGE_TYPE VhdxStorageType() => new()
    {
        DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX,
        VendorId = VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT
    };

    /// <summary>Creates a dynamically-expanding VHDX of the given maximum size.</summary>
    public static void CreateDynamicVhdx(string path, ulong maximumSizeBytes)
    {
        var storageType = VhdxStorageType();
        var parameters = new CREATE_VIRTUAL_DISK_PARAMETERS_V1
        {
            Version = CREATE_VIRTUAL_DISK_VERSION_1,
            UniqueId = Guid.NewGuid(),
            MaximumSize = maximumSizeBytes,
            BlockSizeInBytes = 0,   // provider default
            SectorSizeInBytes = 0,  // provider default
            ParentPath = IntPtr.Zero,
            SourcePath = IntPtr.Zero
        };

        var result = CreateVirtualDisk(
            ref storageType, path, VIRTUAL_DISK_ACCESS_CREATE, IntPtr.Zero,
            CREATE_VIRTUAL_DISK_FLAG_NONE, 0, ref parameters, IntPtr.Zero, out var handle);

        using (handle)
        {
            if (result != ErrorSuccess)
                throw new Win32Exception((int)result, $"CreateVirtualDisk failed for '{path}'");
        }
        Log.Debug("Created VHDX {Path} ({Size} bytes max)", path, maximumSizeBytes);
    }

    /// <summary>
    /// Opens and attaches a VHDX, surfacing it as a physical disk. The returned
    /// <see cref="AttachedDisk"/> detaches on dispose. Requires Administrator.
    /// </summary>
    public static AttachedDisk Attach(string path)
    {
        var storageType = VhdxStorageType();
        var openResult = OpenVirtualDisk(
            ref storageType, path, VIRTUAL_DISK_ACCESS_ALL, OPEN_VIRTUAL_DISK_FLAG_NONE,
            IntPtr.Zero, out var handle);
        if (openResult != ErrorSuccess)
        {
            handle.Dispose();
            throw new Win32Exception((int)openResult, $"OpenVirtualDisk failed for '{path}'");
        }

        var attachParams = new ATTACH_VIRTUAL_DISK_PARAMETERS_V1 { Version = ATTACH_VIRTUAL_DISK_VERSION_1 };
        var attachResult = AttachVirtualDisk(
            handle, IntPtr.Zero, ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER, 0,
            ref attachParams, IntPtr.Zero);
        if (attachResult != ErrorSuccess)
        {
            handle.Dispose();
            throw new Win32Exception((int)attachResult, $"AttachVirtualDisk failed for '{path}'");
        }

        var physicalPath = ReadPhysicalPath(handle);
        var diskNumber = ParseDiskNumber(physicalPath);
        Log.Debug("Attached VHDX {Path} as {Physical} (disk {Number})", path, physicalPath, diskNumber);
        return new AttachedDisk(handle, physicalPath, diskNumber);
    }

    private static string ReadPhysicalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        uint sizeBytes = 512;
        var sb = new StringBuilder((int)sizeBytes / 2);
        var result = GetVirtualDiskPhysicalPath(handle, ref sizeBytes, sb);
        if (result != ErrorSuccess)
            throw new Win32Exception((int)result, "GetVirtualDiskPhysicalPath failed");
        return sb.ToString();
    }

    private static int ParseDiskNumber(string physicalPath)
    {
        var m = Regex.Match(physicalPath, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException($"Unexpected physical path '{physicalPath}'");
        return int.Parse(m.Groups[1].Value);
    }
}
