using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// Click-to-seek waveform scrubber - centred vertical bars (SoundCloud / FUVI look), the played portion
/// in the accent colour, the rest dimmed, a playhead at the current position (from the
/// FUVI clan reference). <see cref="Peaks"/> is a normalised 0..1 envelope (see <c>IWaveformService</c>);
/// the control resamples it to its own pixel width, so it's resize-robust and independent of the compute
/// resolution. Click or drag anywhere runs <see cref="SeekCommand"/> with the 0..1 fraction.
///
/// <para>Deliberately reusable: the JUST SPIN decks will want the same control, so it carries no finder-
/// specific logic - just peaks in, progress in, a seek fraction out. Brushes are styled properties so the
/// host themes it with the usual <c>DynamicResource</c> accent keys.</para>
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<float>?>(nameof(Peaks));

    /// <summary>Playhead position, 0..1 - also the boundary between the played (accent) and unplayed bars.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> UnplayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(UnplayedBrush));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayheadBrush));

    /// <summary>Invoked with the clicked/dragged position as a 0..1 <see cref="double"/> - the host binds
    /// this to its "seek to fraction" command.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<WaveformView, ICommand?>(nameof(SeekCommand));

    /// <summary>Start of the visible view window as a 0..1 track fraction (zoom support: the control
    /// renders only [ViewStart, ViewEnd] across its width). Defaults 0..1 = whole track, so existing
    /// hosts (finder player bar) are untouched. Progress and seek fractions stay in TRACK space -
    /// the window only changes what is painted where. JUST SPIN's decks share this.</summary>
    public static readonly StyledProperty<double> ViewStartProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(ViewStart), 0.0);

    /// <summary>End of the visible view window as a 0..1 track fraction (see <see cref="ViewStart"/>).</summary>
    public static readonly StyledProperty<double> ViewEndProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(ViewEnd), 1.0);

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, ProgressProperty,
            PlayedBrushProperty, UnplayedBrushProperty, PlayheadBrushProperty,
            ViewStartProperty, ViewEndProperty);
    }

    public WaveformView() => Cursor = new Cursor(StandardCursorType.Hand);

    public IReadOnlyList<float>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? UnplayedBrush
    {
        get => GetValue(UnplayedBrushProperty);
        set => SetValue(UnplayedBrushProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public ICommand? SeekCommand
    {
        get => GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    public double ViewStart
    {
        get => GetValue(ViewStartProperty);
        set => SetValue(ViewStartProperty, value);
    }

    public double ViewEnd
    {
        get => GetValue(ViewEndProperty);
        set => SetValue(ViewEndProperty, value);
    }

    /// <summary>The sanitised view window - degenerate values fall back to the whole track.</summary>
    private (double Start, double Span) View()
    {
        var vs = Math.Clamp(ViewStart, 0.0, 1.0);
        var ve = Math.Clamp(ViewEnd, 0.0, 1.0);
        return ve > vs ? (vs, ve - vs) : (0.0, 1.0);
    }

    // -- Click / drag to seek -------------------------------------------------
    private bool _scrubbing;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _scrubbing = true;
        e.Pointer.Capture(this);
        SeekAt(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scrubbing) SeekAt(e); // drag = live scrub
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing) return;
        _scrubbing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void SeekAt(PointerEventArgs e)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var (vs, span) = View();
        var frac = Math.Clamp(vs + e.GetPosition(this).X / w * span, 0.0, 1.0);
        if (SeekCommand is { } cmd && cmd.CanExecute(frac)) cmd.Execute(frac);
    }

    // -- Draw -----------------------------------------------------------------
    private const double Pitch = 3.0;   // 2 px bar + 1 px gap
    private const double BarWidth = 2.0;

    // Cached bar heights. The SampleAmp scan is the per-frame cost and depends only on the peaks, the
    // width (bar count) and the view window - NOT on Progress. But ProgressProperty is in AffectsRender,
    // so Render re-runs on every playhead move; without this cache each frame re-scanned all peaks across
    // ~300 bars x the stacked views (the exact cost that made JUST BOOTLEG's playhead judder). Progress
    // now only re-picks the played/unplayed brush per cached bar and moves the 1.5 px line.
    private double[]? _barAmps;
    private object? _cachePeaks;
    private double _cacheW = -1, _cacheVs, _cacheSpan;

    private double[] EnsureBarCache(double w, double vs, double span)
    {
        var peaks = Peaks;
        if (_barAmps is { } cached && ReferenceEquals(peaks, _cachePeaks)
            && w == _cacheW && vs == _cacheVs && span == _cacheSpan)
            return cached;

        var bars = Math.Max(1, (int)(w / Pitch));
        var amps = new double[bars];
        for (var i = 0; i < bars; i++)
            amps[i] = SampleAmp(peaks, vs + (double)i / bars * span, vs + (double)(i + 1) / bars * span);

        _cachePeaks = peaks; _cacheW = w; _cacheVs = vs; _cacheSpan = span; _barAmps = amps;
        return amps;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Full-bounds transparent layer FIRST: Avalonia's compositor hit-tests a control against its drawn
        // geometry, not its layout rect - without this, only the painted bars (a central band) catch clicks,
        // so the empty top/bottom aren't seekable. This makes the whole height clickable.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var played = PlayedBrush ?? Brushes.DeepSkyBlue;
        var unplayed = UnplayedBrush ?? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        var centreY = h / 2;
        var maxBar = centreY - 1;             // leave a hairline top/bottom margin
        var (vs, span) = View();
        var amps = EnsureBarCache(w, vs, span);
        var bars = amps.Length;
        var progressX = (Math.Clamp(Progress, 0.0, 1.0) - vs) / span * w;

        for (var i = 0; i < bars; i++)
        {
            var barH = Math.Max(1.0, amps[i] * maxBar);     // 1 px hairline even in silence
            var x = i * Pitch;
            var brush = (x + BarWidth / 2) <= progressX ? played : unplayed;
            ctx.FillRectangle(brush, new Rect(x, centreY - barH, BarWidth, barH * 2));
        }

        // Playhead - only once there's a real track loaded AND it is inside the view window.
        if (Peaks is { Count: > 0 } && progressX >= -1 && progressX <= w + 1)
            ctx.FillRectangle(PlayheadBrush ?? played, new Rect(progressX - 0.75, 0, 1.5, h));
    }

    /// <summary>Peak of the compute-resolution buckets between the track fractions
    /// <paramref name="fracStart"/>..<paramref name="fracEnd"/> - the on-screen bar's slice of the
    /// (possibly zoomed) view window. More bars than buckets repeats, fewer averages down.</summary>
    private static float SampleAmp(IReadOnlyList<float>? peaks, double fracStart, double fracEnd)
    {
        if (peaks is not { Count: > 0 }) return 0f;
        var start = Math.Clamp((int)(fracStart * peaks.Count), 0, peaks.Count - 1);
        var end = Math.Clamp((int)(fracEnd * peaks.Count), 0, peaks.Count);
        if (end <= start) end = Math.Min(start + 1, peaks.Count);
        float m = 0f;
        for (var k = start; k < end; k++)
            if (peaks[k] > m) m = peaks[k];
        return m;
    }
}
