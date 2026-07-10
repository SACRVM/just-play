using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for the click-check render pipeline (<see cref="WavWriter"/> +
/// <see cref="ClickTrackRenderer"/>) — the offline half of beatbed POC-0.
/// </summary>
public class ClickRenderPipelineTests
{
    // =========================================================================
    // WavWriter
    // =========================================================================

    [Fact]
    public void WavWriter_ProducesSpecConformantHeader_AndRoundTripsSamples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jp-wavtest-{Guid.NewGuid():N}.wav");
        try
        {
            // A short ramp including out-of-range values (must clamp, not wrap).
            var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f, 1.5f, -1.5f, 0.25f };
            WavWriter.WriteMono16(path, samples, 44100);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + samples.Length * 2, bytes.Length);

            // RIFF/WAVE/fmt/data markers
            Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
            Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
            Assert.Equal("fmt "u8.ToArray(), bytes[12..16]);
            Assert.Equal("data"u8.ToArray(), bytes[36..40]);

            // mono / 16 bit / 44100
            Assert.Equal(1,     BitConverter.ToInt16(bytes, 22));   // channels
            Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));   // sample rate
            Assert.Equal(16,    BitConverter.ToInt16(bytes, 34));   // bits per sample

            // Sample round-trip: 0.5 → ~16384; ±1.5 clamps to full scale (no wrap!)
            short S(int i) => BitConverter.ToInt16(bytes, 44 + i * 2);
            Assert.Equal(0, S(0));
            Assert.InRange(S(1), 16382, 16385);
            Assert.InRange(S(2), -16385, -16382);
            Assert.Equal(32767, S(5));    // 1.5 clamped
            Assert.Equal(-32767, S(6));   // −1.5 clamped
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WavWriter_RejectsBadArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => WavWriter.WriteMono16("", [0f], 44100));
        Assert.ThrowsAny<ArgumentException>(() => WavWriter.WriteMono16("x.wav", [], 44100));
        Assert.ThrowsAny<ArgumentException>(() => WavWriter.WriteMono16("x.wav", null!, 44100));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => WavWriter.WriteMono16("x.wav", [0f], 0));
    }

    // =========================================================================
    // ClickTrackRenderer
    // =========================================================================

    private static BeatMap MakeMap(double[] times, float[] strengths)
        => new(times, strengths, 120.0, 118.0, 122.0, 1.0);

    [Fact]
    public void RenderMix_PlacesPipsAtBeatTimes_AndPreservesLength()
    {
        const int sr = 44100;
        var original = new float[sr * 3];                 // 3 s of silence
        var map = MakeMap([0.5, 1.5, 2.5], [1f, 1f, 1f]);

        var mix = ClickTrackRenderer.RenderMix(original, sr, map);

        Assert.Equal(original.Length, mix.Length);

        // Pip energy present right AT each beat, none between beats.
        double EnergyAround(double t)
        {
            var c = (int)(t * sr);
            var e = 0.0;
            for (var i = c; i < c + (int)(0.02 * sr); i++) e += Math.Abs(mix[i]);
            return e;
        }
        Assert.True(EnergyAround(0.5) > 1.0, "no pip at first beat");
        Assert.True(EnergyAround(1.5) > 1.0, "no pip at second beat");
        Assert.True(EnergyAround(2.5) > 1.0, "no pip at third beat");
        Assert.True(EnergyAround(1.0) < 0.01, "unexpected energy between beats");
    }

    [Fact]
    public void RenderMix_BridgedBeats_GetTheLowPip()
    {
        const int sr = 44100;
        var original = new float[sr * 2];
        // Beat 1 supported (high pip 1500 Hz), beat 2 bridged (low pip 750 Hz).
        var map = MakeMap([0.5, 1.5], [1f, 0f]);

        var mix = ClickTrackRenderer.RenderMix(original, sr, map);

        // Count zero crossings in the first 10 ms of each pip — the high pip must have
        // about twice as many as the low pip.
        int Crossings(double t)
        {
            var c = (int)(t * sr);
            var n = 0;
            for (var i = c + 1; i < c + (int)(0.010 * sr); i++)
                if (Math.Sign(mix[i]) != Math.Sign(mix[i - 1]) && mix[i] != 0) n++;
            return n;
        }
        var high = Crossings(0.5);
        var low  = Crossings(1.5);
        Assert.True(high > low * 1.5,
            $"expected ~2× zero-crossings for the supported pip (high={high}, low={low})");
    }

    [Fact]
    public void RenderMix_BeatOutsideBuffer_IsIgnoredSafely()
    {
        const int sr = 44100;
        var original = new float[sr];                     // 1 s
        var map = MakeMap([0.5, 5.0, -1.0], [1f, 1f, 1f]); // beats beyond both ends

        var mix = ClickTrackRenderer.RenderMix(original, sr, map);
        Assert.Equal(original.Length, mix.Length);        // no throw, length intact
    }
}
