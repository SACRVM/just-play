using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// Suite-wide smooth wheel scrolling: a mouse notch GLIDES the list instead of teleporting it.
///
/// <para>Why: the scrolling wasn't too slow, it was too choppy - not a frame-rate problem. A wheel
/// notch moves a
/// <see cref="ScrollViewer"/> by a fixed chunk in ONE step, so at 30-px rows the list jumps three
/// rows at a time. Every jump reads as a stutter no matter how many frames per second we draw
/// around it. Frames were never the issue; the missing thing was motion BETWEEN the two positions.</para>
///
/// <para>How: swallow the wheel event, keep our own target offset, and ease the real offset toward it
/// once per frame. Scrolling again mid-glide just moves the target, so continuous scrolling stays
/// continuous instead of restarting. Exponential easing (a constant fraction of the remaining
/// distance per frame) - it starts fast, settles softly, and has no fixed duration to get wrong.</para>
///
/// <para>Only the wheel is intercepted. Dragging the scrollbar, keyboard navigation, touch and
/// programmatic scrolling (bring-into-view when the keyboard cursor moves) are untouched - those
/// already move continuously or must stay exact.</para>
///
/// <para>Usage: <c>beh:SmoothScroll.Enabled="True"</c> on the ListBox / ScrollViewer.</para>
/// </summary>
public static class SmoothScroll
{
    /// <summary>Fraction of the remaining distance covered per frame. Higher = snappier, lower = floatier.</summary>
    private const double Ease = 0.22;

    /// <summary>Pixels per wheel notch. Roughly three 30-px rows - the same travel the default gives,
    /// so the FEEL of a notch is unchanged; only the abruptness goes away.</summary>
    private const double PixelsPerNotch = 90;

    /// <summary>Below this the glide is over - chasing sub-pixels would keep a timer alive forever.</summary>
    private const double Done = 0.5;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(SmoothScroll));

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    static SmoothScroll()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                control.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel,
                                   RoutingStrategies.Tunnel);
            else
                control.RemoveHandler(InputElement.PointerWheelChangedEvent, (EventHandler<PointerWheelEventArgs>)OnWheel);
        });
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control control) return;

        var scroll = control as ScrollViewer ?? control.FindDescendantOfType<ScrollViewer>();
        if (scroll is null) return;

        // Horizontal wheels / shift-scroll keep the default behaviour - one axis is the whole point here.
        if (Math.Abs(e.Delta.Y) < 0.01) return;

        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        if (max <= 0) return;   // nothing to scroll: let the parent have the event

        var state = Glide.For(scroll);
        var from  = state.Active ? state.Target : scroll.Offset.Y;
        state.Target = Math.Clamp(from - e.Delta.Y * PixelsPerNotch, 0, max);

        e.Handled = true;
        state.Start();
    }

    /// <summary>One glide per ScrollViewer, kept on the ScrollViewer itself so it dies with the control.</summary>
    private sealed class Glide
    {
        private static readonly AttachedProperty<Glide?> StateProperty =
            AvaloniaProperty.RegisterAttached<ScrollViewer, Glide?>("Glide", typeof(SmoothScroll));

        private readonly ScrollViewer _scroll;
        private readonly DispatcherTimer _timer;

        public double Target;
        public bool Active => _timer.IsEnabled;

        private Glide(ScrollViewer scroll)
        {
            _scroll = scroll;
            // ~60 Hz, the same cadence the rest of this suite polls the UI at (see Vinyl.UpdateSpin).
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Tick);
        }

        public static Glide For(ScrollViewer scroll)
        {
            if (scroll.GetValue(StateProperty) is { } existing) return existing;

            var glide = new Glide(scroll);
            scroll.SetValue(StateProperty, glide);
            return glide;
        }

        public void Start()
        {
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void Tick(object? sender, EventArgs e)
        {
            var current   = _scroll.Offset.Y;
            var remaining = Target - current;

            // Someone else moved the view (keyboard cursor, bring-into-view, a new folder): drop the
            // glide rather than yanking the list back to where the wheel was heading.
            var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            if (Target > max) Target = max;

            if (Math.Abs(remaining) <= Done)
            {
                _scroll.Offset = _scroll.Offset.WithY(Target);
                _timer.Stop();
                return;
            }

            var step = remaining * Ease;
            // Always move at least a pixel, or the tail of the ease crawls.
            if (Math.Abs(step) < 1) step = Math.Sign(step);

            _scroll.Offset = _scroll.Offset.WithY(current + step);
        }
    }
}
