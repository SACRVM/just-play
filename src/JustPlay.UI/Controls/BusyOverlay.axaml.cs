using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace JustPlay.UI.Controls;

/// <summary>
/// Shared J.U.S.T. progress overlay - a floating "busy" pill for a card's OVERLAY layer, so showing
/// progress never reflows the layout: an inline progress bar used to push the layout below it
/// around, which is bad, so this shared overlay - usable everywhere - was built instead. One
/// implementation, one look, used across the suite. Drop it as the LAST child of a card's Panel,
/// bind <see cref="IsActive"/> to a busy flag and (optionally) <see cref="Message"/> to a status
/// line. It is <c>IsHitTestVisible=False</c>, so it is purely visual and never blocks a click.
///
/// <para><b>Anti-flicker.</b> The pill only appears after <see cref="ShowDelayMs"/> of continuous
/// busy - so the fast, constant per-knob re-renders that make a lab feel instant never flash a
/// spinner; only work that actually takes a beat (an export, a heavy render) surfaces it.</para>
/// </summary>
public partial class BusyOverlay : UserControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<BusyOverlay, bool>(nameof(IsActive));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<BusyOverlay, string?>(nameof(Message));

    public static readonly StyledProperty<int> ShowDelayMsProperty =
        AvaloniaProperty.Register<BusyOverlay, int>(nameof(ShowDelayMs), 180);

    public static readonly StyledProperty<double?> ProgressProperty =
        AvaloniaProperty.Register<BusyOverlay, double?>(nameof(Progress));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<BusyOverlay, string?>(nameof(Detail));

    private readonly DispatcherTimer _delay;

    public BusyOverlay()
    {
        InitializeComponent();
        _delay = new DispatcherTimer();
        _delay.Tick += OnDelayElapsed;
    }

    /// <summary>Whether a busy operation is in flight. The pill follows this (after the delay).</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Optional text beside the spinner - a short "Exporting..." style line.</summary>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>
    /// Completion as 0..1, or null when the length is unknown - then the ring travels instead of
    /// filling. The ring eases toward whatever it is given, so reporting per item is fine.
    /// </summary>
    public double? Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>
    /// Optional second line under the message - the counter ("1,203 / 4,096") or anything else that
    /// changes often. It lives in its own fixed-height row so it can churn without moving the card.
    /// </summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>How long a busy state must persist before the pill appears - the anti-flicker guard.
    /// Default 180 ms: long enough that an instant per-knob re-render never flashes it.</summary>
    public int ShowDelayMs
    {
        get => GetValue(ShowDelayMsProperty);
        set => SetValue(ShowDelayMsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Guarded on IsActiveProperty, which only ever changes AFTER InitializeComponent (a host
        // binds it) - so Pill is always the real element here, never null.
        if (change.Property != IsActiveProperty) return;

        if (IsActive)
        {
            _delay.Interval = TimeSpan.FromMilliseconds(Math.Max(0, ShowDelayMs));
            _delay.Stop();
            _delay.Start();   // reveal only if STILL busy once the delay elapses
        }
        else
        {
            _delay.Stop();
            Pill.IsVisible = false;
        }
    }

    private void OnDelayElapsed(object? sender, EventArgs e)
    {
        _delay.Stop();
        if (IsActive) Pill.IsVisible = true;
    }
}
