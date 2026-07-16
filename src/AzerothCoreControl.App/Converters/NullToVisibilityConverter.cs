using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AzerothCoreControl.App.Converters;

/// <summary>Collapsed when the bound value is null, Visible otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
