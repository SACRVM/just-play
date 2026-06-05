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
    public const int CurrentVersion = 4;

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
