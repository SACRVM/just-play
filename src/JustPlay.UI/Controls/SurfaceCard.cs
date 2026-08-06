using Avalonia.Controls;

namespace JustPlay.UI.Controls;

/// <summary>
/// The suite's SURFACE — the one treatment every dialog and every slide-in panel uses so it reads
/// as its own object sitting on the app, instead of as a second window wearing the same skin.
///
/// It is a FLAT fill plus a hairline, and that is the whole idea: the app's windows are gradient,
/// a surface is flat, and that contrast IS the distinction. See JustPalette.axaml (SurfaceFill) for
/// the measured reason the gradient version was abandoned — on a tall, narrow panel a low-contrast
/// ramp bands visibly at 8 bit and Avalonia does not dither gradient fills.
///
/// Why a control and not a style
/// -----------------------------
/// So there is exactly one place to change it. Left as a recipe, every site re-types the fill and
/// they drift — which is how the JUST PLAY tweaks panel and the dialogs ended up on two completely
/// different treatments in the first place.
///
/// What a consumer sets: <see cref="ContentControl.Content"/>, <c>CornerRadius</c>,
/// <c>BorderThickness</c> (a docked panel wants an edge on one side only), <c>Padding</c>.
/// It does NOT take a Background — the surface IS the background, and letting a site override it
/// would re-open the drift.
/// </summary>
public class SurfaceCard : ContentControl
{
}
