using System.Runtime.InteropServices;
using static DiskForge.Engine.Native.NativeMethods;

namespace DiskForge.Engine.Native;

/// <summary>Read-only, unelevated probe results from a physical drive.</summary>
public sealed record NativeDriveProbe(bool? TrimEnabled, bool? IncursSeekPenalty, bool Opened, string? Error);

/// <summary>
/// Issues the two safe, read-only descriptor queries (TRIM support, seek penalty) against
/// <c>\\.\PhysicalDriveN</c>. Opens with no access rights so no elevation is required and no
/// write path is ever reachable from here.
/// </summary>
public static class PhysicalDriveProbe
{
    public static NativeDriveProbe Probe(int diskNumber)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        try
        {
            using var handle = CreateFile(
                path,
                0, // no read/write — query-only, avoids needing admin
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return new NativeDriveProbe(null, null, false, $"open failed (win32 {Marshal.GetLastWin32Error()})");

            bool? trim = QueryTrim(handle);
            bool? seek = QuerySeekPenalty(handle);
            return new NativeDriveProbe(trim, seek, true, null);
        }
        catch (Exception ex)
        {
            return new NativeDriveProbe(null, null, false, ex.Message);
        }
    }

    private static bool? QueryTrim(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceTrimProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery
        };
        int size = Marshal.SizeOf<DEVICE_TRIM_DESCRIPTOR>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf(query),
                    buffer, size, out _, IntPtr.Zero))
            {
                var d = Marshal.PtrToStructure<DEVICE_TRIM_DESCRIPTOR>(buffer);
                return d.TrimEnabled;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool? QuerySeekPenalty(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery
        };
        int size = Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf(query),
                    buffer, size, out _, IntPtr.Zero))
            {
                var d = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(buffer);
                return d.IncursSeekPenalty;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
