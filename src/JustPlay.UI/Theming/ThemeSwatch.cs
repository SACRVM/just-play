using System;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using JustPlay.Core.Theming;

namespace JustPlay.UI.Theming;

/// <summary>
/// The brand-chip gradient for a theme - the SINGLE source of truth shared by the app icon
/// (<see cref="ThemedWindowIcon"/>) and the theme-picker swatch chips, so the two can never
/// diverge again. (Previously the swatches were hand-painted brushes in JustPalette.axaml that
/// drifted from the icon: different stop counts, and for some themes entirely different colours -
/// only Onyx happened to match. The drift between the icon and the swatch colours was confusing
/// and needed consolidating into one source of truth.)
/// </summary>
public static class ThemeBrushes
{
    /// <summary>
    /// The icon/swatch chip gradient for <paramref name="theme"/> - a colourful 2-stop diagonal of the
    /// theme's two HIGHLIGHT accents (<c>AccentB->AccentA</c>), matching the CD-cover placeholder
    /// (Vinyl.axaml). Deliberately 2 colours and NOT the dark <c>AccentC</c> in the middle: AccentC is a
    /// background-bloom tone (dark on Midnight/Onyx), so a 3-stop B->C->A gradient banded dark across the
    /// diagonal there. This is the ONE definition - the OS app icon
    /// (<see cref="ThemedWindowIcon"/>) and the theme-picker swatch markup-extension both call it, so
    /// they can never diverge. (Note: a theme's IconFrom/IconTo override is intentionally NOT consulted
    /// here - icons stay colourful for every theme, including the dark ones.)
    /// </summary>
    public static LinearGradientBrush IconGradient(Theme theme)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentB), 0));
        brush.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentA), 1));
        return brush;
    }

    /// <summary>
    /// The theme's BACKGROUND gradient (<c>BgFrom->BgVia->BgTo</c>, same diagonal as the app window's
    /// BgLinear). Used by <see cref="ThemeSwatchChip"/>'s lower-right triangle so a swatch honestly
    /// previews the theme's surface (dark for Onyx/Midnight), not just its bright accents.
    /// </summary>
    public static LinearGradientBrush BackgroundGradient(Theme theme)
    {
        // BgTo (the DARKEST stop) at the top-left, fading to BgFrom (the theme's lightest / most
        // recognisable background tone) at the bottom-right. This matters because the chip only SHOWS
        // this brush in its lower-right triangle - a plain BgFrom->BgTo diagonal would land the darkest
        // stop exactly in that corner, so every theme's field read as a near-black blob that did not
        // honestly represent the theme's actual background colour. Flipped, the corner shows the
        // theme's actual bg colour.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.Parse(theme.BgTo), 0));
        brush.GradientStops.Add(new GradientStop(Color.Parse(theme.BgVia), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.Parse(theme.BgFrom), 1));
        return brush;
    }
}

/// <summary>
/// XAML markup extension: <c>{theming:ThemeSwatch Aurora}</c> resolves to that theme's brand-chip
/// gradient via <see cref="ThemeBrushes.IconGradient"/>. The theme-picker swatches use this instead
/// of duplicating colours in a resource dictionary, so every swatch is literally its app icon's
/// gradient.
/// </summary>
public sealed class ThemeSwatchExtension : MarkupExtension
{
    public ThemeSwatchExtension() { }

    public ThemeSwatchExtension(string theme) => Theme = theme;

    /// <summary>The theme name (e.g. "Aurora", "Onyx"). Falls back to Aurora if unknown.</summary>
    public string Theme { get; set; } = "Aurora";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => ThemeBrushes.IconGradient(Themes.ByNameOrDefault(Theme));
}

/// <summary>
/// XAML markup extension: <c>{theming:ThemeAccent Aurora}</c> resolves to that theme's HIGHLIGHT
/// colour (its <c>AccentB</c> - the signature glow/halo accent) as a solid brush. Used for the
/// theme-picker swatch ring: each swatch carries ITS OWN theme's highlight (not the globally-active
/// one), shown when the swatch is the active theme or hovered. Per-theme because all swatches are on
/// screen at once, so the live <c>AccentBBrush</c> DynamicResource (= only the active theme) won't do.
/// </summary>
public sealed class ThemeAccentExtension : MarkupExtension
{
    public ThemeAccentExtension() { }

    public ThemeAccentExtension(string theme) => Theme = theme;

    public string Theme { get; set; } = "Aurora";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new SolidColorBrush(Color.Parse(Themes.ByNameOrDefault(Theme).AccentB));
}
