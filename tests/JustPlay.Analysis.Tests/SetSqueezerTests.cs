using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="SetSqueezer"/> (Phase 2 of the harmonic-sort north star).
///
/// The fixtures are built so the "right" answer is obvious from the compatibility scores:
/// a tight cohesive cluster (same Camelot neighbourhood, ~128 BPM, energy 7 -> pairwise
/// compat ~0.9) vs clear outliers (clashing key + un-beatmatchable 320 BPM -> compat &lt; 0.1).
///
/// Fingerprint = null throughout, so the Beat axis is excluded and the compat renormalises over
/// Tempo/Harmonic/Energy - exactly how Squeeze runs over the sidecar index (no fingerprint stored).
/// </summary>
public class SetSqueezerTests
{
    // -- Key factories (Camelot in comments) --------------------------------------
    private static MusicalKey CMajor => new(0, KeyMode.Major);   // 8B
    private static MusicalKey AMinor => new(9, KeyMode.Minor);   // 8A  relative of C major
    private static MusicalKey GMajor => new(7, KeyMode.Major);   // 9B  adjacent to C major
    private static MusicalKey FSharp => new(6, KeyMode.Major);   // 2B  tritone from 8B (clashing)

    private static TrackFeatures F(double? bpm, MusicalKey? key, int? energy)
        => new(bpm, key, energy, Fingerprint: null);

    // A cohesive cluster member (~128 BPM, energy 7, 8-area Camelot key).
    private static TrackFeatures Cluster(MusicalKey key, double bpm = 128.0)
        => F(bpm, key, 7);

    // A clear outlier: un-beatmatchable 320 BPM (tempo 0 to a 128 track) + clashing key + low energy.
    private static TrackFeatures Outlier(double bpm = 320.0)
        => F(bpm, FSharp, 1);

    // =========================================================================
    // 1. Keeps the cohesive cluster, drops the outliers
    // =========================================================================

    [Fact]
    public void Squeeze_KeepsCohesiveCluster_DropsOutliers()
    {
        // 4 cluster + 2 outliers; ask to keep exactly the cluster size.
        var pool = new TrackFeatures?[]
        {
            Cluster(CMajor),        // 0
            Cluster(CMajor),        // 1  identical to 0
            Cluster(AMinor),        // 2
            Cluster(GMajor),        // 3
            Outlier(320.0),         // 4
            Outlier(318.0),         // 5
        };

        var r = SetSqueezer.Squeeze(pool, keep: 4);

        Assert.Equal(4, r.KeptIndices.Count);
        Assert.Equal(2, r.DroppedIndices.Count);

        // The kept set must be exactly the cluster {0,1,2,3} (order may be sequenced).
        Assert.Equal(new[] { 0, 1, 2, 3 }, r.KeptIndices.OrderBy(x => x).ToArray());
        // The outliers {4,5} must be the dropped ones.
        Assert.Equal(new[] { 4, 5 }, r.DroppedIndices.OrderBy(x => x).ToArray());

        Assert.True(r.EnoughCoherent, "cluster of 4 should be fully coherent");
        Assert.Equal(4, r.CoherentCount);
        Assert.True(r.MeanCohesion > 0.8, $"mean cohesion should be high, got {r.MeanCohesion:0.000}");
        Assert.True(r.MinCohesion > 0.6, $"weakest cluster pair should clear threshold, got {r.MinCohesion:0.000}");
        Assert.Equal(0, r.UnanalyzedDropped);

        // Kept  union  Dropped covers the whole pool exactly once (never leave a track behind).
        AssertPartition(pool.Length, r);
    }

    // =========================================================================
    // 2. Honest "not enough coherent" when N exceeds the coherent count
    // =========================================================================

    [Fact]
    public void Squeeze_NotEnoughCoherent_FlagsHonestly()
    {
        // 3 cluster + 2 outliers; ask for 5 (the whole pool) -> outliers must be forced in.
        var pool = new TrackFeatures?[]
        {
            Cluster(CMajor),   // 0
            Cluster(AMinor),   // 1
            Cluster(GMajor),   // 2
            Outlier(320.0),    // 3
            Outlier(318.0),    // 4
        };

        var r = SetSqueezer.Squeeze(pool, keep: 5);

        Assert.Equal(5, r.KeptIndices.Count);          // best-effort: returns N
        Assert.Empty(r.DroppedIndices);                // nothing left to drop
        Assert.False(r.EnoughCoherent);                // ...but honestly flagged
        Assert.Equal(3, r.CoherentCount);              // only the 3 cluster tracks cohere
        Assert.Contains("Only 3", r.Message);
        Assert.True(r.MinCohesion < 0.2,
            $"weakest pair (cluster vs outlier) should be tiny, got {r.MinCohesion:0.000}");

        AssertPartition(pool.Length, r);
    }

    // =========================================================================
    // 3. Seed = the most central track (not just index 0), drops the outlier
    // =========================================================================

    [Fact]
    public void Squeeze_SeedIsMostCentralTrack_NotIndexZero()
    {
        // Outlier sits at index 0; the cohesive cluster is at 1,2,3.
        var pool = new TrackFeatures?[]
        {
            Outlier(320.0),    // 0  outlier first on purpose
            Cluster(CMajor),   // 1
            Cluster(CMajor),   // 2
            Cluster(AMinor),   // 3
        };

        var r = SetSqueezer.Squeeze(pool, keep: 3);

        // Seed must be one of the cluster tracks, never the outlier at 0.
        Assert.NotEqual(0, r.SeedIndex);
        Assert.Contains(r.SeedIndex, new[] { 1, 2, 3 });

        // Kept = the cluster; the outlier is dropped.
        Assert.Equal(new[] { 1, 2, 3 }, r.KeptIndices.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 0 }, r.DroppedIndices.ToArray());
        Assert.True(r.EnoughCoherent);

        AssertPartition(pool.Length, r);
    }

    // =========================================================================
    // 4. Threshold path - a strict threshold shrinks the coherent set
    // =========================================================================

    [Fact]
    public void Squeeze_ThresholdGovernsCoherentCount()
    {
        // A genuine 4-track cluster. With a lenient threshold all 4 cohere;
        // with a near-1.0 threshold only the (near-)identical tracks survive the cut.
        var pool = new TrackFeatures?[]
        {
            Cluster(CMajor),   // 0
            Cluster(CMajor),   // 1  identical -> compat 1.0 with 0
            Cluster(AMinor),   // 2  relative -> ~0.90 harmonic
            Cluster(GMajor),   // 3  adjacent cross -> lower
        };

        var lenient = SetSqueezer.Squeeze(pool, keep: 4, coherenceThreshold: 0.5);
        Assert.True(lenient.EnoughCoherent);
        Assert.Equal(4, lenient.CoherentCount);

        var strict = SetSqueezer.Squeeze(pool, keep: 4, coherenceThreshold: 0.99);
        Assert.False(strict.EnoughCoherent);
        Assert.True(strict.CoherentCount < 4,
            $"a 0.99 threshold must reject the looser cluster joins, got {strict.CoherentCount}");
        // Both still KEEP all 4 (best-effort); only the coherence verdict differs.
        Assert.Equal(4, strict.KeptIndices.Count);
    }

    // =========================================================================
    // 5. keep larger than the pool - keeps all, flags not-enough
    // =========================================================================

    [Fact]
    public void Squeeze_KeepExceedsScoreable_KeepsAll_Flags()
    {
        var pool = new TrackFeatures?[] { Cluster(CMajor), Cluster(AMinor) };

        var r = SetSqueezer.Squeeze(pool, keep: 10);

        Assert.Equal(2, r.KeptIndices.Count);   // can't conjure tracks
        Assert.False(r.EnoughCoherent);         // asked 10, only 2 exist
        Assert.Contains("only 2", r.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 6. Unanalysable tracks are dropped and reported, never silently lost
    // =========================================================================

    [Fact]
    public void Squeeze_UnanalyzedTracks_DroppedAndCounted()
    {
        var pool = new TrackFeatures?[]
        {
            Cluster(CMajor),                       // 0
            Cluster(AMinor),                       // 1
            new(null, null, null, null),           // 2  unanalysable
            null,                                  // 3  null entry
        };

        var r = SetSqueezer.Squeeze(pool, keep: 2);

        Assert.Equal(new[] { 0, 1 }, r.KeptIndices.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 2, 3 }, r.DroppedIndices.OrderBy(x => x).ToArray());
        Assert.Equal(2, r.UnanalyzedDropped);
        Assert.Contains("unanalysed", r.Message, System.StringComparison.OrdinalIgnoreCase);

        AssertPartition(pool.Length, r);
    }

    [Fact]
    public void Squeeze_AllUnanalyzed_DropsAll_NotEnough()
    {
        var pool = new TrackFeatures?[]
        {
            new(null, null, null, null),
            new(null, null, null, null),
            null,
        };

        var r = SetSqueezer.Squeeze(pool, keep: 2);

        Assert.Empty(r.KeptIndices);
        Assert.Equal(3, r.DroppedIndices.Count);
        Assert.Equal(3, r.UnanalyzedDropped);
        Assert.False(r.EnoughCoherent);
    }

    // =========================================================================
    // 7. Edge cases - empty pool, single track
    // =========================================================================

    [Fact]
    public void Squeeze_EmptyPool_ReturnsEmpty()
    {
        var r = SetSqueezer.Squeeze([], keep: 5);

        Assert.Empty(r.KeptIndices);
        Assert.Empty(r.DroppedIndices);
        Assert.Equal(-1, r.SeedIndex);
    }

    [Fact]
    public void Squeeze_SingleScoreable_KeepsIt()
    {
        var pool = new TrackFeatures?[] { Cluster(CMajor) };

        var r = SetSqueezer.Squeeze(pool, keep: 1);

        Assert.Equal(new[] { 0 }, r.KeptIndices.ToArray());
        Assert.Empty(r.DroppedIndices);
        Assert.Equal(0, r.SeedIndex);
        Assert.Equal(1, r.CoherentCount);
        Assert.True(r.EnoughCoherent);
    }

    // -- Helpers -------------------------------------------------------------------

    /// <summary>Kept  union  Dropped must equal {0..count-1} exactly once each - never leave a track behind.</summary>
    private static void AssertPartition(int count, SqueezeResult r)
    {
        var all = r.KeptIndices.Concat(r.DroppedIndices).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(0, count).ToArray(), all);
    }
}
