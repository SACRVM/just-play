using JustPlay.Analysis;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay spectrum &lt;audiofile&gt; [options]</c>
///
/// <para>Offline DSP tuning tool — Phase 1 of the spectral diagram feature.
/// Decodes the track, computes its long-term-average spectrum (LTAS) before (DRY) and after
/// (WET) running the JustPlay bus chain, compares both against a pink-noise reference target
/// curve, and saves a before/after comparison plot as a PNG file.</para>
///
/// <para>
/// Usage:<br/>
///   <c>justplay spectrum &lt;audiofile&gt; [--out path.png]</c><br/>
///   <c>  [--eq-low x] [--eq-mid x] [--eq-high x]</c><br/>
///   <c>  [--tilt x] [--punch x]</c><br/>
///   <c>  [--limiter off|soft|club|loud]</c>
/// </para>
///
/// <para>When DSP flags are omitted the chain defaults to fully neutral / bypass, so the DRY
/// and WET curves are identical unless at least one DSP parameter is passed.
/// // TODO: unify limiter MODE→param mapping with MainWindowViewModel mapping.</para>
/// </summary>
internal static class SpectrumCommand
{
    // ── Mode → parameter mappings ─────────────────────────────────────────────
    // Mirrors the mapping in MainWindowViewModel (hard-coded here for CLI independence).
    // TODO: unify with VM mapping — both should read from a shared constants class.

    private static (bool en, double drive, double ceiling) LimiterParams(string mode) => mode.ToLowerInvariant() switch
    {
        "soft"  => (true,  0.0, -1.0),
        "club"  => (true,  3.0, -1.0),
        "loud"  => (true,  6.0, -0.1),
        _       => (false, 0.0, -1.0), // "off" or unknown
    };

    // ── Entry point ───────────────────────────────────────────────────────────

    public static int Run(string filePath, string? outPath, SpectrumOptions opts)
    {
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[spectrum] ERROR: file not found: {filePath}");
            return 1;
        }

        outPath ??= Path.Combine(
            Path.GetDirectoryName(filePath) ?? ".",
            Path.GetFileNameWithoutExtension(filePath) + ".spectrum.png");

        Console.WriteLine($"[spectrum] Input  : {filePath}");
        Console.WriteLine($"[spectrum] Output : {outPath}");

        // ── Compose engine (BASS needed for decode) ──────────────────────────
        using var composer = EngineComposer.Build();

        // ── 1. Decode mono at 44100 Hz ───────────────────────────────────────
        Console.Write("[spectrum] Decoding...");
        float[] mono;
        try
        {
            var audio = composer.Decoder.DecodeMono(filePath, 44100);
            mono = audio.Samples;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[spectrum] ERROR decoding: {ex.Message}");
            return 1;
        }
        Console.WriteLine($" {mono.Length / 44100.0:F1} s  ({mono.Length:N0} samples)");

        // ── 2. Compute DRY LTAS ───────────────────────────────────────────────
        Console.Write("[spectrum] DRY spectrum...");
        var profiler = new SpectralProfile();
        var dry      = profiler.Compute(mono, 44100);
        Console.WriteLine(" done");

        // ── 3. Build interleaved-stereo buffer and run the bus chain ──────────
        Console.Write("[spectrum] Processing WET...");

        var (limEn, limDrive, limCeil) = LimiterParams(opts.LimiterMode);

        var chain = new BusDspChain(
            eqLow:               opts.EqLow,
            eqMid:               opts.EqMid,
            eqHigh:              opts.EqHigh,
            tiltStrength:        opts.TiltStrength,
            transientPunch:      opts.TransientPunch,
            limiterEnabled:      limEn,
            limiterDriveDb:      limDrive,
            limiterCeilingDbTp:  limCeil,
            sampleRate:          44100);

        // Expand mono → interleaved stereo (L = R = mono sample)
        int     frames = mono.Length;
        float[] stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        { stereo[i * 2] = mono[i]; stereo[i * 2 + 1] = mono[i]; }

        // Process in chunks to avoid Span<T> stack limits
        const int ChunkFrames = 4096;
        for (int off = 0; off < frames; off += ChunkFrames)
        {
            int n = Math.Min(ChunkFrames, frames - off) * 2;
            chain.ProcessInterleavedStereo(stereo.AsSpan(off * 2, n));
        }

        // De-interleave left channel
        float[] wet = new float[frames];
        for (int i = 0; i < frames; i++) wet[i] = stereo[i * 2];

        Console.WriteLine(" done");

        // ── 4. Compute WET LTAS ───────────────────────────────────────────────
        Console.Write("[spectrum] WET spectrum...");
        var wetSpec = profiler.Compute(wet, 44100);
        Console.WriteLine(" done");

        // ── 5. Normalize for tonal-balance comparison ─────────────────────────
        // Anchor: subtract the mean dB of DRY in 200 Hz–2 kHz from both curves so
        // the limiter's absolute loudness lift doesn't dominate the visual.
        // The target curve is independently anchored to its own 200 Hz–2 kHz mean.
        double dryAnchor    = BandMean(dry,     200.0, 2000.0);
        double wetAnchorRaw  = BandMean(wetSpec, 200.0, 2000.0);
        double targetAnchor = TargetMeanInRange(200.0, 2000.0);

        // "shape" (default): anchor each curve to its OWN mid so only tonal balance shows.
        // "level": both share DRY's anchor so the limiter's makeup gain reads as a vertical lift.
        bool   shapeMode = !string.Equals(opts.Mode, "level", StringComparison.OrdinalIgnoreCase);
        double wetAnchor = shapeMode ? wetAnchorRaw : dryAnchor;
        double levelLift = wetAnchorRaw - dryAnchor;   // overall loudness change the chain applied

        var dryNorm    = Shift(dry,     -dryAnchor);
        var wetNorm    = Shift(wetSpec, -wetAnchor);
        var targetCurve = BuildTargetBands();
        var targetNorm  = Shift(targetCurve, -targetAnchor);

        // ── 6. Per-zone deltas (WET − DRY, normalized) ───────────────────────
        double[] zoneDeltas = ComputeZoneDeltas(dryNorm, wetNorm);
        string[] zoneNames  =
        [
            "Sub   (20–60 Hz)",  "Bass  (60–250 Hz)",
            "Mud   (250–500 Hz)","Mid   (500–2 kHz)",
            "Fatigue (2–5 kHz)", "Air   (8–16 kHz)",
        ];

        // ── 7. Render and save PNG ────────────────────────────────────────────
        Console.Write("[spectrum] Rendering PNG...");
        var png = Render(Path.GetFileName(filePath), dryNorm, wetNorm, targetNorm);
        try
        {
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            png.Save(outPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[spectrum] ERROR saving PNG: {ex.Message}");
            return 1;
        }
        Console.WriteLine(" done");

        // ── 8. Summary ────────────────────────────────────────────────────────
        // DRY vs TARGET: how far the raw track's tonal balance sits from the golden curve
        // (the actionable number — where the track is hot vs where it has air). Computed on the
        // shape-normalized curves so it's a fair shape comparison regardless of level.
        double[] targetDev = ComputeZoneDeltas(targetNorm, dryNorm); // dry − target

        Console.WriteLine();
        Console.WriteLine($"  PNG: {outPath}");
        Console.WriteLine();
        Console.WriteLine($"  Mode: {(shapeMode ? "shape (tonal balance; overall level removed)" : "level (loudness lift visible)")}");
        if (shapeMode)
            Console.WriteLine($"  Overall level the chain applied (mid-band): {Sgn(levelLift)} dB");
        Console.WriteLine();
        Console.WriteLine("  DRY vs TARGET  (raw track vs the golden curve; +ve = hotter than ideal, -ve = air/room):");
        for (int z = 0; z < zoneNames.Length; z++)
            Console.WriteLine($"    {zoneNames[z],-24}  {Sgn(targetDev[z])} dB");
        Console.WriteLine();
        Console.WriteLine(shapeMode
            ? "  WET − DRY  (what the chain changed, level-matched; +ve = relatively brighter):"
            : "  WET − DRY  (+ve = WET is louder/brighter there):");
        for (int z = 0; z < zoneNames.Length; z++)
            Console.WriteLine($"    {zoneNames[z],-24}  {Sgn(zoneDeltas[z])} dB");

        return 0;
    }

    // ── Analysis helpers ──────────────────────────────────────────────────────

    /// <summary>Signed, 1-decimal dB string with a clean +/-/zero (avoids the "-+0.0" artifact of a
    /// two-section custom numeric format on values that round to zero).</summary>
    private static string Sgn(double v)
    {
        double r = Math.Round(v, 1);
        if (r == 0.0) r = 0.0; // normalise -0.0 → 0.0
        return r > 0 ? $"+{r:0.0}" : r < 0 ? $"{r:0.0}" : "  0.0";
    }

    private static double BandMean((double FreqHz, double Db)[] spec, double loHz, double hiHz)
    {
        double sum = 0.0; int n = 0;
        foreach (var (f, db) in spec)
            if (f >= loHz && f <= hiHz && db > -100.0) { sum += db; n++; }
        return n > 0 ? sum / n : 0.0;
    }

    private static double TargetMeanInRange(double loHz, double hiHz)
    {
        double sum = 0.0; int n = 0;
        for (int i = 0; i < SpectralProfile.BandCount; i++)
        {
            double f = 20.0 * Math.Pow(2.0, (i + 0.5) / 6.0);
            if (f >= loHz && f <= hiHz) { sum += SpectralTarget.DbAt(f); n++; }
        }
        return n > 0 ? sum / n : 0.0;
    }

    private static (double FreqHz, double Db)[] Shift((double FreqHz, double Db)[] spec, double delta)
    {
        var r = new (double FreqHz, double Db)[spec.Length];
        for (int i = 0; i < spec.Length; i++)
            r[i] = (spec[i].FreqHz, spec[i].Db + delta);
        return r;
    }

    private static (double FreqHz, double Db)[] BuildTargetBands()
    {
        var r = new (double FreqHz, double Db)[SpectralProfile.BandCount];
        for (int i = 0; i < SpectralProfile.BandCount; i++)
        {
            double f = 20.0 * Math.Pow(2.0, (i + 0.5) / 6.0);
            r[i] = (f, SpectralTarget.DbAt(f));
        }
        return r;
    }

    private static double[] ComputeZoneDeltas(
        (double FreqHz, double Db)[] dryN,
        (double FreqHz, double Db)[] wetN)
    {
        (double lo, double hi)[] zones =
        [
            (SpectralTarget.SubLo,     SpectralTarget.SubHi),
            (SpectralTarget.BassLo,    SpectralTarget.BassHi),
            (SpectralTarget.MudLo,     SpectralTarget.MudHi),
            (SpectralTarget.MidLo,     SpectralTarget.MidHi),
            (SpectralTarget.FatigueLo, SpectralTarget.FatigueHi),
            (SpectralTarget.AirLo,     SpectralTarget.AirHi),
        ];
        var deltas = new double[zones.Length];
        for (int z = 0; z < zones.Length; z++)
            deltas[z] = BandMean(wetN, zones[z].lo, zones[z].hi)
                      - BandMean(dryN, zones[z].lo, zones[z].hi);
        return deltas;
    }

    // ── Renderer ──────────────────────────────────────────────────────────────

    private const int CanvasW  = 1100;
    private const int CanvasH  = 600;
    private const int MarginL  = 65;   // space for dB labels
    private const int MarginR  = 20;
    private const int MarginT  = 45;   // space for title
    private const int MarginB  = 52;   // space for freq labels + Hz label
    private const double DbMin = -36.0;
    private const double DbMax =  36.0;  // was +18 — dark-techno sub sits far above the mid anchor (same field report as SpectrumView)
    private const double FMin  =  20.0;
    private const double FMax  = 20000.0;

    private static PngWriter Render(
        string title,
        (double FreqHz, double Db)[] dry,
        (double FreqHz, double Db)[] wet,
        (double FreqHz, double Db)[] target)
    {
        int pX  = MarginL, pY  = MarginT;
        int pW  = CanvasW - MarginL - MarginR;
        int pH  = CanvasH - MarginT  - MarginB;
        int pX2 = pX + pW;
        int pY2 = pY + pH;

        var p = new PngWriter(CanvasW, CanvasH);
        p.Clear(0x16161AFF); // very dark background

        // ── Zone shading ──────────────────────────────────────────────────────
        // Mud zone 250–500 Hz (warm amber tint)
        ShadeZone(p, pX, pY, pW, pH, 250.0,  500.0,  0xC87C0018);
        // Fatigue zone 2–5 kHz (red tint)
        ShadeZone(p, pX, pY, pW, pH, 2000.0, 5000.0, 0xC8282018);

        // ── Tolerance band around target (subtle grey fill) ───────────────────
        DrawToleranceBand(p, target, pX, pY, pW, pH);

        // ── Vertical grid lines + frequency labels ────────────────────────────
        (double f, string label)[] freqGrid =
        [
            (20, "20"), (50, "50"), (100, "100"), (200, "200"), (500, "500"),
            (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k"), (20000, "20k"),
        ];
        foreach (var (f, label) in freqGrid)
        {
            int gx = FToX(f, pX, pW);
            p.DrawVLine(gx, pY, pY2, 0x2E2E3AFF);
            int tw = PngWriter.TextWidth(label);
            p.DrawText(gx - tw / 2, pY2 + 7, label, 0x8888A0FF);
        }
        // "Hz" axis unit label
        string hzLabel = "Hz";
        p.DrawText(pX + pW / 2 - PngWriter.TextWidth(hzLabel) / 2, pY2 + 20, hzLabel, 0x606070FF);

        // ── Horizontal grid lines + dB labels ─────────────────────────────────
        for (double db = DbMin; db <= DbMax + 0.5; db += 6.0)
        {
            int gy = DbToY(db, pY, pH);
            p.DrawHLine(pX, pX2, gy, 0x2E2E3AFF);
            string dlabel = db >= 0 ? $"+{(int)db}" : $"{(int)db}";
            int dtw = PngWriter.TextWidth(dlabel);
            p.DrawText(pX - dtw - 5, gy - PngWriter.TextHeight() / 2, dlabel, 0x8888A0FF);
        }
        // "dB" axis unit label (rotated text not supported → place to the left of plot top)
        p.DrawText(4, pY + pH / 2 - PngWriter.TextHeight() / 2, "dB", 0x606070FF);

        // ── Plot border ───────────────────────────────────────────────────────
        p.DrawRect(pX, pY, pW, pH, 0x3A3A48FF);

        // ── Zone labels inside the plot (bottom strip) ────────────────────────
        int labelY = pY2 - PngWriter.TextHeight() - 3;
        DrawZoneLabel(p, "SUB",  pX, labelY, pW, 20.0,   60.0,   0x585868FF);
        DrawZoneLabel(p, "BASS", pX, labelY, pW, 60.0,   250.0,  0x585868FF);
        DrawZoneLabel(p, "MUD",  pX, labelY, pW, 250.0,  500.0,  0xAA8040FF);
        DrawZoneLabel(p, "MID",  pX, labelY, pW, 500.0,  2000.0, 0x585868FF);
        DrawZoneLabel(p, "FAT",  pX, labelY, pW, 2000.0, 5000.0, 0xC06060FF);
        DrawZoneLabel(p, "AIR",  pX, labelY, pW, 8000.0, 16000.0,0x585868FF);

        // ── TARGET curve (dashed, mid-grey) ──────────────────────────────────
        DrawCurve(p, target, pX, pY, pW, pH, 0x787888FF, dashed: true,  thick: false);

        // ── DRY curve (muted light grey) ──────────────────────────────────────
        DrawCurve(p, dry,    pX, pY, pW, pH, 0xB8B8C4FF, dashed: false, thick: true);

        // ── WET curve (vivid cyan accent) ─────────────────────────────────────
        DrawCurve(p, wet,    pX, pY, pW, pH, 0x18E8C0FF, dashed: false, thick: true);

        // ── Legend ────────────────────────────────────────────────────────────
        int lgX = pX2 - 118, lgY = pY + 10;
        DrawLegend(p, lgX, lgY,      0x787888FF, "TARGET", dashed: true);
        DrawLegend(p, lgX, lgY + 14, 0xB8B8C4FF, "DRY",    dashed: false);
        DrawLegend(p, lgX, lgY + 28, 0x18E8C0FF, "WET",    dashed: false);

        // ── Title ─────────────────────────────────────────────────────────────
        string t = title;
        if (t.Length > 60) t = "..." + t[^57..];
        int ttw = PngWriter.TextWidth(t);
        p.DrawText(Math.Max(pX, (CanvasW - ttw) / 2), 10, t, 0xD8D8ECFF);

        return p;
    }

    // -- Rendering helpers -----------------------------------------------------

    private static void ShadeZone(PngWriter p, int pX, int pY, int pW, int pH,
        double loHz, double hiHz, uint rgba)
    {
        int x0 = FToX(loHz, pX, pW), x1 = FToX(hiHz, pX, pW);
        p.BlendRect(x0, pY, Math.Max(1, x1 - x0), pH, rgba);
    }

    private static void DrawToleranceBand(PngWriter p,
        (double FreqHz, double Db)[] target, int pX, int pY, int pW, int pH)
    {
        // Shade ±ToleranceDb around target as a very subtle fill.
        // For each consecutive pair of band centres, fill a vertical strip.
        for (int i = 0; i < target.Length - 1; i++)
        {
            int x0 = FToX(target[i].FreqHz,   pX, pW);
            int x1 = FToX(target[i+1].FreqHz, pX, pW);
            int y0 = DbToY(target[i].Db   + SpectralTarget.ToleranceDb, pY, pH);
            int y1 = DbToY(target[i].Db   - SpectralTarget.ToleranceDb, pY, pH);
            if (x1 <= x0) continue;
            y0 = Math.Max(pY, Math.Min(pY + pH, y0));
            y1 = Math.Max(pY, Math.Min(pY + pH, y1));
            if (y0 > y1) (y0, y1) = (y1, y0);
            p.BlendRect(x0, y0, x1 - x0, Math.Max(1, y1 - y0), 0xFFFFFF0A);
        }
    }

    private static void DrawCurve(
        PngWriter p,
        (double FreqHz, double Db)[] spec,
        int pX, int pY, int pW, int pH,
        uint rgba, bool dashed, bool thick)
    {
        int? prevX = null, prevY = null;
        foreach (var (f, db) in spec)
        {
            if (f < FMin || f > FMax) continue;
            if (db < DbMin - 10 || db > DbMax + 10) { prevX = null; continue; }

            int cx = FToX(f, pX, pW);
            int cy = DbToY(db, pY, pH);

            if (prevX.HasValue)
            {
                if (dashed)
                    p.DrawDashedLine(prevX.Value, prevY!.Value, cx, cy, rgba, dash: 7, gap: 4);
                else
                {
                    p.DrawLine(prevX.Value, prevY!.Value, cx, cy, rgba);
                    if (thick)
                        p.DrawLine(prevX.Value, prevY.Value + 1, cx, cy + 1, rgba);
                }
            }
            prevX = cx; prevY = cy;
        }
    }

    private static void DrawZoneLabel(PngWriter p, string label, int pX, int y,
        int pW, double loHz, double hiHz, uint rgba)
    {
        int x0 = FToX(loHz, pX, pW), x1 = FToX(hiHz, pX, pW);
        int zoneW = x1 - x0;
        int tw    = PngWriter.TextWidth(label);
        if (zoneW >= tw + 2)
            p.DrawText(x0 + (zoneW - tw) / 2, y, label, rgba);
    }

    private static void DrawLegend(PngWriter p, int x, int y, uint rgba, string label, bool dashed)
    {
        int lineY = y + PngWriter.TextHeight() / 2;
        if (dashed)
            p.DrawDashedLine(x, lineY, x + 20, lineY, rgba, dash: 5, gap: 3);
        else
        {
            p.DrawHLine(x, x + 20, lineY,     rgba);
            p.DrawHLine(x, x + 20, lineY + 1, rgba);
        }
        p.DrawText(x + 24, y, label, rgba);
    }

    // ── Coordinate mapping ────────────────────────────────────────────────────

    // Log-scale frequency → X pixel inside the plot.
    private static int FToX(double f, int pX, int pW)
    {
        double t = Math.Log10(Math.Max(f, FMin) / FMin) / Math.Log10(FMax / FMin);
        return pX + (int)(Math.Clamp(t, 0.0, 1.0) * pW + 0.5);
    }

    // dB value → Y pixel inside the plot (higher dB = lower Y).
    private static int DbToY(double db, int pY, int pH)
    {
        double t = (DbMax - db) / (DbMax - DbMin);
        return pY + (int)(Math.Clamp(t, 0.0, 1.0) * pH + 0.5);
    }
}

/// <summary>Parsed options for the <c>spectrum</c> command.</summary>
internal sealed class SpectrumOptions
{
    public double EqLow          { get; init; } = 1.0;
    public double EqMid          { get; init; } = 1.0;
    public double EqHigh         { get; init; } = 1.0;
    public double TiltStrength   { get; init; } = 0.0;
    public double TransientPunch { get; init; } = 0.0;
    public string LimiterMode    { get; init; } = "off";

    /// <summary>
    /// Normalization mode for the plot:
    ///   "shape" (default) — each curve is anchored to its OWN 200 Hz–2 kHz mean, so only the tonal
    ///     BALANCE (shape vs. the target curve) is shown; the chain's overall loudness lift is removed
    ///     and reported separately.
    ///   "level" — DRY and WET share DRY's anchor, so the limiter's makeup gain shows as a vertical lift.
    /// </summary>
    public string Mode { get; init; } = "shape";
}
