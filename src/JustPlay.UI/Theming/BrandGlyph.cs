namespace JustPlay.UI.Theming;

/// <summary>
/// A JUST app's one simple brand glyph, drawn white on the theme-gradient chip by
/// <see cref="ThemedWindowIcon"/>. Each app supplies its own (JUST PLAY = play triangle,
/// JUST STREAM = radio tower / Funkturm) — see CLAUDE.md "JUST suite UI philosophy".
/// </summary>
/// <param name="PathData">SVG/Avalonia path geometry for the glyph.</param>
/// <param name="Stroked">true = line-art glyph (stroked, no fill, e.g. the radio tower);
/// false = solid filled glyph (e.g. the play triangle).</param>
/// <param name="Thickness">Stroke thickness as a fraction of the icon size (stroked glyphs only).</param>
/// <param name="BoxRatio">Glyph box as a fraction of the icon size (centred).</param>
/// <param name="TranslateX">Optical-centring nudge as a fraction of the icon size (e.g. the play
/// triangle's visual mass sits left of its bounding-box centre).</param>
public sealed record BrandGlyph(
    string PathData,
    bool Stroked,
    double Thickness = 0.04,
    double BoxRatio = 0.4,
    double TranslateX = 0);

/// <summary>The brand glyphs for the JUST suite apps.</summary>
public static class BrandGlyphs
{
    /// <summary>JUST PLAY — the play triangle (solid fill).</summary>
    public static readonly BrandGlyph Play =
        new("M 0,0 L 0,10 L 9,5 Z", Stroked: false, BoxRatio: 0.40);

    /// <summary>JUST STREAM — the radio tower / Funkturm (Lucide "radio-tower", stroked line-art).</summary>
    public static readonly BrandGlyph RadioTower = new(
        "M4.9 16.1 C1 12.2 1 5.8 4.9 1.9 " +
        "M7.8 4.7 a6.14 6.14 0 0 0 -.8 7.5 " +
        "M10 9 A2 2 0 1 0 14 9 A2 2 0 1 0 10 9 " +
        "M16.2 4.8 c2 2 2.26 5.11 .8 7.47 " +
        "M19.1 1.9 a9.96 9.96 0 0 1 0 14.1 " +
        "M9.5 18 h5 " +
        "M8 22 l4 -11 l4 11",
        Stroked: true, Thickness: 0.04, BoxRatio: 0.52);

    /// <summary>JUST TAG — the price-tag / label mark (Lucide "tag", stroked line-art + the punch-hole).</summary>
    public static readonly BrandGlyph Tag = new(
        "M12.586 2.586 A2 2 0 0 0 11.172 2 H4 a2 2 0 0 0 -2 2 v7.172 " +
        "a2 2 0 0 0 .586 1.414 l8.704 8.704 a2.426 2.426 0 0 0 3.42 0 " +
        "l6.58 -6.58 a2.426 2.426 0 0 0 0 -3.42 Z " +
        "M6.3 7.5 A1.2 1.2 0 1 0 8.7 7.5 A1.2 1.2 0 1 0 6.3 7.5",
        Stroked: true, Thickness: 0.04, BoxRatio: 0.5);
}
