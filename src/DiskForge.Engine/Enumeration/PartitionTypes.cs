using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.Engine.Enumeration;

/// <summary>Well-known GPT partition type GUIDs → semantic partition kind.</summary>
internal static class PartitionTypes
{
    private static readonly Guid Efi = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid Msr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid WindowsRe = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    /// <summary>Linux GPT types. A partition DiskForge formatted as ext4/btrfs/xfs carries the first.</summary>
    private static readonly Guid LinuxData = FileSystemTypeExtensions.LinuxFilesystemDataGuid;
    private static readonly Guid LinuxSwap = FileSystemTypeExtensions.LinuxSwapGuid;
    private static readonly Guid LinuxLvm = new("e6d6d379-f507-44c2-a23c-238f2a3df928");
    private static readonly Guid LinuxRaid = new("a19d880f-05fc-4d3b-a006-743f0f84911e");

    public static Guid? ParseGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw.Trim('{', '}', ' '), out var g) ? g : null;
    }

    public static PartitionKind Classify(Guid? gptType, string? typeString, bool isSystem, byte? mbrType = null)
    {
        if (gptType is { } g)
        {
            if (g == Efi) return PartitionKind.Efi;
            if (g == Msr) return PartitionKind.MicrosoftReserved;
            if (g == WindowsRe) return PartitionKind.Recovery;
            if (g == BasicData) return PartitionKind.Basic;
            if (g == LinuxData || g == LinuxSwap || g == LinuxLvm || g == LinuxRaid) return PartitionKind.Linux;
        }

        // MBR: 0x83 Linux, 0x82 swap. Windows reports these as "Unknown", which would hide the fact
        // that the extent holds a Linux filesystem.
        if (mbrType is FileSystemTypeExtensions.LinuxMbrType or FileSystemTypeExtensions.LinuxSwapMbrType)
            return PartitionKind.Linux;

        var t = typeString?.ToLowerInvariant() ?? "";
        if (t.Contains("system") || isSystem) return PartitionKind.Efi;
        if (t.Contains("reserved")) return PartitionKind.MicrosoftReserved;
        if (t.Contains("recovery")) return PartitionKind.Recovery;
        if (t.Contains("basic") || t.Length > 0) return PartitionKind.Basic;
        return PartitionKind.Unknown;
    }
}
