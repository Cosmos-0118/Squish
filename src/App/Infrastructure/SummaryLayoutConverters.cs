using System;
using System.Globalization;
using System.Windows.Data;

namespace Squish.App.Infrastructure;

/// <summary>
/// Turns a measured size into a proportional one, so a control can follow the window instead of a fixed pixel size.
/// Parameter: <c>"factor|min"</c> (for example <c>"0.3|150"</c> = 30% of the value, never below 150). There is no upper limit.
/// </summary>
public sealed class ScaledSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var size = value is double d && double.IsFinite(d) ? d : 0d;
        var parts = (parameter as string ?? string.Empty).Split('|');
        var factor = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1d;
        var min = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0d;
        return Math.Max(min, size * factor);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Bar height = fraction (0–1) of the chart's current height, with a small floor so empty days still show a baseline tick.</summary>
public sealed class BarHeightConverter : IMultiValueConverter
{
    private const double Floor = 3d;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = values.Length > 0 && values[0] is double f && double.IsFinite(f) ? Math.Clamp(f, 0d, 1d) : 0d;
        var height = values.Length > 1 && values[1] is double h && double.IsFinite(h) ? h : 0d;
        return Math.Max(Floor, fraction * (height - 2d));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
