using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using JustPlay.UI.Theming;

namespace JustPlay.UI.Controls;

/// <summary>
/// Camelot key wheel — the DJ harmonic-mixing wheel as a clickable filter. 12 numbered wedges around the
/// circle (the circle of fifths), each split into an inner A ring (minor) and outer B ring (major); every
/// number gets its own hue so harmonic neighbours (adjacent wedges + the A/B flip) read at a glance.
/// Click a segment to toggle its Camelot code (e.g. "8A") in <see cref="SelectedKeys"/> via
/// <see cref="ToggleKeyCommand"/>. <see cref="NeighborKeys"/> are shown faintly lit (the keys a harmonic
/// filter would also let through). Reusable — built for the finder's FILTER tab, ready for JUST SPIN.
/// </summary>
public sealed class KeyWheel : Control
{
    public static readonly StyledProperty<IReadOnlyList<string>?> SelectedKeysProperty =
        AvaloniaProperty.Register<KeyWheel, IReadOnlyList<string>?>(nameof(SelectedKeys));

    /// <summary>Keys that aren't selected but WOULD pass the filter (harmonic neighbours of the selection) —
    /// drawn with a soft glow so you can see the compatible set.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> NeighborKeysProperty =
        AvaloniaProperty.Register<KeyWheel, IReadOnlyList<string>?>(nameof(NeighborKeys));

    public static readonly StyledProperty<ICommand?> ToggleKeyCommandProperty =
        AvaloniaProperty.Register<KeyWheel, ICommand?>(nameof(ToggleKeyCommand));

    static KeyWheel()
    {
        AffectsRender<KeyWheel>(SelectedKeysProperty, NeighborKeysProperty);
    }

    public KeyWheel() => Cursor = new Cursor(StandardCursorType.Hand);

    public IReadOnlyList<string>? SelectedKeys
    {
        get => GetValue(SelectedKeysProperty);
        set => SetValue(SelectedKeysProperty, value);
    }

    public IReadOnlyList<string>? NeighborKeys
    {
        get => GetValue(NeighborKeysProperty);
        set => SetValue(NeighborKeysProperty, value);
    }

    public ICommand? ToggleKeyCommand
    {
        get => GetValue(ToggleKeyCommandProperty);
        set => SetValue(ToggleKeyCommandProperty, value);
    }

    // Ring radii as fractions of the outer radius. The A|B boundary gives the INNER A ring 60% of the
    // usable band and B 40% — a plain 50/50 shortchanges the inner ring (less circumference at a smaller
    // radius), and the A keys sit there (Chloe 2026-07-07). MidFrac = InnerFrac + 0.60·(1 − InnerFrac).
    private const double InnerFrac = 0.34;  // center hole
    private const double MidFrac = 0.736;   // A|B boundary — A gets the bigger (inner) share
    private const double GapDeg = 1.6;      // wedge separation (angular)
    private const double RingGapPx = 2.0;   // radial gap between the A and B rings — matches the wedge gaps
                                            // so the grid reads uniformly (Chloe 2026-07-07); hit-test stays at MidFrac

    // ── Click → toggle the segment's Camelot code ────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var (cx, cy, outerR) = Geo();
        var p = e.GetPosition(this);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < outerR * InnerFrac || r > outerR) return; // hole or outside

        // Screen angle (clockwise, 0 = east); fold to "degrees from the top" so wedge 1 sits at 12 o'clock.
        var deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        var fromTop = (deg + 90.0 + 360.0) % 360.0;
        var n = (int)Math.Floor(((fromTop + 15.0) % 360.0) / 30.0) + 1; // wedges centred on multiples of 30°
        if (n > 12) n = 1;
        var ring = r < outerR * MidFrac ? "A" : "B";
        var code = $"{n}{ring}";

        if (ToggleKeyCommand is { } cmd && cmd.CanExecute(code)) cmd.Execute(code);
        e.Handled = true;
    }

    private (double cx, double cy, double outerR) Geo()
    {
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var outerR = Math.Min(cx, cy) - 2;
        return (cx, cy, outerR);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var (cx, cy, outerR) = Geo();
        if (outerR <= 6) return;

        var innerR = outerR * InnerFrac;
        var midR = outerR * MidFrac;
        var center = new Point(cx, cy);
        var selected = ToSet(SelectedKeys);
        var neighbors = ToSet(NeighborKeys);
        var typeface = new Typeface("Inter");

        for (var n = 1; n <= 12; n++)
        {
            var centre = -90.0 + (n - 1) * 30.0;       // screen degrees, wedge centre
            var a0 = centre - 15.0 + GapDeg / 2;
            var a1 = centre + 15.0 - GapDeg / 2;
            var hue = (n - 1) / 12.0 * 360.0;

            DrawSeg(ctx, center, midR + RingGapPx / 2, outerR, a0, a1, hue, $"{n}B", selected, neighbors, typeface, false);
            DrawSeg(ctx, center, innerR, midR - RingGapPx / 2, a0, a1, hue, $"{n}A", selected, neighbors, typeface, true);
        }

        // Center hole — sits on the panel background, faint ring.
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xF0, 0x1a, 0x18, 0x28)),
            new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1),
            center, innerR, innerR);
    }

    private void DrawSeg(DrawingContext ctx, Point c, double ri, double ro, double a0, double a1,
        double hue, string code, HashSet<string> selected, HashSet<string> neighbors, Typeface tf, bool isA)
    {
        var isSel = selected.Contains(code);
        var isNbr = !isSel && neighbors.Contains(code);

        // Base tint: A rings a touch darker than B so the two are legible; selection lifts sat+lightness.
        var sat = isSel ? 0.80 : isNbr ? 0.55 : 0.42;
        var lig = (isSel ? 0.60 : isNbr ? 0.48 : 0.40) * (isA ? 0.86 : 1.0);
        var alpha = (byte)(isSel ? 0xFF : isNbr ? 0xD0 : 0x8A);
        var fill = new SolidColorBrush(CamelotPalette.Hsl(hue, sat, lig, alpha));
        var pen = isSel
            ? new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)), 1.6)
            : isNbr
                ? new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)), 1.0)
                : null;

        ctx.DrawGeometry(fill, pen, Sector(c, ri, ro, a0, a1));

        // Label at the ring's mid radius, upright, with a dark shadow so white text stays legible on the
        // bright hues (yellow/cyan/green) — drawn as a near-black copy offset behind the white (Chloe 2026-07-07).
        var lr = (ri + ro) / 2;
        var ang = (a0 + a1) / 2 * Math.PI / 180.0;
        var lp = new Point(c.X + lr * Math.Cos(ang), c.Y + lr * Math.Sin(ang));
        var labelBrush = new SolidColorBrush(isSel ? Colors.White : Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF));
        var ft = new FormattedText(code, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 8.5, labelBrush);
        var shadow = new FormattedText(code, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 8.5,
            new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)));
        var origin = new Point(lp.X - ft.Width / 2, lp.Y - ft.Height / 2);
        ctx.DrawText(shadow, new Point(origin.X + 0.7, origin.Y + 0.8));
        ctx.DrawText(ft, origin);
    }

    /// <summary>Annular sector geometry between radii <paramref name="ri"/>..<paramref name="ro"/> from
    /// <paramref name="a0"/>° to <paramref name="a1"/>° (screen degrees, clockwise).</summary>
    private static Geometry Sector(Point c, double ri, double ro, double a0, double a1)
    {
        Point P(double r, double aDeg)
        {
            var a = aDeg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(P(ro, a0), true);
        g.ArcTo(P(ro, a1), new Size(ro, ro), 0, false, SweepDirection.Clockwise);
        g.LineTo(P(ri, a1));
        g.ArcTo(P(ri, a0), new Size(ri, ri), 0, false, SweepDirection.CounterClockwise);
        g.EndFigure(true);
        return geo;
    }

    private static HashSet<string> ToSet(IReadOnlyList<string>? keys)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keys is not null) foreach (var k in keys) set.Add(k);
        return set;
    }
}
