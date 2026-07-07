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

/// <summary>Camelot pill colour from the SHARED 12-hue wheel palette
/// (<see cref="JustPlay.UI.Theming.CamelotPalette"/>) — so a key badge is the exact hue of its segment on the
/// FILTER key wheel (Chloe 2026-07-07). <c>ConverterParameter</c> is an optional hex alpha byte: pass e.g.
/// <c>"2E"</c> for the ~18% background wash, omit it for the full-strength coloured outline. Chloe preferred
/// "outline bunt, text gleich, background nur 15–20% Deckung" over a solid fill — classier read. Used suite-wide
/// (finder + max + mini) — ONE key-colour system, no per-view drift; the old cyan/pink A/B converter is gone.</summary>
public sealed class CamelotWheelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (JustPlay.UI.Theming.CamelotPalette.ForCode(value as string) is not { } c)
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

/// <summary>True → Bold, false → the row's normal weight. Drives the "detected ≠ claimed tag"
/// conflict highlight on the BPM/Key/Energy cells — the bold value IS the affordance (no icon).</summary>
public sealed class ConflictWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Conflict → bright white text, otherwise the muted column colour (#B3FFFFFF). Pairs with
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

/// <summary>True iff the bound string equals ConverterParameter (case-insensitive). Used to drive active-class.</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
