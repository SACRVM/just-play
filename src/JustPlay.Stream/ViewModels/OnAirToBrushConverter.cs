using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// Maps the OnAir bool to the ON-AIR band colour: green when live, muted purple-grey when offline.
/// Used by the status dot / ON-AIR band so the "are we streaming?" cue is unmistakable (Sec.3a hard req).
/// </summary>
public sealed class OnAirToBrushConverter : IValueConverter
{
    public static readonly OnAirToBrushConverter Instance = new();

    private static readonly IBrush OnAir = new SolidColorBrush(Color.Parse("#36e07a"));
    private static readonly IBrush Offline = new SolidColorBrush(Color.Parse("#5a4f78"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? OnAir : Offline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
