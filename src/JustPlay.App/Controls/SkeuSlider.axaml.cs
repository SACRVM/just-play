using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace JustPlay.App.Controls;

/// <summary>
/// Replacement for <see cref="Slider"/> with the design's skeuomorphic look — inset rail,
/// cyan→pink filled portion, glossy 3D ball thumb. Drag/click to seek.
/// Bind <see cref="Value"/> two-way like a normal Slider.
/// Set <see cref="IsChrome"/>=true for the volume-style monochrome ball.
/// </summary>
public partial class SkeuSlider : UserControl
{
    // Inner Grid in the XAML has Margin="9,0". The thumb is positioned with HA=Left + Margin.Left
    // from that inner grid's left edge — so the thumb's left edge in control coords is 9 + Margin.Left.
    private const double SidePad = 9;

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<SkeuSlider, double>(nameof(Minimum), 0d);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SkeuSlider, double>(nameof(Maximum), 1d);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SkeuSlider, double>(
            nameof(Value), 0d,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsChromeProperty =
        AvaloniaProperty.Register<SkeuSlider, bool>(nameof(IsChrome));

    private Border? _fill, _accentThumb, _chromeThumb;
    private bool _dragging;
    private IDisposable? _fillGlowSub;

    public SkeuSlider()
    {
        InitializeComponent();
        _fill = this.FindControl<Border>("Fill");
        _accentThumb = this.FindControl<Border>("AccentThumb");
        _chromeThumb = this.FindControl<Border>("ChromeThumb");

        ValueProperty.Changed.AddClassHandler<SkeuSlider>((s, _) => s.Relayout());
        MinimumProperty.Changed.AddClassHandler<SkeuSlider>((s, _) => s.Relayout());
        MaximumProperty.Changed.AddClassHandler<SkeuSlider>((s, _) => s.Relayout());
        IsChromeProperty.Changed.AddClassHandler<SkeuSlider>((s, _) =>
        {
            s.UpdateThumbVisibility();
            s.Relayout();
        });
        SizeChanged += (_, _) => Relayout();

        UpdateThumbVisibility();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        PointerWheelChanged += OnPointerWheel;
    }

    /// <summary>Scroll the mouse wheel while hovering to nudge the value — the usability touch most
    /// players skip. ~3% of the range per notch; covers BOTH the volume knob and the seek bar (the
    /// transport progress bar hosts a SkeuSlider), so wheel-to-seek and wheel-to-volume both work.</summary>
    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var range = Maximum - Minimum;
        if (range <= 0) return;
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0) return;
        var step = range * 0.03;
        Value = Math.Clamp(Value + (d > 0 ? step : -step), Minimum, Maximum);
        e.Handled = true;
    }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value   { get => GetValue(ValueProperty);   set => SetValue(ValueProperty, value); }
    public bool IsChrome  { get => GetValue(IsChromeProperty); set => SetValue(IsChromeProperty, value); }

    private double ActiveThumbWidth => IsChrome ? 14 : 18;

    /// <summary>Distance the thumb's left edge travels along the inner Grid as Value goes 0→1.</summary>
    private double TrackRange => Math.Max(0, Bounds.Width - 2 * SidePad - ActiveThumbWidth);

    /// <summary>Deterministically hide whichever thumb isn't in use right now, and
    /// bind the fill's glow to the matching theme-driven resource.</summary>
    private void UpdateThumbVisibility()
    {
        var chrome = IsChrome;
        if (_accentThumb is not null) _accentThumb.IsVisible = !chrome;
        if (_chromeThumb is not null) _chromeThumb.IsVisible = chrome;

        // Fill glow differs by mode (progress: cyan+pink bloom; volume: smaller pink).
        // DynamicResource so it tracks live theme switches like the thumb halo does.
        if (_fill is not null)
        {
            _fillGlowSub?.Dispose();
            _fillGlowSub = _fill.Bind(
                Border.BoxShadowProperty,
                this.GetResourceObservable(chrome ? "SkeuSliderChromeFillGlow" : "SkeuSliderProgressFillGlow"));
        }
    }

    private void Relayout()
    {
        if (_fill is null || Bounds.Width <= 0) return;

        var range = Math.Max(1e-9, Maximum - Minimum);
        var p = Math.Clamp((Value - Minimum) / range, 0, 1);
        var thumb = IsChrome ? _chromeThumb : _accentThumb;
        if (thumb is null) return;

        var travel = p * TrackRange;
        thumb.Margin = new Thickness(travel, 0, 0, 0);
        // Fill width: zero at p=0 (no phantom blob), grows up to the thumb centre as Value grows.
        _fill.Width = Math.Max(0, travel + ActiveThumbWidth / 2);
        _fill.IsVisible = p > 1e-4;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        UpdateFromPointer(e);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging) UpdateFromPointer(e);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Map a click X to a Value that puts the THUMB CENTRE at the click point.</summary>
    private void UpdateFromPointer(PointerEventArgs e)
    {
        if (TrackRange <= 0) return;
        var clickX = e.GetPosition(this).X;
        // Centre at click → thumb left edge = clickX - half. Subtract padding + half-thumb to map to p.
        var halfThumb = ActiveThumbWidth / 2;
        var p = Math.Clamp((clickX - SidePad - halfThumb) / TrackRange, 0, 1);
        Value = Minimum + p * (Maximum - Minimum);
    }
}
