using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskForge.Engine.Native;

/// <summary>
/// Minimal P/Invoke surface for the Phase-2 read-only capability probe. Everything here opens a
/// physical drive with <b>zero</b> access rights and issues only <c>IOCTL_STORAGE_QUERY_PROPERTY</c>,
/// which is non-destructive and works without elevation. No write IOCTLs live in this file.
/// </summary>
internal static partial class NativeMethods
{
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;

    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    internal enum STORAGE_PROPERTY_ID
    {
        StorageDeviceProperty = 0,
        StorageAdapterProperty = 1,
        StorageDeviceSeekPenaltyProperty = 7,
        StorageDeviceTrimProperty = 8
    }

    internal enum STORAGE_QUERY_TYPE
    {
        PropertyStandardQuery = 0,
        PropertyExistsQuery = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public STORAGE_PROPERTY_ID PropertyId;
        public STORAGE_QUERY_TYPE QueryType;
        // 1-byte AdditionalParameters area (unused). Kept as a scalar so the struct stays
        // blittable for source-generated marshalling.
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_TRIM_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool TrimEnabled;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
