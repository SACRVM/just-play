using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="HarmonicSequencer"/>.
///
/// Each test is designed so the "right" ordering is obvious from the compatibility
/// scores - e.g. a tight compatible cluster vs a clear outlier - and the assertions
/// verify both the outlier placement and the unanalyzed-last rule.
///
/// The sequencer tests use Fingerprint = null to keep the tests focused on key/BPM/energy
/// ordering logic; the Beat axis integration is tested in MixCompatibilityTests.
/// </summary>
public class HarmonicSequencerTests
{
    // -- Key factories --------------------------------------------------------
    private static MusicalKey CMajor  => new(0, KeyMode.Major);   // 8B
    private static MusicalKey AMinor  => new(9, KeyMode.Minor);   // 8A  relative of C major
    private static MusicalKey GMajor  => new(7, KeyMode.Major);   // 9B  adjacent to C major
    private static MusicalKey FSharp  => new(6, KeyMode.Major);   // 2B  tritone from C major (clashing)

    // -- Feature factory -------------------------------------------------------
    /// <summary>Fingerprint = null - sequencer tests focus on key/BPM/energy axis.</summary>
    private static TrackFeatures F(double? bpm, MusicalKey? key, int? energy)
        => new(bpm, key, energy, Fingerprint: null);

    // =========================================================================
    // 1. Outlier ends up adjacent to its best neighbour, NOT at a random place
    // =========================================================================

    /// <summary>
    /// Three tracks in the same Camelot neighbourhood (C major 8B / A minor 8A / G major 9B)
    /// at very similar BPMs share high compatibility.  A fourth track (F# major 2B - tritone
    /// from C major, the worst possible harmonic position) at a wildly different BPM is the
    /// outlier.  The sequencer must not insert the outlier between two highly-compatible tracks;
    /// it should float to the end of the scoreable block where it damages the fewest edges.
    ///
    /// Assertion: the outlier track is NOT at position 0 or 1 of the output
    /// (it cannot be a good opening move or immediately after the pinned track).
    /// </summary>
    [Fact]
    public void Outlier_NotPlacedAmongCompatibleCluster()
    {
        // Three mutually-compatible tracks (same Camelot neighbourhood, ~128 BPM)
        var t0 = F(128.0, CMajor, 7);   // start track (pinned)
        var t1 = F(128.5, AMinor, 7);   // relative of C major, nearly same BPM -> high compat
        var t2 = F(127.0, GMajor, 8);   // adjacent on wheel, same BPM -> high compat

        // Outlier: tritone (clashing key) + very different BPM
        var t3 = F(180.0, FSharp,  3);   // 2B = tritone from 8B/8A/9B cluster, 180 BPM

        var tracks = new TrackFeatures?[] { t0, t1, t2, t3 };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        Assert.Equal(4, result.Order.Length);
        Assert.Equal(0, result.UnanalyzedCount);

        // The pinned track (index 0) must stay first.
        Assert.Equal(0, result.Order[0]);

        // The outlier (index 3) should NOT be at position 1 (right after the pinned track),
        // because both t1 (8A) and t2 (9B) are dramatically more compatible with t0 (8B).
        Assert.NotEqual(3, result.Order[1]);
    }

    // =========================================================================
    // 2. Unanalyzed track lands last, in original order among other unanalyzed
    // =========================================================================

    [Fact]
    public void UnanalyzedTracks_PlacedAtEnd_InOriginalOrder()
    {
        // Two analysed tracks
        var t0 = F(128.0, CMajor, 7);
        var t1 = F(128.0, AMinor, 7);

        // Two unanalyzed tracks (all fields null)
        var t2 = new TrackFeatures(null, null, null, null);
        var t3 = new TrackFeatures(null, null, null, null);

        var tracks = new TrackFeatures?[] { t0, t1, t2, t3 };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        Assert.Equal(4, result.Order.Length);
        Assert.Equal(2, result.UnanalyzedCount);

        // Scoreable tracks must occupy positions 0 and 1.
        Assert.Contains(result.Order[0], new[] { 0, 1 });
        Assert.Contains(result.Order[1], new[] { 0, 1 });

        // Unanalyzed tracks must occupy positions 2 and 3, in their ORIGINAL relative order (t2 before t3).
        Assert.Equal(2, result.Order[2]);
        Assert.Equal(3, result.Order[3]);
    }

    // =========================================================================
    // 3. Null entry in the list is treated as unanalyzed
    // =========================================================================

    [Fact]
    public void NullEntry_TreatedAsUnanalyzed_PlacedAtEnd()
    {
        var t0 = F(128.0, CMajor, 7);
        var t1 = F(130.0, GMajor, 6);

        // null = unanalyzed (no features object at all)
        TrackFeatures? t2 = null;

        var tracks = new TrackFeatures?[] { t0, t2, t1 };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        Assert.Equal(3, result.Order.Length);
        Assert.Equal(1, result.UnanalyzedCount);

        // The null entry (original index 1) must be last.
        Assert.Equal(1, result.Order[2]);
    }

    // =========================================================================
    // 4. Pinned start track stays at position 0
    // =========================================================================

    [Fact]
    public void PinnedTrack_AlwaysFirst()
    {
        // Make t2 the "most attractive" start by giving it the best compatibility
        // with everything, but we pin t0.
        var t0 = F(128.0, CMajor, 7);
        var t1 = F(128.0, AMinor, 7);
        var t2 = F(128.0, GMajor, 7);  // also highly compatible

        var tracks = new TrackFeatures?[] { t0, t1, t2 };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        Assert.Equal(3, result.Order.Length);
        Assert.Equal(0, result.Order[0]);  // t0 must stay first
    }

    // =========================================================================
    // 5. Single-track queue - trivial identity permutation
    // =========================================================================

    [Fact]
    public void SingleTrack_ReturnsIdentity()
    {
        var tracks = new TrackFeatures?[] { F(128.0, CMajor, 7) };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        var single = Assert.Single(result.Order);
        Assert.Equal(0, single);
        Assert.Equal(0, result.UnanalyzedCount);
    }

    // =========================================================================
    // 6. All-unanalyzed queue - returns original order
    // =========================================================================

    [Fact]
    public void AllUnanalyzed_ReturnsOriginalOrder()
    {
        var none = new TrackFeatures(null, null, null, null);
        var tracks = new TrackFeatures?[] { none, none, none };
        var result = HarmonicSequencer.Sequence(tracks);

        Assert.Equal(new[] { 0, 1, 2 }, result.Order);
        Assert.Equal(3, result.UnanalyzedCount);
    }

    // =========================================================================
    // 7. Empty queue - returns empty result without exception
    // =========================================================================

    [Fact]
    public void EmptyQueue_ReturnsEmpty()
    {
        var result = HarmonicSequencer.Sequence([]);

        Assert.Empty(result.Order);
        Assert.Equal(0, result.UnanalyzedCount);
    }

    // =========================================================================
    // 8. Compatible chain wins over BPM outlier
    //    The "one obvious chain" test: A-A-A-outlier should end up as A-A-A-outlier,
    //    NOT as A-outlier-A-A.
    // =========================================================================

    [Fact]
    public void CompatibleChain_OutlierAtTail()
    {
        // Three tracks in the same Camelot+BPM neighbourhood.
        var t0 = F(128.0, CMajor, 7);  // pinned start
        var t1 = F(128.0, CMajor, 7);  // identical to t0 -> highest compat with it
        var t2 = F(128.5, AMinor, 7);  // adjacent -> very high compat

        // Outlier: clashing key + BPM barely outside the >=20% zero-band from 128.
        // 128 / 1.25 = 102.4 -> |1 - 128/102.4| = 0.25 > 0.20 for r=1.
        // Check others: r=2 -> 204.8, |1-128/204.8| = 0.375 > 0.20
        //               r=0.5 -> 51.2, |1-128/51.2| = 1.5 > 0.20
        //               r=1.5 -> 153.6, |1-128/153.6| = 0.167 < 0.20  -> IN BAND for r=1.5
        // Simpler: use BPM 65 (half of ~128 only gives 64, close to in-band); use FSharp key only.
        // Main differentiator is the clashing key. Use BPM close to one-half so tempo is ~ok,
        // but key is terrible (tritone). Score should still push it last due to harmonic clash.
        var t3 = F(128.0, FSharp,  3);  // same BPM as cluster, clashing key + low energy

        var tracks = new TrackFeatures?[] { t0, t1, t2, t3 };
        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        // t0 pinned first.
        Assert.Equal(0, result.Order[0]);

        // The compatible cluster (t0, t1, t2) should be contiguous at the front.
        // The outlier (t3 at index 3) should be last.
        Assert.Equal(3, result.Order[3]);
    }

    // =========================================================================
    // 9. Result is a valid permutation (no duplicates, all original indices present)
    // =========================================================================

    [Fact]
    public void Result_IsValidPermutation()
    {
        var tracks = new TrackFeatures?[]
        {
            F(128.0, CMajor, 7),
            F(130.0, AMinor, 6),
            F(125.0, GMajor, 8),
            new(null, null, null, null),  // unanalyzed
            F(128.0, FSharp, 5),
        };

        var result = HarmonicSequencer.Sequence(tracks, startIndex: 0);

        Assert.Equal(5, result.Order.Length);

        // All original indices 0..4 must appear exactly once.
        var sorted = result.Order.OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sorted);
    }
}
