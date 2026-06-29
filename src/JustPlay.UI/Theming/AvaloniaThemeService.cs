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
/// StaticResource captures the value at XAML-load and never updates — that's the trap that
/// makes theme-switch frameworks "look like they worked the first time" but stay broken on
/// the rest of the app.
///
/// Brushes composed from multiple theme colours (e.g. <c>BgLinear</c>, <c>AccentGradient</c>)
/// are declared with DynamicResource colour stops — so updating the underlying Color keys is
/// enough; we don't rebuild the brushes themselves here.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    private Theme _current = Themes.Aurora;

    public Theme Current => _current;

    public event EventHandler<Theme>? ThemeChanged;

    public void Apply(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var app = Application.Current ?? throw new InvalidOperationException(
            "AvaloniaThemeService.Apply called before Application is initialised.");

        var sameName = string.Equals(_current.Name, theme.Name, StringComparison.OrdinalIgnoreCase);

        // Resources are written EVERY call, even on the no-op same-theme path, so cold-start
        // publishes keys that don't have hard-coded XAML defaults (PlayHaloIdle/Hover).

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
        // ON-AIR band wash (JUST STREAM live state) — same strength the old green had (0x33→0x14), accent-tinted.
        app.Resources["OnAirWashTop"]      = WithAlpha(accentB, 0x33);
        app.Resources["OnAirWashBottom"]   = WithAlpha(accentB, 0x14);

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
        // α 0x66 blur 28 + inset α 0x40 blur 18) — just in the THEME ACCENT instead of a clashing green.
        // Chloe liked the punch; only the colour was the problem.
        app.Resources["OnAirHalo"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 28, Spread = -2, Color = WithAlpha(accentB, 0x66) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 18, Spread = -6, Color = WithAlpha(accentB, 0x40), IsInset = true },
            });

        // Sleeve outer card shadow — drop + outer ring + soft accent aura.
        app.Resources["SleeveOuter"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 30, Blur = 60, Spread = -8, Color = Black(0xBF) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 0,  Spread = 1,   Color = White(0x1F) },
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 50, Spread = -10, Color = sleeveAura },
            });

        // Main window card shadow — gravity drop + white edge highlight + soft accent haze.
        app.Resources["WindowOuter"] = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 10, Blur = 22, Spread = -6, Color = Black(0x80) },
            new BoxShadow[] {
                new BoxShadow { OffsetX = 0, OffsetY = 0,  Blur = 0,  Spread = 1,   Color = White(0x14) },
                new BoxShadow { OffsetX = 0, OffsetY = 16, Blur = 40, Spread = -18, Color = windowHaze },
            });

        // SkeuSlider thumbs — five-layer skeu (inset highlight, inset bottom shade, drop, hairline, halo).
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

        // SkeuSlider filled portion — inset bevel + OUTER accent glow.
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

        if (sameName) return;
        _current = theme;
        ThemeChanged?.Invoke(this, theme);
        Console.WriteLine($"[Theme] applied: {theme.Name}");
    }
}
