using System.Management;

namespace DiskForge.Engine.Enumeration;

/// <summary>Null-tolerant typed accessors over <see cref="ManagementBaseObject"/> property bags.</summary>
internal static class WmiExtensions
{
    public static string? GetString(this ManagementBaseObject o, string name)
    {
        var v = o.SafeGet(name);
        return v?.ToString()?.Trim() is { Length: > 0 } s ? s : null;
    }

    public static ulong GetU64(this ManagementBaseObject o, string name)
        => o.SafeGet(name) is { } v ? Convert.ToUInt64(v) : 0UL;

    public static uint GetU32(this ManagementBaseObject o, string name)
        => o.SafeGet(name) is { } v ? Convert.ToUInt32(v) : 0U;

    public static uint? GetU32Nullable(this ManagementBaseObject o, string name)
        => o.SafeGet(name) is { } v ? Convert.ToUInt32(v) : null;

    public static ushort GetU16(this ManagementBaseObject o, string name)
        => o.SafeGet(name) is { } v ? Convert.ToUInt16(v) : (ushort)0;

    public static bool GetBool(this ManagementBaseObject o, string name)
        => o.SafeGet(name) is { } v && Convert.ToBoolean(v);

    public static string[] GetStringArray(this ManagementBaseObject o, string name)
        => o.SafeGet(name) as string[] ?? Array.Empty<string>();

    /// <summary>MSFT_Partition.DriveLetter is a char code; 0 means unassigned.</summary>
    public static string? GetDriveLetter(this ManagementBaseObject o, string name)
    {
        if (o.SafeGet(name) is not { } v) return null;
        var code = Convert.ToUInt16(v);
        if (code is >= (ushort)'A' and <= (ushort)'Z') return ((char)code).ToString();
        if (code is >= (ushort)'a' and <= (ushort)'z') return char.ToUpperInvariant((char)code).ToString();
        return null;
    }

    private static object? SafeGet(this ManagementBaseObject o, string name)
    {
        try { return o[name]; }
        catch { return null; }
    }
}
