using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JustPlay.UI.Controls;

/// <summary>
/// One-channel output PEAK level bar — the SHARED meter for the whole J.U.S.T. suite. Compose as many
/// as you need: JUST PLAY stacks two vertical ones (L + R) in the analyzer's OUT column; JUST STREAM
/// drops a horizontal one into each of its L/R rows. Fed each render frame via <see cref="Push"/>
/// (raw peak + frame dt): the control OWNS the ballistics (fast attack / slow release, dt-rescaled
/// so the glide is identical at any refresh rate — the SAME K values as STREAM's old meter) and a
/// peak-hold marker. The marker is a FLAT 2 px line with square ends (white with headroom, red within
/// 1 dB of 0 dBFS). dBFS scale −60..0; green → amber → red toward 0. <see cref="Orientation"/>
/// = bar grows bottom→top (Vertical) or left→right (Horizontal). No labels — the host adds them.
///
/// <para><b>The fed level is NOT clamped to 1.0</b> (2026-07-13). Float buses can and do go past
/// 0 dBFS, and that overshoot is precisely the news — clamping it at the source made "over" invisible
/// by construction. Anything that reaches 0 dBFS lights the <b>OVER</b> cap at the top of the track,
/// which holds for <see cref="OverHoldSeconds"/> s after the last one. The bar itself still pins at
/// 0 dBFS: the scale stays −60..0 so the useful range isn't squeezed to make room for headroom nobody
/// reads in numbers.</para>
///
/// <para><b>Optional pre-limiter ghost</b> (<see cref="PushInput"/>, cyan hairline): on a master meter
/// the bar is measured AFTER the limiter, so by construction it can never go over — holding it under is
/// the limiter's entire job — and it therefore cannot answer "am I driving this too hard?". The ghost
/// can: it is the peak the limiter SEES. Ghost above the bar = the limiter is eating the difference,
/// and the GR meter next to it says how much. Hosts that never call PushInput never see it.</para>
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<LevelMeter, Orientation>(nameof(Orientation), Orientation.Vertical);

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private const double MinDb = -60.0;
    private const double AttackK = 0.40, ReleaseK = 0.12, RefStep = 0.016; // STREAM's meter ballistics
    private const double PeakHoldSeconds = 1.2, PeakFallDbPerSec = 36.0;
    private const double MaxThickness = 20.0, Radius = 4.0;

    /// <summary>How long the OVER cap stays lit after the last sample that touched 0 dBFS. This is a
    /// live-preview hold, NOT a mastering latch: the question a DJ asks the meter is "am I hot right
    /// NOW", and a persistent overdrive simply keeps re-arming it.</summary>
    private const double OverHoldSeconds = 3.0;

    /// <summary>−0.1 dBFS counts as over. A 16-bit round-trip can't resolve finer, and a meter that
    /// insists on exactly 0.0 would sit there silent while the signal is audibly against the ceiling.</summary>
    private const double OverDb = -0.1;

    private double _lvl;             // smoothed linear peak — NOT capped at 1
    private double _peakDb = MinDb;
    private double _hold;            // peak-hold seconds remaining
    private double _over;            // OVER cap: seconds left to show

    private bool _inputFed;          // has this meter ever been given a pre-limiter peak?
    private double _inputDb = MinDb;
    private double _inputHold;

    static LevelMeter() => AffectsRender<LevelMeter>(OrientationProperty);

    /// <summary>Feed one frame: raw peak (linear; >1 is allowed and means OVER) + seconds since the
    /// last frame. Push owns the clock — it is the only method that ages the holds.</summary>
    public void Push(double level, double dt)
    {
        double steps = dt > 0 ? dt / RefStep : 1.0;
        double aK = 1.0 - Math.Pow(1.0 - AttackK, steps);
        double rK = 1.0 - Math.Pow(1.0 - ReleaseK, steps);
        double tgt = Math.Max(0, level);          // no upper clamp — see the class remarks
        _lvl += (tgt - _lvl) * (tgt > _lvl ? aK : rK);

        double db = ToDb(_lvl);
        if (db >= _peakDb) { _peakDb = db; _hold = PeakHoldSeconds; }
        else if (_hold > 0) _hold -= dt;
        else _peakDb = Math.Max(db, _peakDb - PeakFallDbPerSec * dt);

        // Age the holds owned by the other feed here too, so a meter that gets no PushInput still decays.
        if (_inputHold > 0) _inputHold -= dt;
        else _inputDb = Math.Max(MinDb, _inputDb - PeakFallDbPerSec * dt);

        if (ToDb(tgt) >= OverDb) _over = OverHoldSeconds;
        else if (_over > 0) _over -= dt;

        InvalidateVisual();
    }

    /// <summary>Optional: the peak BEFORE the limiter (linear; >1 expected when driving it). Drawn as a
    /// cyan hairline over the bar. Call it every frame you call <see cref="Push"/>, or never.</summary>
    public void PushInput(double peak, double dt)
    {
        _inputFed = true;
        double db = ToDb(Math.Max(0, peak));
        if (db >= _inputDb) { _inputDb = db; _inputHold = PeakHoldSeconds; }
        if (db >= OverDb) _over = OverHoldSeconds;   // Push ages it; we only ever arm it
        InvalidateVisual();
    }

    private static double ToDb(double lin) => lin <= 1e-5 ? MinDb : Math.Max(MinDb, 20.0 * Math.Log10(lin));
    private static double Frac(double db) => Math.Clamp((db - MinDb) / (0.0 - MinDb), 0, 1);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width < 4 || b.Height < 4) return;
        bool vert = Orientation == Orientation.Vertical;

        // Track: full length on the main axis, bar thickness (≤20, centred) on the cross axis.
        double thick = Math.Min(MaxThickness, vert ? b.Width : b.Height);
        double x = vert ? (b.Width - thick) / 2 : 0;
        double y = vert ? 0 : (b.Height - thick) / 2;
        double w = vert ? thick : b.Width;
        double h = vert ? b.Height : thick;
        var track = new RoundedRect(new Rect(x, y, w, h), Radius);

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x10, 0x16)), null, track);

        double frac = Frac(ToDb(_lvl));
        if (frac > 0.001)
        {
            // Clip to the filled portion, then paint the full-track green→amber→red gradient so the
            // colour reflects the dB level (not the fill size).
            Rect fill = vert
                ? new Rect(x, y + h - frac * h, w, frac * h)  // grow up
                : new Rect(x, y, frac * w, h);                 // grow right
            using (ctx.PushClip(fill))
                ctx.DrawRectangle(ZoneGradient(vert), null, track);
        }

        // Pre-limiter GHOST (master meters only): what the limiter SEES. Cyan so it can't be mistaken
        // for the white output peak-hold — they are different signals at different points in the chain.
        if (_inputFed && _inputDb > MinDb + 0.5)
        {
            double gf = Frac(_inputDb);
            var gb = new SolidColorBrush(Color.FromArgb(0xCC, 0x32, 0xC8, 0xE8));   // AccentA
            if (vert) ctx.FillRectangle(gb, new Rect(x, y + h - gf * h - 0.5, w, 1));
            else      ctx.FillRectangle(gb, new Rect(x + gf * w - 0.5, y, 1, h));
        }

        // Peak-hold: FLAT 2 px line, square ends (FillRectangle, not a Pen → can't get rounded caps).
        if (_peakDb > MinDb + 0.5)
        {
            double pf = Frac(_peakDb);
            var pc = _peakDb >= -1.0 ? Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
            var pb = new SolidColorBrush(pc);
            if (vert) ctx.FillRectangle(pb, new Rect(x, y + h - pf * h - 1, w, 2));
            else      ctx.FillRectangle(pb, new Rect(x + pf * w - 1, y, 2, h));
        }

        // OVER — 0 dBFS was touched. A solid red cap at the 0 dB END of the track, drawn last so it
        // wins over the bar and both markers. This is the only thing on the meter that means "stop".
        if (_over > 0)
        {
            var ob = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            if (vert) ctx.FillRectangle(ob, new Rect(x, y, w, 3));
            else      ctx.FillRectangle(ob, new Rect(x + w - 3, y, 3, h));
        }
    }

    // Green (low) → amber → red (high = 0 dBFS), along the bar's main axis.
    private static LinearGradientBrush ZoneGradient(bool vert)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(vert ? 0.5 : 0.0, vert ? 1.0 : 0.5, RelativeUnit.Relative), // low end
            EndPoint   = new RelativePoint(vert ? 0.5 : 1.0, vert ? 0.0 : 0.5, RelativeUnit.Relative), // 0 dBFS end
        };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0xD4, 0x6A), 0.0));   // green
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0xD4, 0x6A), 0.78));  // green to ~−13 dB
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0xC0, 0x20), 0.86));  // amber ~−8 dB
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x3B, 0x30), 1.0));   // red at 0 dB
        return g;
    }
}
