namespace JustPlay.Core.Theming;

/// <summary>
/// The four built-in palettes — direct port of the design's <c>THEMES</c>
/// object (<c>.design/just-play-music-player/project/player.jsx</c>:7-56).
///
/// CSS <c>rgba(r,g,b,a)</c> values are converted to ARGB-hex by stuffing the
/// alpha byte into the front: <c>round(a × 255)</c> → leading byte. Glow on
/// each theme is the design's <c>rgba(...,0.45..0.55)</c> — that 0.55 = 0x8C
/// is what was missing from the original Aurora resource in App.axaml.
/// </summary>
public static class Themes
{
    public static readonly Theme Aurora = new(
        Name:            "Aurora",
        BgFrom:          "#3a1d6b",
        BgVia:           "#251347",
        BgTo:            "#120a26",
        AccentA:         "#6ce0ff",
        AccentB:         "#ff5fae",
        AccentC:         "#8a5fff",
        Glow:            "#8C8C69FF",   // rgba(140, 105, 255, 0.55)
        BottomBarFrom:   "#8a4fe0",
        BottomBarTo:     "#5fa0ff");

    public static readonly Theme Sunset = new(
        Name:            "Sunset",
        BgFrom:          "#5b1a52",
        BgVia:           "#3a1235",
        BgTo:             "#180a1c",
        AccentA:         "#ffd166",
        AccentB:         "#ff6f91",
        AccentC:         "#ff9966",
        Glow:            "#8CFF7878",   // rgba(255, 120, 120, 0.55)
        BottomBarFrom:   "#ff7a59",
        BottomBarTo:     "#d94a8a");

    public static readonly Theme Midnight = new(
        Name:            "Midnight",
        BgFrom:          "#16224a",
        BgVia:           "#0b1230",
        BgTo:            "#050817",
        AccentA:         "#7ad7ff",
        AccentB:         "#7c5cff",
        AccentC:         "#2a7bff",
        Glow:            "#803C78FF",   // rgba(60, 120, 255, 0.5)
        BottomBarFrom:   "#3957d9",
        BottomBarTo:     "#6dc3ff");

    public static readonly Theme Neon = new(
        Name:            "Neon",
        BgFrom:          "#0e2a26",
        BgVia:           "#08161e",
        BgTo:            "#040a10",
        AccentA:         "#5cffd4",
        AccentB:         "#a8ff5c",
        AccentC:         "#5cd0ff",
        Glow:            "#735CFFC8",   // rgba(92, 255, 200, 0.45)
        BottomBarFrom:   "#2bd4a8",
        BottomBarTo:     "#83e85a");

    public static readonly IReadOnlyList<Theme> All =
        new[] { Aurora, Sunset, Midnight, Neon };

    /// <summary>
    /// Look up a theme by its <see cref="Theme.Name"/>. Falls back to Aurora
    /// when the name isn't recognised — important on first-run (settings file
    /// missing) and after a theme is removed (stale settings referring to it).
    /// </summary>
    public static Theme ByNameOrDefault(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Aurora;
}
