namespace JustPlay.Core.Models;

/// <summary>
/// Perceptual character constants - kept for backward-compat with existing serialised blobs
/// and for any code that compares against the noisy/punchy/groovy strings.
///
/// <para>The vibe model changed in v8 (2026-06-09): the discrete four-way label
/// (punchy/groovy/noisy/dreamy) was dropped. The continuous <b>vibe quartet</b>
/// (BassPunch, BassGroove, Hypnotic, Dark) now describes each track. The "noisy"
/// concept lives on as <c>Harshness</c> (a separate fatigue flag, not a mix axis).
/// "dreamy" was retired - low-energy tracks are now identified by low
/// <see cref="JustPlay.Core.Models.AnalysisResult.RawEnergyScore"/> directly.
/// </para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.</para>
/// </summary>
public static class TrackCharacter
{
    // These three survive as named strings so that any persisted v7 blobs that are
    // read back by AnalysisStateCodec can still be compared without magic strings.
    // They are NOT used by the classifier in v8+.
    public const string Punchy = "punchy";
    public const string Groovy = "groovy";
    public const string Noisy  = "noisy";
    // Dreamy was retired in v8 - do NOT add it back; use RawEnergyScore instead.
}
