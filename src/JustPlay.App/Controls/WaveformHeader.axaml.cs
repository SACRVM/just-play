using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App.Controls;

/// <summary>
/// Four overlapping sine-wave polylines stacked across the top, dancing to the
/// live FFT spectrum of the playing audio. The shape generator matches
/// <c>Waveform.jsx</c> in the design bundle (same amplitudes, frequencies,
/// phase offsets, drift speeds, base opacities); the per-band scale comes from
/// <see cref="IAudioEngine.GetFftBands"/>.
///
/// When <see cref="Playing"/> is false, the timer stops and the lines settle
/// gently to an idle scaleY = 1.0 - the engine itself decays its smoothed band
/// values so we don't snap to zero.
/// </summary>
public partial class WaveformHeader : UserControl
{
    public static readonly StyledProperty<bool> PlayingProperty =
        AvaloniaProperty.Register<WaveformHeader, bool>(nameof(Playing));

    /// <summary>True while a track is playing. Drives the animation timer on/off.</summary>
    public bool Playing
    {
        get => GetValue(PlayingProperty);
        set => SetValue(PlayingProperty, value);
    }

    public static readonly StyledProperty<double?> BpmHintProperty =
        AvaloniaProperty.Register<WaveformHeader, double?>(nameof(BpmHint));

    /// <summary>
    /// Tempo of the current track. Drives the beat-phase opacity pulse on
    /// top of the FFT-driven scaleY. Null falls back to 120 BPM so the
    /// pulse still feels musical on tracks without an analysed tempo.
    /// </summary>
    public double? BpmHint
    {
        get => GetValue(BpmHintProperty);
        set => SetValue(BpmHintProperty, value);
    }

    public static readonly StyledProperty<int?> EnergyHintProperty =
        AvaloniaProperty.Register<WaveformHeader, int?>(nameof(EnergyHint));

    /// <summary>
    /// 1..10 energy estimate for the current track. Scales the beat-pulse
    /// intensity - low-energy tracks pulse subtly, high-energy tracks pulse
    /// hard. Null falls back to 6 (the design's default).
    /// </summary>
    public int? EnergyHint
    {
        get => GetValue(EnergyHintProperty);
        set => SetValue(EnergyHintProperty, value);
    }

    // Per-polyline transforms. ScaleY drives band amplitude; TranslateX gives
    // each line a constant drift. Fields so the timer can write to them
    // directly without re-creating objects every frame.
    private readonly ScaleTransform[] _scales = new ScaleTransform[4];
    private readonly TranslateTransform[] _translates = new TranslateTransform[4];

    // Cached polyline refs so Tick() doesn't FindControl every frame.
    private readonly Polyline?[] _polylines = new Polyline?[4];

    private DispatcherTimer? _timer;
    private DateTime _animStart;
    private double _w = 1280;
    private double _h = 90;
    private readonly float[] _bands = new float[4];
    private IAudioEngine? _engine;

    // Drift speeds (px/s) per line - port of the design's apply(i, ..., speed, ...)
    // arguments: bass=18, mid=34, lowMid=12, treble=52.
    private static readonly double[] DriftSpeeds = { 18, 34, 12, 52 };

    // Base opacities per line - match the static XAML defaults (P0=0.85, P1=0.75,
    // P2=0.60, P3=0.50). The beat-pulse only ADDs to these, never subtracts, so
    // even on the off-beat the lines keep their resting glow.
    private static readonly double[] BaseOpacities = { 0.85, 0.75, 0.60, 0.50 };

    public WaveformHeader()
    {
        InitializeComponent();
        for (var i = 0; i < 4; i++)
        {
            _scales[i] = new ScaleTransform(1, 1);
            _translates[i] = new TranslateTransform(0, 0);
        }
        SizeChanged += (_, _) => Rebuild();
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireTransforms();
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    private void WireTransforms()
    {
        for (var i = 0; i < 4; i++)
        {
            var p = this.FindControl<Polyline>($"P{i}");
            _polylines[i] = p;
            if (p is null) continue;
            var group = new TransformGroup();
            group.Children.Add(_scales[i]);
            group.Children.Add(_translates[i]);
            p.RenderTransform = group;
            // RenderTransformOrigin defaults to Center in Avalonia 12 - leaving it
            // alone makes scaleY grow from the line's vertical midline outward,
            // matching the design's CSS `transform-origin: 50% 50%`.
        }
    }

    private void Rebuild()
    {
        _w = Bounds.Width > 0 ? Bounds.Width : 1280;
        _h = Bounds.Height > 0 ? Bounds.Height : 90;

        // Horizontal-only clip: x is bounded to the control's width so the
        // 2-w-wide wave doesn't render into adjacent UI, but y is left wide
        // open so scaleY > 1 (loud passages) can grow past the 90-px control
        // height without being decapitated. A plain ClipToBounds=True would
        // cut top AND bottom - the bug spotted on 2026-05-31.
        Clip = new RectangleGeometry(new Rect(0, -2000, _w, 4000));
        // Periods are INTEGER counts so the wave is exactly periodic with period _w
        // (y(x) = y(x + _w)). That's what makes the drift wrap seamless when the
        // animation does Drift = -((t-speed) % _w) - the right half of the wave
        // slides into the visible region indistinguishable from the left half.
        // Original design freqs (2.0 / 3.1 / 1.6 / 4.6) rounded to 2 / 3 / 1 / 5 -
        // still four visually distinct characters: medium / tight / broad / dense.
        Fill(this.FindControl<Polyline>("P0")!, _w, _h, amp: 28, periods: 2, phase: 0.0, yOff: _h * 0.55);
        Fill(this.FindControl<Polyline>("P1")!, _w, _h, amp: 22, periods: 3, phase: 1.2, yOff: _h * 0.50);
        Fill(this.FindControl<Polyline>("P2")!, _w, _h, amp: 34, periods: 1, phase: 2.4, yOff: _h * 0.58);
        Fill(this.FindControl<Polyline>("P3")!, _w, _h, amp: 14, periods: 5, phase: 0.8, yOff: _h * 0.45);
    }

    /// <summary>
    /// Render the wave from x=0 to x=2-w with exactly <paramref name="periods"/>-2
    /// sine cycles. That gives the wave two seamlessly-tileable halves: the right
    /// half is identical in shape to the left half (just offset by w). When the
    /// drift animation reaches -w and wraps back, the visible window slides from
    /// "showing the left half" to "showing the right half" without a visible jump.
    ///
    /// The double-length is the whole trick - with a 1-w wave drifted by up to
    /// 1-w, the right edge of the visible region would be empty after the wave
    /// has slid past. With a 2-w wave drifted modulo w, there's always another
    /// full period of wave ready on the right.
    /// </summary>
    private static void Fill(Polyline p, double w, double h, double amp, int periods, double phase, double yOff)
    {
        const int steps = 160;  // doubled resolution for the doubled length
        var pts = new Points();
        for (var i = 0; i <= steps; i++)
        {
            var x = i / (double)steps * 2.0 * w;
            // 2-periods cycles over 2w -> periods cycles per w -> y(x) = y(x + w)
            var y = yOff + Math.Sin(i / (double)steps * Math.PI * 2 * (2 * periods) + phase) * amp;
            pts.Add(new Point(x, y));
        }
        p.Points = pts;
    }

    /// <summary>
    /// Resolve the audio engine lazily - the static handler on PlayingProperty
    /// fires on every change but we don't want to look up DI on construction
    /// (designer / xaml previewer won't have a built service provider).
    /// </summary>
    private IAudioEngine? GetEngine()
    {
        if (_engine is not null) return _engine;
        try
        {
            _engine = Program.Services?.GetService<IAudioEngine>();
        }
        catch
        {
            _engine = null;
        }
        return _engine;
    }

    private void UpdateAnimation()
    {
        _timer?.Stop();
        _timer = null;

        if (!Playing) return;

        _animStart = DateTime.UtcNow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var engine = GetEngine();
        if (engine is null) return;

        engine.GetFftBands(_bands);

        var t = (DateTime.UtcNow - _animStart).TotalSeconds;

        // Per-band scaleY targets - engine pre-scaled the magnitudes, we clamp to
        // a sane range and add a floor of 0.45 so even silence shows a flat line
        // instead of a flat zero.
        var bassAmp   = 0.45 + Math.Clamp(_bands[0], 0, 1.4);
        var lowMidAmp = 0.45 + Math.Clamp(_bands[1], 0, 1.4);
        var midAmp    = 0.45 + Math.Clamp(_bands[2], 0, 1.4);
        var trebAmp   = 0.45 + Math.Clamp(_bands[3], 0, 1.4);

        // Drift modulo _w, NOT _w/5 like the design. Since each polyline is
        // 2-_w wide with seamless tiling, the visible region always shows a
        // full _w-wide slice of wave even at the wrap point. The visual speed
        // (px/s) is still set by the `speed` argument - only the silent reset
        // period changes (now _w/speed seconds instead of (_w/5)/speed).
        double Drift(double speed) => -((t * speed) % _w);

        // Index -> band mapping follows the design's apply() call order:
        //   apply(0, bassAmp,   18, 0.85)  -> P0 is the BASS line
        //   apply(1, midAmp,    34, 0.75)  -> P1 is the MID line
        //   apply(2, lowMidAmp, 12, 0.60)  -> P2 is the LOWMID line
        //   apply(3, trebAmp,   52, 0.50)  -> P3 is the TREBLE line
        _scales[0].ScaleY = bassAmp;
        _scales[1].ScaleY = midAmp;
        _scales[2].ScaleY = lowMidAmp;
        _scales[3].ScaleY = trebAmp;

        _translates[0].X = Drift(DriftSpeeds[0]);
        _translates[1].X = Drift(DriftSpeeds[1]);
        _translates[2].X = Drift(DriftSpeeds[2]);
        _translates[3].X = Drift(DriftSpeeds[3]);

        // Beat-pulse layer - sharp-attack exponential-decay envelope on each
        // beat lifts the opacity of every line. Independent of FFT: phase is
        // driven purely by elapsed time and the current BPM, so the pulse hits
        // on the metronomic beat even when the bass FFT bin is momentarily
        // quiet (drop-outs, pickup notes). Direct port of the design's
        // `beatEnv = Math.exp(-beatPhase * 4.5)` + `opGlow(base) =
        //  min(1, base + 0.18 * beatEnv * energyF)`.
        var bpm     = BpmHint ?? 120.0;
        var energy  = EnergyHint ?? 6;
        var beatSec = 60.0 / Math.Max(40.0, bpm);
        var energyF = Math.Clamp(energy / 8.0, 0.3, 1.1);

        var beatPhase = (t / beatSec) % 1.0;
        var beatEnv   = Math.Exp(-beatPhase * 4.5);
        var pulseAdd  = 0.18 * beatEnv * energyF;

        for (var i = 0; i < 4; i++)
        {
            var poly = _polylines[i];
            if (poly is null) continue;
            poly.Opacity = Math.Min(1.0, BaseOpacities[i] + pulseAdd);
        }
    }

    static WaveformHeader()
    {
        // React to Playing toggles. Class handler is cheaper than overriding
        // OnPropertyChanged and gives us a typed (control, args) callback.
        PlayingProperty.Changed.AddClassHandler<WaveformHeader>((c, _) => c.UpdateAnimation());
    }
}
