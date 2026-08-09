using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// A distribution-histogram range picker - deliberately NOT a classic slider. It draws the folder's value distribution as bars; the bars
/// inside the chosen [<see cref="LowerValue"/>, <see cref="UpperValue"/>] band glow with
/// <see cref="RangeBrush"/>, the rest stay dim so the selection reads at a glance. The BARS are the control:
/// a plain click nudges the nearer edge to it; a click-drag paints a whole new band. Every bar carries a
/// ~3 px floor so there are no click-dead gaps even where a bucket is empty. Reusable - the finder FILTER
/// ranges use it, and it fits any "pick a band over a distribution" job.
/// </summary>
public sealed class RangeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<double> LowerValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(LowerValue),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> UpperValueProperty =
        AvaloniaProperty.Register<RangeSlider, double>(nameof(UpperValue), 1.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> RangeBrushProperty =
        AvaloniaProperty.Register<RangeSlider, IBrush?>(nameof(RangeBrush));

    /// <summary>Normalised (0..1) per-bucket counts - the distribution the control draws and clicks on.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> HistogramProperty =
        AvaloniaProperty.Register<RangeSlider, IReadOnlyList<double>?>(nameof(Histogram));

    static RangeSlider()
    {
        AffectsRender<RangeSlider>(MinimumProperty, MaximumProperty, LowerValueProperty,
            UpperValueProperty, RangeBrushProperty, HistogramProperty);
    }

    public RangeSlider()
    {
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public IBrush? RangeBrush { get => GetValue(RangeBrushProperty); set => SetValue(RangeBrushProperty, value); }
    public IReadOnlyList<double>? Histogram { get => GetValue(HistogramProperty); set => SetValue(HistogramProperty, value); }

    // -- The bars ARE the control: click the nearer edge, or drag to paint a new band --------------
    private const double BaseBarPx = 3.0;     // every bar shows at least this - no click-dead gaps
    private const double DragThreshold = 4.0; // moved past this since press -> it's a drag-new-band, not a click

    private bool _pressed;
    private bool _dragged;
    private double _pressX;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragged = false;
        _pressX = e.GetPosition(this).X;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed) return;
        var x = e.GetPosition(this).X;
        if (!_dragged && Math.Abs(x - _pressX) > DragThreshold) _dragged = true;
        if (_dragged)
        {
            // Live-paint the new band from the press point to here.
            LowerValue = XToValue(Math.Min(_pressX, x));
            UpperValue = XToValue(Math.Max(_pressX, x));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (_dragged) return; // the drag already set the band

        // Plain click -> move whichever edge is nearer the click toward it.
        var v = XToValue(_pressX);
        if (Math.Abs(v - LowerValue) <= Math.Abs(v - UpperValue)) LowerValue = Math.Min(v, UpperValue);
        else UpperValue = Math.Max(v, LowerValue);
    }

    private double XToValue(double x)
    {
        var w = Bounds.Width;
        if (w <= 0) return Minimum;
        return Minimum + Math.Clamp(x / w, 0.0, 1.0) * (Maximum - Minimum);
    }

    private double ValueToX(double v)
    {
        var range = Maximum - Minimum;
        return range <= 0 ? 0 : (v - Minimum) / range * Bounds.Width;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Full-bounds transparent layer so the WHOLE strip is clickable - Avalonia hit-tests drawn pixels,
        // not layout bounds, so the gaps above short bars would otherwise be dead.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var hist = Histogram;
        if (hist is not { Count: > 0 }) return;

        var sel = RangeBrush ?? Brushes.DeepSkyBlue;
        var dim = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var lx = ValueToX(LowerValue);
        var ux = ValueToX(UpperValue);
        var n = hist.Count;
        var maxBarH = h - 1;

        for (var i = 0; i < n; i++)
        {
            var barH = BaseBarPx + Math.Clamp(hist[i], 0, 1) * (maxBarH - BaseBarPx);
            var x0 = (double)i / n * w;
            var x1 = (double)(i + 1) / n * w;
            var cx = (x0 + x1) / 2;
            var bw = Math.Max(1.0, x1 - x0 - 0.7);
            var brush = cx >= lx && cx <= ux ? sel : dim; // in the band = accent, outside = dim
            ctx.FillRectangle(brush, new Rect(x0, h - barH, bw, barH));
        }
    }
}
