using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Synthetic-signal tests for <see cref="ChromagramKeyDetector"/>. All signals are
/// generated deterministically (fixed frequencies, no randomness) so the asserted
/// key is reproducible across runs and machines.
/// </summary>
public class ChromagramKeyDetectorTests
{
    private const int SampleRate = 11025; // matches the decoder's downsample target
    private const double DurationSeconds = 6.0;

    // Equal-tempered frequencies (A4 = 440 Hz). Octave chosen mid-range so several
    // harmonics land inside the analysis band.
    private const double C4 = 261.6256;
    private const double E4 = 329.6276;
    private const double G4 = 391.9954;
    private const double A4 = 440.0000;

    [Fact]
    public void DetectsCMajorTriad()
    {
        var samples = MakeTriad(C4, E4, G4);
        var detector = new ChromagramKeyDetector();

        var result = detector.Detect(new DecodedAudio(samples, SampleRate));

        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.Key.PitchClass);          // C
        Assert.Equal(KeyMode.Major, result.Value.Key.Mode);
        Assert.Equal("8B", result.Value.Key.Camelot);
        Assert.InRange(result.Value.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void DetectsAMinorTriad()
    {
        // A minor triad: A - C - E.
        var samples = MakeTriad(A4, C4, E4);
        var detector = new ChromagramKeyDetector();

        var result = detector.Detect(new DecodedAudio(samples, SampleRate));

        Assert.NotNull(result);
        Assert.Equal(9, result!.Value.Key.PitchClass);          // A
        Assert.Equal(KeyMode.Minor, result.Value.Key.Mode);
        Assert.Equal("8A", result.Value.Key.Camelot);
        Assert.InRange(result.Value.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void ConfidenceIsInUnitRange()
    {
        var samples = MakeTriad(C4, E4, G4);
        var result = new ChromagramKeyDetector().Detect(new DecodedAudio(samples, SampleRate));

        Assert.NotNull(result);
        Assert.True(result!.Value.Confidence >= 0.0 && result.Value.Confidence <= 1.0);
    }

    [Fact]
    public void SilenceReturnsNull()
    {
        var silence = new float[(int)(SampleRate * DurationSeconds)]; // all zeros
        var result = new ChromagramKeyDetector().Detect(new DecodedAudio(silence, SampleRate));

        Assert.Null(result);
    }

    [Fact]
    public void EmptyReturnsNull()
    {
        var result = new ChromagramKeyDetector().Detect(new DecodedAudio([], SampleRate));
        Assert.Null(result);
    }

    [Fact]
    public void TooShortReturnsNull()
    {
        // Fewer than one frame's worth of samples.
        var tiny = new float[1024];
        for (var i = 0; i < tiny.Length; i++)
            tiny[i] = 0.5f * (float)Math.Sin(2.0 * Math.PI * C4 * i / SampleRate);

        var result = new ChromagramKeyDetector().Detect(new DecodedAudio(tiny, SampleRate));
        Assert.Null(result);
    }

    /// <summary>
    /// Builds a sustained triad by summing three equal-amplitude sine tones.
    /// Amplitude leaves headroom so the sum never clips outside [-1, 1].
    /// </summary>
    private static float[] MakeTriad(double f1, double f2, double f3)
    {
        var count = (int)(SampleRate * DurationSeconds);
        var samples = new float[count];
        const double amp = 0.3;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var v = amp * Math.Sin(2.0 * Math.PI * f1 * t)
                  + amp * Math.Sin(2.0 * Math.PI * f2 * t)
                  + amp * Math.Sin(2.0 * Math.PI * f3 * t);
            samples[i] = (float)v;
        }
        return samples;
    }
}
