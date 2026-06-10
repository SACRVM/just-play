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
    /// BS.1770 / EBU R128 K-weighted gated integrated loudness in LUFS.
    /// Null when the loudness analysis has not yet run or the track was silent.
    /// </summary>
    public double? LoudnessLufs { get; init; }

    /// <summary>
    /// ReplayGain 2.0 track gain in dB: the adjustment to bring the track to −18 LUFS.
    /// Positive = turn up; negative = turn down. Null when <see cref="LoudnessLufs"/> is null.
    /// Written as <c>REPLAYGAIN_TRACK_GAIN</c> (e.g. <c>"-6.35 dB"</c>).
    /// </summary>
    public double? ReplayGainDb { get; init; }

    /// <summary>
    /// Maximum absolute sample value (linear, 0..~1) in the decoded buffer.
    /// Sample-domain peak (not true-peak / inter-sample). Null when loudness is null.
    /// Written as <c>REPLAYGAIN_TRACK_PEAK</c> (e.g. <c>"0.988553"</c>).
    /// </summary>
    public double? Peak { get; init; }

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

    /// <summary>
    /// Interpretable rhythm-pattern features (four-on-floor strength, swing, syncopation,
    /// off-beat energy, half-time feel) plus a rule-derived <see cref="RhythmPattern.BeatType"/>
    /// label ("4x4-driving", "4x4-groovy", "breaks", etc.).
    /// Computed by <c>JustPlay.Analysis.RhythmPatternDetector</c> alongside the beat fingerprint
    /// from the same 11 kHz onset envelope. Null when analysis has not yet run or the track is
    /// too short.
    /// </summary>
    public RhythmPattern? Rhythm { get; init; }

    // ── Vibe quartet + fatigue flag (v8+) ───────────────────────────────────

    /// <summary>
    /// Continuous blended energy score in [0, 1] — the raw value before the 1–10 integer
    /// mapping. Stored so the 1–10 scale can be re-calibrated to the DJ's ear later without
    /// re-running the full analysis. Null when energy has not been computed.
    /// </summary>
    public double? RawEnergyScore { get; init; }

    /// <summary>
    /// Spectral flatness (geometric-mean / arithmetic-mean of the magnitude spectrum, per
    /// frame, averaged). 0 = tonal/pure; 1 = noise-like/saturated. Core "noisy" signal.
    /// Null when character analysis has not run.
    /// </summary>
    public double? SpectralFlatness { get; init; }

    /// <summary>
    /// Harshness / "noisy" fatigue flag in [0, 1]: combines spectral flatness + crest factor
    /// + HF-energy ratio. High = wall-of-noise/schranz. Kept SEPARATE from the vibe quartet
    /// — it is a quality/fatigue signal, not a mix-character axis.
    /// Null when character analysis has not run.
    /// </summary>
    public double? Harshness { get; init; }

    /// <summary>
    /// Vibe quartet — PUNCH [0, 1].
    /// Low-band (bass, &lt; ~237 Hz) transient sharpness — attack steepness of low-band onsets.
    /// High = fast hard attacks (punchy techno); low = soft or absent bass transients.
    /// Null when character analysis has not run.
    /// </summary>
    public double? BassPunch { get; init; }

    /// <summary>
    /// Vibe quartet — GROOVE [0, 1].
    /// Swing / syncopation of the low-band onset PATTERN relative to the beat grid.
    /// High = swung or off-grid bass (groovy/house feel); low = straight grid-locked bass.
    /// Null when character analysis has not run.
    /// </summary>
    public double? BassGroove { get; init; }

    /// <summary>
    /// Vibe quartet — DARK [0, 1].
    /// Tonal darkness from spectral brightness: 1 = dark (little/no high-frequency content,
    /// e.g. deep minimal); 0 = bright (lots of highs, e.g. euphoric festival trance).
    /// Computed as 1 − normalizedBrightness where brightness = spectral centroid normalised
    /// over [CentroidLo=300 Hz, CentroidHi=3000 Hz] (same normalization as
    /// <c>SpectralEnergyDetector</c>'s energy blend — no extra FFT pass).
    /// Null when character analysis has not run.
    /// </summary>
    public double? Dark { get; init; }

    /// <summary>
    /// Vibe quartet — HYPNOTIC [0, 1].
    /// Repetition / low structural variation: 1 = looping/minimal (same loop repeating),
    /// 0 = evolving/progressive (continuous timbral development).
    /// Computed as 1 − normalizedCentroidVariance: tracks whose spectral centroid is nearly
    /// constant over time score high (minimal/techno loops); tracks with wide timbral
    /// variation score low (progressive/evolving sets). Uses per-frame centroid values from
    /// the existing spectral flatness pass — no extra FFT or decode.
    /// Normalization: CentroidVarLo = 0 Hz², CentroidVarHi = 200 000 Hz² (first-guess).
    /// Null when character analysis has not run.
    /// </summary>
    public double? Hypnotic { get; init; }

    public static readonly AnalysisResult Empty = new();
}
