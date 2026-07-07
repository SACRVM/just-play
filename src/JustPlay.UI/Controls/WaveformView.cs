using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// Click-to-seek waveform scrubber — centred vertical bars (SoundCloud / FUVI look), the played portion
/// in the accent colour, the rest dimmed, a playhead at the current position (Chloe 2026-07-07, from the
/// FUVI clan reference). <see cref="Peaks"/> is a normalised 0..1 envelope (see <c>IWaveformService</c>);
/// the control resamples it to its own pixel width, so it's resize-robust and independent of the compute
/// resolution. Click or drag anywhere runs <see cref="SeekCommand"/> with the 0..1 fraction.
///
/// <para>Deliberately reusable: the JUST SPIN decks will want the same control, so it carries no finder-
/// specific logic — just peaks in, progress in, a seek fraction out. Brushes are styled properties so the
/// host themes it with the usual <c>DynamicResource</c> accent keys.</para>
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<float>?>(nameof(Peaks));

    /// <summary>Playhead position, 0..1 — also the boundary between the played (accent) and unplayed bars.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> UnplayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(UnplayedBrush));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayheadBrush));

    /// <summary>Invoked with the clicked/dragged position as a 0..1 <see cref="double"/> — the host binds
    /// this to its "seek to fraction" command.</summary>
    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<WaveformView, ICommand?>(nameof(SeekCommand));

    static WaveformView()
    {
        AffectsRender<WaveformView>(PeaksProperty, ProgressProperty,
            PlayedBrushProperty, UnplayedBrushProperty, PlayheadBrushProperty);
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

    // ── Click / drag to seek ─────────────────────────────────────────────────
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
        var frac = Math.Clamp(e.GetPosition(this).X / w, 0.0, 1.0);
        if (SeekCommand is { } cmd && cmd.CanExecute(frac)) cmd.Execute(frac);
    }

    // ── Draw ─────────────────────────────────────────────────────────────────
    private const double Pitch = 3.0;   // 2 px bar + 1 px gap
    private const double BarWidth = 2.0;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Full-bounds transparent layer FIRST: Avalonia's compositor hit-tests a control against its drawn
        // geometry, not its layout rect — without this, only the painted bars (a central band) catch clicks,
        // so the empty top/bottom aren't seekable (Chloe 2026-07-07). This makes the whole height clickable.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var peaks = Peaks;
        var played = PlayedBrush ?? Brushes.DeepSkyBlue;
        var unplayed = UnplayedBrush ?? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        var centreY = h / 2;
        var maxBar = centreY - 1;             // leave a hairline top/bottom margin
        var bars = Math.Max(1, (int)(w / Pitch));
        var progressX = Math.Clamp(Progress, 0.0, 1.0) * w;

        for (var i = 0; i < bars; i++)
        {
            var amp = SampleAmp(peaks, i, bars);
            var barH = Math.Max(1.0, amp * maxBar);         // 1 px hairline even in silence
            var x = i * Pitch;
            var brush = (x + BarWidth / 2) <= progressX ? played : unplayed;
            ctx.FillRectangle(brush, new Rect(x, centreY - barH, BarWidth, barH * 2));
        }

        // Playhead — only once there's a real track loaded.
        if (peaks is { Count: > 0 })
            ctx.FillRectangle(PlayheadBrush ?? played, new Rect(progressX - 0.75, 0, 1.5, h));
    }

    /// <summary>Peak of the compute-resolution buckets that fall under on-screen bar <paramref name="i"/>
    /// (so more bars than buckets simply repeats, fewer bars averages down) — keeps the shape at any width.</summary>
    private static float SampleAmp(IReadOnlyList<float>? peaks, int i, int bars)
    {
        if (peaks is not { Count: > 0 }) return 0f;
        var start = (int)((long)i * peaks.Count / bars);
        var end = (int)((long)(i + 1) * peaks.Count / bars);
        if (end <= start) end = Math.Min(start + 1, peaks.Count);
        float m = 0f;
        for (var k = start; k < end; k++)
            if (peaks[k] > m) m = peaks[k];
        return m;
    }
}
