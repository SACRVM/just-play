using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JustPlay.Core.Theming;

namespace JustPlay.App.Theming;

/// <summary>
/// Renders the JustPlay brand mark (white ▶ on the theme's cyan→pink chip) into a
/// <see cref="WindowIcon"/> at runtime, so the taskbar / Alt-Tab / title-bar icon
/// tracks the active palette. App-layer because it touches Avalonia rendering;
/// driven by <c>IThemeService.ThemeChanged</c> from App.axaml.cs.
///
/// Built as the SAME visual tree as the in-app brand chip (Border + Path +
/// DropShadowEffect) and rasterised via <see cref="RenderTargetBitmap.Render"/>,
/// so the play glyph gets the same soft drop shadow — a raw DrawGeometry can't
/// carry a blurred effect.
///
/// Note: this themes the WINDOW icon (live, Windows + most Linux DEs). The .exe
/// file icon and the macOS dock icon are separate, build-time/platform concerns.
/// </summary>
public static class ThemedWindowIcon
{
    public static WindowIcon Render(Theme theme)
    {
        // Render large so Windows has plenty of source pixels when it scales the
        // icon down to taskbar / Alt-Tab size — the smaller the downscale ratio's
        // source, the more the rounded corners stair-step.
        const int size = 512;

        // Cyan→pink diagonal chip — same as AccentGradient / the in-app brand chip
        // (corner ratio 0.25 mirrors radius 5 on the 20-px chip).
        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
        };
        bg.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentA), 0));
        bg.GradientStops.Add(new GradientStop(Color.Parse(theme.AccentB), 1));

        var chip = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Background = bg,
            Child = new Path
            {
                // Brand glyph, Stretch=Uniform into a centred ~40 % box (mirrors the
                // 8-px glyph on the 20-px chip).
                Data = Geometry.Parse("M 0,0 L 0,10 L 9,5 Z"),
                Fill = Brushes.White,
                Stretch = Stretch.Uniform,
                Width = size * 0.4,
                Height = size * 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Same soft drop shadow as the in-app icons, scaled up for the
                // 256-px render so it reads at icon size.
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 8, OffsetX = 0, OffsetY = 4, Opacity = 0.5,
                },
            },
        };

        // Force smooth edges + high-quality scaling so the rounded corners carry a
        // proper alpha ramp (anti-aliased), not a 1-bit transparent/opaque edge.
        RenderOptions.SetEdgeMode(chip, EdgeMode.Antialias);
        RenderOptions.SetBitmapInterpolationMode(chip, BitmapInterpolationMode.HighQuality);

        // Off-screen controls must be measured + arranged before rasterising.
        chip.Measure(new Size(size, size));
        chip.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(chip);
        return new WindowIcon(bitmap);
    }
}
