using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;

namespace JustPlay.UI.Views;

/// <summary>
/// SHARED live spectrum analyzer window — used by JUST PLAY today, reusable by JUST STREAM and (later)
/// JUST MASTER. A control panel showing tonal balance BEFORE the DSP bus (DRY) vs AFTER it (WET) against
/// the golden target curve, the limiter gain-reduction meter, and an L/R output level meter. Non-modal.
/// Driven by a vsync <c>RequestAnimationFrame</c> pump; polls the thread-safe <see cref="ISpectrumSource"/>
/// each frame. Depends only on <see cref="ISpectrumSource"/>, NOT any one app's engine type, so every
/// JUST app can open it with its own engine. Capture taps run only while open
/// (<see cref="ISpectrumSource.SetSpectrumTapEnabled"/>).
/// </summary>
public partial class SpectrumWindow : Window
{
    private readonly ISpectrumSource? _source;
    private readonly bool _showOutputLevels;
    private readonly float[] _dry = new float[SpectralProfile.BandCount];
    private readonly float[] _wet = new float[SpectralProfile.BandCount];
    private SpectrumView? _view;
    private GainReductionMeter? _grMeter;
    private LevelMeter? _meterL, _meterR;
    private bool _running;
    private TimeSpan _lastFrame;

    // Parameterless ctor for the XAML previewer / designer.
    public SpectrumWindow() : this(null) { }

    /// <param name="source">The thread-safe spectrum/level source to poll each frame (any JUST engine).</param>
    /// <param name="showOutputLevels">Show the OUT L/R column. JUST PLAY shows it; JUST STREAM hides it
    /// (it already has its own output meter in the main window), so it isn't duplicated here.</param>
    public SpectrumWindow(ISpectrumSource? source, bool showOutputLevels = true)
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY — re-setting it here trips
        // Avalonia's macOS opaque-fallback (black surround); see JustPlay MainWindow ctor.

        _source = source;
        _showOutputLevels = showOutputLevels;
        _view = this.FindControl<SpectrumView>("View");
        _grMeter = this.FindControl<GainReductionMeter>("GrMeter");
        _meterL = this.FindControl<LevelMeter>("MeterL");
        _meterR = this.FindControl<LevelMeter>("MeterR");

        // Host opted out of the OUT meter — collapse the whole column (its Auto width goes to 0).
        if (!showOutputLevels && this.FindControl<Control>("OutColumn") is { } outCol)
            outCol.IsVisible = false;

        WindowPlacement.Track(this, "JustPlay.Spectrum");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _source?.SetSpectrumTapEnabled(true); // arm the capture taps only while we're open
        _running = true;
        RequestAnimationFrame(OnRenderFrame);
    }

    private void OnRenderFrame(TimeSpan now)
    {
        if (!_running) return;

        // dt for the level-meter ballistics (refresh-rate independent), same approach as STREAM's pump.
        double dt = _lastFrame == TimeSpan.Zero ? 0.016 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.25) dt = 0.016; // clamp first frame / hitches

        if (_source is not null)
        {
            if (_view is not null)
            {
                _source.GetSpectrum(_dry, _wet);
                _view.SetData(_dry, _wet);
            }
            if (_grMeter is not null)
            {
                _grMeter.Value = _source.GetLimiterGainReductionDb();
                _grMeter.InvalidateVisual(); // keep the peak-hold easing even when GR holds steady
            }
            if (_showOutputLevels && (_meterL is not null || _meterR is not null))
            {
                _source.GetOutputLevels(out var l, out var r);
                _meterL?.Push(l, dt);
                _meterR?.Push(r, dt);
            }
        }
        RequestAnimationFrame(OnRenderFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        _running = false;
        _source?.SetSpectrumTapEnabled(false); // taps cost nothing again
        base.OnClosed(e);
    }

    // Drag the frameless card from the chrome bar (but not from interactive controls).
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Legend click → show/hide a curve + dim the clicked entry as the off-state cue.
    private void OnToggleDry(object? sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.ShowDry = !_view.ShowDry;
        if (sender is Control c) c.Opacity = _view.ShowDry ? 1.0 : 0.35;
    }

    private void OnToggleWet(object? sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.ShowWet = !_view.ShowWet;
        if (sender is Control c) c.Opacity = _view.ShowWet ? 1.0 : 0.35;
    }
}
