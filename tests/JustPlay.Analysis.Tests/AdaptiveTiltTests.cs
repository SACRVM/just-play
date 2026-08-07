using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="AdaptiveTilt"/> - the gentle spectral auto-tilt processor.
///
/// Coverage:
///   AT1 - Strength = 0 is bit-transparent (exact equality, no processing at all).
///   AT2 - A bass-heavy input (strong 80 Hz + weak 6 kHz) processed over 2 s at Strength = 1
///          ends with the high/low power ratio measurably improved toward balance vs. the input.
///   AT3 - Applied gain at both measurement-band frequencies never exceeds MaxTiltDb,
///          even under an extreme imbalance that would saturate the tilt control.
///   AT4 - Output stays finite; Reset() clears all state so silence-in -> silence-out.
/// </summary>
public class AdaptiveTiltTests
{
    private const int Rate = 44100;

    // -- Signal generators ----------------------------------------------------

    private static float[] StereoSine(double amp, double freq, double seconds)
    {
        int n = (int)(seconds * Rate);
        var buf = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            double s = amp * Math.Sin(2.0 * Math.PI * freq * i / Rate);
            buf[i * 2] = (float)s; buf[i * 2 + 1] = (float)s;
        }
        return buf;
    }

    /// <summary>Two summed tones (a low body + a high component), interleaved stereo.</summary>
    private static float[] StereoTwoTone(
        double ampLo, double freqLo, double ampHi, double freqHi, double seconds)
    {
        int n = (int)(seconds * Rate);
        var buf = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            double s = ampLo * Math.Sin(2.0 * Math.PI * freqLo * i / Rate)
                     + ampHi * Math.Sin(2.0 * Math.PI * freqHi * i / Rate);
            buf[i * 2] = (float)s; buf[i * 2 + 1] = (float)s;
        }
        return buf;
    }

    // -- Goertzel magnitude helper (left channel, second half of buffer) ------

    /// <summary>
    /// Goertzel DFT magnitude at <paramref name="freq"/> Hz, measured on the LEFT channel
    /// from the midpoint of the buffer onward (so short settling transients don't pollute the
    /// measurement - the tilt adapts over the first half, the second half is steady-state).
    /// </summary>
    private static double MagL(float[] interleaved, double freq)
    {
        int frames = interleaved.Length / 2, start = frames / 2;
        double w = 2.0 * Math.PI * freq / Rate, cw = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0; int count = 0;
        for (var i = start; i < frames; i++)
        {
            double x = interleaved[i * 2];
            double s0 = x + cw * s1 - s2; s2 = s1; s1 = s0; count++;
        }
        double power = s1 * s1 + s2 * s2 - cw * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) * 2.0 / count;
    }

    // -- Tests ----------------------------------------------------------------

    [Fact]
    public void Strength0_IsBitTransparent()
    {
        // At Strength = 0 the processor short-circuits immediately - not even the biquad
        // state machines run. Output must be identical to input down to the last bit.
        var tilt = new AdaptiveTilt(Rate, strength: 0.0);
        var orig = StereoTwoTone(0.6, 80.0, 0.3, 4000.0, seconds: 0.5);
        var buf  = (float[])orig.Clone();

        tilt.ProcessInterleavedStereo(buf);

        for (int i = 0; i < orig.Length; i++)
            Assert.Equal(orig[i], buf[i]);
    }

    [Fact]
    public void BassHeavy_HighLowRatioMovesTowardBalance()
    {
        // Bass-heavy two-tone: 80 Hz at amp 0.7 (strong body) + 6 kHz at amp 0.05 (weak high).
        // Input high/low amplitude ratio ~ 0.05 / 0.7 ~ 0.071.
        //
        // After 2 s with Strength = 1 and MaxTiltDb = 3 dB the tilt saturates at 3 dB total
        // (high shelf ~ +1.5 dB, low shelf ~ -1.5 dB). Expected settled output ratio ~ 0.101
        // (~41% improvement). The test only demands >= 10% improvement - conservative to account
        // for the smoothers still converging during the second half of the buffer.
        const double seconds = 2.0;
        var input = StereoTwoTone(0.7, 80.0, 0.05, 6000.0, seconds);
        var buf   = (float[])input.Clone();

        new AdaptiveTilt(Rate, strength: 1.0, maxTiltDb: 3.0)
            .ProcessInterleavedStereo(buf);

        // Measure both in the SECOND HALF (after adaptation has settled).
        double inLow   = MagL(input, 80.0);
        double inHigh  = MagL(input, 6000.0);
        double outLow  = MagL(buf,   80.0);
        double outHigh = MagL(buf,   6000.0);

        double inputRatio  = inHigh  / inLow;
        double outputRatio = outHigh / outLow;

        Assert.True(outputRatio > inputRatio * 1.10,
            $"High/low ratio did not improve: input={inputRatio:F4}  output={outputRatio:F4}");
    }

    [Fact]
    public void BrighterThanGoldenCurve_HighsAreCut()
    {
        // Regression guard for the 0-dB-target bug (fixed 2026-06-16): a track that is BRIGHTER than the
        // golden curve must have its high/low ratio REDUCED (highs cut / lows raised), not raised.
        // Equal-amplitude 80 Hz + 6 kHz tones sit at ~0 dB high/low ratio - far above the golden-curve
        // setpoint (highs should be well below lows) - so AutoTilt must tilt DOWNWARD on the highs.
        const double seconds = 2.0;
        var input = StereoTwoTone(0.3, 80.0, 0.3, 6000.0, seconds);
        var buf   = (float[])input.Clone();

        new AdaptiveTilt(Rate, strength: 1.0, maxTiltDb: 3.0)
            .ProcessInterleavedStereo(buf);

        double inputRatio  = MagL(input, 6000.0) / MagL(input, 80.0);
        double outputRatio = MagL(buf,   6000.0) / MagL(buf,   80.0);

        Assert.True(outputRatio < inputRatio * 0.95,
            $"High/low ratio was not reduced for a too-bright track: input={inputRatio:F4} output={outputRatio:F4}");
    }

    [Fact]
    public void AppliedTilt_NeverExceedsMaxTiltDb()
    {
        // Extreme imbalance: nearly pure bass (80 Hz at 0.8, 6 kHz at 0.001).
        // The controller will saturate at MaxTiltDb = 3 dB immediately.
        // The per-shelf gain is at most MaxTiltDb/2 = 1.5 dB, so the gain change measured
        // at both frequencies must stay well within +/-3 dB.
        const double maxTilt = 3.0;
        var input = StereoTwoTone(0.8, 80.0, 0.001, 6000.0, seconds: 2.5);
        var buf   = (float[])input.Clone();

        new AdaptiveTilt(Rate, strength: 1.0, maxTiltDb: maxTilt)
            .ProcessInterleavedStereo(buf);

        double refLow  = MagL(input, 80.0);
        double refHigh = MagL(input, 6000.0);
        double outLow  = MagL(buf,   80.0);
        double outHigh = MagL(buf,   6000.0);

        double gainLowDb  = 20.0 * Math.Log10(outLow  / refLow);
        double gainHighDb = 20.0 * Math.Log10(outHigh / refHigh);

        // Allow 0.1 dB margin for floating-point and Goertzel windowing error.
        Assert.True(Math.Abs(gainLowDb)  <= maxTilt + 0.1,
            $"Low band gain {gainLowDb:+0.0;-0.0} dB exceeded +/-{maxTilt} dB cap");
        Assert.True(Math.Abs(gainHighDb) <= maxTilt + 0.1,
            $"High band gain {gainHighDb:+0.0;-0.0} dB exceeded +/-{maxTilt} dB cap");
    }

    [Fact]
    public void Output_IsFinite_AndResetClearsState()
    {
        var buf  = StereoTwoTone(0.7, 80.0, 0.05, 6000.0, seconds: 0.3);
        var tilt = new AdaptiveTilt(Rate, strength: 0.8, maxTiltDb: 3.0);

        tilt.ProcessInterleavedStereo(buf);

        foreach (var s in buf)
            Assert.True(float.IsFinite(s), "non-finite sample in output");

        // After Reset, all filter state is zeroed and shelves return to 0 dB (identity).
        // Silence in -> all paths produce zero -> silence out.
        tilt.Reset();
        var silence = new float[Rate * 2];   // 1 s of interleaved stereo silence
        tilt.ProcessInterleavedStereo(silence);

        Assert.All(silence, s => Assert.Equal(0f, s));
    }
}
