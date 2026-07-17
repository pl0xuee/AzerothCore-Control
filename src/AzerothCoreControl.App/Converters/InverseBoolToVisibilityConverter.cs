using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AzerothCoreControl.App.Converters;

/// <summary>Visible when the bound bool is false — for "nothing selected" / empty-state overlays.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed or Visibility.Hidden;
}
