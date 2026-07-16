using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AzerothCoreControl.Core.Models;

namespace AzerothCoreControl.App.Converters;

/// <summary>Maps a <see cref="ServerState"/> to the matching status brush from App resources.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ServerState.Running => "RunningBrush",
            ServerState.Crashed => "CrashedBrush",
            ServerState.Starting or ServerState.Restarting => "RestartingBrush",
            _ => "StoppedBrush",
        };
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
