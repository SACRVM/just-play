using System;
using Avalonia.Controls;

namespace JustPlay.UI.Rendering;

/// <summary>
/// The J.U.S.T. suite's ONE vsync frame pump. Drives a per-frame callback off a <see cref="TopLevel"/>'s
/// <c>RequestAnimationFrame</c> clock - the single place the suite hooks vsync, so no window re-implements
/// the re-arm + dt-clamp dance (SpectrumWindow, JUST STREAM's MainWindow and JUST BOOTLEG each hand-rolled
/// their own copy of it before this).
///
/// <para><b>Why not a <c>DispatcherTimer</c>:</b> a free-running timer ticks on its own clock, unrelated to
/// when Avalonia actually paints a frame. The two drift, so the painted value lands late and beats against
/// vsync - visible judder, and a picture drawn from a stale position (Chloe saw exactly this on STREAM's
/// meter and BOOTLEG's playhead; JUST BEAT's playhead ran on a 50 ms / 20 fps timer and lagged the sound by
/// up to a frame-and-a-half). <c>RequestAnimationFrame</c> runs the callback at the START of each rendered
/// frame, at the display's refresh rate - the update lands ON the frame, every frame.</para>
///
/// <para><b>Usage - the VIEW owns the pump, the ViewModel exposes a per-frame method:</b>
/// <code>
/// private SuiteFramePump? _pump;
///
/// protected override void OnOpened(EventArgs e)
/// {
///     base.OnOpened(e);
///     _pump = new SuiteFramePump(this, dt =&gt; (DataContext as FooViewModel)?.PumpFrame(dt));
///     _pump.Start();                       // or Start/Stop on a state flip, to avoid idle churn
/// }
///
/// protected override void OnClosed(EventArgs e) { _pump?.Stop(); base.OnClosed(e); }
/// </code>
/// A pump that is armed keeps the compositor rendering at the refresh rate, so <see cref="Stop"/> it when
/// nothing is animating (e.g. gate <see cref="Start"/>/<see cref="Stop"/> on an <c>IsPlaying</c> flag) unless
/// the surface truly animates continuously (a live meter does; an idle beat lab does not).</para>
/// </summary>
public sealed class SuiteFramePump
{
    /// <summary>Fallback dt for the very first frame and for any hitch - one 60 Hz step. Callbacks stay
    /// refresh-rate independent because their ballistics scale by this dt, not by a fixed per-frame amount.</summary>
    private const double RefStep = 1.0 / 60.0;

    private readonly TopLevel _top;
    private readonly Action<double> _onFrame;
    private bool _running;
    private TimeSpan _last;

    /// <param name="top">The window / top-level whose vsync clock drives the pump. The callback only fires
    /// while it renders, so <see cref="Start"/> from <c>OnOpened</c> (or a later state flip) and
    /// <see cref="Stop"/> from <c>OnClosed</c>.</param>
    /// <param name="onFrame">Invoked once per rendered frame with the seconds elapsed since the previous
    /// frame - clamped to <see cref="RefStep"/> on the first frame and on any hitch &gt; 0.25 s, so the
    /// caller never sees a zero, a negative, or a giant dt after the app was suspended.</param>
    public SuiteFramePump(TopLevel top, Action<double> onFrame)
    {
        _top = top ?? throw new ArgumentNullException(nameof(top));
        _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
    }

    /// <summary>Whether the pump is currently armed.</summary>
    public bool IsRunning => _running;

    /// <summary>Arm the pump - schedule the first frame. Idempotent: a second call while already running is
    /// a no-op (two live RequestAnimationFrame chains would double the callback rate).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;                 // first Tick clamps to RefStep instead of a bogus delta
        _top.RequestAnimationFrame(Tick);
    }

    /// <summary>Stop after the current frame. The in-flight callback re-checks <see cref="_running"/> and
    /// does not re-arm, so the chain unwinds cleanly. Idempotent.</summary>
    public void Stop() => _running = false;

    private void Tick(TimeSpan now)
    {
        if (!_running) return;                 // a frame scheduled before Stop() must do nothing
        var dt = _last == TimeSpan.Zero ? RefStep : (now - _last).TotalSeconds;
        _last = now;
        if (dt <= 0 || dt > 0.25) dt = RefStep; // clamp the first frame and any hitch
        _onFrame(dt);
        if (_running) _top.RequestAnimationFrame(Tick);
    }
}
