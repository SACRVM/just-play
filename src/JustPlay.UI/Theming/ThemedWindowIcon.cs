using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JustPlay.Core.Theming;

namespace JustPlay.UI.Theming;

/// <summary>
/// Renders a JUST app's brand mark — its <see cref="BrandGlyph"/> (white) on the active
/// theme's cyan→pink chip — into a <see cref="WindowIcon"/> at runtime, so the taskbar /
/// Alt-Tab / title-bar icon tracks the live palette. Shared by every JUST suite app
/// (CLAUDE.md "JUST suite UI philosophy": theme-gradient chip + one simple glyph, repaints
/// on theme switch).
///
/// Built as the SAME visual tree as the in-app brand chip (Border + Path + DropShadowEffect)
/// and rasterised via <see cref="RenderTargetBitmap.Render"/>, so the glyph carries the same
/// soft drop shadow — a raw DrawGeometry can't carry a blurred effect.
/// </summary>
public static class ThemedWindowIcon
{
    public static WindowIcon Render(Theme theme, BrandGlyph glyph)
    {
        // Render large so Windows has plenty of source pixels when downscaling to taskbar size.
        const int size = 512;

        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        bg.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentA), 0));
        bg.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentB), 1));

        var path = new Path
        {
            Data = Geometry.Parse(glyph.PathData),
            Stretch = Stretch.Uniform,
            Width = size * glyph.BoxRatio,
            Height = size * glyph.BoxRatio,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 8, OffsetX = 0, OffsetY = 4, Opacity = 0.5,
            },
        };

        if (glyph.Stroked)
        {
            path.Stroke = Brushes.White;
            path.StrokeThickness = size * glyph.Thickness;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
            path.Fill = null;
        }
        else
        {
            path.Fill = Brushes.White;
        }

        if (glyph.TranslateX != 0)
            path.RenderTransform = new TranslateTransform(size * glyph.TranslateX, 0);

        var chip = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Background = bg,
            Child = path,
        };

        RenderOptions.SetEdgeMode(chip, EdgeMode.Antialias);
        RenderOptions.SetBitmapInterpolationMode(chip, BitmapInterpolationMode.HighQuality);

        chip.Measure(new Size(size, size));
        chip.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(chip);
        return new WindowIcon(bitmap);
    }
}
