using JustPlay.Core.Models;
using JustPlay.Analysis;
using Xunit;

namespace JustPlay.Analysis.Tests;

public class SpectralEnergyDetectorTests
{
    private const int Rate = 11025;
    private readonly SpectralEnergyDetector _det = new();

    [Fact]
    public void Silence_ReturnsLowestOrNull()
    {
        var audio = new DecodedAudio(new float[Rate * 2], Rate); // 2 s of zeros
        var e = _det.Detect(audio);
        Assert.True(e is null or 1);
    }

    [Fact]
    public void TooShort_ReturnsNull()
    {
        Assert.Null(_det.Detect(new DecodedAudio(new float[100], Rate)));
    }

    [Fact]
    public void ResultInRange_1To10()
    {
        var e = _det.Detect(new DecodedAudio(BusyBright(3.0, 0.9), Rate));
        Assert.NotNull(e);
        Assert.InRange(e!.Value, 1, 10);
    }

    [Fact]
    public void LoudBusyBright_ScoresHigherThan_QuietSparseDull()
    {
        var hot = _det.Detect(new DecodedAudio(BusyBright(3.0, 0.9), Rate));
        var calm = _det.Detect(new DecodedAudio(QuietSparseDull(3.0), Rate));
        Assert.NotNull(hot);
        Assert.NotNull(calm);
        Assert.True(hot!.Value > calm!.Value,
            $"expected busy/bright ({hot}) > quiet/sparse ({calm})");
    }

    // Loud, spectrally-busy, bright signal: high-freq tones + amplitude bursts (onsets).
    private static float[] BusyBright(double seconds, double amp)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var rng = new Random(1);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            // Bright partials.
            var s = Math.Sin(2 * Math.PI * 1800 * t)
                  + Math.Sin(2 * Math.PI * 3200 * t)
                  + 0.5 * (rng.NextDouble() * 2 - 1); // broadband sizzle
            // 8 Hz amplitude bursts → strong spectral flux / onset density.
            var env = 0.5 + 0.5 * Math.Sign(Math.Sin(2 * Math.PI * 8 * t));
            x[i] = (float)(amp * env * s / 2.0);
        }
        return x;
    }

    // Quiet, steady, dull signal: single low sine, low amplitude, no bursts.
    private static float[] QuietSparseDull(double seconds)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            x[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 220 * t));
        }
        return x;
    }
}
