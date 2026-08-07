using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Native;

/// <summary>
/// Interop for correlating a physical disk to its USB hub port and reading the negotiated vs.
/// capable link speed via <c>IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2</c>. Read-only,
/// works unelevated. Mirrors the approach used by the Windows USBView sample.
/// </summary>
internal static partial class UsbNative
{
    internal static readonly Guid GUID_DEVINTERFACE_DISK = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    internal static readonly Guid GUID_DEVINTERFACE_USB_HUB = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    internal const uint DIGCF_PRESENT = 0x02;
    internal const uint DIGCF_DEVICEINTERFACE = 0x10;

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x01;
    internal const uint FILE_SHARE_WRITE = 0x02;
    internal const uint OPEN_EXISTING = 3;

    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    internal const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x00220448;
    internal const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x0022045C;

    internal const uint CR_SUCCESS = 0;
    internal const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    internal const uint DEVPROP_TYPE_UINT32 = 0x00000007;

    // USB_NODE_CONNECTION_INFORMATION_EX_V2 flag bits
    internal const uint DeviceIsOperatingAtSuperSpeedOrHigher = 0x01;
    internal const uint DeviceIsSuperSpeedCapableOrHigher = 0x02;
    internal const uint DeviceIsOperatingAtSuperSpeedPlusOrHigher = 0x04;
    internal const uint DeviceIsSuperSpeedPlusCapableOrHigher = 0x08;

    // SupportedUsbProtocols bits
    internal const uint Usb300Supported = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_NUMBER
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct USB_NODE_CONNECTION_INFORMATION_EX_V2
    {
        public uint ConnectionIndex;
        public uint Length;
        public uint SupportedUsbProtocols;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Device_Address — the port number on the parent hub.
    internal static readonly DEVPROPKEY DEVPKEY_Device_Address = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 30
    };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid,
        uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    internal static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint CM_Get_Device_IDW(uint dnDevInst, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = false)]
    internal static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        IntPtr PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint pulLen, ref Guid InterfaceClassGuid, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern uint CM_Get_Device_Interface_ListW(
        ref Guid InterfaceClassGuid, string pDeviceID, [Out] char[] Buffer, uint BufferLen, uint ulFlags);
}
