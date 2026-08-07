using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="BusDspChain"/>.
///
/// Coverage:
///   BC1 - All-neutral parameters -> output ~ input (bit-transparent through all bypass paths).
///   BC2 - EQ low-boost raises low-band energy vs. neutral.
///   BC3 - Limiter with a hot signal reduces peak amplitude.
///   BC4 - Reset() clears state (two independent runs of the same signal produce equal results).
/// </summary>
public class BusDspChainTests
{
    private const int Rate = 44100;

    // -- BC1: Neutral chain is bit-transparent ---------------------------------

    [Fact]
    public void NeutralParams_OutputApproximatesInput()
    {
        // All DSP stages at bypass/neutral values:
        // EQ unity (identity biquads - mathematically but not bit-exactly transparent;
        // float rounding in the filter accumulates ~1e-7 relative error per sample),
        // tilt=0, punch=0, no limiter.
        // Stages with explicit code-level bypasses (Tilt, Transient) are
        // bit-exact; the ThreeBandEqualizer at unity is near-identity within float epsilon.
        var chain = new BusDspChain(
            eqLow: 1.0, eqMid: 1.0, eqHigh: 1.0,
            tiltStrength: 0.0,
            transientPunch: 0.0,
            limiterEnabled: false);

        float[] input  = StereoSine(440.0, 0.5, 2.0, Rate);
        float[] output = (float[])input.Clone();
        chain.ProcessInterleavedStereo(output);

        const float Tolerance = 5e-5f; // 3x float machine eps per biquad stage, 3 stages in series
        float maxErr = 0f;
        for (int i = 0; i < input.Length; i++)
        {
            float err = Math.Abs(output[i] - input[i]);
            if (err > maxErr) maxErr = err;
        }
        Assert.True(maxErr <= Tolerance,
            $"Neutral chain max absolute error {maxErr:E3} exceeds tolerance {Tolerance:E0}");
    }

    // -- BC2: EQ low-boost raises low-band energy -----------------------------

    [Fact]
    public void EqLowBoost_RaisesLowBandEnergy()
    {
        // Synthesise a broadband noise signal.
        var rng   = new Random(42);
        int n     = Rate * 4;
        float[] stereo = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float s = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.4f;
            stereo[i * 2] = s; stereo[i * 2 + 1] = s;
        }

        // Neutral chain - measure low-band RMS
        var chainFlat = new BusDspChain(eqLow: 1.0);
        var flatOut   = (float[])stereo.Clone();
        chainFlat.ProcessInterleavedStereo(flatOut);
        double rmsFlat = LowBandRms(flatOut, Rate);

        // Low-boosted chain (+6 dB EQ low shelf - linear gain 2.0)
        var chainBoosted = new BusDspChain(eqLow: 2.0);
        var boostOut     = (float[])stereo.Clone();
        chainBoosted.ProcessInterleavedStereo(boostOut);
        double rmsBoosted = LowBandRms(boostOut, Rate);

        Assert.True(rmsBoosted > rmsFlat * 1.3,
            $"EQ low boost should raise low-band RMS; flat={rmsFlat:F4}, boosted={rmsBoosted:F4}");
    }

    // -- BC3: Limiter clamps hot signal ---------------------------------------

    [Fact]
    public void LimiterEnabled_ClampsPeak()
    {
        // Generate a signal at 0 dBFS (+6 dB drive -> would clip at +6 dBFS without limiter).
        float[] hot = StereoSine(1000.0, 1.0, 2.0, Rate);

        var chain  = new BusDspChain(
            limiterEnabled: true, limiterDriveDb: 6.0, limiterCeilingDbTp: -1.0);
        var output = (float[])hot.Clone();
        chain.ProcessInterleavedStereo(output);

        // Skip the first 100 ms (look-ahead ramp-in) and check sample peak <= ceiling.
        const float Ceiling = 0.8913f; // 10^(-1/20) ~ -1 dBTP
        int skip = (int)(0.1 * Rate);
        float peak = 0f;
        for (int i = skip; i < output.Length; i++)
        {
            float a = Math.Abs(output[i]);
            if (a > peak) peak = a;
        }
        // Sample peak should be <= true-peak ceiling (sample peak <= true peak by definition)
        Assert.True(peak <= Ceiling + 0.01f,
            $"Output sample peak {peak:F4} exceeds -1 dBTP ceiling {Ceiling:F4}");
    }

    // -- BC4: Reset isolates sequential runs -----------------------------------

    [Fact]
    public void Reset_TwoRunsProduceSameOutput()
    {
        float[] signal = StereoSine(200.0, 0.3, 1.0, Rate);

        var chain = new BusDspChain(eqLow: 1.5, tiltStrength: 0.3, transientPunch: 0.2);

        // First run
        float[] out1 = (float[])signal.Clone();
        chain.ProcessInterleavedStereo(out1);

        // Reset and second run
        chain.Reset();
        float[] out2 = (float[])signal.Clone();
        chain.ProcessInterleavedStereo(out2);

        for (int i = 0; i < out1.Length; i++)
            Assert.Equal(out1[i], out2[i]);
    }

    // -- Helpers ---------------------------------------------------------------

    private static float[] StereoSine(double freqHz, double amp, double seconds, int fs)
    {
        int n   = (int)(seconds * fs);
        var buf = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            float s = (float)(amp * Math.Sin(2.0 * Math.PI * freqHz * i / fs));
            buf[i * 2] = s; buf[i * 2 + 1] = s;
        }
        return buf;
    }

    /// <summary>
    /// RMS energy of the left channel filtered below 300 Hz (crude low-pass by averaging
    /// groups of samples, sufficient for a relative comparison test).
    /// </summary>
    private static double LowBandRms(float[] interleaved, int fs)
    {
        // Downsample by 64 (equivalent LP at fs/128 ~ 345 Hz) then compute RMS.
        const int Decimate = 64;
        double sumSq = 0;
        int count    = 0;
        for (int i = 0; i < interleaved.Length - Decimate * 2; i += Decimate * 2)
        {
            double avg = 0;
            for (int j = 0; j < Decimate; j++) avg += interleaved[i + j * 2]; // L channel
            avg /= Decimate;
            sumSq += avg * avg;
            count++;
        }
        return count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
    }
}
