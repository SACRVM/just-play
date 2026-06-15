namespace JustPlay.Core.Models;

/// <summary>
/// The self-describing analysis record JustPlay stores in a file's JUSTPLAY tag:
/// which engine version ran, what we detected, and the user's per-field decision.
/// Reading it back lets the app reproduce the exact queue picture after a restart
/// without re-analysing — the file is the memory, the app stays stateless.
/// </summary>
public sealed record TrackAnalysisState
{
    /// <summary>
    /// Internal detection-engine version, decoupled from the app version. Bump this
    /// ONLY when an analyzer (BPM/key/energy) actually changes its output, so a stored
    /// state with <c>Version &lt; CurrentVersion</c> triggers a re-analysis while a
    /// cosmetic app release does not.
    /// </summary>
    // v3 (2026-06-03): BPM tempo-octave correction + DEAM-calibrated energy scale —
    // both change detector output, so previously-stamped tracks re-analyse on next drop.
    //
    // v4 (2026-06-05): Beat fingerprint (Scale Transform + Cyclic Tempogram + DFA danceability)
    // added to the normal analysis pass and persisted in the blob. Old blobs (v < 4) are
    // missing the fingerprint → trigger lazy re-analysis on next drop so Harmonic Sort's
    // Beat axis (weight 0.40) becomes populated.
    //
    // v5 (2026-06-05): loudness/ReplayGain (BS.1770 LUFS + ReplayGain 2.0 gain + sample peak)
    // added to the analysis pass and persisted. Old blobs (v < 5) lack it → lazy re-analysis
    // on next drop populates it, enabling REPLAYGAIN_TRACK_GAIN / _PEAK tag writes.
    //
    // v6 (2026-06-08): RhythmPattern (FourOnFloor, OffbeatEnergy, Swing, Syncopation,
    // HalfTimeFeel, BeatType) added to the analysis pass and persisted. Old blobs (v < 6)
    // lack it → lazy re-analysis on next drop so the "Sort by beat character" UI axis works.
    //
    // v7 (2026-06-09): Character classification (punchy/groovy/noisy/dreamy) + supporting
    // scalars (SpectralFlatness, Harshness, BassPunch, BassGroove) + RawEnergyScore added.
    // Old blobs (v < 7) lack it → lazy re-analysis on next drop populates character labels.
    //
    // v8 (2026-06-09): Vibe quartet replaces discrete classifier. The Character string label
    // and "dreamy" were dropped. Two new continuous scores added:
    //   Dark    = 1 − normalizedBrightness (spectral centroid; 1=dark/no highs, 0=bright)
    //   Hypnotic = 1 − normalizedCentroidCV (CV=std_dev/mean; 1=looping/minimal, 0=evolving)
    // All five continuous scores (punch, groove, dark, hypnotic, harshness/noisy) persist.
    // Old v7 blobs lack Dark + Hypnotic → lazy re-analysis on next drop.
    //
    // v9 (2026-06-10): Grid-confidence bundle.
    //   AcfSharpness = ACF peak sharpness ratio [0,1] from TempoOctaveCorrector — zero extra
    //     decode. Predicts tempo-tracking ambiguity (1=sharp, 0=ambiguous/competing peaks).
    //   GridConfidence = 0.40×FoF + 0.25×AcfSharpness + 0.20×(1−HalfTime) + 0.15×(1−Sync2)
    //     [0,1]; ⚠ threshold 0.45. Predicts beatgrid fragility on syncopated genres (UKG/2-step).
    //   Hypnotic bug fixed: switched from absolute std_dev/450 Hz to CV=std_dev/mean (threshold
    //     now 0.5). Real tracks' centroid std-dev was always > 450 Hz → Hypnotic was stuck at 0.
    // Old v8 blobs lack AcfSharpness + GridConfidence → lazy re-analysis on next drop.
    public const int CurrentVersion = 9;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>What our DSP detected (the values, regardless of what's in the standard tags).</summary>
    public AnalysisResult Detected { get; init; } = AnalysisResult.Empty;

    /// <summary>
    /// The foreign tag value(s) that were present BEFORE we overwrote a standard tag with our
    /// own — captured per field on the first <see cref="FieldDecision.Applied"/> write so the
    /// action is reversible ("restore original") and a consciously kept-divergent field can be
    /// told apart from a plain match. Null / per-field-null when nothing was overwritten.
    /// </summary>
    public AnalysisResult? Original { get; init; }

    public FieldDecision BpmDecision { get; init; }
    public FieldDecision KeyDecision { get; init; }
    public FieldDecision EnergyDecision { get; init; }
}
