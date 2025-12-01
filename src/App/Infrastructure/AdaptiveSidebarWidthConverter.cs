using System;
using System.Globalization;
using System.Windows.Data;

namespace ClopWindows.App.Infrastructure;

/// <summary>
/// Calculates a responsive sidebar width while keeping it within a sensible range.
/// </summary>
public sealed class AdaptiveSidebarWidthConverter : IValueConverter
{
    public double MinWidth { get; set; } = 220;
    public double MaxWidth { get; set; } = 360;
    public double WidthRatio { get; set; } = 0.24;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double actualWidth && !double.IsNaN(actualWidth) && !double.IsInfinity(actualWidth))
        {
            var desired = actualWidth * WidthRatio;
            return Math.Max(MinWidth, Math.Min(MaxWidth, desired));
        }

        return MinWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
