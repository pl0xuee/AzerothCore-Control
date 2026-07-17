using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AzerothCoreControl.App.ViewModels;

namespace AzerothCoreControl.App.Converters;

/// <summary>Maps a <see cref="ConsoleSeverity"/> to the brush a console line is drawn in.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Frozen("#FFCBD3DC");
    private static readonly SolidColorBrush Warning = Frozen("#FFE9B85A");
    private static readonly SolidColorBrush Error = Frozen("#FFFF6F60");
    private static readonly SolidColorBrush Command = Frozen("#FF5FD98B");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConsoleSeverity.Warning => Warning,
        ConsoleSeverity.Error => Error,
        ConsoleSeverity.Command => Command,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
