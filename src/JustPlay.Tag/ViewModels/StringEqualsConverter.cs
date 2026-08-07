using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// True when the bound string equals the ConverterParameter (case-insensitive). Used to light the
/// active theme swatch (CurrentTheme == "Aurora" ...), mirroring JUST STREAM's converter.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
