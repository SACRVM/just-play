namespace JustPlay.Core.Models;

/// <summary>
/// Time-domain beat map of one track: the measured position of EVERY beat, following
/// local tempo drift — a human drummer's timeline, not a rigid grid.
///
/// <para>Computed by <c>JustPlay.Analysis.BeatMapTracker</c>. The record lives in
/// <c>JustPlay.Core</c> (same split as <see cref="BeatFingerprint"/>) so downstream
/// consumers — the beatbed renderer, future JUST SPIN decks — can carry it around
/// without referencing the heavy analysis project.</para>
///
/// <para>NOT persisted in the analysis tag blob: a 6-minute track has ~800 beats and the
/// map is cheap to recompute on demand by the tools that need it. The scalar features
/// (BPM, FourOnFloor, …) remain the persistent summary.</para>
///
/// Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.
/// </summary>
public sealed class BeatMap
{
    /// <summary>Beat instants in seconds from track start; strictly increasing.</summary>
    public double[] BeatTimes { get; }

    /// <summary>
    /// Per-beat onset support in [0, 1] (p95-normalised onset strength at the beat).
    /// Near 0 = the tracker interpolated through a breakdown/silence; high = a real hit.
    /// Same length as <see cref="BeatTimes"/>.
    /// </summary>
    public float[] BeatStrengths { get; }

    /// <summary>Median local BPM over all inter-beat intervals (robust to outliers).</summary>
    public double MedianBpm { get; }

    /// <summary>5th-percentile local BPM — the slow edge of the tempo-drift range.</summary>
    public double MinBpm { get; }

    /// <summary>95th-percentile local BPM — the fast edge of the tempo-drift range.</summary>
    public double MaxBpm { get; }

    /// <summary>
    /// Fraction of beats with real onset support (not interpolated). 1.0 = every beat
    /// had an audible hit; lower values indicate breakdowns/silence the tracker bridged.
    /// </summary>
    public double Coverage { get; }

    /// <summary>Number of beats in the map.</summary>
    public int Count => BeatTimes.Length;

    public BeatMap(
        double[] beatTimes,
        float[] beatStrengths,
        double medianBpm,
        double minBpm,
        double maxBpm,
        double coverage)
    {
        BeatTimes     = beatTimes;
        BeatStrengths = beatStrengths;
        MedianBpm     = medianBpm;
        MinBpm        = minBpm;
        MaxBpm        = maxBpm;
        Coverage      = coverage;
    }
}
