using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using JustPlay.UI.Rendering;

namespace JustPlay.UI.Controls;

/// <summary>
/// The suite's progress indicator: a round, hand-drawn ring.
///
/// <para>Replaces the 74x3 px bar the busy overlay used to carry (Chloe 2026-07-30: <i>"die bar ist
/// viel zu schmal ... ich haette lieber was rundes modernes schickes"</i>). Determinate when
/// <see cref="Progress"/> has a value - a sweeping arc plus the percentage in the middle - and a
/// travelling arc when it is null.</para>
///
/// <para><b>It never twitches.</b> The drawn value EASES toward the target instead of jumping, so a
/// job reporting per file (dozens of updates a second) still shows one smooth sweep. Animation runs
/// on the shared vsync pump, and only while it has something to move.</para>
/// </summary>
public sealed class ProgressRing : Control
{
    /// <summary>0..1, or null for "working, length unknown".</summary>
    public static readonly StyledProperty<double?> ProgressProperty =
        AvaloniaProperty.Register<ProgressRing, double?>(nameof(Progress));

    /// <summary>Ring stroke width.</summary>
    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(RingThickness), 4.0);

    /// <summary>The faint full circle behind the value arc.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));

    /// <summary>The value arc itself.</summary>
    public static readonly StyledProperty<IBrush?> ArcBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(ArcBrush));

    /// <summary>Percentage text colour (determinate only).</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(LabelBrush));

    /// <summary>Show the percentage inside the ring. Off for a small ring where it would not fit.</summary>
    public static readonly StyledProperty<bool> ShowPercentProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(ShowPercent), true);

    public double? Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? ArcBrush
    {
        get => GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public bool ShowPercent
    {
        get => GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    // -- Animation state ------------------------------------------------------

    /// <summary>Seconds for the eased value to cover most of the remaining distance. Slow enough to
    /// read as one motion, fast enough that a finished job snaps shut.</summary>
    private const double EaseSeconds = 0.25;

    /// <summary>Turns per second of the indeterminate arc.</summary>
    private const double SpinTurnsPerSecond = 0.6;

    /// <summary>Sweep of the travelling arc, in degrees.</summary>
    private const double SpinArcDegrees = 96;

    private SuiteFramePump? _pump;
    private double _shown;      // eased 0..1
    private double _spinAngle;  // degrees

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(
            ProgressProperty, RingThicknessProperty, TrackBrushProperty,
            ArcBrushProperty, LabelBrushProperty, ShowPercentProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Jump to the current value on appear - easing UP from zero every time the overlay opens
        // would read as a replay of work that is already done.
        _shown = Math.Clamp(Progress ?? 0, 0, 1);

        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _pump = new SuiteFramePump(top, OnFrame);
            _pump.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pump?.Stop();
        _pump = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFrame(double dt)
    {
        var dirty = false;

        if (Progress is { } target)
        {
            var goal = Math.Clamp(target, 0, 1);
            if (Math.Abs(goal - _shown) > 0.0005)
            {
                // Exponential approach - frame-rate independent, no overshoot, and a value that
                // jumps forward (a batch of files landing at once) still arrives smoothly.
                _shown += (goal - _shown) * (1 - Math.Exp(-dt / (EaseSeconds / 3.0)));
                dirty = true;
            }
            else if (_shown != goal)
            {
                _shown = goal;
                dirty = true;
            }
        }
        else
        {
            _spinAngle = (_spinAngle + dt * SpinTurnsPerSecond * 360.0) % 360.0;
            dirty = true;
        }

        if (dirty) InvalidateVisual();
    }

    // -- Drawing --------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var thickness = Math.Max(1, Math.Min(RingThickness, size / 2 - 1));
        var radius    = (size - thickness) / 2;
        var centre    = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
            ctx.DrawEllipse(null, new Pen(track, thickness), centre, radius, radius);

        var arc = ArcBrush;
        if (arc is null) return;

        // Round caps: the ring should read as a drawn stroke, not a cut pie slice.
        var pen = new Pen(arc, thickness, lineCap: PenLineCap.Round);

        if (Progress is null)
        {
            DrawArc(ctx, pen, centre, radius, _spinAngle, SpinArcDegrees);
            return;
        }

        var fraction = Math.Clamp(_shown, 0, 1);
        if (fraction >= 0.999)
            ctx.DrawEllipse(null, pen, centre, radius, radius);
        else if (fraction > 0.0005)
            DrawArc(ctx, pen, centre, radius, -90, fraction * 360.0);   // 12 o'clock, clockwise

        if (!ShowPercent || LabelBrush is null || size < 34) return;

        var text = new FormattedText(
            $"{(int)Math.Round(fraction * 100)}%",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            // A bare Control has no FontFamily of its own - take the inherited one so the ring's
            // label is in the same face as everything around it.
            new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.SemiBold),
            size * 0.26,
            LabelBrush);

        ctx.DrawText(text, new Point(
            centre.X - text.Width / 2,
            centre.Y - text.Height / 2));
    }

    private static void DrawArc(
        DrawingContext ctx, Pen pen, Point centre, double radius, double startDeg, double sweepDeg)
    {
        if (sweepDeg <= 0) return;
        sweepDeg = Math.Min(sweepDeg, 359.9);   // a full turn is drawn as an ellipse, not an arc

        var geometry = new StreamGeometry();
        using (var geo = geometry.Open())
        {
            geo.BeginFigure(PointOn(centre, radius, startDeg), isFilled: false);
            geo.ArcTo(
                PointOn(centre, radius, startDeg + sweepDeg),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweepDeg > 180,
                sweepDirection: SweepDirection.Clockwise);
            geo.EndFigure(isClosed: false);
        }

        ctx.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOn(Point centre, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(centre.X + radius * Math.Cos(rad), centre.Y + radius * Math.Sin(rad));
    }
}
