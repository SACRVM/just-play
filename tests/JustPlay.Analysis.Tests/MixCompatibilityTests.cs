using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="MixCompatibility"/> and <see cref="CompatResult"/>.
///
/// All sub-score formulae are tested in isolation first (internal methods via
/// InternalsVisibleTo is not needed — the internal methods are tested indirectly
/// through <see cref="MixCompatibility.Score"/> and directly where methods are
/// marked internal for testability).
///
/// Signal generation (click trains) follows the same pattern as BeatFingerprintTests.
/// </summary>
public class MixCompatibilityTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private const int    SampleRate      = 11025;
    private const double DurationSeconds = 15.0;

    /// <summary>
    /// Returns a set of features with all four fields populated.
    /// </summary>
    private static TrackFeatures MakeFeatures(
        double bpm, MusicalKey key, int energy, double? fingerprintBpm = null)
    {
        var samples     = ClickTrain(fingerprintBpm ?? bpm, DurationSeconds, SampleRate);
        var fingerprint = BeatFingerprintExtractor.Extract(samples, SampleRate);
        return new TrackFeatures(bpm, key, energy, fingerprint);
    }

    // Convenience key factories.
    private static MusicalKey Key(int pitchClass, KeyMode mode) => new(pitchClass, mode);
    private static MusicalKey CMajor    => Key(0,  KeyMode.Major);  // 8B
    private static MusicalKey AMinor    => Key(9,  KeyMode.Minor);  // 8A  (relative of C major)
    private static MusicalKey GMajor    => Key(7,  KeyMode.Major);  // 9B  (adjacent to 8B)
    private static MusicalKey EMinor    => Key(4,  KeyMode.Minor);  // 9A  (adjacent to 8A)
    private static MusicalKey CMinor    => Key(0,  KeyMode.Minor);  // 5A  (parallel of C major)
    private static MusicalKey FSharpMaj => Key(6,  KeyMode.Major);  // 2B  (across the wheel from 8B)
    private static MusicalKey DMinor    => Key(2,  KeyMode.Minor);  // 7A

    // =========================================================================
    // 1. Identical track vs itself → score ≈ 1.0
    // =========================================================================

    [Fact]
    public void IdenticalTrack_ScoreIsOne()
    {
        var f = MakeFeatures(128.0, CMajor, 7);
        var r = MixCompatibility.Score(f, f);

        Assert.InRange(r.Combined, 0.95, 1.0);
    }

    [Fact]
    public void IdenticalTrack_SubScoresAreOne()
    {
        var f = MakeFeatures(128.0, CMajor, 7);
        var r = MixCompatibility.Score(f, f);

        // Beat: self-similarity must be exactly 1.0.
        Assert.NotNull(r.Beat);
        Assert.True(r.Beat!.Value >= 0.999, $"Beat self-sim = {r.Beat:0.000}");

        // Tempo: same BPM → score 1.0.
        Assert.NotNull(r.Tempo);
        Assert.Equal(1.0, r.Tempo!.Value, precision: 5);

        // Harmonic: same key → score 1.0.
        Assert.NotNull(r.Harmonic);
        Assert.Equal(1.0, r.Harmonic!.Value, precision: 5);

        // Energy: same energy → score 1.0.
        Assert.NotNull(r.Energy);
        Assert.Equal(1.0, r.Energy!.Value, precision: 5);
    }

    // =========================================================================
    // 2. Same Camelot, same BPM, close energy → high combined score
    // =========================================================================

    [Fact]
    public void SameCamelotSameBpmCloseEnergy_HighScore()
    {
        // A minor (8A) vs A minor (8A) — same Camelot code, near-identical BPM,
        // one energy step apart.
        var a = MakeFeatures(128.0, AMinor, 7);
        var b = MakeFeatures(128.5, AMinor, 8);   // 0.4% BPM diff — same Camelot

        var r = MixCompatibility.Score(a, b);
        Assert.True(r.Combined >= 0.80,
            $"Expected high score for near-identical tracks, got {r.Combined:0.000}");
    }

    // =========================================================================
    // 3. Clashing key + very different tempo → low combined score
    // =========================================================================

    [Fact]
    public void ClashingKeyVeryDifferentTempo_LowScore()
    {
        // C major (8B) vs F# major (2B): maximum tonal distance (tritone, 6 semitones).
        // Harmonic sub-score = 0.10 (Tier 5 — clashing).
        //
        // BPM: 128 vs 320. Best ratio distance:
        //   r=0.5 → 0.5×320=160, |1−128/160| = 0.20 → ZeroBand boundary → Tempo = 0.0.
        // Energy: 5 vs 1 → |Δ|=4 → 1−4/9 ≈ 0.556.
        // Beat: click trains at 128 vs 320 BPM (very different rhythmic texture) → low.
        // Combined (all available, renormalised): should be clearly below 0.45.
        var a = MakeFeatures(128.0, CMajor,    5, 128.0);
        var b = MakeFeatures(320.0, FSharpMaj, 1, 320.0);

        var r = MixCompatibility.Score(a, b);
        Assert.True(r.Combined <= 0.45,
            $"Expected low score for clashing pair, got {r.Combined:0.000}");
    }

    // =========================================================================
    // 4. Half/double tempo pair → tempo sub-score stays HIGH
    // =========================================================================

    [Fact]
    public void HalfDoubleTempo_TempoSubScoreIsHigh()
    {
        // 70 BPM vs 140 BPM: exact 2:1 ratio → bestRelDist = 0 → tempo score = 1.0.
        // Even if the combined score is moderated by other axes, the TEMPO axis alone
        // must not penalise this pair.
        var a = new TrackFeatures(70.0, CMajor, 6, null);
        var b = new TrackFeatures(140.0, CMajor, 6, null);

        var r = MixCompatibility.Score(a, b);

        Assert.NotNull(r.Tempo);
        Assert.Equal(1.0, r.Tempo!.Value, precision: 5);
    }

    [Fact]
    public void HalfDoubleTempo_MismatchedKeys_TempoSubScoreIsHigh()
    {
        // Verify the half/double awareness is preserved even when other features differ.
        var a = new TrackFeatures(70.0,  CMajor, 5, null);
        var b = new TrackFeatures(140.0, AMinor, 8, null);

        var r = MixCompatibility.Score(a, b);

        Assert.NotNull(r.Tempo);
        Assert.True(r.Tempo!.Value >= 0.99,
            $"70 vs 140 BPM should have tempo score ≈ 1.0, got {r.Tempo:0.000}");
    }

    [Fact]
    public void ThreeHalvesTempo_TempoSubScoreIsHigh()
    {
        // 150 BPM vs 100 BPM: ratio 1.5 applied to bpmB (100) → shifted = 150 = bpmA
        // → bestRelDist = |1 − 150/150| = 0 → score = 1.0.
        // (The ratio candidates are applied to bpmB: ratio × bpmB; 1.5 × 100 = 150.)
        var a = new TrackFeatures(150.0, CMajor, 6, null);
        var b = new TrackFeatures(100.0, CMajor, 6, null);

        var r = MixCompatibility.Score(a, b);

        Assert.NotNull(r.Tempo);
        Assert.Equal(1.0, r.Tempo!.Value, precision: 5);
    }

    // =========================================================================
    // 5. Missing key on one track → no crash, sensible neutral harmonic term
    // =========================================================================

    [Fact]
    public void MissingKeyOnOneTrack_NoCrash_NeutralHarmonic()
    {
        var a = new TrackFeatures(128.0, null,   7, null);   // key missing
        var b = new TrackFeatures(128.0, CMajor, 7, null);

        CompatResult r;
        var ex = Record.Exception(() => r = MixCompatibility.Score(a, b));
        Assert.Null(ex);  // must not throw

        r = MixCompatibility.Score(a, b);
        Assert.Null(r.Harmonic);   // missing key → harmonic term unavailable

        // Combined score is still valid (only Tempo + Energy contribute here since
        // Fingerprint is also null → Beat missing; Harmonic missing).
        Assert.InRange(r.Combined, 0.0, 1.0);
    }

    [Fact]
    public void BothKeysMissing_HarmonicIsNull_CombinedStillValid()
    {
        var a = new TrackFeatures(128.0, null, 7, null);
        var b = new TrackFeatures(128.0, null, 7, null);

        var r = MixCompatibility.Score(a, b);

        Assert.Null(r.Harmonic);
        Assert.InRange(r.Combined, 0.0, 1.0);
    }

    [Fact]
    public void AllFieldsMissing_ReturnsNeutral()
    {
        // If absolutely no data is available, the combined score should be the
        // neutral 0.5 (not 0.0, not 1.0 — see WeightedAverage docs).
        var a = new TrackFeatures(null, null, null, null);
        var b = new TrackFeatures(null, null, null, null);

        var r = MixCompatibility.Score(a, b);

        Assert.Equal(0.5, r.Combined, precision: 5);
        Assert.Null(r.Beat);
        Assert.Null(r.Tempo);
        Assert.Null(r.Harmonic);
        Assert.Null(r.Energy);
    }

    // =========================================================================
    // 6. Sub-scores are all in [0,1] and combined is in [0,1]
    // =========================================================================

    [Theory]
    [InlineData(120.0, 128.0)]
    [InlineData(70.0,  140.0)]
    [InlineData(128.0, 95.0)]
    [InlineData(200.0, 60.0)]
    public void SubScoresAndCombined_AlwaysInUnitInterval(double bpmA, double bpmB)
    {
        var a = MakeFeatures(bpmA, AMinor, 5);
        var b = MakeFeatures(bpmB, GMajor, 8);

        var r = MixCompatibility.Score(a, b);

        Assert.InRange(r.Combined, 0.0, 1.0);
        if (r.Beat     is not null) Assert.InRange(r.Beat.Value,     0.0, 1.0);
        if (r.Tempo    is not null) Assert.InRange(r.Tempo.Value,    0.0, 1.0);
        if (r.Harmonic is not null) Assert.InRange(r.Harmonic.Value, 0.0, 1.0);
        if (r.Energy   is not null) Assert.InRange(r.Energy.Value,   0.0, 1.0);
    }

    // =========================================================================
    // 7. Harmonic tier spot-checks (via full Score call)
    // =========================================================================

    [Fact]
    public void Harmonic_SameKey_IsOne()
    {
        var a = new TrackFeatures(128.0, CMajor, 5, null);
        var b = new TrackFeatures(128.0, CMajor, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(1.0, r.Harmonic!.Value, precision: 5);
    }

    [Fact]
    public void Harmonic_RelativeMajorMinor_IsHighest()
    {
        // C major (8B) ↔ A minor (8A) — same Camelot number, A↔B → Tier 1 = 0.90.
        var a = new TrackFeatures(128.0, CMajor, 5, null);
        var b = new TrackFeatures(128.0, AMinor, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.90, r.Harmonic!.Value, precision: 5);
    }

    [Fact]
    public void Harmonic_AdjacentNumberSameMode_IsHighSecondTier()
    {
        // C major (8B) ↔ G major (9B) — adjacent number, same mode → Tier 2 = 0.85.
        var a = new TrackFeatures(128.0, CMajor, 5, null);
        var b = new TrackFeatures(128.0, GMajor, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.85, r.Harmonic!.Value, precision: 5);
    }

    [Fact]
    public void Harmonic_AdjacentNumberCrossMode_IsTier3()
    {
        // A minor (8A) ↔ G major (9B) — adjacent number, mode differs → Tier 3 = 0.65.
        var a = new TrackFeatures(128.0, AMinor, 5, null);
        var b = new TrackFeatures(128.0, GMajor, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.65, r.Harmonic!.Value, precision: 5);
    }

    [Fact]
    public void Harmonic_ParallelMajorMinor_IsMedium()
    {
        // C major (8B) ↔ C minor (5A) — same tonic, different mode → Tier 4 = 0.50.
        var a = new TrackFeatures(128.0, CMajor, 5, null);
        var b = new TrackFeatures(128.0, CMinor, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.50, r.Harmonic!.Value, precision: 5);
    }

    [Fact]
    public void Harmonic_ClashingKeys_IsLow()
    {
        // C major (8B) ↔ F# major (2B): maximum tonal distance (tritone) → Tier 5 = 0.10.
        var a = new TrackFeatures(128.0, CMajor,    5, null);
        var b = new TrackFeatures(128.0, FSharpMaj, 5, null);

        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.10, r.Harmonic!.Value, precision: 5);
    }

    // =========================================================================
    // 8. Tempo sub-score boundary conditions
    // =========================================================================

    [Fact]
    public void Tempo_SameBpm_IsOne()
    {
        var a = new TrackFeatures(128.0, null, null, null);
        var b = new TrackFeatures(128.0, null, null, null);
        var r = MixCompatibility.Score(a, b);
        Assert.Equal(1.0, r.Tempo!.Value, precision: 5);
    }

    [Fact]
    public void Tempo_WithinPerfectBand_IsOne()
    {
        // 128 vs 130: relDist ≈ 1.56% < 4% → score 1.0.
        var a = new TrackFeatures(128.0, null, null, null);
        var b = new TrackFeatures(130.0, null, null, null);
        var r = MixCompatibility.Score(a, b);
        Assert.Equal(1.0, r.Tempo!.Value, precision: 5);
    }

    [Fact]
    public void Tempo_BeyondZeroBand_IsZero()
    {
        // 128 vs 80: check all candidate ratios r × 80:
        //   r=1.0  → 80,   |1−128/80|   = 0.600 > 0.20
        //   r=2.0  → 160,  |1−128/160|  = 0.200 = ZeroBand threshold → boundary → 0
        //   r=0.5  → 40,   |1−128/40|   = 2.200 > 0.20
        //   r=1.5  → 120,  |1−128/120|  = 0.067 → IN DECAY BAND → score ≈ 0.833
        // Oops — ratio 1.5 saves it. Use 128 vs 60 instead:
        //   r=1.0  → 60,   |1−128/60|   = 1.133
        //   r=2.0  → 120,  |1−128/120|  = 0.067 → IN DECAY BAND
        // Use a pair where no ratio puts it inside the ZeroBand.
        // 128 vs 77: r=1.0→77, |1−128/77|=0.662; r=2→154, |1−128/154|=0.169 < 0.20 → IN BAND.
        // Simpler: use 128 vs 200:
        //   r=1.0  → 200,  |1−128/200|  = 0.360
        //   r=2.0  → 400,  |1−128/400|  = 0.680
        //   r=0.5  → 100,  |1−128/100|  = 0.280
        //   r=1.5  → 300,  |1−128/300|  = 0.573
        //   r=0.75 → 150,  |1−128/150|  = 0.147 < 0.20 → IN BAND (score ≈ 0.331)
        // The ratio table intentionally covers 1.5/0.75 so BPM pairs that are
        // in a 3:2 feel can still score well. A pair that is truly "not beatmatchable"
        // must lie outside ALL candidate ratio windows.
        // 128 vs 85:
        //   r=1.0  → 85,   |1−128/85|   = 0.506
        //   r=2.0  → 170,  |1−128/170|  = 0.247
        //   r=0.5  → 42.5, |1−128/42.5| = 2.012
        //   r=1.5  → 127.5,|1−128/127.5|= 0.004 → PERFECT BAND! (≤0.04) → score = 1
        // 1.5 × 85 = 127.5 ≈ 128 — this pair is beatmatchable via the 3:2 feel.
        //
        // Use 128 vs 107 (chosen so NO ratio gives < 0.20):
        //   r=1.0  → 107,  |1−128/107|  = 0.196 < 0.20  → IN BAND too.
        //
        // The 1.5 and 0.75 ratios make it hard to find a pair in the true "zero zone"
        // that also avoids all five ratio windows. Use a pair analytically:
        // Need bestRelDist > 0.20 over {1, 2, 0.5, 1.5, 0.75}.
        // bpmA=128, scan bpmB values: b such that for ALL r in set, |1 - 128/(r*b)| > 0.20.
        // r=1:   |1-128/b| > 0.20 → b < 128/1.2 ≈ 106.7 OR b > 128/0.8 = 160
        // r=2:   |1-128/(2b)| > 0.20 → 2b < 106.7 → b < 53.3 OR 2b > 160 → b > 80
        // r=0.5: |1-128/(0.5b)| > 0.20 → 0.5b < 106.7 → b < 213.4 OR 0.5b > 160 → b > 320
        // r=1.5: |1-128/(1.5b)| > 0.20 → 1.5b < 106.7 → b < 71.1 OR 1.5b > 160 → b > 106.7
        // r=0.75:|1-128/(0.75b)|> 0.20 → 0.75b < 106.7→ b<142.3 OR 0.75b > 160 → b > 213.4
        //
        // Intersection of all OR-clauses (b must fail ALL ratio windows):
        // This is complex — the score is designed to be inclusive. The test is changed to
        // verify the MIDWAY DECAY case instead, which is well-defined.
        // A clearer zero-zone pair: bpmB = 320 (well above anything usable even at 0.5×).
        //   r=1.0  → 320,  |1−128/320|  = 0.600
        //   r=2.0  → 640,  |1−128/640|  = 0.800
        //   r=0.5  → 160,  |1−128/160|  = 0.200 = ZeroBand exactly → score 0 (boundary)
        //   r=1.5  → 480,  |1−128/480|  = 0.733
        //   r=0.75 → 240,  |1−128/240|  = 0.467
        // Best relDist = 0.200 → score = 0 (boundary condition, ZeroBand = 0.20).
        var a = new TrackFeatures(128.0, null, null, null);
        var b = new TrackFeatures(320.0, null, null, null);
        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.0, r.Tempo!.Value, precision: 5);
    }

    [Fact]
    public void Tempo_MidwayInDecayBand_IsHalf()
    {
        // Design: at relDist = 0.12, score = 1 - (0.12 - 0.04)/0.16 = 1 - 0.5 = 0.5.
        // bpmA/bpmB = 1 + 0.12 → bpmB = 128 / 1.12 ≈ 114.286 BPM.
        // Best ratio=1: relDist = |1 − 128/114.286| = 0.12 exactly.
        var bpmA = 128.0;
        var bpmB = bpmA / 1.12;

        var a = new TrackFeatures(bpmA, null, null, null);
        var b = new TrackFeatures(bpmB, null, null, null);
        var r = MixCompatibility.Score(a, b);

        Assert.True(Math.Abs(r.Tempo!.Value - 0.5) < 0.01,
            $"Expected tempo score ≈ 0.50 for midway decay, got {r.Tempo:0.000}");
    }

    // =========================================================================
    // 9. Energy sub-score
    // =========================================================================

    [Fact]
    public void Energy_SameEnergy_IsOne()
    {
        var a = new TrackFeatures(null, null, 7, null);
        var b = new TrackFeatures(null, null, 7, null);
        var r = MixCompatibility.Score(a, b);
        Assert.Equal(1.0, r.Energy!.Value, precision: 5);
    }

    [Fact]
    public void Energy_MaxDifference_IsZero()
    {
        // 1 vs 10 → |Δ| = 9 → score = 1 − 9/9 = 0.
        var a = new TrackFeatures(null, null, 1,  null);
        var b = new TrackFeatures(null, null, 10, null);
        var r = MixCompatibility.Score(a, b);
        Assert.Equal(0.0, r.Energy!.Value, precision: 5);
    }

    [Fact]
    public void Energy_ThreeDifference_IsCorrect()
    {
        // |Δ| = 3 → score = 1 − 3/9 = 0.667.
        var a = new TrackFeatures(null, null, 4, null);
        var b = new TrackFeatures(null, null, 7, null);
        var r = MixCompatibility.Score(a, b);
        Assert.True(Math.Abs(r.Energy!.Value - (1.0 - 3.0 / 9.0)) < 1e-6,
            $"Expected energy score ≈ 0.667, got {r.Energy:0.000}");
    }

    // =========================================================================
    // 10. Symmetry: Score(a,b) == Score(b,a)
    // =========================================================================

    [Fact]
    public void Score_IsSymmetric()
    {
        var a = MakeFeatures(128.0, CMajor, 7);
        var b = MakeFeatures(132.0, EMinor, 5);

        var ab = MixCompatibility.Score(a, b);
        var ba = MixCompatibility.Score(b, a);

        Assert.True(Math.Abs(ab.Combined - ba.Combined) < 1e-10,
            $"Score(a,b)={ab.Combined:0.000} ≠ Score(b,a)={ba.Combined:0.000}");
    }

    // =========================================================================
    // 11. Missing fingerprint degrades gracefully (Beat null, rest still scored)
    // =========================================================================

    [Fact]
    public void MissingFingerprint_BeatIsNull_CombinedIsValid()
    {
        var a = new TrackFeatures(128.0, CMajor, 7, null);
        var b = new TrackFeatures(128.0, CMajor, 7, null);

        var r = MixCompatibility.Score(a, b);

        Assert.Null(r.Beat);
        Assert.InRange(r.Combined, 0.0, 1.0);

        // With identical Tempo, Harmonic, Energy, combined should be close to 1.
        Assert.True(r.Combined >= 0.90,
            $"Expected high combined even without Beat, got {r.Combined:0.000}");
    }

    // =========================================================================
    // Signal generators (same as BeatFingerprintTests)
    // =========================================================================

    private static float[] ClickTrain(double trueBpm, double durationSeconds, int sampleRate)
    {
        var n                 = (int)(durationSeconds * sampleRate);
        var samples           = new float[n];
        var beatPeriodSamples = sampleRate * 60.0 / trueBpm;
        var sigma             = sampleRate * 0.003;  // ~3 ms Gaussian pulse

        for (var beat = 0; beat * beatPeriodSamples < n; beat++)
        {
            var beatCentre = beat * beatPeriodSamples;
            var lo = (int)Math.Max(0,     Math.Round(beatCentre - 4 * sigma));
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
