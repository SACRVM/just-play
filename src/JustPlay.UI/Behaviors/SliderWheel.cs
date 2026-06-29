using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// Attached behavior that lets the mouse wheel adjust an Avalonia <see cref="Slider"/> while hovering
/// over it — the usability touch most apps skip. Set <c>beh:SliderWheel.Enabled="True"</c> on a Slider
/// (or via a style on a fader class). One notch nudges by 2.5% of the slider's range.
///
/// SHARED across the J.U.S.T. suite (was JUST PLAY-only; moved here so JUST STREAM's faders behave
/// identically — same logic, same feel — per the "one shared operation" rule). The custom SkeuSlider
/// has its own equivalent handler.
/// </summary>
public static class SliderWheel
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("Enabled", typeof(SliderWheel));

    public static void SetEnabled(Slider element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Slider element) => element.GetValue(EnabledProperty);

    static SliderWheel()
    {
        EnabledProperty.Changed.AddClassHandler<Slider>((slider, e) =>
        {
            slider.PointerWheelChanged -= OnWheel;
            if (e.GetNewValue<bool>())
                slider.PointerWheelChanged += OnWheel;
        });
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Slider s) return;
        var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (d == 0) return;
        // ALWAYS proportional to the range. Slider.SmallChange defaults to 1, which on a 0..2 EQ fader
        // is half the whole range per notch ("viel zu krass") — never use it. 2.5% = a fine, even step
        // (≈40 notches end to end), so a 0..2 EQ moves ~0.05 per tick.
        var step = (s.Maximum - s.Minimum) * 0.025;
        s.Value = Math.Clamp(s.Value + (d > 0 ? step : -step), s.Minimum, s.Maximum);
        e.Handled = true;
    }
}
