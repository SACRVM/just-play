using JustPlay.Analysis;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="TempoOctaveCorrector"/>.
///
/// All signals are synthetic click trains generated at exactly known tempos.
/// The corrector is tested against three scenarios:
/// <list type="number">
///   <item>BASS_FX-style double-tempo error (click train at 120 BPM, raw reported
///     as 240 BPM → should correct to 120 BPM).</item>
///   <item>BASS_FX-style half-tempo error (click train at 120 BPM, raw reported
///     as 60 BPM → should correct to 120 BPM).</item>
///   <item>Confident-correct case (click train at 128 BPM, raw correctly at 128
///     BPM → should remain 128 BPM, NOT be "corrected" to 64 or 256).</item>
/// </list>
/// </summary>
public class TempoOctaveCorrectorTests
{
    // Analysis runs at this rate (matches TrackAnalysisService.EnergySampleRate).
    private const int SampleRate = 11025;

    // Duration long enough for several autocorrelation periods to accumulate
    // (≥ 10 beats at 60 BPM = 10 s).
    private const double DurationSeconds = 12.0;

    private readonly TempoOctaveCorrector _corrector = new();

    // -------------------------------------------------------------------------
    // Edge / guard cases
    // -------------------------------------------------------------------------

    [Fact]
    public void ZeroBpm_ReturnsZero()
    {
        var samples = ClickTrain(120.0, DurationSeconds, SampleRate);
        var result = _corrector.Correct(0.0, samples, SampleRate);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void EmptySamples_ReturnsRawBpm()
    {
        var result = _corrector.Correct(120.0, [], SampleRate);
        Assert.Equal(120.0, result);
    }

    [Fact]
    public void TooShortSamples_ReturnsRawBpm()
    {
        // Fewer than one onset frame — corrector must not throw and must return raw.
        var tiny = new float[16];
        var result = _corrector.Correct(120.0, tiny, SampleRate);
        Assert.Equal(120.0, result);
    }

    // -------------------------------------------------------------------------
    // Double-tempo correction
    //
    // BASS_FX's range is 45–230 BPM (MinMaxBPM = 0 default). Double-tempo errors
    // therefore occur when the true tempo is 45–115 BPM and BASS_FX detects 2×
    // (still within 230). The tests below use realistic raw BPM values within that
    // range (not 256 / 280, which BASS_FX cannot produce).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Click train at 80 BPM (slow half-time groove), raw BPM reported as 160
    /// (double — common BASS_FX mistake on half-time feels). The corrector should
    /// detect the 80 BPM period and snap down.
    /// </summary>
    [Fact]
    public void DoubleTempo_80Bpm_CorrectedTo_80()
    {
        var samples = ClickTrain(trueBpm: 80.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 160.0, samples, SampleRate);

        // Corrected value must be close to 80 BPM (within 5 BPM tolerance).
        Assert.InRange(corrected, 75.0, 85.0);
    }

    /// <summary>
    /// Click train at 95 BPM, raw reported as 190 (double).
    /// 95 BPM sits in a common hip-hop / RnB range where BASS_FX's kick-pattern
    /// detector can lock onto the 2× level.
    /// </summary>
    [Fact]
    public void DoubleTempo_95Bpm_CorrectedTo_95()
    {
        var samples = ClickTrain(trueBpm: 95.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 190.0, samples, SampleRate);

        Assert.InRange(corrected, 90.0, 100.0);
    }

    /// <summary>
    /// Click train at 100 BPM, raw reported as 200 (double).
    /// </summary>
    [Fact]
    public void DoubleTempo_100Bpm_CorrectedTo_100()
    {
        var samples = ClickTrain(trueBpm: 100.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 200.0, samples, SampleRate);

        Assert.InRange(corrected, 95.0, 105.0);
    }

    // -------------------------------------------------------------------------
    // Half-tempo correction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Click train at 120 BPM, raw BPM reported as 60 (half). The corrector
    /// should prefer the 120 BPM candidate.
    /// </summary>
    [Fact]
    public void HalfTempo_CorrectedTo_TrueTempo()
    {
        var samples = ClickTrain(trueBpm: 120.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 60.0, samples, SampleRate);

        Assert.InRange(corrected, 115.0, 125.0);
    }

    /// <summary>
    /// Click train at 130 BPM (peak-time EDM), raw reported as 65.
    /// </summary>
    [Fact]
    public void HalfTempo_130Bpm_CorrectedTo_130()
    {
        var samples = ClickTrain(trueBpm: 130.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 65.0, samples, SampleRate);

        Assert.InRange(corrected, 125.0, 135.0);
    }

    // -------------------------------------------------------------------------
    // Confident-correct: must NOT be changed
    // -------------------------------------------------------------------------

    /// <summary>
    /// Click train at 128 BPM where BASS_FX also reports 128. The corrector must
    /// leave this unchanged — the conservative margin guard should reject any
    /// deviation when the raw candidate has the dominant autocorrelation peak.
    /// </summary>
    [Fact]
    public void CorrectBpm_IsNotChanged()
    {
        var samples = ClickTrain(trueBpm: 128.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 128.0, samples, SampleRate);

        // Must stay at 128, not jump to 64 or 256.
        Assert.InRange(corrected, 123.0, 133.0);
    }

    /// <summary>
    /// Click train at 120 BPM where raw is correctly 120. Must not drift.
    /// </summary>
    [Fact]
    public void CorrectBpm_120_IsNotChanged()
    {
        var samples = ClickTrain(trueBpm: 120.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 120.0, samples, SampleRate);

        Assert.InRange(corrected, 115.0, 125.0);
    }

    /// <summary>
    /// Click train at 140 BPM, raw correctly 140. Must stay near 140.
    /// This tests that the prior's 120-BPM bias does NOT drag a correct 140-BPM
    /// estimate down to 70 BPM.
    /// </summary>
    [Fact]
    public void CorrectBpm_140_IsNotChanged()
    {
        var samples = ClickTrain(trueBpm: 140.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 140.0, samples, SampleRate);

        // Must stay near 140, not be "corrected" to 70 (which has higher prior weight
        // but a weaker ACF peak — the margin guard ensures the ACF signal wins).
        Assert.InRange(corrected, 135.0, 145.0);
    }

    /// <summary>
    /// A near-silence signal with raw BPM 120. With no clear onset pattern, the
    /// corrector should not change the BPM (no margin can be established from noise).
    /// </summary>
    [Fact]
    public void NearSilence_ReturnsBpmUnchanged()
    {
        // Extremely quiet signal (≈ -100 dBFS) — no meaningful onset structure.
        var n = (int)(DurationSeconds * SampleRate);
        var samples = new float[n];
        var rng = new Random(42);
        for (var i = 0; i < n; i++)
            samples[i] = (float)(1e-5 * (rng.NextDouble() * 2 - 1));

        var corrected = _corrector.Correct(rawBpm: 120.0, samples, SampleRate);

        // Raw value should be returned (no confident correction from noise).
        Assert.Equal(120.0, corrected);
    }

    // -------------------------------------------------------------------------
    // Signal generator
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a synthetic click train at <paramref name="trueBpm"/> BPM: short
    /// Gaussian-shaped pulses at exact beat positions. This models what a BPM
    /// corrector should see — clear onset spikes at the true tempo period — without
    /// needing any real audio files.
    /// </summary>
    private static float[] ClickTrain(double trueBpm, double durationSeconds, int sampleRate)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        var beatPeriodSamples = sampleRate * 60.0 / trueBpm;
        // Click width: ~3 ms Gaussian envelope (tight enough to be onset-like)
        var sigma = sampleRate * 0.003;

        // Place a click at every beat position.
        for (var beat = 0; beat * beatPeriodSamples < n; beat++)
        {
            var beatCentre = beat * beatPeriodSamples;
            // Render a Gaussian pulse of ±4σ around the beat centre.
            var lo = (int)Math.Max(0,    Math.Round(beatCentre - 4 * sigma));
            var hi = (int)Math.Min(n - 1, Math.Round(beatCentre + 4 * sigma));
            for (var i = lo; i <= hi; i++)
            {
                var diff = (i - beatCentre) / sigma;
                samples[i] += (float)(0.9 * Math.Exp(-0.5 * diff * diff));
            }
        }

        return samples;
    }
}
