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
    /// Build the white brand glyph on the chip — same approach as
    /// <see cref="ThemedWindowIcon"/> (Stretch.Uniform + BoxRatio sizing, stroke thickness as a
    /// fraction of the chip), so the About mark matches the taskbar / Alt-Tab icon exactly.
    /// </summary>
    private static Control BuildGlyph(BrandGlyph g)
    {
        const double chip = 74;
        var box = chip * g.BoxRatio;

        var path = new Path
        {
            Data = Geometry.Parse(g.PathData),
            Stretch = Stretch.Uniform,
            Width = box,
            Height = box,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 3, OffsetX = 0, OffsetY = 1.5, Opacity = 0.5,
            },
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

        return path;
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
