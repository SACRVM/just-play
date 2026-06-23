using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// True when the bound string equals the ConverterParameter. Used to light the active segment
/// of the limiter-drive segmented control (Classes.active binding).
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
