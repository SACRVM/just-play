using System;
using Avalonia;
using Avalonia.Media;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;

namespace JustPlay.UI.Theming;

/// <summary>
/// Applies a <see cref="Theme"/> to the running Avalonia application by overwriting the
/// Color resources declared in the merged design-system dictionary. Shared by every JUST
/// suite app (lived in JustPlay.App before the JustPlay.UI extraction).
///
/// How the swap actually reaches the UI
/// -------------------------------------
/// All theme-coloured XAML references the palette through <c>{DynamicResource AccentA}</c>,
/// NOT <c>{StaticResource AccentA}</c>. Avalonia's DynamicResource is "live": when a key in
/// the resource dictionary changes, every element bound to it re-renders automatically.
/// StaticResource captures the value at XAML-load and never updates - that's the trap that
/// makes theme-switch frameworks "look like they worked the first time" but stay broken on
/// the rest of the app.
///
/// Brushes composed from multiple theme colours (e.g. <c>BgLinear</c>, <c>AccentGradient</c>)
/// are declared with DynamicResource colour stops - so updating the underlying Color keys is
/// enough; we don't rebuild the brushes themselves here.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    private Theme _current = Themes.Aurora;
    private bool _previewing;

    public Theme Current => _current;

    public event EventHandler<Theme>? ThemeChanged;

    /// <summary>The live instance (one per app process) so the shared <see cref="ThemeSwatchChip"/> can
    /// drive whole-app hover preview without per-app wiring. Set in the constructor.</summary>
    public static AvaloniaThemeService? Active { get; private set; }

    public AvaloniaThemeService() => Active = this;

    public void Apply(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        WriteResources(theme);
        _previewing = false; // a real commit ends any in-flight hover preview

        if (string.Equals(_current.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            return; // resources still (re)written above for cold-start keys; just don't re-commit/fire
        _current = theme;
        ThemeChanged?.Invoke(this, theme);
        Console.WriteLine($"[Theme] applied: {theme.Name}");
    }

    /// <summary>
    /// Temporarily restyle the WHOLE app to <paramref name="theme"/> without committing or persisting -
    /// the theme-swatch hover preview ("try before you click"). <see cref="EndPreview"/> restores the
    /// committed theme. Deliberately does NOT fire <see cref="ThemeChanged"/> (no taskbar-icon repaint
    /// churn per hover) and leaves <see cref="Current"/> unchanged, so the active ring + persistence keep
    /// tracking the real choice.
    /// </summary>
    public void ApplyPreview(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.Equals(_current.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            return; // already showing the committed theme - nothing to preview
        _previewing = true;
        WriteResources(theme);
    }

    /// <summary>Revert a hover preview (started by <see cref="ApplyPreview"/>) back to the committed theme.</summary>
    public void EndPreview()
    {
        if (!_previewing) return;
        _previewing = false;
        WriteResources(_current);
    }

    /// <summary>
    /// Write the full palette + composed-brush resources for <paramref name="theme"/> into the app
    /// dictionary. Every key is written each call (even no-ops) so cold start publishes keys that have no
    /// hard-coded XAML default (PlayHaloIdle/Hover). Shared by <see cref="Apply"/> / <see cref="ApplyPreview"/>
    /// / <see cref="EndPreview"/>.
    /// </summary>
    private static void WriteResources(Theme theme)
    {
        var app = Application.Current ?? throw new InvalidOperationException(
            "AvaloniaThemeService.WriteResources called before Application is initialised.");

        // The name the UI DISPLAYS as "current" - because WriteResources runs for hover preview too
        // (ApplyPreview), the theme-name label updates on HOVER, not just on click. Labels bind it via
        // {DynamicResource DisplayThemeName} -> zero per-app / per-VM wiring.
        app.Resources["DisplayThemeName"] = theme.Name;

        app.Resources["BgFrom"]        = Color.Parse(theme.BgFrom);
        app.Resources["BgVia"]         = Color.Parse(theme.BgVia);
        app.Resources["BgTo"]          = Color.Parse(theme.BgTo);
        app.Resources["AccentA"]       = Color.Parse(theme.AccentA);
        app.Resources["AccentB"]       = Color.Parse(theme.AccentB);
        app.Resources["AccentC"]       = Color.Parse(theme.AccentC);
        app.Resources["Glow"]          = Color.Parse(theme.Glow);
        app.Resources["BottomBarFrom"] = Color.Parse(theme.BottomBarFrom);
        app.Resources["BottomBarTo"]   = Color.Parse(theme.BottomBarTo);

        var accentA = Color.Parse(theme.AccentA);
        var accentB = Color.Parse(theme.AccentB);
        var accentC = Color.Parse(theme.AccentC);

        Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        var glowIdle   = WithAlpha(accentB, 0x99);
        var glowStrong = WithAlpha(accentB, 0xCC);

        // AccentB-derived
        app.Resources["AccentBGlow"]       = glowIdle;
        app.Resources["AccentBGlowStrong"] = glowStrong;
        app.Resources["AccentBSolid"]      = accentB;
        app.Resources["AccentBRow"]        = WithAlpha(accentB, 0x10);
        app.Resources["AccentBRowHover"]   = WithAlpha(accentB, 0x1A);
        // ON-AIR band wash (JUST STREAM live state) - same strength the old green had (0x33->0x14), accent-tinted.
        app.Resources["OnAirWashTop"]      = WithAlpha(accentB, 0x33);
        app.Resources["OnAirWashBottom"]   = WithAlpha(accentB, 0x14);

        // AccentA-derived (the cyan highlight - used by the PRE CUE FINDER's focused-pane header wash
        // and its selected-row accent bar so BOTH accents actually earn their keep there. Chloe 2026-07-06).
        app.Resources["AccentARow"]        = WithAlpha(accentA, 0x22);
        app.Resources["AccentARowStrong"]  = WithAlpha(accentA, 0x40);

        // AccentC-derived
        app.Resources["AccentCRow"]        = WithAlpha(accentC, 0x22);
        app.Resources["AccentCRowHover"]   = WithAlpha(accentC, 0x33);

        // Extra glow alphas used by multi-layer BoxShadows further down.
        var sliderGlow = WithAlpha(accentB, 0x80);
        var thumbGlow  = WithAlpha(accentB, 0xAA);
        var fillGlowB  = WithAlpha(accentB, 0x88);
        var fillGlowA  = WithAlpha(accentA, 0x55);
        var sleeveAura = WithAlpha(accentC, 0x40);
        var windowHaze = WithAlpha(accentC, 0x2A);
        app.Resources["AccentBHalfGlow"]   = sliderGlow;
        app.Resources["AccentCSoftAura"]   = sleeveAura;
        app.Resources["AccentCWindowGlow"] = windowHaze;

        static Color Black(byte a) => Color.FromArgb(a, 0x00, 0x00, 0x00);
        static Color White(byte a) => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);

        // Play-button halo (Button.primary Border#Halo).
        app.Resources["PlayHaloIdle"]  = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 30, Spread = 0, Color = glowIdle,
        });
        app.Resources["PlayHaloHover"] = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 40, Spread = 0, Color = glowStrong,
        });

        // Active-row left-edge accent bar halo.
        app.Resources["ActiveRowEdgeHalo"] = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 6, Spread = 0, Color = accentB,
        });

        // ON-AIR band halo (JUST STREAM): the SAME strong two-layer glow the old green had (outer bloom
        // alpha 0x66 blur 28 + inset alpha 0x40 blur 18) - just in the THEME ACCENT instead of a clashing green.
        // Chloe liked the punch; only the colour was the problem.
        app.Resources["OnAirHalo"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 28, Spread = -2, Color = WithAlpha(accentB, 0x66) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 18, Spread = -6, Color = WithAlpha(accentB, 0x40), IsInset = true },
            });

        // Sleeve outer card shadow - drop + outer ring + soft accent aura.
        app.Resources["SleeveOuter"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 30, Blur = 60, Spread = -8, Color = Black(0xBF) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 0,  Spread = 1,   Color = White(0x1F) },
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 50, Spread = -10, Color = sleeveAura },
            });

        // Main window card shadow - gravity drop + white edge highlight + soft accent haze.
        app.Resources["WindowOuter"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 10, Blur = 22, Spread = -6, Color = Black(0x80) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 0,  Spread = 1,   Color = White(0x14) },
                new BoxShadow { OffsetX = 0, OffsetY = 16, Blur = 40, Spread = -18, Color = windowHaze },
            });

        // DIALOG card shadow - the same three layers as WindowOuter, with more gravity. A dialog sits
        // ABOVE a window, and until now it cast exactly the same shadow, so it read as lying on the
        // same plane (Chloe 2026-08-06: "ist mehr drop shadow drin? damit sie mehr ueber dem fenster
        // schweben?" - the answer was no). Deeper drop (14 vs 10), wider blur (32 vs 22), darker
        // (0xA6 vs 0x80). The numbers are bounded by the card's transparent margin: with spread -8 a
        // 32-blur reaches 8 px past the edge, plus the 14 offset = 22 px below, and the haze layer
        // reaches 21 - both inside the 26 px bottom margin Border#DialogCard sets. Grow one and the
        // shadow clips at the window edge instead of fading out.
        app.Resources["DialogOuter"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 14, Blur = 32, Spread = -8, Color = Black(0xA6) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 0,  Spread = 1,   Color = White(0x14) },
                new BoxShadow { OffsetX = 0, OffsetY = 18, Blur = 46, Spread = -20, Color = windowHaze },
            });

        // SkeuSlider thumbs - five-layer skeu (inset highlight, inset bottom shade, drop, hairline, halo).
        BoxShadows MakeSliderShadow(double insetBlur, double dropBlur, double haloBlur) =>
            new BoxShadows(
                new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = 1,  Blur = 1,         Spread = 0, Color = White(0xB3) },
                new BoxShadow[] {
                    new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = -2, Blur = insetBlur, Spread = 0, Color = Black(0x66) },
                    new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = dropBlur,                   Spread = 0, Color = Black(0x99) },
                    new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 0,                          Spread = 1, Color = Black(0x59) },
                    new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = haloBlur,                   Spread = 0, Color = thumbGlow },
                });
        app.Resources["SkeuSliderProgressThumbShadow"] = MakeSliderShadow(3, 4, 12);
        app.Resources["SkeuSliderChromeThumbShadow"]   = MakeSliderShadow(2, 3, 10);

        // SkeuSlider filled portion - inset bevel + OUTER accent glow.
        app.Resources["SkeuSliderProgressFillGlow"] = new BoxShadows(
            new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = 1, Blur = 0, Spread = 0, Color = White(0x80) },
            new BoxShadow[] {
                new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = -1, Blur = 0,  Spread = 0, Color = Black(0x33) },
                new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 10, Spread = 0, Color = fillGlowB },
                new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 16, Spread = 0, Color = fillGlowA },
            });
        app.Resources["SkeuSliderChromeFillGlow"] = new BoxShadows(
            new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = 1, Blur = 0, Spread = 0, Color = White(0x66) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 6, Spread = 0, Color = fillGlowB },
            });
    }
}
