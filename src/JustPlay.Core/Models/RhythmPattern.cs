namespace JustPlay.Core.Models;

/// <summary>
/// Interpretable rhythm-pattern features for a track. Computed by
/// <c>JustPlay.Analysis.RhythmPatternDetector</c> from the onset envelope and beat
/// grid, then persisted in the JUSTPLAY blob (v6+).
///
/// <para>
/// All scalar features are in [0, 1]. <see cref="BeatType"/> is a rule-derived label
/// (not ML) that groups tracks by beat character - four-on-floor driving, groovy house,
/// offbeat-bass, breakbeat, or half-time. Thresholds that drive the labelling are
/// named constants in <c>RhythmPatternDetector.Thresholds</c> and are intentionally
/// tuned on Chloe's ear rather than a ground-truth corpus.
/// </para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.</para>
/// </summary>
public sealed record RhythmPattern
{
    /// <summary>
    /// Strength of a kick on EVERY beat (low-band onset regularity at the beat period).
    /// 0 -> broken/breaks; 1 -> perfect four-on-the-floor.
    /// </summary>
    public double FourOnFloor { get; init; }

    /// <summary>
    /// Emphasis on the off-beat "and" position (mid-point between beats).
    /// Low -> straight techno; high -> house hi-hat feel.
    /// </summary>
    public double OffbeatEnergy { get; init; }

    /// <summary>
    /// Shuffle/swing amount of off-beat events. 0 -> straight (quantised to exact half-beat);
    /// higher -> events land later than the half-beat (swung groove, house feel).
    /// </summary>
    public double Swing { get; init; }

    /// <summary>
    /// Broken-ness: how much onset energy falls off the beat grid.
    /// 0 -> straight 4/4; high -> breakbeat or syncopated pattern.
    /// </summary>
    public double Syncopation { get; init; }

    /// <summary>
    /// Half-time tendency: dominant pulse at half the detected tempo (snare every
    /// other beat). High -> dubstep / trap / half-time feel.
    /// </summary>
    public double HalfTimeFeel { get; init; }

    /// <summary>
    /// Rule-derived beat-type label. One of:
    /// <list type="bullet">
    ///   <item><c>"4x4-driving"</c> - straight four-on-the-floor (techno).</item>
    ///   <item><c>"4x4-groovy"</c> - four-on-the-floor with swing or offbeat emphasis (house).</item>
    ///   <item><c>"offbeat-bass"</c> - four-on-the-floor with strong off-beat bass / synth hook.</item>
    ///   <item><c>"breaks"</c> - low four-on-floor strength / high syncopation (breakbeat).</item>
    ///   <item><c>"halftime"</c> - dominant half-time feel (dubstep/trap).</item>
    /// </list>
    /// Thresholds: see <c>RhythmPatternDetector.Thresholds</c> in JustPlay.Analysis.
    /// </summary>
    public string BeatType { get; init; } = "";
}
