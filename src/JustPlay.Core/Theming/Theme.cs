namespace JustPlay.Core.Theming;

/// <summary>
/// A complete palette for a single theme. Values are stored as ARGB/RGB hex
/// strings (e.g. "#FF5FAE", "#8C8C69FF") rather than typed Color values so
/// JustPlay.Core stays free of any UI-framework dependency - the parsing
/// into <c>Avalonia.Media.Color</c> happens in the App layer's theme service.
///
/// The palette mirrors the design's <c>THEMES</c> object
/// (<c>.design/just-play-music-player/project/player.jsx</c>). When you add a
/// new theme, port every field 1:1 from there - partial palettes leave the
/// previous theme's colour visible on whichever element you missed.
/// </summary>
public sealed record Theme(
    /// <summary>Display name, also the key under which the theme is stored.</summary>
    string Name,

    /// <summary>Top-of-window background gradient stop.</summary>
    string BgFrom,
    /// <summary>Middle background gradient stop.</summary>
    string BgVia,
    /// <summary>Bottom background gradient stop.</summary>
    string BgTo,

    /// <summary>Primary accent - typically the "cool" colour (cyan / yellow / ...).</summary>
    string AccentA,
    /// <summary>Secondary accent - the "warm" pair to <see cref="AccentA"/>.</summary>
    string AccentB,
    /// <summary>Tertiary accent - usually a third hue between A and B.</summary>
    string AccentC,

    /// <summary>Soft halo colour used behind glowing elements. Includes alpha.</summary>
    string Glow,

    /// <summary>Optional bottom-bar gradient start (now-playing dock - design-defined, unused as of 2026-05-31).</summary>
    string BottomBarFrom,
    /// <summary>Optional bottom-bar gradient end.</summary>
    string BottomBarTo,

    /// <summary>
    /// Optional app-icon chip gradient start (top-left). When null the icon uses <see cref="AccentA"/>
    /// - set this (+ <see cref="IconTo"/>) only when a theme's ICON should differ from its accents,
    /// e.g. a pitch-black theme whose icon must read DARK while its in-app accents stay a bright pop.
    /// </summary>
    string? IconFrom = null,
    /// <summary>Optional app-icon chip gradient end (bottom-right). Null -> <see cref="AccentB"/>.</summary>
    string? IconTo = null);
