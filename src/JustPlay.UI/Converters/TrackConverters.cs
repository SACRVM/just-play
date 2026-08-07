using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using JustPlay.UI.Theming;

namespace JustPlay.UI.Converters;

/// <summary>Camelot pill colour from the SHARED 12-hue wheel palette (<see cref="CamelotPalette"/>) - so a key
/// badge is the exact hue of its segment on the FILTER key wheel (Chloe 2026-07-07). <c>ConverterParameter</c> is
/// an optional hex alpha byte: pass e.g. <c>"2E"</c> for the ~18% background wash, omit it for the full-strength
/// coloured outline. Chloe preferred "outline bunt, text gleich, background nur 15-20% Deckung" over a solid fill -
/// classier read. Used suite-wide (finder + max + mini + JUST TAG) - ONE key-colour system, no per-view drift.</summary>
public sealed class CamelotWheelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (CamelotPalette.ForCode(value as string) is not { } c)
            return Brushes.Transparent;
        return new SolidColorBrush(
            ParseAlpha(parameter) is { } a ? Color.FromArgb(a, c.R, c.G, c.B) : c);
    }

    private static byte? ParseAlpha(object? parameter) =>
        parameter?.ToString() is { Length: > 0 } s &&
        byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True -> Bold, false -> the row's normal weight. Drives the "detected != claimed tag"
/// conflict highlight on the BPM/Key/Energy cells - the bold value IS the affordance (no icon).</summary>
public sealed class ConflictWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Conflict -> bright white text, otherwise the muted column colour (#B3FFFFFF). Pairs with
/// <see cref="ConflictWeightConverter"/> so a divergent BPM reads as bold + bright.</summary>
public sealed class ConflictForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Bright = new(Colors.White);
    private static readonly SolidColorBrush Muted = new(Color.Parse("#B3FFFFFF"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Bright : Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
