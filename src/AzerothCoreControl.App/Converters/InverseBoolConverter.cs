using System.Globalization;
using System.Windows.Data;

namespace AzerothCoreControl.App.Converters;

/// <summary>Negates a bool — for binding IsEnabled to an "is busy" flag.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}
