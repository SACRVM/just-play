using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="MasteringLimiter"/> — the production stereo true-peak limiter / maximizer
/// wired onto the engine mixer bus.
///
/// Coverage:
///   M1 — a hot stereo sine (+6 dB over) is brought down to the −1 dBTP ceiling.
///   M2 — a signal already under the ceiling at 0 dB drive ("Soft") passes through unchanged.
///   M3 — gain reduction is stereo-LINKED: a peak in L pulls R down by the same factor (image holds).
///   M4 — maximizer drive raises level when there is headroom (no limiting), and never above ceiling.
///   M5 — anti-AGC: 0 dB drive never amplifies a quiet signal (gain ≤ unity).
///   M6 — output stays finite (no NaN/Inf) and Reset() clears state.
///
/// The ceiling check uses the SAMPLE peak of the output: the limiter targets the oversampled TRUE
/// peak to the ceiling, and sample-peak ≤ true-peak, so a compliant output never shows a sample over
/// the ceiling. Measurements skip the first 100 ms to ignore the look-ahead ramp-in.
/// </summary>
public class MasteringLimiterTests
{
    private const int    Rate        = 44100;
    private const double CeilingDbTp = -1.0;
    private static readonly float CeilingLinear = (float)Math.Pow(10.0, CeilingDbTp / 20.0); // ≈ 0.8913

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Interleaved-stereo sine; L and R can have different amplitudes.</summary>
    private static float[] StereoSine(double ampL, double ampR, double freq, double seconds)
    {
        int n = (int)(seconds * Rate);
        var buf = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            double s = Math.Sin(2.0 * Math.PI * freq * i / Rate);
            buf[i * 2]     = (float)(ampL * s);
            buf[i * 2 + 1] = (float)(ampR * s);
        }
        return buf;
    }

    /// <summary>Per-channel sample peak over the steady-state region (skips the first 100 ms).</summary>
    private static (float l, float r) SteadyPeak(float[] interleaved)
    {
        int skip = (int)(0.1 * Rate);
        float l = 0f, r = 0f;
        for (var i = skip; i < interleaved.Length / 2; i++)
        {
            float al = Math.Abs(interleaved[i * 2]);
            float ar = Math.Abs(interleaved[i * 2 + 1]);
            if (al > l) l = al;
            if (ar > r) r = ar;
        }
        return (l, r);
    }

    private static double Db(float linear) => linear <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(linear);

    // ── M1: hot sine is limited to the ceiling ───────────────────────────────────

    [Fact]
    public void HotSine_IsBroughtToCeiling()
    {
        var buf = StereoSine(2.0, 2.0, 1000.0, 0.5);   // +6 dBFS, way over
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 0.0);
        lim.ProcessInterleavedStereo(buf);

        var (l, r) = SteadyPeak(buf);
        // Never above the ceiling (allow a hair of tolerance for envelope/measurement).
        Assert.True(Db(l) <= CeilingDbTp + 0.3, $"L out {Db(l):F2} dBTP exceeds ceiling");
        Assert.True(Db(r) <= CeilingDbTp + 0.3, $"R out {Db(r):F2} dBTP exceeds ceiling");
        // And it actually reached up near the ceiling (not crushed to silence).
        Assert.True(Db(l) >= CeilingDbTp - 2.0, $"L out {Db(l):F2} dBTP is over-attenuated");
    }

    // ── M2: already-quiet signal at Soft passes through unchanged ─────────────────

    [Fact]
    public void UnderCeiling_Soft_PassesThroughUnchanged()
    {
        var buf = StereoSine(0.5, 0.5, 1000.0, 0.3);   // ~−6 dBFS, well under ceiling
        var input = (float[])buf.Clone();
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 0.0);
        lim.ProcessInterleavedStereo(buf);

        var (lOut, _) = SteadyPeak(buf);
        var (lIn, _)  = SteadyPeak(input);
        Assert.Equal(lIn, lOut, 2);   // unchanged to ~2 decimals (gain stayed at unity)
    }

    // ── M3: gain reduction is stereo-linked ──────────────────────────────────────

    [Fact]
    public void GainReduction_IsStereoLinked()
    {
        // L is hot (will be limited); R is quiet (would NOT be touched by an independent mono limiter).
        var buf = StereoSine(ampL: 2.0, ampR: 0.1, freq: 1000.0, seconds: 0.5);
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 0.0);
        lim.ProcessInterleavedStereo(buf);

        var (lOut, rOut) = SteadyPeak(buf);
        double gainL = lOut / 2.0;   // factor actually applied to L
        double gainR = rOut / 0.1;   // factor actually applied to R

        // Linked: both channels see the SAME gain factor (within tolerance).
        Assert.Equal(gainL, gainR, 2);
        // And R was genuinely pulled down despite being quiet (proves it's not independent).
        Assert.True(gainR < 0.95, $"R gain {gainR:F3} — quiet channel was not linked to L's reduction");
    }

    // ── M4: maximizer drive raises level when there's headroom ───────────────────

    [Fact]
    public void Drive_RaisesLevel_WhenHeadroomExists_ButNeverAboveCeiling()
    {
        // Quiet input; +6 dB drive lands it at ~0.4 (still under the 0.8913 ceiling) → no limiting,
        // so the output should be ~2× the input and clean.
        var buf = StereoSine(0.2, 0.2, 1000.0, 0.3);
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 6.0);
        lim.ProcessInterleavedStereo(buf);

        var (l, _) = SteadyPeak(buf);
        Assert.True(l > 0.36 && l < 0.45, $"driven peak {l:F3} not ≈ +6 dB of 0.2");
        Assert.True(Db(l) <= CeilingDbTp + 0.3, "driven output exceeded ceiling");
    }

    // ── M5: anti-AGC — 0 dB drive never amplifies ────────────────────────────────

    [Fact]
    public void Soft_NeverAmplifies()
    {
        var buf = StereoSine(0.3, 0.3, 1000.0, 0.3);
        var input = (float[])buf.Clone();
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 0.0);
        lim.ProcessInterleavedStereo(buf);

        var (lOut, _) = SteadyPeak(buf);
        var (lIn, _)  = SteadyPeak(input);
        Assert.True(lOut <= lIn + 1e-4, $"output {lOut:F4} exceeds input {lIn:F4} — limiter inflated gain");
    }

    // ── M6: numerically stable + Reset clears state ──────────────────────────────

    [Fact]
    public void Output_IsFinite_AndResetClearsState()
    {
        var buf = StereoSine(1.5, 1.5, 220.0, 0.2);
        var lim = new MasteringLimiter(Rate, CeilingDbTp, driveDb: 3.0);
        lim.ProcessInterleavedStereo(buf);
        foreach (var s in buf)
            Assert.True(float.IsFinite(s), "non-finite sample in output");

        // Reset then push silence — output must be silence (no leftover envelope/delay energy).
        lim.Reset();
        var silence = new float[Rate];   // 0.5 s stereo of zeros
        lim.ProcessInterleavedStereo(silence);
        Assert.All(silence, s => Assert.Equal(0f, s));
    }
}
