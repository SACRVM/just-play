using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JustPlay.Analysis;

namespace JustPlay.UI.Controls;

/// <summary>
/// Live tonal-balance analyzer: draws the DRY (pre-bus) and WET (post-bus) spectra of the playing
/// track against the golden pink-slope target curve, with the mud/fatigue zones shaded — the in-app
/// home of the offline <c>spectrum</c> CLI tool (v0.4). Fed each render frame by
/// <see cref="SetData"/> with per-band POWER from <c>IAudioEngine.GetSpectrum</c> (60 × 1/6-octave
/// bands, matching <see cref="SpectralProfile"/>); converts to dB, level-anchors DRY+WET to the same
/// reference so the bus's effect is visible, and anchors the target to its own mid-band.
/// </summary>
public sealed class SpectrumView : Control
{
    private const int Bands = SpectralProfile.BandCount; // 60

    private const double FMin = 20.0, FMax = 20000.0;
    // +24 dB top so a bass-heavy track's sub/bass (often well above +12 relative to the mids) isn't
    // clipped at the ceiling; −36 floor covers rolled-off air. Grid labels land every 12 dB.
    private const double DbMin = -36.0, DbMax = 24.0;
    private const double AnchorLo = 200.0, AnchorHi = 2000.0;

    // Smoothed per-band POWER (linear). EMA so the live curve isn't jittery.
    private readonly double[] _dry = new double[Bands];
    private readonly double[] _wet = new double[Bands];
    private bool _hasData;

    // Curve visibility — toggled by clicking the DRY / WET legend entries.
    private bool _showDry = true, _showWet = true;
    public bool ShowDry { get => _showDry; set { _showDry = value; InvalidateVisual(); } }
    public bool ShowWet { get => _showWet; set { _showWet = value; InvalidateVisual(); } }

    private static readonly double[] Centre = BuildCentres();

    private static double[] BuildCentres()
    {
        var c = new double[Bands];
        for (int n = 0; n < Bands; n++) c[n] = 20.0 * Math.Pow(2.0, (n + 0.5) / 6.0);
        return c;
    }

    /// <summary>Feed one frame of per-band power (dry/wet). EMA-smoothed; triggers a redraw.</summary>
    public void SetData(ReadOnlySpan<float> dry, ReadOnlySpan<float> wet)
    {
        // Temporal EMA toward the new frame. Lower = smoother (the live single-frame FFT is jittery). The
        // smooth-spline curve below de-jags it visually; this damps the bounce. ~0.33 ≈ ~40 ms at 60 fps —
        // clearly calmer than the snappy 0.6, still tracks the track.
        const double a = 0.33;
        for (int n = 0; n < Bands; n++)
        {
            double d = n < dry.Length ? Math.Max(0.0, dry[n]) : 0.0;
            double w = n < wet.Length ? Math.Max(0.0, wet[n]) : 0.0;
            _dry[n] += (d - _dry[n]) * a;
            _wet[n] += (w - _wet[n]) * a;
        }
        _hasData = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 2 || b.Height <= 2) return;

        // ── plot rect (margins for labels) ──
        double mL = 40, mR = 12, mT = 24, mB = 26; // taller top margin = feather room for the spill-over fade
        double pX = mL, pY = mT, pW = b.Width - mL - mR, pH = b.Height - mT - mB;
        if (pW <= 4 || pH <= 4) return;
        double pX2 = pX + pW, pY2 = pY + pH;

        var grid = new SolidColorBrush(Color.FromArgb(0x33, 0xC0, 0xC4, 0xE0));
        var gridPen = new Pen(grid, 1);
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x88, 0x88, 0xA8));
        var face = new Typeface("Inter");

        // ── zone shading (mud warm, fatigue red) ──
        FillBand(ctx, pX, pY, pW, pH, SpectralTarget.MudLo, SpectralTarget.MudHi, Color.FromArgb(0x1C, 0xC8, 0x7C, 0x18));
        FillBand(ctx, pX, pY, pW, pH, SpectralTarget.FatigueLo, SpectralTarget.FatigueHi, Color.FromArgb(0x1C, 0xD0, 0x40, 0x30));

        // ── vertical freq grid + labels ──
        (double f, string s)[] fg =
        {
            (20, "20"), (50, "50"), (100, "100"), (200, "200"), (500, "500"),
            (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k"), (20000, "20k"),
        };
        foreach (var (f, s) in fg)
        {
            double gx = FToX(f, pX, pW);
            ctx.DrawLine(gridPen, new Point(gx, pY), new Point(gx, pY2));
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9.5, labelBrush);
            ctx.DrawText(ft, new Point(gx - ft.Width / 2, pY2 + 4));
        }

        // ── horizontal dB grid + labels (every 12 dB) ──
        for (double db = DbMin; db <= DbMax + 0.1; db += 12.0)
        {
            double gy = DbToY(db, pY, pH);
            ctx.DrawLine(gridPen, new Point(pX, gy), new Point(pX2, gy));
            string s = db > 0 ? $"+{db:0}" : $"{db:0}";
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9.5, labelBrush);
            ctx.DrawText(ft, new Point(pX - ft.Width - 5, gy - ft.Height / 2));
        }

        // ── plot border ──
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x3A, 0x3A, 0x48)), 1),
            new Rect(pX, pY, pW, pH));

        // Axis caption: the scale is RELATIVE to each curve's own mid-band (200 Hz–2 kHz = 0 dB), a
        // tonal-balance view — NOT absolute dBFS. Make that explicit so "+12" isn't read as output level.
        var capBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x88, 0x88, 0xA8));
        var cap = new FormattedText("dB · rel. mid (200 Hz–2 kHz)", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, 9.0, capBrush);
        ctx.DrawText(cap, new Point(pX + 5, pY + 3));

        // ── target curve (own mid-band anchor) ──
        double tgtAnchor = TargetMidMean();
        var targetPen = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x90)), 1.4)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
        };
        DrawTarget(ctx, targetPen, pX, pY, pW, pH, tgtAnchor);

        if (!_hasData) return;

        // Anchor EACH curve to its OWN mid-band (200 Hz–2 kHz) mean — a tonal-balance ("shape") view,
        // like the spectrum CLI's default. This matters because DRY (our windowed FFT of the pre-bus
        // snapshot) and WET (BASS's post-bus FFT) come from DIFFERENT FFT pipelines with different
        // absolute scaling: a SHARED anchor pushed WET to a meaningless vertical offset — often off the
        // chart, so it was "only sometimes visible". Self-anchored, the two overlap when the bus is
        // neutral (WET sits ON the DRY line) and separate exactly where the bus reshapes the tone.
        var dryDb = ToDb(_dry);
        var wetDb = ToDb(_wet);
        double dryAnchor = MidMean(dryDb);
        double wetAnchor = MidMean(wetDb);

        // Let the curves spill OVER the top plot border (over-ceiling peaks) for a lively "no-limit" look —
        // but instead of a hard chop at the edge, FADE them out as they rise toward the title: an opacity
        // mask that's fully opaque over the plot and ramps to transparent at the control top. Peaks dissolve
        // over the edge (no visible cut, never painting over the title — they're transparent long before it).
        // PushClip still hard-bounds width + bottom so nothing spills onto the freq/dB labels.
        double fadeEnd = pY2 > 0 ? pY / pY2 : 0.0; // solid by the plot ceiling, feathers to nothing above it
        var spillFade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),     // top edge → invisible
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), fadeEnd), // plot ceiling → solid
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1.0),
            },
        };

        using (ctx.PushClip(new Rect(pX, 0, pW, pY2)))
        using (ctx.PushOpacityMask(spillFade, new Rect(pX, 0, pW, pY2)))
        {
            if (_showDry && !double.IsNaN(dryAnchor))
            {
                var dryPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xB8, 0xB8, 0xC8)), 1.6)
                { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                DrawCurve(ctx, dryPen, dryDb, dryAnchor, pX, pY, pW, pH);
            }

            // WET drawn LAST so it sits ON TOP of DRY (the accent line is what you see when they coincide).
            if (_showWet && !double.IsNaN(wetAnchor))
            {
                var wetBrush = (this.TryFindResource("AccentBBrush", out var r) && r is IBrush ab)
                    ? ab : new SolidColorBrush(Color.FromArgb(0xFF, 0x18, 0xE8, 0xC0));
                var wetPen = new Pen(wetBrush, 2.2) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                DrawCurve(ctx, wetPen, wetDb, wetAnchor, pX, pY, pW, pH);
            }
        }
    }

    // ── helpers ──

    private static double[] ToDb(double[] power)
    {
        var db = new double[Bands];
        for (int n = 0; n < Bands; n++)
            db[n] = power[n] > 1e-12 ? 10.0 * Math.Log10(power[n]) : -120.0;
        return db;
    }

    private static double MidMean(double[] db)
    {
        double sum = 0; int k = 0;
        for (int n = 0; n < Bands; n++)
            if (Centre[n] >= AnchorLo && Centre[n] <= AnchorHi && db[n] > -100.0) { sum += db[n]; k++; }
        return k > 0 ? sum / k : double.NaN;
    }

    private static double TargetMidMean()
    {
        double sum = 0; int k = 0;
        for (int n = 0; n < Bands; n++)
            if (Centre[n] >= AnchorLo && Centre[n] <= AnchorHi) { sum += SpectralTarget.DbAt(Centre[n]); k++; }
        return k > 0 ? sum / k : 0.0;
    }

    private void DrawCurve(DrawingContext ctx, Pen pen, double[] db, double anchor,
        double pX, double pY, double pW, double pH)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            var run = new List<Point>(Bands);
            for (int n = 0; n < Bands; n++)
            {
                bool valid = db[n] > -100.0; // only the FFT floor breaks the line; over-ceiling peaks draw on
                double v = db[n] - anchor;
                if (valid) run.Add(new Point(FToX(Centre[n], pX, pW), DbToY(v, pY, pH)));
                else { SmoothThrough(c, run); run.Clear(); } // gap (band at floor) → break the line
            }
            SmoothThrough(c, run);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>Draw a smooth Catmull-Rom curve through the points (emitted as cubic Béziers) so the
    /// 60-band live spectrum reads as a flowing line, not angular segments.</summary>
    private static void SmoothThrough(StreamGeometryContext c, List<Point> pts)
    {
        if (pts.Count == 0) return;
        c.BeginFigure(pts[0], false);
        if (pts.Count == 1) return;
        if (pts.Count == 2) { c.LineTo(pts[1]); return; }
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            c.CubicBezierTo(c1, c2, p2);
        }
    }

    private void DrawTarget(DrawingContext ctx, Pen pen, double pX, double pY, double pW, double pH, double anchor)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int n = 0; n < Bands; n++)
            {
                double v = SpectralTarget.DbAt(Centre[n]) - anchor;
                var p = new Point(FToX(Centre[n], pX, pW), DbToY(v, pY, pH));
                if (n == 0) c.BeginFigure(p, false);
                else c.LineTo(p);
            }
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static void FillBand(DrawingContext ctx, double pX, double pY, double pW, double pH,
        double loHz, double hiHz, Color color)
    {
        double x0 = FToX(loHz, pX, pW), x1 = FToX(hiHz, pX, pW);
        ctx.FillRectangle(new SolidColorBrush(color), new Rect(x0, pY, Math.Max(1, x1 - x0), pH));
    }

    private static double FToX(double f, double pX, double pW)
    {
        double t = Math.Log10(Math.Clamp(f, FMin, FMax) / FMin) / Math.Log10(FMax / FMin);
        return pX + Math.Clamp(t, 0, 1) * pW;
    }

    private static double DbToY(double db, double pY, double pH)
    {
        // NOT clamped — a band hotter than the ceiling maps ABOVE the plot border ("über den Tellerrand").
        // The curve draw is clipped to a generous rect (Render) so it spills over the TOP edge — lively —
        // without painting over the axis labels or the chrome.
        double t = (DbMax - db) / (DbMax - DbMin);
        return pY + t * pH;
    }
}
