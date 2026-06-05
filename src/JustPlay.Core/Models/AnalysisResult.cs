namespace JustPlay.Core.Models;

/// <summary>
/// Result of our own DSP analysis of a track. Every field is nullable so the UI
/// can show partial results as each analyzer finishes (BPM may land before key).
/// </summary>
public sealed record AnalysisResult
{
    /// <summary>Detected tempo in beats per minute.</summary>
    public double? Bpm { get; init; }

    /// <summary>Detected musical key.</summary>
    public MusicalKey? Key { get; init; }

    /// <summary>
    /// Confidence of the key estimate, 0..1. Lets the UI flag uncertain calls
    /// (e.g. show "8A?" when correlation between the top two candidates is close).
    /// </summary>
    public double? KeyConfidence { get; init; }

    /// <summary>
    /// Perceived energy on a 1..10 scale (Mixed-In-Key style), or null if not computed.
    /// </summary>
    public int? Energy { get; init; }

    /// <summary>
    /// Tempo-invariant beat/groove fingerprint (Scale Transform + Cyclic Tempogram + DFA).
    /// Computed by <c>JustPlay.Analysis.BeatFingerprintExtractor</c> during the normal
    /// analysis pass (from the same 11 kHz decode used for energy/BPM-correction).
    /// Null when the track is too short (&lt; ~2 s) or analysis has not yet run.
    ///
    /// <para>
    /// Not shown in the UI directly; used internally for Harmonic Sort's Beat axis
    /// (weight 0.40 in <c>MixCompatibility.Score</c>).
    /// Persisted in the JUSTPLAY file tag so it survives reload without re-analysis.
    /// </para>
    /// </summary>
    public BeatFingerprint? Fingerprint { get; init; }

    public static readonly AnalysisResult Empty = new();
}
