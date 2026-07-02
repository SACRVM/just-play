using System;
using System.Globalization;
using Avalonia.Data.Converters;
using JustPlay.Core.Audio;

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

/// <summary>
/// Maps a linear monitor volume (0..1) to a dB string with one decimal: 1.0→"0.0 dB", 0.5→"-6.0 dB",
/// ≤0→"-∞". Same visual style as GainDbConverter.
/// </summary>
public sealed class VolumeDbConverter : IValueConverter
{
    public static readonly VolumeDbConverter Instance = new();

    private VolumeDbConverter() { }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            if (d <= 0.0) return "-∞";
            var db = 20.0 * Math.Log10(d);
            return $"{db:F1} dB";
        }
        return "-∞";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps the app-capture channel choice to a friendly label for the "BROADCAST CHANNELS" ComboBox
/// (App source only): channels 1-2 (the default), channels 3-4, or the full stereo mix.
/// </summary>
public sealed class AppChannelsLabelConverter : IValueConverter
{
    public static readonly AppChannelsLabelConverter Instance = new();

    private AppChannelsLabelConverter() { }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            AppCaptureChannels.Master34 => "Channels 3-4",
            AppCaptureChannels.FullMix => "Full mix (stereo)",
            _ => "Channels 1-2", // Master12 (the default) + any fallback
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a sample-rate int (44100 / 48000) to a human label ("44.1 kHz" / "48 kHz").
/// Used by the RATE ComboBox ItemTemplate in the STREAM INFO grid.
/// </summary>
public sealed class SampleRateLabelConverter : IValueConverter
{
    public static readonly SampleRateLabelConverter Instance = new();

    private SampleRateLabelConverter() { }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            44100 => "44.1 kHz",
            48000 => "48 kHz",
            int v => $"{v} Hz",
            _ => "—",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
