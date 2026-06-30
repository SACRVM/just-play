using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// Vertical limiter gain-reduction meter — shows how hard the output limiter is pulling peaks down
/// right now (the "where does it flatten" readout, Chloe 2026-06-28). Fed each render frame via
/// <see cref="Value"/> (non-negative dB; 0 = not limiting). The bar fills from the TOP downward (more
/// reduction = more fill), warms amber→red as it bites harder, and a peak-hold tick marks the recent
/// maximum. A "−X.X dB" numeric sits at the bottom. Self-contained: no engine reference (the window
/// pushes the value).
/// </summary>
public sealed class GainReductionMeter : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GainReductionMeter, double>(nameof(Value));

    /// <summary>Current gain reduction in dB (≥ 0). 0 = limiter idle.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private const double MaxDb = 12.0;       // full-scale of the meter
    private const int HoldFrames = 28;       // peak-hold dwell before it starts falling

    private double _peak;
    private int _hold;

    static GainReductionMeter() => AffectsRender<GainReductionMeter>(ValueProperty);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width < 8 || b.Height < 24) return;

        double v = Math.Clamp(Value, 0, MaxDb);

        // peak-hold: jump up instantly, dwell, then ease down.
        if (v >= _peak) { _peak = v; _hold = HoldFrames; }
        else if (_hold > 0) _hold--;
        else _peak = Math.Max(v, _peak - 0.25);

        double labelH = 16;
        double trackW = Math.Min(20, b.Width - 6);
        double trackX = (b.Width - trackW) / 2;
        double trackY = 2;
        double trackH = b.Height - labelH - 6;
        if (trackH < 8) return;

        var face = new Typeface("Inter");

        // ── track background ──
        var trackRect = new Rect(trackX, trackY, trackW, trackH);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x10, 0x16)), null,
            new RoundedRect(trackRect, 4));

        // tick marks every 3 dB
        var tickBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xC0, 0xC4, 0xE0));
        for (double db = 3; db < MaxDb; db += 3)
        {
            double ty = trackY + (db / MaxDb) * trackH;
            ctx.DrawLine(new Pen(tickBrush, 1), new Point(trackX, ty), new Point(trackX + trackW, ty));
        }

        // ── fill (from top down) ──
        if (v > 0.05)
        {
            double fillH = (v / MaxDb) * trackH;
            var fillRect = new Rect(trackX, trackY, trackW, fillH);
            ctx.DrawRectangle(new SolidColorBrush(GrColor(v)), null, new RoundedRect(fillRect, 4));
        }

        // ── peak-hold tick ──
        if (_peak > 0.05)
        {
            double py = trackY + (_peak / MaxDb) * trackH;
            // Flat 2 px line, square ends (FillRectangle, not a Pen — no rounded caps).
            ctx.FillRectangle(new SolidColorBrush(GrColor(_peak)), new Rect(trackX - 1, py - 1, trackW + 2, 2));
        }

        // ── numeric readout ──
        string s = v < 0.05 ? "0.0" : $"−{v:0.0}";
        var num = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11,
            new SolidColorBrush(v < 0.05 ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : GrColor(v)));
        ctx.DrawText(num, new Point((b.Width - num.Width) / 2, b.Height - labelH + 1));
    }

    /// <summary>Amber when gently limiting → red as it bites harder.</summary>
    private static Color GrColor(double db)
    {
        double t = Math.Clamp(db / 8.0, 0, 1); // fully red by ~8 dB
        byte r = 0xFF;
        byte g = (byte)(0xC0 - 0x9C * t); // 0xC0 → 0x24
        byte bch = (byte)(0x30 - 0x18 * t);
        return Color.FromArgb(0xFF, r, g, bch);
    }
}
