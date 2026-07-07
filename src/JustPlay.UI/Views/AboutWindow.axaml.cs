using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using JustPlay.UI.Theming;

namespace JustPlay.UI.Views;

/// <summary>
/// The shared, themed "About" dialog for every JUST app. Pass an <see cref="AboutInfo"/>
/// (name, tagline, version, glyph); the dialog renders in the active palette and follows
/// live theme switches. Frameless rounded card matching the suite chrome.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>Parameterless ctor for the XAML designer / hot-reload only.</summary>
    public AboutWindow()
    {
        InitializeComponent();
    }

    public AboutWindow(AboutInfo info) : this()
    {
        DataContext = info;
        GlyphHost.Content = BuildGlyph(info.Glyph);
    }

    /// <summary>
    /// Build the white brand glyph for the About card. No chip background anymore (Chloe 2026-06-30) —
    /// just the graphic, enlarged to fill the area the gradient chip used to occupy. Same render
    /// approach as <see cref="ThemedWindowIcon"/> (Stretch.Uniform, stroke thickness as a fraction of
    /// the box) so the glyph shape itself stays identical to the taskbar mark.
    /// </summary>
    private static Control BuildGlyph(BrandGlyph g)
    {
        const double chip = 74;
        // Fill ~70% of the (now chip-less) area, vs the old BoxRatio (~40%) that suited a glyph sitting
        // ON a chip. Bigger reads right now that the glyph stands alone on the card background.
        var box = chip * 0.7;

        var path = new Path
        {
            Data = Geometry.Parse(g.PathData),
            Stretch = Stretch.Uniform,
            Width = box,
            Height = box,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (g.Stroked)
        {
            path.Stroke = Brushes.White;
            path.StrokeThickness = chip * g.Thickness;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
            path.Fill = null;
        }
        else
        {
            path.Fill = Brushes.White;
        }

        if (g.TranslateX != 0)
            path.RenderTransform = new TranslateTransform(chip * g.TranslateX, 0);

        // Stretch.Uniform sizes the glyph to its FILL bounds and ignores the stroke, so on the
        // stretch-limiting axis the stroke pokes half-its-thickness past the Path's layout bounds.
        // A DropShadowEffect rasterises its target to a layer sized to those layout bounds and
        // clips that overflow — which sliced STREAM's wider-than-tall radio-tower glyph on the left
        // and right in the About card (Chloe 2026-07-06). The taskbar icon dodges it only because
        // its glyph sits with far more slack in a 512px chip. Host the glyph in a padded container
        // and hang the shadow THERE, so the effect layer is always larger than the stroke.
        return new Border
        {
            Padding = new Thickness(chip * 0.06),   // comfortably ≥ half the max stroke thickness
            Child = path,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 3, OffsetX = 0, OffsetY = 1.5, Opacity = 0.5,
            },
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Frameless window: drag it by the card, but let button clicks through.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        BeginMoveDrag(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
