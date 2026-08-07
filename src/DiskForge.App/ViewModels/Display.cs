using System.Windows.Media;
using DiskForge.Core.Model;

namespace DiskForge.App.ViewModels;

/// <summary>Shared formatting + color mapping for the dashboard.</summary>
internal static class Display
{
    public static string Size(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    /// <summary>Color-code partition segments by role (§5).</summary>
    public static Brush BrushFor(PartitionKind kind) => kind switch
    {
        PartitionKind.System => Frozen(0xF5, 0x9E, 0x0B),            // amber
        PartitionKind.Efi => Frozen(0x7C, 0x5C, 0xFF),              // purple
        PartitionKind.Recovery => Frozen(0x14, 0xB8, 0xA6),         // teal
        PartitionKind.MicrosoftReserved => Frozen(0x6B, 0x72, 0x80),// grey
        PartitionKind.Basic => Frozen(0x3B, 0x82, 0xF6),           // blue
        PartitionKind.Linux => Frozen(0xE9, 0x6C, 0x2B),           // ext4/btrfs/xfs — Linux orange
        PartitionKind.Unallocated => Frozen(0x37, 0x3F, 0x4B),     // muted slate
        _ => Frozen(0x9C, 0xA3, 0xAF)
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
