using System;
using Avalonia;
using Avalonia.Media;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;

namespace JustPlay.App.Theming;

/// <summary>
/// Applies a <see cref="Theme"/> to the running Avalonia application by
/// overwriting the Color resources declared in <c>App.axaml</c>.
///
/// How the swap actually reaches the UI
/// -------------------------------------
/// All theme-coloured XAML in this codebase references the palette through
/// <c>{DynamicResource AccentA}</c>, NOT <c>{StaticResource AccentA}</c>.
/// Avalonia's DynamicResource is "live": when a key in the resource
/// dictionary changes, every element bound to it re-renders automatically.
/// StaticResource captures the value at XAML-load and never updates — that's
/// the trap that makes theme-switch frameworks "look like they worked the
/// first time you tried" but stay broken on the rest of the app.
///
/// Brushes that are composed from multiple theme colours
/// (e.g. <c>BgLinear</c>, <c>AccentGradient</c>) are declared in
/// <c>App.axaml</c> with DynamicResource colour stops — so updating the
/// underlying Color keys is enough; we don't need to also rebuild the
/// brushes themselves here.
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

        // Resources are written EVERY call, even on the no-op same-theme path.
        // Reason: cold-start with the default theme would otherwise skip
        // resource publication entirely and the keys that don't have
        // hard-coded XAML defaults (PlayHaloIdle/PlayHaloHover) would never
        // exist — the play button would render without a halo until the user
        // toggled themes and came back. Cheaper to always publish than to
        // sentinel-track first-run.

        // The seven theme-driven Color resources declared in App.axaml. Keys
        // here must match the x:Key values in App.axaml.Application.Resources
        // exactly — Avalonia is silent when a Set lands on a key nothing
        // reads from, so a typo here just looks like "the theme doesn't
        // change" with no warning.
        app.Resources["BgFrom"]        = Color.Parse(theme.BgFrom);
        app.Resources["BgVia"]         = Color.Parse(theme.BgVia);
        app.Resources["BgTo"]          = Color.Parse(theme.BgTo);
        app.Resources["AccentA"]       = Color.Parse(theme.AccentA);
        app.Resources["AccentB"]       = Color.Parse(theme.AccentB);
        app.Resources["AccentC"]       = Color.Parse(theme.AccentC);
        app.Resources["Glow"]          = Color.Parse(theme.Glow);
        // Bottom-bar gradient colours — declared in App.axaml so future UI
        // that wants them can DynamicResource them, even though nothing in
        // the current tree consumes them yet.
        app.Resources["BottomBarFrom"] = Color.Parse(theme.BottomBarFrom);
        app.Resources["BottomBarTo"]   = Color.Parse(theme.BottomBarTo);

        // Pre-alphaised AccentB / AccentC variants. CSS gradients in the design
        // express these as `${accent}22` / `${accent}10` — same colour, different
        // alpha byte. Avalonia's BoxShadow string and LinearGradientBrush stops
        // can't compose alpha onto a DynamicResource Color, so we publish each
        // alpha variant as its own key.
        var accentB = Color.Parse(theme.AccentB);
        var accentC = Color.Parse(theme.AccentC);

        Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        var glowIdle   = WithAlpha(accentB, 0x99);
        var glowStrong = WithAlpha(accentB, 0xCC);

        // AccentB-derived
        app.Resources["AccentBGlow"]       = glowIdle;
        app.Resources["AccentBGlowStrong"] = glowStrong;
        app.Resources["AccentBSolid"]      = accentB;
        app.Resources["AccentBRow"]        = WithAlpha(accentB, 0x10);   // .063 — design's selected-row pink stop
        app.Resources["AccentBRowHover"]   = WithAlpha(accentB, 0x1A);   // .10  — selected+hover pink stop

        // AccentC-derived
        app.Resources["AccentCRow"]        = WithAlpha(accentC, 0x22);   // .133 — design's selected-row purple stop
        app.Resources["AccentCRowHover"]   = WithAlpha(accentC, 0x33);   // .20  — selected+hover purple stop

        // Extra glow alphas used by multi-layer BoxShadows further down.
        var sliderGlow = WithAlpha(accentB, 0x80);   // SkeuSlider thumb halo
        var sleeveAura = WithAlpha(accentC, 0x40);   // Vinyl sleeve outer aura
        var windowHaze = WithAlpha(accentC, 0x2A);   // Main window outer glow
        app.Resources["AccentBHalfGlow"]   = sliderGlow;
        app.Resources["AccentCSoftAura"]   = sleeveAura;
        app.Resources["AccentCWindowGlow"] = windowHaze;

        // BoxShadows objects emitted whole rather than declared in XAML — Avalonia 12's
        // XAML compiler trips on `<BoxShadow Color="{DynamicResource …}"/>` (AVLN2000:
        // ResolveContentPropertyTransformer Index out of range). The BoxShadowsTransition
        // between idle/hover variants still animates because it operates on the value
        // as a whole, not on the colour inside it.

        static Color Black(byte a) => Color.FromArgb(a, 0x00, 0x00, 0x00);
        static Color White(byte a) => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);

        // Play-button halo (Button.primary Border#Halo). Direct port of the
        // design's pink halo, now warm-accent-driven so every theme glows.
        app.Resources["PlayHaloIdle"]  = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 30, Spread = 0, Color = glowIdle,
        });
        app.Resources["PlayHaloHover"] = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 40, Spread = 0, Color = glowStrong,
        });

        // Active-row left-edge accent bar halo. Direct port of the design's
        // `boxShadow: 0 0 6px ${theme.accentB}` (app.jsx:170).
        app.Resources["ActiveRowEdgeHalo"] = new BoxShadows(new BoxShadow {
            OffsetX = 0, OffsetY = 0, Blur = 6, Spread = 0, Color = accentB,
        });

        // Sleeve outer card shadow — drop + outer ring + soft accent aura.
        // First two layers from the design (deep black drop, 1-px white edge);
        // the third is our Aurora-style extension, now theme-driven so
        // Sunset/Midnight/Neon get their own room-light tint.
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

        // SkeuSlider thumbs — same five-layer skeu (inset highlight, inset bottom shade,
        // drop, hairline outline, accent halo). Progress (18-px) uses blur 3/4/10;
        // chrome (14-px) uses blur 2/3/8. Only the accent layer tracks the theme.
        BoxShadows MakeSliderShadow(double insetBlur, double dropBlur, double haloBlur) =>
            new BoxShadows(
                new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = 1,  Blur = 1,         Spread = 0, Color = White(0xB3) },
                new BoxShadow[] {
                    new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = -2, Blur = insetBlur, Spread = 0, Color = Black(0x66) },
                    new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = dropBlur,                   Spread = 0, Color = Black(0x99) },
                    new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = 0,                          Spread = 1, Color = Black(0x59) },
                    new BoxShadow { OffsetX = 0, OffsetY = 0, Blur = haloBlur,                   Spread = 0, Color = sliderGlow },
                });
        app.Resources["SkeuSliderProgressThumbShadow"] = MakeSliderShadow(3, 4, 10);
        app.Resources["SkeuSliderChromeThumbShadow"]   = MakeSliderShadow(2, 3, 8);

        // Only fire ThemeChanged and update _current on a REAL theme switch —
        // initial cold-start writes still call this method to publish the
        // colour keys, but they shouldn't be reported as a transition.
        if (sameName) return;
        _current = theme;
        ThemeChanged?.Invoke(this, theme);
        Console.WriteLine($"[Theme] applied: {theme.Name}");
    }
}
