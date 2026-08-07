using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="ThreeBandEqualizer"/> - the 3-band DJ EQ / isolator.
///
/// Coverage:
///   EQ1 - flat (1,1,1) passes a mid tone at ~unity (transparent reconstruction).
///   EQ2 - low kill (0,...) silences a 60 Hz tone while leaving a 1 kHz tone intact.
///   EQ3 - high kill (...,0) silences a 10 kHz tone while leaving a 1 kHz tone intact.
///   EQ4 - low boost (2,...) lifts a 60 Hz tone by ~+6 dB (~ x2).
///   EQ5 - output stays finite and Reset() clears state.
///
/// Magnitudes are read with a Goertzel filter over the steady-state second half (skips filter
/// settling + the EQ's small group delay).
/// </summary>
public class ThreeBandEqualizerTests
{
    private const int Rate = 44100;

    private static float[] StereoSine(double amp, double freq, double seconds)
    {
        int n = (int)(seconds * Rate);
        var buf = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            double s = amp * Math.Sin(2.0 * Math.PI * freq * i / Rate);
            buf[i * 2] = (float)s;
            buf[i * 2 + 1] = (float)s;
        }
        return buf;
    }

    private static double MagL(float[] interleaved, double freq)
    {
        int frames = interleaved.Length / 2;
        int start = frames / 2;
        double w = 2.0 * Math.PI * freq / Rate;
        double cw = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        int count = 0;
        for (var i = start; i < frames; i++)
        {
            double x = interleaved[i * 2];
            double s0 = x + cw * s1 - s2;
            s2 = s1; s1 = s0;
            count++;
        }
        double power = s1 * s1 + s2 * s2 - cw * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) * 2.0 / count;
    }

    /// <summary>out/in magnitude ratio at <paramref name="freq"/> for the given band gains.</summary>
    private static double Ratio(double freq, double low, double mid, double high)
    {
        var buf = StereoSine(0.5, freq, 0.4);
        double inMag = MagL(buf, freq);
        var eq = new ThreeBandEqualizer(Rate, low, mid, high);
        eq.ProcessInterleavedStereo(buf);
        return MagL(buf, freq) / inMag;
    }

    [Fact]
    public void Flat_PassesMidToneUnchanged()
        => Assert.Equal(1.0, Ratio(1000.0, 1.0, 1.0, 1.0), 1);   // ~unity (+/-0.05)

    [Fact]
    public void LowKill_Silences60Hz_KeepsMid()
    {
        Assert.True(Ratio(60.0, 0.0, 1.0, 1.0) < 0.15, "60 Hz not killed by low-band kill");
        Assert.True(Ratio(1000.0, 0.0, 1.0, 1.0) > 0.85, "low kill bled into the mid (1 kHz)");
    }

    [Fact]
    public void HighKill_Silences10kHz_KeepsMid()
    {
        Assert.True(Ratio(10000.0, 1.0, 1.0, 0.0) < 0.2, "10 kHz not killed by high-band kill");
        Assert.True(Ratio(1000.0, 1.0, 1.0, 0.0) > 0.8, "high kill bled into the mid (1 kHz)");
    }

    [Fact]
    public void LowBoost_Lifts60Hz_By6dB()
    {
        double r = Ratio(60.0, 2.0, 1.0, 1.0);
        Assert.True(r is > 1.7 and < 2.2, $"60 Hz +6 dB boost ratio was {r:F2}, expected ~ 2.0");
    }

    [Fact]
    public void Output_IsFinite_AndResetClearsState()
    {
        var buf = StereoSine(0.6, 120.0, 0.2);
        var eq = new ThreeBandEqualizer(Rate, 1.5, 0.5, 1.8);
        eq.ProcessInterleavedStereo(buf);
        foreach (var s in buf)
            Assert.True(float.IsFinite(s), "non-finite sample in output");

        eq.Reset();
        var silence = new float[Rate];
        eq.ProcessInterleavedStereo(silence);
        Assert.All(silence, s => Assert.True(Math.Abs(s) < 1e-6f, $"residual after reset: {s}"));
    }
}
