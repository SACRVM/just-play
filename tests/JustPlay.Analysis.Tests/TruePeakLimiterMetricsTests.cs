using JustPlay.Analysis;
using Xunit.Abstractions;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Diagnostic / metrics tests for <see cref="TruePeakLimiter"/>.
/// These tests ALWAYS PASS but emit measurements to the test output for review.
/// Chloe can read the numbers here to evaluate limiter quality.
/// </summary>
public class TruePeakLimiterMetricsTests(ITestOutputHelper output)
{
    private const int    SampleRate  = 44100;
    private const double CeilingDbTp = -1.0;

    // -------------------------------------------------------------------------
    // Signal generators
    // -------------------------------------------------------------------------

    private static float[] Sine(double amplitude, double freq, double seconds)
    {
        var n   = (int)(seconds * SampleRate);
        var buf = new float[n];
        for (var i = 0; i < n; i++)
            buf[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * freq * i / SampleRate));
        return buf;
    }

    // -------------------------------------------------------------------------
    // M1 - THD on a quiet sine (limiter should be transparent)
    // -------------------------------------------------------------------------

    [Fact]
    public void M1_THD_QuietSine_Transparent()
    {
        // Measure THD of a 997 Hz sine at 0.5 amplitude (-6 dBFS, under ceiling).
        // Method: compare input peak with output peak in steady state.
        // For a transparent path, the ratio should be ~1.0.

        const double freq = 997.0;
        const double amp  = 0.5;

        var samples = Sine(amp, freq, seconds: 2.0);
        var limiter = new TruePeakLimiter(SampleRate, CeilingDbTp);

        var processed = (float[])samples.Clone();
        limiter.ProcessInPlace(processed);

        // Steady-state: second half
        int half = processed.Length / 2;

        float inPeak  = 0f; for (var i = half; i < samples.Length; i++) { var a = Math.Abs(samples[i]); if (a > inPeak) inPeak = a; }
        float outPeak = 0f; for (var i = half; i < processed.Length; i++) { var a = Math.Abs(processed[i]); if (a > outPeak) outPeak = a; }

        double gainRatio = outPeak / (double)inPeak;
        double gainDb    = 20.0 * Math.Log10(gainRatio);

        output.WriteLine($"[M1] Quiet 997 Hz sine (0.5 amp, -6 dBFS):");
        output.WriteLine($"     Steady-state peak in:  {inPeak:F5}");
        output.WriteLine($"     Steady-state peak out: {outPeak:F5}");
        output.WriteLine($"     Gain ratio:            {gainRatio:F5} ({gainDb:+0.00;-0.00} dB)");
        output.WriteLine($"     Deviation from unity: {Math.Abs(gainDb):F3} dB (expect < 0.1 dB for transparent pass)");

        // Always passes - just a measurement.
    }

    // -------------------------------------------------------------------------
    // M2 - Gain reduction on a +6 dBFS hot sine
    // -------------------------------------------------------------------------

    [Fact]
    public void M2_GainReduction_HotSine()
    {
        const double amp  = 2.0; // +6 dBFS (linear 2.0 vs max 1.0 = +6 dB)
        const double freq = 997.0;

        var samples   = Sine(amp, freq, seconds: 2.0);
        var limiter   = new TruePeakLimiter(SampleRate, CeilingDbTp, lookaheadMs: 2.0, attackMs: 0.5, releaseMs: 100.0);
        var processed = (float[])samples.Clone();
        limiter.ProcessInPlace(processed);

        int half = processed.Length / 2;
        float inTp  = TruePeakLimiter.MeasureTruePeak(samples[half..]);
        float outTp = TruePeakLimiter.MeasureTruePeak(processed[half..]);

        double inDbTp  = TruePeakLimiter.LinearToDbTp(inTp);
        double outDbTp = TruePeakLimiter.LinearToDbTp(outTp);
        double grDb    = outDbTp - inDbTp;

        output.WriteLine($"[M2] Hot 997 Hz sine (+6 dBFS), ceiling {CeilingDbTp} dBTP:");
        output.WriteLine($"     Input true peak (steady):  {inDbTp:+0.00;-0.00} dBTP ({inTp:F5} linear)");
        output.WriteLine($"     Output true peak (steady): {outDbTp:+0.00;-0.00} dBTP ({outTp:F5} linear)");
        output.WriteLine($"     Peak gain reduction:       {grDb:+0.00;-0.00} dB");
        output.WriteLine($"     Ceiling compliance: {(outDbTp <= CeilingDbTp + 0.5 ? "PASS" : "FAIL")}");
    }

    // -------------------------------------------------------------------------
    // M3 - True-peak vs sample-peak discrepancy on near-Nyquist content
    // -------------------------------------------------------------------------

    [Fact]
    public void M3_TruePeak_vs_SamplePeak_NearNyquist()
    {
        // Near-Nyquist sine: peaks can fall between samples.
        double freq = (double)SampleRate / 4.0 + 1000.0;
        double amp  = 0.90;

        var n = SampleRate; // 1 second
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = (float)(amp * Math.Cos(2.0 * Math.PI * freq * i / SampleRate));

        float samplePeak = 0f;
        foreach (var s in samples) { var a = Math.Abs(s); if (a > samplePeak) samplePeak = a; }
        float truePeak = TruePeakLimiter.MeasureTruePeak(samples);

        double spDb = TruePeakLimiter.LinearToDbTp(samplePeak);
        double tpDb = TruePeakLimiter.LinearToDbTp(truePeak);

        output.WriteLine($"[M3] Near-Nyquist sine ({freq:F0} Hz, amp {amp}):");
        output.WriteLine($"     Sample peak:  {samplePeak:F5} ({spDb:+0.00;-0.00} dBFS)");
        output.WriteLine($"     True peak 4x: {truePeak:F5} ({tpDb:+0.00;-0.00} dBTP)");
        output.WriteLine($"     Discrepancy:  {tpDb - spDb:+0.00;-0.00} dB");
        output.WriteLine($"     -> True peak exceeds sample peak by the above amount (inter-sample overshoot)");
    }

    // -------------------------------------------------------------------------
    // M4 - Transient preservation: single impulse followed by steady sine
    // -------------------------------------------------------------------------

    [Fact]
    public void M4_TransientPreservation_ImpulseFollowedBySine()
    {
        const double sineAmp = 0.5; // under ceiling
        const double impulseAmp = 5.0; // +14 dBFS spike

        var n = SampleRate; // 1 second
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = (float)(sineAmp * Math.Sin(2.0 * Math.PI * 1000.0 * i / SampleRate));
        samples[2205] = (float)impulseAmp; // at 50 ms

        var proc    = (float[])samples.Clone();
        var limiter = new TruePeakLimiter(SampleRate, CeilingDbTp, lookaheadMs: 2.0, attackMs: 0.5, releaseMs: 100.0);
        limiter.ProcessInPlace(proc);

        // Peak at impulse position (limited)
        float limitedImpulse = Math.Abs(proc[2205]);
        double impulseDbTp   = TruePeakLimiter.LinearToDbTp(limitedImpulse);

        // Tail steady-state (last 100 ms)
        int tailStart = n - (int)(0.1 * SampleRate);
        float tailPeak = 0f;
        for (var i = tailStart; i < n; i++) { var a = Math.Abs(proc[i]); if (a > tailPeak) tailPeak = a; }

        double tailVsExpected = 20.0 * Math.Log10(tailPeak / sineAmp);

        // Lookahead samples for reference
        int lookaheadSamples = (int)Math.Round(2.0 * SampleRate / 1000.0);

        // Measure "hole" depth: minimum in the 20 ms after the impulse
        int holeStart = 2205 + lookaheadSamples;
        int holeEnd   = holeStart + (int)(0.020 * SampleRate);
        float holeMin = float.MaxValue;
        for (var i = Math.Max(holeStart, 0); i < Math.Min(holeEnd, n); i++)
        {
            var a = Math.Abs(proc[i]);
            if (a < holeMin) holeMin = a;
        }

        output.WriteLine($"[M4] Transient preservation (impulse at 50 ms, {impulseAmp}x amplitude):");
        output.WriteLine($"     Limited impulse amplitude: {limitedImpulse:F5} ({impulseDbTp:+0.00;-0.00} dBTP)");
        output.WriteLine($"     Lookahead delay:           {lookaheadSamples} samples ({2.0:F1} ms)");
        output.WriteLine($"     Tail steady-state peak:    {tailPeak:F5} (expected ~{sineAmp:F3})");
        output.WriteLine($"     Tail deviation from expected: {tailVsExpected:+0.00;-0.00} dB");
        output.WriteLine($"     Post-impulse 'hole' min:   {holeMin:F5} (low = deep hole = bad)");
    }

    // -------------------------------------------------------------------------
    // M5 - Gain-reduction behaviour over time (snapshot of envelope state)
    // -------------------------------------------------------------------------

    [Fact]
    public void M5_GainReduction_EnvelopeProfile()
    {
        // Process a hot sine and sample the envelope every 10 ms by comparing
        // output vs input at fixed windows.
        const double amp  = 2.0;
        const double freq = 997.0;
        const double dur  = 0.5; // 0.5 s

        var samples = Sine(amp, freq, dur);
        var proc    = (float[])samples.Clone();
        var limiter = new TruePeakLimiter(SampleRate, CeilingDbTp, lookaheadMs: 2.0, attackMs: 0.5, releaseMs: 100.0);
        limiter.ProcessInPlace(proc);

        output.WriteLine($"[M5] Gain reduction envelope (hot sine +6 dBFS -> ceiling {CeilingDbTp} dBTP):");
        output.WriteLine($"     Time(ms) | OutputTP(dBTP) | GainRedux(dB)");

        for (var windowMs = 10; windowMs <= 490; windowMs += 20)
        {
            int start = (int)(windowMs * SampleRate / 1000.0);
            int end   = Math.Min(start + SampleRate / 100, proc.Length); // 10 ms window
            if (start >= proc.Length) break;

            var segment = proc[start..end];
            float tp    = TruePeakLimiter.MeasureTruePeak(segment);
            double tpDb = TruePeakLimiter.LinearToDbTp(tp);
            double grDb = tpDb - 20.0 * Math.Log10(amp);

            output.WriteLine($"     {windowMs,8} ms | {tpDb,14:+0.00;-0.00} | {grDb,13:+0.00;-0.00}");
        }
    }
}
