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
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>What our DSP detected (the values, regardless of what's in the standard tags).</summary>
    public AnalysisResult Detected { get; init; } = AnalysisResult.Empty;

    public FieldDecision BpmDecision { get; init; }
    public FieldDecision KeyDecision { get; init; }
    public FieldDecision EnergyDecision { get; init; }
}
