using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Squish.App.Infrastructure;

/// <summary>
/// Shows an element only when the bound value's name matches the converter parameter,
/// e.g. <c>Visibility="{Binding SelectedSettingsSection, Converter=..., ConverterParameter=Appearance}"</c>.
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
