using Avalonia;
using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// Position / slider / duration seek bar, shared by both views. Defaults to
/// MaxView sizing; set <see cref="Compact"/>=True for MiniView's tighter text,
/// min-widths and slider inset. Same pattern as <see cref="TransportButtons"/>.
/// </summary>
public partial class TransportProgressBar : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<TransportProgressBar, bool>(nameof(Compact));

    public TransportProgressBar()
    {
        InitializeComponent();
        CompactProperty.Changed.AddClassHandler<TransportProgressBar>((c, _) => c.ApplySize());
    }

    /// <summary>True → MiniView sizing (font 10, time min-width 32, slider inset 8).
    /// False → MaxView sizing (font 12, min-width 42, slider inset 12).</summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplySize();
    }

    private void ApplySize()
    {
        var pos = this.FindControl<TextBlock>("PosText");
        var dur = this.FindControl<TextBlock>("DurText");
        var slider = this.FindControl<SkeuSlider>("Slider");
        if (pos is null || dur is null || slider is null) return;

        var font = Compact ? 10d : 12d;
        var minW = Compact ? 32d : 42d;
        var inset = Compact ? 8d : 12d;

        pos.FontSize = dur.FontSize = font;
        pos.MinWidth = dur.MinWidth = minW;
        slider.Margin = new Thickness(inset, 0);
    }
}
