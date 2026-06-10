using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Verifies that <see cref="RhythmPattern"/> round-trips correctly through
/// <see cref="AnalysisStateCodec"/> — i.e. that a pattern stored in the JUSTPLAY
/// blob is restored exactly after deserialisation.
///
/// All types involved are in JustPlay.Core — no dependency on JustPlay.Analysis needed.
/// </summary>
public class RhythmPatternCodecTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static RhythmPattern MakeRhythmPattern(string beatType = "4x4-driving") =>
        new()
        {
            FourOnFloor   = 0.81,
            OffbeatEnergy = 0.23,
            Swing         = 0.15,
            Syncopation   = 0.11,
            HalfTimeFeel  = 0.09,
            BeatType      = beatType,
        };

    private static TrackAnalysisState MakeState(RhythmPattern? rhythm) =>
        new()
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm    = 128.0,
                Energy = 7,
                Rhythm = rhythm,
            },
        };

    // =========================================================================
    // 1. All six scalar + label fields survive a round-trip
    // =========================================================================

    [Fact]
    public void RhythmPattern_RoundTrips_AllFields()
    {
        var rp      = MakeRhythmPattern("4x4-driving");
        var state   = MakeState(rp);
        var blob    = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        var rpRestored = restored!.Detected.Rhythm;
        Assert.NotNull(rpRestored);

        Assert.Equal(rp.FourOnFloor,   rpRestored!.FourOnFloor,   5);
        Assert.Equal(rp.OffbeatEnergy, rpRestored.OffbeatEnergy,  5);
        Assert.Equal(rp.Swing,         rpRestored.Swing,          5);
        Assert.Equal(rp.Syncopation,   rpRestored.Syncopation,    5);
        Assert.Equal(rp.HalfTimeFeel,  rpRestored.HalfTimeFeel,   5);
        Assert.Equal(rp.BeatType,      rpRestored.BeatType);
    }

    [Theory]
    [InlineData("4x4-driving")]
    [InlineData("4x4-groovy")]
    [InlineData("offbeat-bass")]
    [InlineData("breaks")]
    [InlineData("halftime")]
    public void RhythmPattern_RoundTrips_AllBeatTypeLabels(string beatType)
    {
        var state    = MakeState(MakeRhythmPattern(beatType));
        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(beatType, restored!.Detected.Rhythm!.BeatType);
    }

    // =========================================================================
    // 2. Null RhythmPattern round-trips as null (backwards-compatible)
    // =========================================================================

    [Fact]
    public void NullRhythmPattern_RoundTrips_AsNull()
    {
        var state    = MakeState(rhythm: null);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Rhythm);
    }

    // =========================================================================
    // 3. Older blob (v5, no rp_* keys) parses without error → Rhythm is null
    // =========================================================================

    [Fact]
    public void OlderBlob_WithoutRhythmFields_ParsesAsNull()
    {
        // Hand-crafted v5 blob — no rp_* keys.
        const string v5Blob = "{\"v\":5,\"bpm\":128.0,\"kpc\":0,\"kmd\":\"maj\",\"nrg\":7," +
                              "\"lufs\":-9.5,\"rg\":8.5,\"pk\":0.988}";

        var restored = AnalysisStateCodec.TryParse(v5Blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Rhythm);
        Assert.Equal(128.0, restored.Detected.Bpm);
    }

    // =========================================================================
    // 4. Blob with partial rp_* fields (only fof) → Rhythm is null
    // =========================================================================

    [Fact]
    public void PartialRhythmFields_GiveNullRhythm()
    {
        // Only rp_fof present — not enough to reconstruct the pattern.
        const string partialBlob = "{\"v\":6,\"bpm\":128.0,\"rp_fof\":0.81}";

        var restored = AnalysisStateCodec.TryParse(partialBlob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Rhythm);
    }

    // =========================================================================
    // 5. Other fields unaffected when RhythmPattern is present
    // =========================================================================

    [Fact]
    public void OtherFields_ArePreserved_WhenRhythmIsPresent()
    {
        var rp    = MakeRhythmPattern();
        var state = new TrackAnalysisState
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm           = 127.5,
                Key           = new MusicalKey(3, KeyMode.Minor),
                KeyConfidence = 0.88,
                Energy        = 8,
                Rhythm        = rp,
            },
            BpmDecision = FieldDecision.Applied,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(127.5, restored!.Detected.Bpm);
        Assert.Equal(new MusicalKey(3, KeyMode.Minor), restored.Detected.Key);
        Assert.Equal(0.88, restored.Detected.KeyConfidence!.Value, 5);
        Assert.Equal(8, restored.Detected.Energy);
        Assert.NotNull(restored.Detected.Rhythm);
    }

    // =========================================================================
    // 6. Version bump: CurrentVersion is now 6
    // =========================================================================

    [Fact]
    public void CurrentVersion_Is_Eight()
    {
        Assert.Equal(8, TrackAnalysisState.CurrentVersion);
    }

    // =========================================================================
    // 7. Blob size remains reasonable — RhythmPattern adds at most ~140 chars
    // =========================================================================

    [Fact]
    public void BlobWithRhythmPattern_IsCompact()
    {
        var rpState   = MakeState(MakeRhythmPattern());
        var noRpState = MakeState(null);
        var rpBlob    = AnalysisStateCodec.Serialize(rpState);
        var noRpBlob  = AnalysisStateCodec.Serialize(noRpState);
        var delta     = rpBlob.Length - noRpBlob.Length;

        // 6 fields × ~15 chars average (key + colon + value + comma) ≈ 90 chars;
        // allow 200 to be generous.
        Assert.True(delta <= 200,
            $"RhythmPattern added {delta} chars to the blob (expected ≤ 200). " +
            $"With: {rpBlob.Length}, without: {noRpBlob.Length}.");
    }
}
