using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace JustPlay.App.Controls;

/// <summary>
/// Prev / play / next cluster. Defaults to MaxView sizes; set <see cref="Compact"/>=True
/// for MiniView sizes. All other visual treatment (gradient, halo, hover glow, centred
/// scale) is inherited from App.axaml's Button.ctrl / Button.primary styles - so both
/// view-modes look identical apart from absolute size.
/// </summary>
public partial class TransportButtons : UserControl
{
    // Icon size factor in Mini mode. 0.78 = same ratio as the original Mini design
    // (14/18 for ctrl, 16/20 for play). Single constant because all three icons scale
    // by the same amount.
    private const double CompactIconScale = 0.78;

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<TransportButtons, bool>(nameof(Compact));

    public TransportButtons()
    {
        InitializeComponent();
        CompactProperty.Changed.AddClassHandler<TransportButtons>((c, _) => c.ApplySize());
    }

    /// <summary>
    /// True -> MiniView sizes (54-px play, 38-px ctrl, spacing 14, icons scaled 0.78x).
    /// False -> MaxView sizes (70 / 46 / 18 / standard icons). App.axaml's class styles
    /// set the default 70/46, so for False we just CLEAR the local overrides we set when
    /// switching to compact.
    /// </summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ApplySize is also called on Compact property changes, but the very first attach
        // happens AFTER the property has its initial value - call here so the visual tree
        // is up-to-date as soon as it's rendered.
        ApplySize();
    }

    private void ApplySize()
    {
        // Named elements come from x:Name in XAML - FindControl is safe once attached.
        var prev = this.FindControl<Button>("PrevButton");
        var play = this.FindControl<Button>("PlayButton");
        var next = this.FindControl<Button>("NextButton");
        var stack = this.FindControl<StackPanel>("ButtonStack");
        var prevIcon = this.FindControl<Path>("PrevIcon");
        var playIcon = this.FindControl<Path>("PlayIcon");
        var pauseIcon = this.FindControl<Path>("PauseIcon");
        var nextIcon = this.FindControl<Path>("NextIcon");
        if (prev is null || play is null || next is null || stack is null) return;

        if (Compact)
        {
            // MiniView sizes: override Width/Height/CornerRadius locally (= Local-precedence,
            // wins over the App.axaml style values).
            prev.Width = prev.Height = 38;
            next.Width = next.Height = 38;
            prev.CornerRadius = next.CornerRadius = new CornerRadius(19);
            play.Width = play.Height = 54;
            play.CornerRadius = new CornerRadius(27);
            stack.Spacing = 14;

            // Icons keep their original Width/Height (layout box) - only the RENDER is
            // scaled, around the icon's own centre (RenderTransformOrigin=50%,50% in XAML).
            // That way the carefully-balanced centroid of the play triangle stays at the
            // box centre = button centre. Just shrinking Width/Height would re-introduce
            // the Stretch-style left shift bug.
            var scale = new ScaleTransform(CompactIconScale, CompactIconScale);
            ApplyScale(prevIcon, scale);
            ApplyScale(playIcon, scale);
            ApplyScale(pauseIcon, scale);
            ApplyScale(nextIcon, scale);
        }
        else
        {
            // Default = MaxView. Clear local overrides so the App.axaml class styles
            // (Button.ctrl / Button.primary) supply 46 / 70 / radius again.
            prev.ClearValue(WidthProperty);    prev.ClearValue(HeightProperty);    prev.ClearValue(CornerRadiusProperty);
            next.ClearValue(WidthProperty);    next.ClearValue(HeightProperty);    next.ClearValue(CornerRadiusProperty);
            play.ClearValue(WidthProperty);    play.ClearValue(HeightProperty);    play.ClearValue(CornerRadiusProperty);
            stack.Spacing = 18;

            // Remove any compact-mode scale so icons render at their natural 1.0x.
            ApplyScale(prevIcon, null);
            ApplyScale(playIcon, null);
            ApplyScale(pauseIcon, null);
            ApplyScale(nextIcon, null);
        }
    }

    /// <summary>Set RenderTransform on a Path (null = clear so we render at natural scale).</summary>
    private static void ApplyScale(Path? path, ScaleTransform? scale)
    {
        if (path is null) return;
        if (scale is null) path.ClearValue(Visual.RenderTransformProperty);
        else path.RenderTransform = scale;
    }
}
