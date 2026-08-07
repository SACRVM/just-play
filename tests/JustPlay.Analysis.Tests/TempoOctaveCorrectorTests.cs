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
///     as 240 BPM -> should correct to 120 BPM).</item>
///   <item>BASS_FX-style half-tempo error (click train at 120 BPM, raw reported
///     as 60 BPM -> should correct to 120 BPM).</item>
///   <item>Confident-correct case (click train at 128 BPM, raw correctly at 128
///     BPM -> should remain 128 BPM, NOT be "corrected" to 64 or 256).</item>
/// </list>
/// </summary>
public class TempoOctaveCorrectorTests
{
    // Analysis runs at this rate (matches TrackAnalysisService.EnergySampleRate).
    private const int SampleRate = 11025;

    // Duration long enough for several autocorrelation periods to accumulate
    // (>= 10 beats at 60 BPM = 10 s).
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
        // Fewer than one onset frame - corrector must not throw and must return raw.
        var tiny = new float[16];
        var result = _corrector.Correct(120.0, tiny, SampleRate);
        Assert.Equal(120.0, result);
    }

    // -------------------------------------------------------------------------
    // Double-tempo correction
    //
    // BASS_FX's range is 45-230 BPM (MinMaxBPM = 0 default). Double-tempo errors
    // therefore occur when the true tempo is 45-115 BPM and BASS_FX detects 2x
    // (still within 230). The tests below use realistic raw BPM values within that
    // range (not 256 / 280, which BASS_FX cannot produce).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Click train at 80 BPM (slow half-time groove), raw BPM reported as 160
    /// (double - common BASS_FX mistake on half-time feels). The corrector should
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
    /// detector can lock onto the 2x level.
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
    /// leave this unchanged - the conservative margin guard should reject any
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
        // but a weaker ACF peak - the margin guard ensures the ACF signal wins).
        Assert.InRange(corrected, 135.0, 145.0);
    }

    /// <summary>
    /// A near-silence signal with raw BPM 120. With no clear onset pattern, the
    /// corrector should not change the BPM (no margin can be established from noise).
    /// </summary>
    [Fact]
    public void NearSilence_ReturnsBpmUnchanged()
    {
        // Extremely quiet signal (~ -100 dBFS) - no meaningful onset structure.
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
    // Quarter-tempo and other gross-error recovery (new hybrid ACF-scan corrector)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Simulates the KRENON "No Need Booster" failure: BASS_FX returned 31 BPM
    /// (a fifth of the true ~155 BPM, below BASS's own 45 BPM floor). The old
    /// corrector could only reach 62 (rawx2). The new corrector's full ACF scan
    /// finds the 155 BPM peak directly.
    /// </summary>
    [Fact]
    public void QuarterTempo_Raw31_CorrectedTo_155()
    {
        // Click train at 155 BPM (energetic tech-house / hard techno territory).
        var samples = ClickTrain(trueBpm: 155.0, DurationSeconds, SampleRate);

        // raw=31 mimics the BASS_FX gross error: it's 155/5, far below any sane window.
        var corrected = _corrector.Correct(rawBpm: 31.0, samples, SampleRate);

        // The ACF scan should bring us close to 155 BPM.
        Assert.InRange(corrected, 148.0, 162.0);
    }

    /// <summary>
    /// Raw BPM is 38 (below BASS's 45 BPM floor - another gross error), true tempo 152.
    /// The old corrector's x2=76 / x3=114 / x4=152 multiples didn't include x4 in the
    /// old code; the new corrector finds 152 via the ACF scan.
    /// </summary>
    [Fact]
    public void Raw38_CorrectedTo_152_ViaScan()
    {
        var samples = ClickTrain(trueBpm: 152.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 38.0, samples, SampleRate);

        Assert.InRange(corrected, 145.0, 158.0);
    }

    /// <summary>
    /// True tempo 170 BPM (hard techno), raw BPM from BASS_FX is 42 (1/4 tempo, out of range).
    /// Since 42 is below BASS_FX's native window (45-230), it is not defended by the
    /// conservative guard. The ACF scan should produce at least a harmonically-correct
    /// result: either 170 itself or its half-tempo 85 (which is octave-adjacent).
    /// Reaching 85 is still a big win over staying at 42 or 84 (rawx2).
    /// Note: ACF aliasing makes 85 and 170 symmetric under the prior - both are equally
    /// plausible from ACF + prior alone. We only assert the corrector escapes the gross
    /// error (raw=42) and reaches a harmonically-sane value.
    /// </summary>
    [Fact]
    public void Raw42OutOfRange_CorrectedToHarmonicNeighbourhood_Of170()
    {
        var samples = ClickTrain(trueBpm: 170.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 42.0, samples, SampleRate);

        // Must escape the gross-error range (not stay at 42 or 84).
        // Accept 85 (half) or 170 (exact) - both are correct-harmonic.
        Assert.True(
            (corrected >= 80.0 && corrected <= 90.0) ||
            (corrected >= 163.0 && corrected <= 177.0),
            $"Expected ~85 or ~170, got {corrected:0.0}");
    }

    /// <summary>
    /// Third-tempo case: true 135 BPM, raw 45 (135/3). Old corrector reaches 90 (x2)
    /// or 135 (x3 - which was NOT in the old code). New corrector finds 135 via scan.
    /// </summary>
    [Fact]
    public void ThirdTempo_Raw45_CorrectedTo_135()
    {
        var samples = ClickTrain(trueBpm: 135.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 45.0, samples, SampleRate);

        Assert.InRange(corrected, 128.0, 142.0);
    }

    /// <summary>
    /// Confident-correct at 155 BPM stays at 155 with the new corrector.
    /// The conservative margin guard must not flip a correct 155 BPM to some other value.
    /// </summary>
    [Fact]
    public void CorrectBpm_155_IsNotChanged()
    {
        var samples = ClickTrain(trueBpm: 155.0, DurationSeconds, SampleRate);
        var corrected = _corrector.Correct(rawBpm: 155.0, samples, SampleRate);

        // Must stay near 155, not regress to 77 or 128 etc.
        Assert.InRange(corrected, 148.0, 162.0);
    }

    // -------------------------------------------------------------------------
    // AcfSharpness (v9+)
    // -------------------------------------------------------------------------

    /// <summary>
    /// CorrectWithSharpness returns the same BPM as Correct AND a sharpness in [0, 1].
    /// </summary>
    [Fact]
    public void CorrectWithSharpness_ReturnsConsistentBpmAndInRangeSharpness()
    {
        var samples = ClickTrain(trueBpm: 128.0, DurationSeconds, SampleRate);

        var corrected          = _corrector.Correct(128.0, samples, SampleRate);
        var (corrBpm, sharpness) = _corrector.CorrectWithSharpness(128.0, samples, SampleRate);

        // BPM from both overloads must match.
        Assert.Equal(corrected, corrBpm, precision: 3);
        // Sharpness must be in [0, 1].
        Assert.InRange(sharpness, 0.0, 1.0);
    }

    /// <summary>
    /// A pure click train at a single tempo has one dominant ACF peak - the sharpness
    /// should be clearly above 0.5 (the midpoint), indicating a well-defined grid.
    /// </summary>
    [Fact]
    public void ClickTrain_AcfSharpness_IsHigherThan_0_5()
    {
        var samples = ClickTrain(trueBpm: 128.0, DurationSeconds, SampleRate);
        var (_, sharpness) = _corrector.CorrectWithSharpness(128.0, samples, SampleRate);

        Assert.True(sharpness > 0.5,
            $"Clean click train should have AcfSharpness > 0.5 (got {sharpness:F3})");
    }

    /// <summary>
    /// Fallback guard: CorrectWithSharpness on a guard input returns 0.0 sharpness.
    /// </summary>
    [Fact]
    public void CorrectWithSharpness_ZeroBpm_ReturnsZeroSharpness()
    {
        var samples = ClickTrain(120.0, DurationSeconds, SampleRate);
        var (bpm, sharpness) = _corrector.CorrectWithSharpness(0.0, samples, SampleRate);

        Assert.Equal(0.0, bpm);
        Assert.Equal(0.0, sharpness);
    }

    // -------------------------------------------------------------------------
    // Signal generator
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a synthetic click train at <paramref name="trueBpm"/> BPM: short
    /// Gaussian-shaped pulses at exact beat positions. This models what a BPM
    /// corrector should see - clear onset spikes at the true tempo period - without
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
            // Render a Gaussian pulse of +/-4sigma around the beat centre.
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
