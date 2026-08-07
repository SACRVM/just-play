using JustPlay.Analysis;
using JustPlay.Core.Models;

namespace JustPlay.Analysis.Tests;

public class Bs1770LoudnessDetectorTests
{
    private const int Rate = 11025;
    private readonly Bs1770LoudnessDetector _det = new();

    // -------------------------------------------------------------------------
    // Helpers - shared signal generators
    // -------------------------------------------------------------------------

    /// <summary>Mono sine at the given frequency, amplitude, duration, and sample rate.</summary>
    private static float[] Sine(double freq, double amplitude, double seconds, int rate = Rate)
    {
        var n = (int)(seconds * rate);
        var buf = new float[n];
        for (var i = 0; i < n; i++)
            buf[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / rate));
        return buf;
    }

    // -------------------------------------------------------------------------
    // Null / empty / silent guard tests
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptySamples_ReturnsNull()
    {
        Assert.Null(_det.Detect(new DecodedAudio([], Rate)));
    }

    [Fact]
    public void ZeroSampleRate_ReturnsNull()
    {
        Assert.Null(_det.Detect(new DecodedAudio(Sine(997, 1.0, 2.0), 0)));
    }

    [Fact]
    public void SilentBuffer_ReturnsNull()
    {
        // All zeros - LUFS = -inf -> null.
        Assert.Null(_det.Detect(new DecodedAudio(new float[Rate * 3], Rate)));
    }

    [Fact]
    public void NearlySilent_BelowGate_ReturnsNull()
    {
        // Amplitude 1e-6 (~-120 dBFS) - well below absolute gate (-70 LUFS -> ~-70 dBFS).
        var samples = Sine(997, 1e-6, 5.0);
        Assert.Null(_det.Detect(new DecodedAudio(samples, Rate)));
    }

    // -------------------------------------------------------------------------
    // BS.1770 calibration anchor: 997 Hz full-scale sine ~ -3.01 LUFS
    // -------------------------------------------------------------------------

    [Fact]
    public void FullScaleSine_997Hz_ReturnsLufs_Near_Minus3()
    {
        // The BS.1770 reference: a full-scale 997 Hz sine at 11025 Hz must read ~ -3.01 LUFS.
        // Tolerance +/-0.3 (same as the spec and the energy-detection notes).
        var samples = Sine(997, 1.0, 5.0);
        var result  = _det.Detect(new DecodedAudio(samples, Rate));

        Assert.NotNull(result);
        var lufs = result!.Value.IntegratedLufs;
        Assert.InRange(lufs, -3.31, -2.71);
    }

    // -------------------------------------------------------------------------
    // Relative level: -6 dB sine reads ~6 LU lower
    // -------------------------------------------------------------------------

    [Fact]
    public void HalfAmplitudeSine_Reads_6LU_BelowFullScale()
    {
        // Amplitude 0.5 = -6 dBFS. Because LUFS = offset + 10-log10(mean-square)
        // and mean-square scales as amplitude^2, a half-amplitude sine reads
        // 20-log10(0.5) = -6 dB below full scale. Tolerance +/-0.5 LU.
        var full = Sine(997, 1.0, 5.0);
        var half = Sine(997, 0.5, 5.0);

        var rFull = _det.Detect(new DecodedAudio(full, Rate));
        var rHalf = _det.Detect(new DecodedAudio(half, Rate));

        Assert.NotNull(rFull);
        Assert.NotNull(rHalf);

        var delta = rFull!.Value.IntegratedLufs - rHalf!.Value.IntegratedLufs;
        Assert.InRange(delta, 5.5, 6.5);
    }

    // -------------------------------------------------------------------------
    // Peak tests
    // -------------------------------------------------------------------------

    [Fact]
    public void FullScaleSine_Peak_NearOne()
    {
        // max|sin(...)| = 1.0 for amplitude 1.0 (up to a sample-grid rounding at 5 seconds).
        var samples = Sine(997, 1.0, 5.0);
        var result  = _det.Detect(new DecodedAudio(samples, Rate));

        Assert.NotNull(result);
        Assert.InRange(result!.Value.Peak, 0.99, 1.01);
    }

    [Fact]
    public void HalfAmplitudeSine_Peak_NearHalf()
    {
        var samples = Sine(997, 0.5, 5.0);
        var result  = _det.Detect(new DecodedAudio(samples, Rate));

        Assert.NotNull(result);
        Assert.InRange(result!.Value.Peak, 0.49, 0.51);
    }

    // -------------------------------------------------------------------------
    // Sanity: returned fields are physically plausible
    // -------------------------------------------------------------------------

    [Fact]
    public void NormalSine_BothFieldsPresent_AndPlausible()
    {
        var samples = Sine(880, 0.7, 5.0);
        var result  = _det.Detect(new DecodedAudio(samples, Rate));

        Assert.NotNull(result);
        // LUFS must be negative and not absurdly low.
        Assert.InRange(result!.Value.IntegratedLufs, -60.0, 0.0);
        // Peak must be in [0, 1] for a sub-full-scale sine.
        Assert.InRange(result.Value.Peak, 0.0, 1.0);
    }
}
