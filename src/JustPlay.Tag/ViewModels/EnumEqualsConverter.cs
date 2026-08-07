using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// True when the bound enum value's name equals the ConverterParameter (case-insensitive). Lights the
/// active write-format option (WriteFormat == "Id3v23Utf16" ...) - the enum sibling of
/// <see cref="StringEqualsConverter"/> (which only handles string-typed bindings).
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
