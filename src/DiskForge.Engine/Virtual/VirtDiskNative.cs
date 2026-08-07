using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Virtual;

/// <summary>
/// P/Invoke surface for the Windows Virtual Disk API (virtdisk.dll). Used to create and attach
/// throwaway VHDX loopback disks for the test harness (Phase 3) and, later, VHDX imaging (Phase 7).
/// </summary>
internal static class VirtDiskNative
{
    internal const uint VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;
    internal static readonly Guid VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT =
        new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    // VIRTUAL_DISK_ACCESS_MASK
    internal const uint VIRTUAL_DISK_ACCESS_CREATE = 0x00100000;
    internal const uint VIRTUAL_DISK_ACCESS_ALL = 0x003f0000;

    // CREATE_VIRTUAL_DISK_FLAG
    internal const uint CREATE_VIRTUAL_DISK_FLAG_NONE = 0x00000000;

    // ATTACH_VIRTUAL_DISK_FLAG
    internal const uint ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x00000002;

    // DETACH / OPEN flags
    internal const uint DETACH_VIRTUAL_DISK_FLAG_NONE = 0x00000000;
    internal const uint OPEN_VIRTUAL_DISK_FLAG_NONE = 0x00000000;

    internal const int CREATE_VIRTUAL_DISK_VERSION_1 = 1;
    internal const int ATTACH_VIRTUAL_DISK_VERSION_1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CREATE_VIRTUAL_DISK_PARAMETERS_V1
    {
        public int Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public IntPtr ParentPath;
        public IntPtr SourcePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ATTACH_VIRTUAL_DISK_PARAMETERS_V1
    {
        public int Version;
        public ulong Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint CreateVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE VirtualStorageType,
        string Path,
        uint VirtualDiskAccessMask,
        IntPtr SecurityDescriptor,
        uint Flags,
        uint ProviderSpecificFlags,
        ref CREATE_VIRTUAL_DISK_PARAMETERS_V1 Parameters,
        IntPtr Overlapped,
        out SafeFileHandle Handle);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint OpenVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE VirtualStorageType,
        string Path,
        uint VirtualDiskAccessMask,
        uint Flags,
        IntPtr Parameters,
        out SafeFileHandle Handle);

    [DllImport("virtdisk.dll", SetLastError = false)]
    internal static extern uint AttachVirtualDisk(
        SafeFileHandle VirtualDiskHandle,
        IntPtr SecurityDescriptor,
        uint Flags,
        uint ProviderSpecificFlags,
        ref ATTACH_VIRTUAL_DISK_PARAMETERS_V1 Parameters,
        IntPtr Overlapped);

    [DllImport("virtdisk.dll", SetLastError = false)]
    internal static extern uint DetachVirtualDisk(
        SafeFileHandle VirtualDiskHandle,
        uint Flags,
        uint ProviderSpecificFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle VirtualDiskHandle,
        ref uint DiskPathSizeInBytes,
        StringBuilder DiskPath);
}
