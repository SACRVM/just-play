using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace JustPlay.UI.Controls;

/// <summary>
/// Tiny animated spectrum CURVE — the glyph on the "open analyzer" transport button (next to
/// shuffle/repeat). While <see cref="Active"/> (music playing) the curve undulates like a live
/// analyzer; when idle it rests as a still curve ("animieren — nur wenn Musik läuft, sonst still",
/// Chloe 2026-06-28). The motion is decorative ("so fake wie die Balken beim Playback") — a few
/// phase-offset sines — so it needs no audio-engine hookup, and the timer stops once it settles so
/// an idle button costs nothing.
/// </summary>
public sealed class SpectrumGlyph : Control
{
    public static readonly StyledProperty<bool> ActiveProperty =
        AvaloniaProperty.Register<SpectrumGlyph, bool>(nameof(Active));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SpectrumGlyph, IBrush?>(nameof(Stroke), Brushes.White);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SpectrumGlyph, double>(nameof(StrokeThickness), 1.6);

    /// <summary>Music playing → animate; otherwise ease to a still resting curve.</summary>
    public bool Active { get => GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    // 7 control points across the width. Base = the resting "spectrum hump" (normalised Y, 0=top..1=bottom);
    // Amp = how much each point bobs; Phase = per-point offset so the wiggle flows left→right.
    private static readonly double[] Base  = { 0.66, 0.44, 0.28, 0.18, 0.32, 0.52, 0.72 };
    private static readonly double[] Amp   = { 0.10, 0.20, 0.30, 0.34, 0.26, 0.16, 0.08 };
    private static readonly double[] Phase = { 0.0, 0.7, 1.4, 2.1, 2.8, 3.5, 4.2 };

    private readonly DispatcherTimer _timer;
    private double _t;       // wave phase
    private double _energy;  // 0..1 eased; >0 ⇒ wiggling

    static SpectrumGlyph()
    {
        AffectsRender<SpectrumGlyph>(StrokeProperty, StrokeThicknessProperty);
        ActiveProperty.Changed.AddClassHandler<SpectrumGlyph>((g, _) => g.OnActiveChanged());
    }

    public SpectrumGlyph()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 fps
        _timer.Tick += (_, _) => OnTick();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Active) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void OnActiveChanged()
    {
        // Begin animating when playback starts; when it stops we let the wiggle ease out and the timer
        // stops itself in OnTick once it's fully at rest.
        if (Active && !_timer.IsEnabled) _timer.Start();
    }

    private void OnTick()
    {
        double target = Active ? 1.0 : 0.0;
        _energy += (target - _energy) * 0.14;
        if (!Active && _energy < 0.004)
        {
            _energy = 0.0;
            _timer.Stop();        // settled → idle = zero CPU
            InvalidateVisual();   // one last frame at the resting curve
            return;
        }
        _t += 0.18;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 1 || b.Height <= 1) return;

        var pen = new Pen(Stroke ?? Brushes.White, StrokeThickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        int n = Base.Length;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double x = b.Width * i / (n - 1);
                double yN = Base[i] + Amp[i] * _energy * Math.Sin(_t + Phase[i]);
                yN = Math.Clamp(yN, 0.06, 0.94);
                var p = new Point(x, b.Height * yN);
                if (i == 0) c.BeginFigure(p, false);
                else c.LineTo(p);
            }
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
