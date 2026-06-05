using JustPlay.Core.Models;

namespace JustPlay.Core.Tests;

public class ReplayGainTests
{
    // -------------------------------------------------------------------------
    // Reference-level identity
    // -------------------------------------------------------------------------

    [Fact]
    public void TrackGainDb_AtReferenceLufs_ReturnsZero()
    {
        // A track already at −18 LUFS needs 0 dB gain.
        Assert.Equal(0.0, ReplayGain.TrackGainDb(-18.0));
    }

    // -------------------------------------------------------------------------
    // Directional: louder tracks need negative gain, quieter need positive
    // -------------------------------------------------------------------------

    [Fact]
    public void TrackGainDb_QuietTrack_ReturnsPositiveGain()
    {
        // −28 LUFS is 10 LU quieter than reference → needs +10 dB.
        Assert.Equal(10.0, ReplayGain.TrackGainDb(-28.0));
    }

    [Fact]
    public void TrackGainDb_LoudTrack_ReturnsNegativeGain()
    {
        // −8 LUFS is 10 LU louder than reference → needs −10 dB.
        Assert.Equal(-10.0, ReplayGain.TrackGainDb(-8.0));
    }

    // -------------------------------------------------------------------------
    // Clamping at ±51 dB
    // -------------------------------------------------------------------------

    [Fact]
    public void TrackGainDb_ExtremelyQuiet_ClampedAt_PositiveFiftyOne()
    {
        // −200 LUFS → unclamped = +182, clamped to +51.
        Assert.Equal(51.0, ReplayGain.TrackGainDb(-200.0));
    }

    [Fact]
    public void TrackGainDb_ExtremelyLoud_ClampedAt_NegativeFiftyOne()
    {
        // +100 LUFS → unclamped = −118, clamped to −51.
        Assert.Equal(-51.0, ReplayGain.TrackGainDb(100.0));
    }

    // -------------------------------------------------------------------------
    // Reference constant
    // -------------------------------------------------------------------------

    [Fact]
    public void ReferenceLufs_Is_MinusEighteen()
    {
        Assert.Equal(-18.0, ReplayGain.ReferenceLufs);
    }

    // -------------------------------------------------------------------------
    // Codec round-trip: LoudnessLufs / ReplayGainDb / Peak survive serialisation
    // -------------------------------------------------------------------------

    [Fact]
    public void CodecRoundTrip_LoudnessFields_Preserved()
    {
        var state = new TrackAnalysisState
        {
            Version = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm          = 128.0,
                LoudnessLufs = -9.72,
                ReplayGainDb = ReplayGain.TrackGainDb(-9.72),
                Peak         = 0.988553,
            },
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(5, restored!.Version);
        Assert.Equal(-9.72, restored.Detected.LoudnessLufs);
        Assert.Equal(ReplayGain.TrackGainDb(-9.72), restored.Detected.ReplayGainDb);
        Assert.Equal(0.988553, restored.Detected.Peak!.Value, precision: 5);
    }

    [Fact]
    public void CodecRoundTrip_NullLoudnessFields_PreservedAsNull()
    {
        var state = new TrackAnalysisState
        {
            Detected = new AnalysisResult { Bpm = 120.0 },
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.LoudnessLufs);
        Assert.Null(restored.Detected.ReplayGainDb);
        Assert.Null(restored.Detected.Peak);
    }
}
