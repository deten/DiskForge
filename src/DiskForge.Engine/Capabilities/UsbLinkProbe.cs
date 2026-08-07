using System.Runtime.InteropServices;
using static DiskForge.Engine.Native.UsbNative;

namespace DiskForge.Engine.Capabilities;

/// <summary>Raw USB port capability read for one physical disk (V2 flags and/or V1 speed+bcdUSB).</summary>
public sealed record UsbPortInfo(
    bool HasV2, uint Flags, uint SupportedProtocols,
    bool HasV1, int V1Speed, int BcdUsb);

/// <summary>
/// Correlates a physical disk to its USB hub port and reads negotiated-vs-capable link speed.
/// Read-only and unelevated. Returns null when the disk is not on USB or the port cannot be read.
/// </summary>
public static class UsbLinkProbe
{
    private static readonly IntPtr InvalidHandle = new(-1);

    public static UsbPortInfo? Probe(int diskNumber, out string stage)
    {
        stage = "start";
        try
        {
            if (!TryGetDiskDevInst(diskNumber, out var diskDevInst)) { stage = "disk-devinst"; return null; }
            if (!TryFindUsbDevice(diskDevInst, out var usbDevInst)) { stage = "usb-device"; return null; }
            if (CM_Get_Parent(out var hubDevInst, usbDevInst, 0) != CR_SUCCESS) { stage = "hub-parent"; return null; }
            if (!TryGetPort(usbDevInst, out var port)) { stage = "port"; return null; }
            var hubPath = GetHubPath(hubDevInst);
            if (hubPath is null) { stage = "hub-path"; return null; }

            bool hasV2 = TryQueryV2(hubPath, port, out var flags, out var protocols);
            bool hasV1 = TryQueryV1(hubPath, port, out var v1Speed, out var bcdUsb);
            if (!hasV2 && !hasV1) { stage = "ioctl"; return null; }

            stage = "ok";
            return new UsbPortInfo(hasV2, flags, protocols, hasV1, v1Speed, bcdUsb);
        }
        catch (Exception ex)
        {
            stage = "ex:" + ex.Message;
            return null;
        }
    }

    private static bool TryGetDiskDevInst(int diskNumber, out uint devInst)
    {
        devInst = 0;
        var guid = GUID_DEVINTERFACE_DISK;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return false;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index++, ref did))
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out var required, ref info);
                if (required == 0) continue;

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86. DevicePath begins at offset 4.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref did, buffer, required, out _, ref info))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (path is not null && GetDeviceNumber(path) == diskNumber)
                        {
                            devInst = info.DevInst;
                            return true;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return false;
    }

    private static int GetDeviceNumber(string devicePath)
    {
        using var handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return -1;

        int size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
                return Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf).DeviceNumber;
            return -1;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool TryFindUsbDevice(uint diskDevInst, out uint usbDevInst)
    {
        usbDevInst = 0;
        var cur = diskDevInst;
        for (var hop = 0; hop < 16; hop++)
        {
            if (CM_Get_Parent(out var parent, cur, 0) != CR_SUCCESS) return false;
            var id = GetDeviceId(parent) ?? "";
            if (id.StartsWith(@"USB\VID", StringComparison.OrdinalIgnoreCase))
            {
                usbDevInst = parent;
                return true;
            }
            cur = parent;
        }
        return false;
    }

    private static bool TryGetPort(uint usbDevInst, out uint port)
    {
        port = 0;
        uint size = 4;
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            var key = DEVPKEY_Device_Address;
            if (CM_Get_DevNode_PropertyW(usbDevInst, ref key, out var type, buf, ref size, 0) != CR_SUCCESS)
                return false;
            if (type != DEVPROP_TYPE_UINT32) return false;
            port = (uint)Marshal.ReadInt32(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? GetHubPath(uint hubDevInst)
    {
        var hubId = GetDeviceId(hubDevInst);
        if (hubId is null) return null;

        var guid = GUID_DEVINTERFACE_USB_HUB;
        if (CM_Get_Device_Interface_List_SizeW(out var len, ref guid, hubId,
                CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len == 0)
            return null;

        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, hubId, buf, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
            return null;

        var s = new string(buf);
        var nul = s.IndexOf('\0');
        var path = nul >= 0 ? s[..nul] : s;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static bool TryQueryV2(string hubPath, uint port, out uint flags, out uint protocols)
    {
        flags = 0; protocols = 0;
        using var handle = CreateFile(hubPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var info = new USB_NODE_CONNECTION_INFORMATION_EX_V2 { ConnectionIndex = port, Length = 16 };
        int size = Marshal.SizeOf<USB_NODE_CONNECTION_INFORMATION_EX_V2>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buf, false);
            if (DeviceIoControl(handle, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2,
                    buf, size, buf, size, out _, IntPtr.Zero))
            {
                var res = Marshal.PtrToStructure<USB_NODE_CONNECTION_INFORMATION_EX_V2>(buf);
                flags = res.Flags;
                protocols = res.SupportedUsbProtocols;
                return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool TryQueryV1(string hubPath, uint port, out int speed, out int bcdUsb)
    {
        speed = -1; bcdUsb = 0;
        using var handle = CreateFile(hubPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        // USB_NODE_CONNECTION_INFORMATION_EX: ConnectionIndex(0..4), packed USB_DEVICE_DESCRIPTOR(4..22)
        // with bcdUSB at +2 (=offset 6), CurrentConfigurationValue(22), Speed(23). Extra room for pipe list.
        const int bufSize = 512;
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            for (var i = 0; i < bufSize; i += 4) Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, (int)port); // ConnectionIndex

            if (!DeviceIoControl(handle, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                    buf, bufSize, buf, bufSize, out _, IntPtr.Zero))
                return false;

            bcdUsb = (ushort)Marshal.ReadInt16(buf, 6);
            speed = Marshal.ReadByte(buf, 23);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string? GetDeviceId(uint devInst)
    {
        var buf = new char[512];
        if (CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) != CR_SUCCESS) return null;
        var s = new string(buf);
        var nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}
