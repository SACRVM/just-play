using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace JustPlay.App.Converters;

// Aurora-theme accent colours mirrored from THEMES.aurora in the design bundle.
// Kept here (rather than fetched from resources at runtime) so converters are pure.
internal static class ThemeColors
{
    public static readonly Color AccentA = Color.Parse("#6ce0ff"); // cyan
    public static readonly Color AccentB = Color.Parse("#ff5fae"); // pink
    public static readonly Color AccentC = Color.Parse("#8a5fff"); // purple
}

/// <summary>
/// Camelot pill background — cool gradient for A (minor), warm gradient for B (major),
/// matching the design bundle's KeyPill component.
/// </summary>
public sealed class CamelotToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return Brushes.Transparent;

        var isB = s.EndsWith("B", StringComparison.OrdinalIgnoreCase);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(isB ? WithAlpha(ThemeColors.AccentB, 0x55) : WithAlpha(ThemeColors.AccentA, 0x55), 0),
                new GradientStop(isB ? WithAlpha(ThemeColors.AccentB, 0x22) : WithAlpha(ThemeColors.AccentC, 0x22), 1),
            }
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>Border colour for the Camelot pill — accentB for B keys, accentA for A keys.</summary>
public sealed class CamelotBorderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return Brushes.Transparent;
        var isB = s.EndsWith("B", StringComparison.OrdinalIgnoreCase);
        return new SolidColorBrush(
            CamelotToBrushConverter.WithAlpha(isB ? ThemeColors.AccentB : ThemeColors.AccentA, 0x80));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Per-bar colour for the energy meter: positions 0-1 use cyan (accentA), 2 purple (accentC),
/// 3-4 pink (accentB). Pass the bar index (0..4) as ConverterParameter.
/// </summary>
public sealed class EnergyBarColorConverter : IValueConverter
{
    private static readonly SolidColorBrush A = new(ThemeColors.AccentA);
    private static readonly SolidColorBrush B = new(ThemeColors.AccentB);
    private static readonly SolidColorBrush C = new(ThemeColors.AccentC);
    private static readonly SolidColorBrush Inactive = new(Color.Parse("#1FFFFFFF"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryGetInt(value, out var energy) || !TryGetInt(parameter, out var index))
            return Inactive;

        // value 1..10 mapped to 1..5 filled bars (matches the design's EnergyBars)
        var filled = Math.Max(1, Math.Min(5, (int)Math.Round(energy / 2.0)));
        if (index >= filled) return Inactive;
        return index >= 3 ? B : index >= 2 ? C : A;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool TryGetInt(object? o, out int n)
    {
        switch (o)
        {
            case int i: n = i; return true;
            case string s when int.TryParse(s, out var p): n = p; return true;
            case null: n = 0; return false;
            default: return int.TryParse(o.ToString(), out n);
        }
    }
}

/// <summary>True (playing) → pause path; false → play triangle path. Both fit a 20×22 box.</summary>
/// <remarks>
/// The play triangle's VISUAL CENTROID is at ((x1+x2+x3)/3, (y1+y2+y3)/3), not the bounding-box
/// centre. A naive triangle "M 0,0 L 0,22 L 20,11 Z" has centroid x=6.67 vs box-centre x=10 —
/// the icon looks left-shifted at rest and that asymmetry becomes obvious on hover-scale.
/// We use "M 5,0 L 5,22 L 20,11 Z": narrower (15-wide instead of 20) but centroid lands at
/// x=(5+5+20)/3 = 10 — exactly the box centre. Same height; reads as a clean, symmetric play head.
/// </remarks>
public sealed class PlayPausePathConverter : IValueConverter
{
    private static readonly Geometry PlayGeom = Geometry.Parse("M 5,0 L 5,22 L 20,11 Z");
    private static readonly Geometry PauseGeom = Geometry.Parse(
        "M 0,0 L 7,0 L 7,22 L 0,22 Z M 13,0 L 20,0 L 20,22 L 13,22 Z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? PauseGeom : PlayGeom;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True iff the bound string equals ConverterParameter (case-insensitive). Used to drive active-class.</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
