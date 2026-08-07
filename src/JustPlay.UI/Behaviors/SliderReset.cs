using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// Double-click (double-tap) a <see cref="Slider"/> to reset it to a default value - the standard
/// "reset to default" gesture. Set <c>beh:SliderReset.Default="X"</c> on the slider, where X is its
/// neutral value (e.g. 1 for a 0..2 EQ band, 0 for an off-by-default fader, 0 dB for a gain trim).
///
/// SHARED across the J.U.S.T. suite so the gesture is identical in every app (pairs with
/// <see cref="SliderWheel"/>). The value is clamped to the slider's range.
/// </summary>
public static class SliderReset
{
    public static readonly AttachedProperty<double> DefaultProperty =
        AvaloniaProperty.RegisterAttached<Slider, double>("Default", typeof(SliderReset), double.NaN);

    public static void SetDefault(Slider element, double value) => element.SetValue(DefaultProperty, value);
    public static double GetDefault(Slider element) => element.GetValue(DefaultProperty);

    static SliderReset()
    {
        DefaultProperty.Changed.AddClassHandler<Slider>((slider, e) =>
        {
            slider.DoubleTapped -= OnDoubleTapped;
            if (!double.IsNaN(e.GetNewValue<double>()))
                slider.DoubleTapped += OnDoubleTapped;
        });
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Slider s) return;
        var d = GetDefault(s);
        if (double.IsNaN(d)) return;
        s.Value = Math.Clamp(d, s.Minimum, s.Maximum);
        e.Handled = true;
    }
}
