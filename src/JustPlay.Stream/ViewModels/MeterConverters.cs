using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// Maps a linear level (0..1) to a 0..1 fill position on a −48..0 dB ProgressBar.
/// Formula: pos = clamp((20·log10(max(x, 1e-4)) + 48) / 48, 0, 1)
/// Used by the L/R meter bars in the METERS ROW (just-stream-blueprint.md §3a).
/// </summary>
public sealed class LinearToDbFillConverter : IValueConverter
{
    public static readonly LinearToDbFillConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            var db = 20.0 * Math.Log10(Math.Max(d, 1e-4));
            return Math.Clamp((db + 48.0) / 48.0, 0.0, 1.0);
        }
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
