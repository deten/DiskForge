using System.Globalization;
using System.Windows.Data;

namespace DiskForge.App.Converters;

/// <summary>
/// MultiBinding [fraction, hostActualWidth] → pixel width, so partition segments fill the map bar
/// proportionally and reflow on window resize. A minimum keeps tiny-but-real partitions (a 16 MB MSR on
/// a multi-TB disk is ~0.0004% wide) visible and clickable instead of collapsing to an invisible sliver.
/// </summary>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    /// <summary>Smallest on-screen width for any real segment — enough to see and click.</summary>
    private const double MinSegmentPx = 10d;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double fraction || values[1] is not double width) return 0d;
        if (double.IsNaN(width) || width <= 0) return 0d;

        // A genuinely absent segment stays 0; any partition with real size gets at least the minimum.
        if (fraction <= 0) return 0d;

        var px = fraction * width;
        return double.IsFinite(px) ? Math.Max(px, MinSegmentPx) : 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
