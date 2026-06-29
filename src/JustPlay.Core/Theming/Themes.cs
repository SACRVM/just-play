namespace JustPlay.Core.Theming;

/// <summary>
/// The built-in palettes — the first four are a direct port of the design's <c>THEMES</c>
/// object (<c>.design/just-play-music-player/project/player.jsx</c>:7-56); <c>Hardcore</c> is a
/// JustPlay-original (black/red + cyan) added for the hardcore/schranz crowd.
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

    /// <summary>
    /// "Midnight" — a TRUE dark / night mode (Chloe 2026-06-29): black → dark grey-blue background,
    /// calm steel/ice-blue accents (no neon, low strain — easy on the eyes, esp. for JUST STREAM's
    /// long sessions). This REPLACES the old JSX-port Midnight (a saturated navy with bright
    /// cyan/violet accents) — that one didn't read as "night mode". Background is desaturated
    /// (grey-blue, not navy); AccentB stays present enough to drive the play-halo glow without glaring.
    /// </summary>
    public static readonly Theme Midnight = new(
        Name:            "Midnight",
        BgFrom:          "#1a2233",       // dark slate grey-blue (top)
        BgVia:           "#10151f",       // darker mid
        BgTo:            "#05070b",       // near-black (bottom)
        AccentA:         "#84b6da",       // soft ice-blue — the cool pop (EQ fill, like, cyan side)
        AccentB:         "#5b8ad8",       // steel blue — dominant glow/halo, calm not neon
        AccentC:         "#33507a",       // deep desaturated blue — bg blooms + row wash (blue-lit, not purple)
        Glow:            "#805b8ad8",     // rgba(91, 138, 216, 0.50) — soft blue halo
        BottomBarFrom:   "#3a5896",
        BottomBarTo:     "#6f9ed0");

    /// <summary>
    /// "Onyx" — pitch black (Chloe 2026-06-29): a darker sibling of Midnight. Background goes to PURE
    /// black at the bottom (OLED-true), with only the faintest cool breath at the top. A single crisp
    /// ice/azure accent that pops cleanly against the black without any warmth or neon. The blackest
    /// theme in the set; pairs naturally with JUST STREAM's night console.
    /// </summary>
    public static readonly Theme Onyx = new(
        Name:            "Onyx",
        BgFrom:          "#0a0b0e",       // near-black with a faint cool tint (top)
        BgVia:           "#050507",
        BgTo:            "#000000",       // pure black (bottom)
        AccentA:         "#b8d4ec",       // cool ice — the pop against the black
        AccentB:         "#6f9fd0",       // azure — halo/glow, crisp on pure black
        AccentC:         "#1a2230",       // deep charcoal-blue — minimal background bloom
        Glow:            "#736f9fd0",     // rgba(111, 159, 208, 0.45) — soft azure halo
        BottomBarFrom:   "#2c4870",
        BottomBarTo:     "#5f93c8",
        // Icon must read PITCH-BLACK like the theme — NOT the bright in-app accents. Dark slate → near
        // black, with the white glyph on top, so the taskbar/About icon matches Onyx's character.
        IconFrom:        "#28384f",
        IconTo:          "#05070c");

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

    /// <summary>
    /// "Hardcore" — black/red with electric-cyan accents. NOT a design-file port (the JSX THEMES has
    /// only the four above); this is a JustPlay-original aggressive palette for the hardcore/schranz
    /// crowd. Role mapping is deliberate: AccentB is the heavily-glowing one (play halo, active-row
    /// edge, slider/fill glow, toggles) → RED so the chrome reads aggressive; AccentA is the "cool"
    /// primary (like-heart, EQ fill, gradient-cyan side) → CYAN as the pop accent; AccentC (radial
    /// background blooms + selected-row wash + sleeve aura) → deep crimson so the near-black bg gets a
    /// red glow rather than purple. Background is near-pure-black with a faint blood tint.
    /// </summary>
    public static readonly Theme Hardcore = new(
        Name:            "Hardcore",
        BgFrom:          "#1e0709",
        BgVia:           "#120406",
        BgTo:            "#060203",
        AccentA:         "#22e6ff",     // electric cyan — the accent pop
        AccentB:         "#ff2233",     // hot red — dominant glow/halo colour
        AccentC:         "#c8112a",     // deep crimson — background blooms + row washes
        Glow:            "#80ff2233",   // rgba(255, 34, 51, 0.5) — red bloom behind glowing elements
        BottomBarFrom:   "#e01024",
        BottomBarTo:     "#ff4d3a");

    public static readonly IReadOnlyList<Theme> All =
        new[] { Aurora, Sunset, Midnight, Onyx, Neon, Hardcore };

    /// <summary>
    /// Look up a theme by its <see cref="Theme.Name"/>. Falls back to Aurora
    /// when the name isn't recognised — important on first-run (settings file
    /// missing) and after a theme is removed (stale settings referring to it).
    /// </summary>
    public static Theme ByNameOrDefault(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Aurora;
}
