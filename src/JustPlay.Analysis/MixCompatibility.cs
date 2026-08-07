using JustPlay.Core.Models;

namespace JustPlay.Analysis;

// ============================================================================
// TUNE ON CHLOE'S EAR (Bradley-Terry, see harmonic-mixing.md Sec.Personalisation)
//
// These are provisional defaults derived from the DJ literature consensus:
// - Kell & Tzanetakis ISMIR 2013: "timbre is the strongest predictor" of
//   adjacent track ordering (~ our Beat/groove axis).
// - Kim et al. ISMIR 2020: 86.1% of real DJ tempo adjustments < 5% -> tempo
//   matters but beat/groove texture dominates.
// - Harmonic: Camelot compatibility is the DJ standard but ranks below groove
//   in empirical EDM mix studies.
// - Energy: smoothest set transitions stay within <=2 energy steps (DEAM data).
//
// TO RETUNE: gather >=20 "these mix well / these clash" track pairs from Chloe,
// fit Bradley-Terry logistic regression on the four sub-score differences.
// See harmonic-mixing.md Sec."Bradley-Terry = logistic regression on feature
// differences" for the exact protocol.
// ============================================================================
file static class Weights
{
    public const double Beat     = 0.40;  // BeatFingerprint blended similarity (groove texture)
    public const double Tempo    = 0.30;  // Beatmatch closeness, half/double-aware
    public const double Harmonic = 0.20;  // Camelot-wheel compatibility
    public const double Energy   = 0.10;  // Energy proximity (1-10 scale)

    // Sanity check: must sum to 1.0. Verified below at class init time.
    // If weights are changed, update the check.
    public const double Sum = Beat + Tempo + Harmonic + Energy;  // must equal 1.0
}

// ============================================================================
// Tempo curve parameters
//
// Beatmatch tolerance:
// - ACC1 (Schreiber et al. TISMIR 2020): +/-4% = comfortably beatmatchable without
//   a noticeable pitch-fader push. Score = 1.0 within this band.
// - Decay band: score drops linearly from 1.0 to 0.0 between 4% and 20% relative
//   distance. 20% is approximately the limit a DJ would stretch with EQ help only.
//   Larger offsets score 0.
// - Half/double awareness: fold over {1, 2, 0.5, 1.5, 0.75} before computing
//   the relative distance.  1.5 / 0.75 cover waltz / swing feels and 3:2 polka
//   tempi that DJs occasionally cross between.
// ============================================================================
file static class TempoParams
{
    // Relative-ratio tolerance thresholds.
    public const double PerfectBand   = 0.04;  // <=4%  -> score 1.0 (ACC1, Schreiber et al.)
    public const double ZeroBand      = 0.20;  // >=20% -> score 0.0 (not beatmatchable)

    // Candidate beat-ratio multipliers (the list of "same groove, different metrical level").
    // 1.0 first so same-tempo tracks short-circuit quickly.
    public static readonly double[] Ratios = [1.0, 2.0, 0.5, 1.5, 0.75];
}

/// <summary>
/// Input bundle for <see cref="MixCompatibility.Score"/>.
/// Wraps the four features that contribute to pairwise mix compatibility.
/// All nullable fields degrade gracefully - missing data never crashes the scorer;
/// instead the affected sub-score is excluded and the remaining weights are
/// renormalised to sum to 1.0.
/// </summary>
/// <param name="Bpm">
/// Detected BPM. Null means BPM detection failed or was not run.
/// </param>
/// <param name="Key">
/// Detected musical key. Null means key detection failed or was not run.
/// </param>
/// <param name="Energy">
/// Perceived energy, 1-10 scale (DEAM-calibrated, see SpectralEnergyDetector).
/// Null means energy detection failed or was not run.
/// </param>
/// <param name="Fingerprint">
/// Beat/groove fingerprint (see BeatFingerprintExtractor). Null means the
/// fingerprint was not extracted (short tracks, live material).
/// </param>
public sealed record TrackFeatures(
    double?           Bpm,
    MusicalKey?       Key,
    int?              Energy,
    BeatFingerprint?  Fingerprint);

/// <summary>
/// Combined score and per-axis breakdown returned by <see cref="MixCompatibility.Score"/>.
/// </summary>
/// <param name="Combined">
/// Weighted-average compatibility  in  [0, 1] (1 = perfectly compatible).
/// Computed from whichever sub-scores had data; unavailable axes are excluded
/// and surviving weights renormalised.
/// </param>
/// <param name="Beat">
/// Beat/groove similarity  in  [0, 1], or null if no fingerprint available.
/// High = same rhythmic texture; computed via BeatFingerprintExtractor.BlendedSimilarity
/// (ST 70% + CT 30%, per BeatFingerprint documentation).
/// </param>
/// <param name="Tempo">
/// Beatmatch closeness  in  [0, 1], or null if either BPM is missing.
/// 1.0 within +/-4% (ACC1 beatmatch band); decays linearly to 0 at +/-20%;
/// half/double-aware (best distance over ratios {1, 2, 0.5, 1.5, 0.75}).
/// </param>
/// <param name="Harmonic">
/// Camelot-wheel compatibility  in  [0, 1], or null if either key is missing.
/// Uses a 4-tier table derived from circle-of-fifths tonal-centroid distances
/// (Harte et al. 2006; harmonic-mixing.md).
/// </param>
/// <param name="Energy">
/// Energy proximity  in  [0, 1], or null if either energy value is missing.
/// Formula: 1 - |deltaE| / 9   (linear; max distance = 9 -> score 0).
/// </param>
public sealed record CompatResult(
    double  Combined,
    double? Beat,
    double? Tempo,
    double? Harmonic,
    double? Energy);

/// <summary>
/// Pairwise DJ mix-compatibility scorer - north-star P1 implementation.
///
/// <para><b>API:</b></para>
/// <code>
///   var features_a = new TrackFeatures(bpmA, keyA, energyA, fingerprintA);
///   var features_b = new TrackFeatures(bpmB, keyB, energyB, fingerprintB);
///   CompatResult r = MixCompatibility.Score(features_a, features_b);
///   // r.Combined  in  [0,1]; 1 = best possible mix.
///   // r.Beat / r.Tempo / r.Harmonic / r.Energy are the four sub-scores.
/// </code>
///
/// <para><b>Sub-score formulae (all  in  [0,1]):</b></para>
/// <list type="bullet">
///   <item>
///     <b>Beat</b> = BlendedSimilarity(fingerprintA, fingerprintB) - ST 70% + CT 30%
///     (Holzapfel &amp; Stylianou TASLP 2011; tempo-invariant).
///   </item>
///   <item>
///     <b>Tempo</b> = 1 - clamp((bestRelDist - 0.04) / 0.16, 0, 1)
///     where bestRelDist = min over {1,2,0.5,1.5,0.75} of |1 - bpmA/(ratio-bpmB)|.
///     Score = 1 within +/-4%; linear decay to 0 at +/-20% (ACC1, Schreiber et al. 2020).
///   </item>
///   <item>
///     <b>Harmonic</b> = CamelotScore(keyA, keyB) from a 4-tier table:
///     same = 1.0, relative/fifth = 0.85, parallel = 0.5, clashing = 0.1.
///     (Harte et al. 2006 circle-of-fifths tonal-centroid distances; harmonic-mixing.md.)
///   </item>
///   <item>
///     <b>Energy</b> = 1 - |energyA - energyB| / 9  (linear; energies 1-10).
///   </item>
/// </list>
///
/// <para><b>Combination:</b></para>
/// Weighted average with provisional defaults Beat 0.40, Tempo 0.30, Harmonic 0.20,
/// Energy 0.10 (sum = 1.0). Missing inputs reduce the active term count;
/// surviving weights are renormalised to maintain unit sum.
/// Tune with Bradley-Terry logistic regression on Chloe's pairwise labels
/// (see harmonic-mixing.md Sec."Personalisation").
///
/// <para>
/// Platform-agnostic: no Avalonia, no ManagedBass, no NuGet DSP libs, reflection-free,
/// trim-/AOT-safe. Part of the north-star "sort by harmony" roadmap.
/// </para>
/// </summary>
public static class MixCompatibility
{
    // -------------------------------------------------------------------------
    // Weight-sum guard (compile-time constant arithmetic is verifiable here)
    // -------------------------------------------------------------------------

    static MixCompatibility()
    {
        // If this throws you changed Weights without updating the sum.
        if (Math.Abs(Weights.Sum - 1.0) > 1e-9)
            throw new InvalidOperationException(
                $"MixCompatibility weight constants must sum to 1.0 (got {Weights.Sum}).");
    }

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Compute pairwise mix-compatibility between two tracks.
    /// Score is symmetric (Score(a,b) == Score(b,a)).
    /// </summary>
    public static CompatResult Score(TrackFeatures a, TrackFeatures b)
    {
        // ---- Compute each sub-score where data is available ----
        double? beat     = ComputeBeat(a.Fingerprint, b.Fingerprint);
        double? tempo    = ComputeTempo(a.Bpm, b.Bpm);
        double? harmonic = ComputeHarmonic(a.Key, b.Key);
        double? energy   = ComputeEnergy(a.Energy, b.Energy);

        // ---- Weighted average over available terms ----
        var combined = WeightedAverage(beat, tempo, harmonic, energy);

        return new CompatResult(combined, beat, tempo, harmonic, energy);
    }

    // =========================================================================
    // Sub-score implementations
    // =========================================================================

    // ---- Beat/groove ---------------------------------------------------------

    /// <summary>
    /// Beat/groove sub-score: BlendedSimilarity(ST 70% + CT 30%) from BeatFingerprint.
    /// Returns null if either fingerprint is absent.
    /// Already in [0,1] and tempo-invariant (Holzapfel &amp; Stylianou TASLP 2011).
    /// </summary>
    private static double? ComputeBeat(BeatFingerprint? a, BeatFingerprint? b)
    {
        if (a is null || b is null) return null;
        return BeatFingerprintExtractor.BlendedSimilarity(a, b);
    }

    // ---- Tempo ---------------------------------------------------------------

    /// <summary>
    /// Beatmatch closeness sub-score, half/double-aware.
    ///
    /// <para>Algorithm:</para>
    /// <list type="number">
    ///   <item>
    ///     For each candidate ratio r  in  {1, 2, 0.5, 1.5, 0.75}, compute
    ///     relDist = |1 - bpmA / (r x bpmB)|.  This measures how far bpmA
    ///     would have to stretch to match the shifted bpmB.
    ///   </item>
    ///   <item>
    ///     Take bestRelDist = min(relDist over all ratios).
    ///   </item>
    ///   <item>
    ///     Map to [0,1]:
    ///     <list type="bullet">
    ///       <item>bestRelDist <= 0.04 -> 1.0 (ACC1 beatmatch band, <=4% stretch,
    ///             Schreiber et al. TISMIR 2020).</item>
    ///       <item>bestRelDist >= 0.20 -> 0.0 (not beatmatchable with EQ alone).</item>
    ///       <item>Between 0.04 and 0.20 -> linear decay:
    ///             score = 1 - (bestRelDist - 0.04) / 0.16.</item>
    ///     </list>
    ///   </item>
    /// </list>
    ///
    /// Returns null if either BPM is missing or <= 0.
    /// </summary>
    internal static double? ComputeTempo(double? bpmA, double? bpmB)
    {
        if (bpmA is not > 0.0 || bpmB is not > 0.0) return null;

        var a = bpmA.Value;
        var b = bpmB.Value;

        var bestRelDist = double.MaxValue;
        foreach (var ratio in TempoParams.Ratios)
        {
            var shifted   = ratio * b;
            var relDist   = Math.Abs(1.0 - a / shifted);
            if (relDist < bestRelDist) bestRelDist = relDist;
        }

        if (bestRelDist <= TempoParams.PerfectBand) return 1.0;
        if (bestRelDist >= TempoParams.ZeroBand)    return 0.0;

        // Linear decay in (PerfectBand, ZeroBand).
        var spread = TempoParams.ZeroBand - TempoParams.PerfectBand;   // 0.16
        return 1.0 - (bestRelDist - TempoParams.PerfectBand) / spread;
    }

    // ---- Harmonic ------------------------------------------------------------

    /// <summary>
    /// Camelot-wheel compatibility sub-score.
    ///
    /// <para>
    /// Compatibility tiers are derived from the circle-of-fifths tonal-centroid
    /// distances (Harte, Sandler &amp; Gasser 2006; Lerdahl tonal pitch space;
    /// harmonic-mixing.md Sec."Why the Camelot wheel works").
    /// </para>
    ///
    /// <para><b>Tier table (Camelot distance -> score):</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Tier 0 - Same key</b> (deltanum=0, same letter): score 1.00.
    ///     All 7 pitch-classes shared; tonal-centroid distance = 0.
    ///   </item>
    ///   <item>
    ///     <b>Tier 1 - Relative major/minor</b> (deltanum=0, A<->B): score 0.90.
    ///     Same 7 notes, different tonic; maximum note-sharing.
    ///     E.g. C major (8B) <-> A minor (8A).
    ///   </item>
    ///   <item>
    ///     <b>Tier 2 - Adjacent number, same letter</b> (|deltanum|=1 on the
    ///     circular wheel, same letter): score 0.85.
    ///     One accidental different (6 shared pitch-classes); perfect fifth/fourth
    ///     relationship. The +/-1 energy-boost move falls into this tier.
    ///     E.g. 8A -> 9A or 7A.
    ///   </item>
    ///   <item>
    ///     <b>Tier 3 - Adjacent number, relative of adjacent</b> (|deltanum|=1,
    ///     A<->B cross): score 0.65.  Compatible but less obvious; many DJs use this.
    ///     E.g. 8A -> 9B.
    ///   </item>
    ///   <item>
    ///     <b>Tier 4 - Parallel major/minor</b> (deltanum=+/-3 on the circular
    ///     wheel, same letter, mapping to the parallel mode at same tonic):
    ///     score 0.50. "Halb" compatible per harmonic-mixing.md.
    ///     E.g. C major (8B) -> C minor (5A) = |8-5|=3.
    ///   </item>
    ///   <item>
    ///     <b>Tier 5 - All other</b>: score 0.10 (clashing; significant tonal
    ///     tension that an untrained ear notices).
    ///   </item>
    /// </list>
    ///
    /// Returns null if either key is null (gracefully excluded from the weighted average).
    ///
    /// References: harmonic-mixing.md; Harte et al. ISMIR 2006; Vande Veire &amp; De Bie 2018.
    /// </summary>
    internal static double? ComputeHarmonic(MusicalKey? keyA, MusicalKey? keyB)
    {
        if (keyA is null || keyB is null) return null;

        var a = keyA.Value;
        var b = keyB.Value;

        // Get Camelot wheel number (1-12) for each key.
        var numA = CamelotNumber(a);
        var numB = CamelotNumber(b);

        // Circular distance on the 12-position wheel (wrap 12->1).
        var rawDiff   = Math.Abs(numA - numB);
        var circDiff  = Math.Min(rawDiff, 12 - rawDiff);  // shortest arc, 0-6

        bool sameMode = a.Mode == b.Mode;

        // ---- Tier 0: same key ----
        if (circDiff == 0 && sameMode) return 1.00;

        // ---- Tier 1: relative major/minor (same number, A<->B) ----
        if (circDiff == 0 && !sameMode) return 0.90;

        // ---- Tier 2: adjacent number, same mode ----
        if (circDiff == 1 && sameMode) return 0.85;

        // ---- Tier 3: adjacent number, relative cross (|deltanum|=1, mode differs) ----
        if (circDiff == 1 && !sameMode) return 0.65;

        // ---- Tier 4: parallel major/minor ----
        // Parallel keys share the same tonic but opposite modes.
        // On the Camelot wheel their numbers are +/-3 apart (same letter circle).
        // E.g. C major (8B) <-> C minor (5A): |8-5|=3, circular min = 3.
        // Check: same pitch class, different mode - works regardless of circDiff.
        if (a.PitchClass == b.PitchClass && !sameMode) return 0.50;

        // Also catch the Camelot +/-3 case for the same-mode letter (a rarer tool):
        // e.g. jumping two fifths same direction, same mode.
        if (circDiff == 3 && sameMode) return 0.45;

        // ---- Tier 5: clashing ----
        return 0.10;
    }

    // ---- Energy --------------------------------------------------------------

    /// <summary>
    /// Energy proximity sub-score: 1 - |deltaE| / 9.
    /// Energies are 1-10 so the maximum gap is 9 -> score 0; zero gap -> score 1.
    /// Linear, no curve fitting needed at this stage.
    /// Returns null if either energy value is missing.
    /// </summary>
    internal static double? ComputeEnergy(int? energyA, int? energyB)
    {
        if (energyA is null || energyB is null) return null;
        var delta = Math.Abs(energyA.Value - energyB.Value);
        return 1.0 - delta / 9.0;
    }

    // =========================================================================
    // Weighted average with graceful missing-data handling
    // =========================================================================

    /// <summary>
    /// Computes the weighted average over whichever sub-scores are non-null.
    /// The surviving weights are renormalised to sum to 1.0 so that missing
    /// data doesn't systematically deflate the combined score.
    /// If ALL four are null (no analysis at all), returns 0.5 (neutral/unknown).
    /// </summary>
    private static double WeightedAverage(
        double? beat, double? tempo, double? harmonic, double? energy)
    {
        var sum = 0.0;
        var wSum = 0.0;

        void Add(double? score, double weight)
        {
            if (score is null) return;
            sum  += score.Value * weight;
            wSum += weight;
        }

        Add(beat,     Weights.Beat);
        Add(tempo,    Weights.Tempo);
        Add(harmonic, Weights.Harmonic);
        Add(energy,   Weights.Energy);

        if (wSum <= 0.0) return 0.5;  // no data - neutral

        // Renormalise: sum / wSum gives the weighted mean as if the active weights
        // were scaled to sum to 1.0.
        return Math.Clamp(sum / wSum, 0.0, 1.0);
    }

    // =========================================================================
    // Camelot-number helper
    // =========================================================================

    // Tables replicated from MusicalKey (internal implementation detail - MusicalKey
    // only exposes the formatted "8A"/"8B" string, not the raw number as int).
    // Kept in sync: CamelotMajor[pc] and CamelotMinor[pc] where pc = 0..11 (C..B).
    private static readonly int[] _camelotMajor = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1];
    private static readonly int[] _camelotMinor = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10];

    private static int CamelotNumber(MusicalKey key)
    {
        var pc = ((key.PitchClass % 12) + 12) % 12;
        return key.Mode == KeyMode.Major ? _camelotMajor[pc] : _camelotMinor[pc];
    }
}
