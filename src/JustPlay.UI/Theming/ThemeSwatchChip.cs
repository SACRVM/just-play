using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using JustPlay.Core.Theming;

namespace JustPlay.UI.Theming;

/// <summary>
/// Theme-picker swatch chip (Chloe 2026-06-30). A square split DIAGONALLY from bottom-left -> top-right:
/// the UPPER-left triangle shows the theme's HIGHLIGHT/accent gradient (<see cref="ThemeBrushes.IconGradient"/>),
/// the LOWER-right shows the theme's BACKGROUND gradient (<see cref="ThemeBrushes.BackgroundGradient"/>) -
/// so dark themes (Onyx/Midnight) read dark and the chip honestly previews the theme, not just its bright
/// accents. A thin <c>AccentA</c> ring is always shown; on hover (and persistently while
/// <see cref="IsActive"/>) the ring switches to <c>AccentB</c> with a glow, and hover adds a slight zoom.
///
/// All colours come PER-THEME from the <see cref="Theme"/> record (all six chips are on screen at once),
/// so the chip never tracks the globally-active palette. ONE reusable control = JUST PLAY + JUST STREAM
/// (+ JUST TAG) render identically and can't drift. Wrap it in a <c>Button Classes="swatchbtn"</c> for the
/// click/command; this control owns the look (split, ring, hover, active, glow).
///
/// RENDERING (avalonia skill, verified): the glow is a <see cref="BoxShadow"/> on the NON-clipped outer
/// <c>PART_Glow</c> layer - NOT a <see cref="DropShadowEffect"/> on the control. An Effect rasterises the
/// whole chip to an offscreen bitmap, which aliased the rounded/diagonal edges ("pixelig"), dropped the
/// glow, and blacked out the background field. BoxShadow draws crisp and vector-clean; the inner layer
/// clips the split. The wrapping <c>Button.swatchbtn</c> (and its ContentPresenter) are ClipToBounds=False
/// so the bloom escapes.
/// </summary>
public class ThemeSwatchChip : TemplatedControl
{
    // Named ThemeName, NOT Theme - StyledElement already has a Theme property (ControlTheme); a string
    // Theme here would hide it (CS0108) and tangle with the control-theme system.
    public static readonly StyledProperty<string?> ThemeNameProperty =
        AvaloniaProperty.Register<ThemeSwatchChip, string?>(nameof(ThemeName));

    /// <summary>True when this chip's theme is the active one - keeps the AccentB ring + glow on at rest.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ThemeSwatchChip, bool>(nameof(IsActive));

    /// <summary>The theme name to preview (e.g. "Aurora", "Onyx"). Falls back to Aurora if unknown.</summary>
    public string? ThemeName
    {
        get => GetValue(ThemeNameProperty);
        set => SetValue(ThemeNameProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private Border? _glow;   // sibling behind, not clipped - carries the glow BoxShadow
    private Border? _card;   // the per-theme ring (outline on top)
    private Border? _bg;     // the chip face - Background = theme background gradient
    private Shape? _accent;  // upper-left triangle - accent gradient
    private bool _hover;
    private Color _accentA;
    private Color _accentB;

    static ThemeSwatchChip()
    {
        ThemeNameProperty.Changed.AddClassHandler<ThemeSwatchChip>((c, _) => c.Rebuild());
        IsActiveProperty.Changed.AddClassHandler<ThemeSwatchChip>((c, _) => c.UpdateState());
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _glow = e.NameScope.Find<Border>("PART_Glow");
        _card = e.NameScope.Find<Border>("PART_Card");
        _bg = e.NameScope.Find<Border>("PART_Bg");
        _accent = e.NameScope.Find<Shape>("PART_Accent");
        Rebuild();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        UpdateState();
        // "Try before you click": hovering a swatch previews its theme across the WHOLE app live; the
        // committed theme is restored on exit (or made permanent on click). Shared service = all 3 apps.
        AvaloniaThemeService.Active?.ApplyPreview(Themes.ByNameOrDefault(ThemeName));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        UpdateState();
        AvaloniaThemeService.Active?.EndPreview();
    }

    // Safety net: if a chip is removed while still hovered (e.g. the settings window closes mid-preview),
    // PointerExited may not fire - revert here so the app never sticks on an un-committed preview theme.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_hover)
        {
            _hover = false;
            AvaloniaThemeService.Active?.EndPreview();
        }
    }

    private void Rebuild()
    {
        var t = Themes.ByNameOrDefault(ThemeName);
        _accentA = Color.Parse(t.AccentA);
        _accentB = Color.Parse(t.AccentB);
        if (_bg is not null) _bg.Background = ThemeBrushes.BackgroundGradient(t);  // the face / lower-right field
        if (_accent is not null) _accent.Fill = ThemeBrushes.IconGradient(t);     // upper-left triangle
        UpdateState();
    }

    private void UpdateState()
    {
        var lit = _hover || IsActive;

        if (_card is not null)
            _card.BorderBrush = new SolidColorBrush(lit ? _accentB : _accentA);

        // Glow only when lit - a crisp BoxShadow on the non-clipped outer layer (NOT an offscreen Effect).
        if (_glow is not null)
            _glow.BoxShadow = lit
                // Reach kept modest (~11px) so it fits the breathing room the host views leave around the
                // swatch row (and the JUST PLAY tweaks ScrollViewer viewport) and isn't clipped.
                ? new BoxShadows(new BoxShadow
                {
                    OffsetX = 0, OffsetY = 0, Blur = 10, Spread = 1,
                    Color = new Color(0xCC, _accentB.R, _accentB.G, _accentB.B),
                })
                : default;

        // Slight zoom on hover only (active stays put but glows). TransformOperations so the style's
        // transition animates it; "scale(1)"/"scale(1.06)" pivot at centre (RenderTransformOrigin 50%,50%).
        RenderTransform = TransformOperations.Parse(_hover ? "scale(1.06)" : "scale(1)");
    }
}
