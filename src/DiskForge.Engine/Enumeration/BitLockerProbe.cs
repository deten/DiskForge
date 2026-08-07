using System.Management;
using DiskForge.Core.Model;
using Serilog;

namespace DiskForge.Engine.Enumeration;

/// <summary>
/// Reads BitLocker state per drive letter from Win32_EncryptableVolume (§1A.3). Bus-independent and
/// read-only. Requires elevation for full detail; degrades to Unknown rather than throwing when not
/// available. Never touches key material.
/// </summary>
public static class BitLockerProbe
{
    private const string Scope = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

    public static IReadOnlyDictionary<string, BitLockerInfo> ProbeByDriveLetter()
    {
        var map = new Dictionary<string, BitLockerInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(Scope);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));

            foreach (ManagementObject vol in searcher.Get().Cast<ManagementObject>())
            {
                using (vol)
                {
                    var letterRaw = vol.GetString("DriveLetter"); // e.g. "C:"
                    if (letterRaw is null || letterRaw.Length < 1) continue;
                    var letter = letterRaw[..1].ToUpperInvariant();
                    map[letter] = Read(vol);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BitLocker probe unavailable (likely not elevated); reporting Unknown");
        }
        return map;
    }

    private static BitLockerInfo Read(ManagementObject vol)
    {
        var protection = vol.GetU32("ProtectionStatus") switch
        {
            0 => BitLockerProtection.Off,
            1 => BitLockerProtection.On,
            _ => BitLockerProtection.Unknown
        };

        var conversion = BitLockerConversion.Unknown;
        int? percent = null;
        try
        {
            var result = vol.InvokeMethod("GetConversionStatus", null, null);
            if (result is not null)
            {
                conversion = result.GetU32("ConversionStatus") switch
                {
                    0 => BitLockerConversion.FullyDecrypted,
                    1 => BitLockerConversion.FullyEncrypted,
                    2 => BitLockerConversion.EncryptionInProgress,
                    3 => BitLockerConversion.DecryptionInProgress,
                    _ => BitLockerConversion.Unknown
                };
                percent = (int)result.GetU32("EncryptionPercentage");
            }
        }
        catch { /* method needs elevation; leave Unknown */ }

        return new BitLockerInfo
        {
            Protection = protection,
            Conversion = conversion,
            ConversionPercent = percent,
            EncryptionMethod = MapMethod(vol.GetU32("EncryptionMethod"))
        };
    }

    private static string? MapMethod(uint code) => code switch
    {
        0 => null,
        1 => "AES-128 + Diffuser",
        2 => "AES-256 + Diffuser",
        3 => "AES-128",
        4 => "AES-256",
        5 => "Hardware encryption",
        6 => "XTS-AES-128",
        7 => "XTS-AES-256",
        _ => null
    };
}
