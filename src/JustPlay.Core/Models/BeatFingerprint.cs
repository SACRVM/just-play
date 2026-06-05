namespace JustPlay.Core.Models;

/// <summary>
/// Tempo-invariant beat/groove fingerprint for DJ mix-compatibility scoring.
///
/// <para>
/// Three-component descriptor computed by <c>JustPlay.Analysis.BeatFingerprintExtractor</c>
/// from decoded mono audio.  The record lives in <c>JustPlay.Core</c> so it can be
/// stored on <see cref="AnalysisResult"/> and round-tripped through
/// <see cref="AnalysisStateCodec"/> without a circular project dependency.
/// The extractor (heavy DSP computation) stays in <c>JustPlay.Analysis</c>.
/// </para>
///
/// <list type="bullet">
///   <item>
///     <b>ScaleTransform</b> — L2-normalised Scale Transform magnitude vector (64 bins).
///     Primary groove descriptor; cosine similarity is tempo-invariant.
///     (Holzapfel &amp; Stylianou TASLP 2011; Panteli &amp; Dixon ISMIR 2016.)
///   </item>
///   <item>
///     <b>CyclicTempogram</b> — octave-folded tempo periodicity histogram (24 bins),
///     L2-normalised. Invariant to 2:1 tempo scaling.
///     (Grosche, Müller &amp; Kurth ICASSP 2010.)
///   </item>
///   <item>
///     <b>Danceability</b> — DFA (Detrended Fluctuation Analysis) scalar ≈ 0–3;
///     higher = more rhythmically steady / danceable.
///     (Streich &amp; Herrera AES 2005 / Essentia danceability.)
///   </item>
/// </list>
///
/// Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.
/// </summary>
public sealed class BeatFingerprint
{
    /// <summary>
    /// L2-normalised Scale Transform magnitude vector (64 bins).
    /// Primary groove descriptor — cosine similarity is the recommended distance.
    /// </summary>
    public float[] ScaleTransform { get; }

    /// <summary>
    /// Cyclic tempogram — octave-folded periodicity histogram (24 bins), L2-normalised.
    /// Complement to the Scale Transform; invariant to power-of-two tempo scaling.
    /// </summary>
    public float[] CyclicTempogram { get; }

    /// <summary>
    /// DFA danceability scalar — reciprocal of mean DFA exponent α.
    /// Range roughly 0–3; higher = more rhythmically steady / danceable.
    /// </summary>
    public float Danceability { get; }

    public BeatFingerprint(float[] scaleTransform, float[] cyclicTempogram, float danceability)
    {
        ScaleTransform  = scaleTransform;
        CyclicTempogram = cyclicTempogram;
        Danceability    = danceability;
    }
}
