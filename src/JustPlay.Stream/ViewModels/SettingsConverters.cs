using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// Formats a double as an integer dB label: e.g. -12 -> "-12 dB".
/// Used by the Audio-tab input-gain readout TextBlock.
///
/// WHY A CONVERTER instead of StringFormat:
///   Avalonia's AVLN compiler (AvaloniaResource step) scans every StringFormat value for
///   {...} patterns and tries to resolve them as XAML type references.  A colon inside the
///   format specifier - e.g. {0:F0} - is mis-read as an XML namespace separator, producing
///   AVLN2000 "Unable to resolve type F0 from namespace" even when the binding has
///   x:CompileBindings="False".  A plain IValueConverter sidesteps the issue entirely.
/// </summary>
public sealed class GainDbConverter : IValueConverter
{
    public static readonly GainDbConverter Instance = new();

    private GainDbConverter() { }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d:F0} dB" : "- dB";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Formats a double to two decimal places: e.g. 1.0 -> "1.00".
/// Used by the DSP-tab EQ / AutoTilt / Punch slider readout TextBlocks.
/// Same AVLN2000 rationale as <see cref="GainDbConverter"/>.
/// </summary>
public sealed class RatioF2Converter : IValueConverter
{
    public static readonly RatioF2Converter Instance = new();

    private RatioF2Converter() { }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{d:F2}" : "-";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
