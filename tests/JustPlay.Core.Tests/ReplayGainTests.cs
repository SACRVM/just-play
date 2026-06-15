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
    // AppliedGainDb — the gain a PLAYER applies: re-reference the −18 tag to the
    // chosen Quiet/Normal/Loud target, then clip-prevent. The PlaybackController
    // tests only ever use the default −18 target (so the re-reference term is 0);
    // these exercise the Quiet/Normal/Loud math that is otherwise untested.
    // -------------------------------------------------------------------------

    [Fact]
    public void AppliedGainDb_AtReferenceTarget_ReturnsTagGainVerbatim()
    {
        // target == −18 (the RG reference) → re-reference term is 0 → tag value unchanged.
        Assert.Equal(-6.0, ReplayGain.AppliedGainDb(-6.0, peakLinear: null, targetLufs: -18.0));
    }

    [Fact]
    public void AppliedGainDb_NormalTarget_AddsFourLU()
    {
        // Normal (−14) is 4 LU above the −18 reference → tag gain + 4. −6 → −2.
        Assert.Equal(-2.0, ReplayGain.AppliedGainDb(-6.0, peakLinear: null, targetLufs: -14.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_QuietTarget_SubtractsOneLU()
    {
        // Quiet (−19) is 1 LU below the reference → tag gain − 1. −6 → −7.
        Assert.Equal(-7.0, ReplayGain.AppliedGainDb(-6.0, peakLinear: null, targetLufs: -19.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_LoudTarget_PeakUnknown_PositiveResultSuppressed()
    {
        // Loud (−11) is +7 LU; −6 tag → +1 desired. Peak unknown → never amplify blind → 0.
        Assert.Equal(0.0, ReplayGain.AppliedGainDb(-6.0, peakLinear: null, targetLufs: -11.0));
    }

    [Fact]
    public void AppliedGainDb_LoudTarget_HeadroomAvailable_PositiveGainApplied()
    {
        // Loud (−11): −6 tag → +1 desired. peak 0.5 (−6 dBFS) leaves plenty of headroom → +1 applied.
        Assert.Equal(1.0, ReplayGain.AppliedGainDb(-6.0, peakLinear: 0.5, targetLufs: -11.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_LoudTarget_PositiveGainCappedByPeak()
    {
        // Loud (−11): +2 tag → +9 desired. peak 0.9 (−0.915 dBFS) can only rise by +0.915
        // before clipping → gain is capped to −peakDb, NOT +9.
        var expected = -20.0 * System.Math.Log10(0.9);   // ≈ +0.9151
        Assert.Equal(expected, ReplayGain.AppliedGainDb(2.0, peakLinear: 0.9, targetLufs: -11.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_NegativeGain_NeverCapped_EvenAtFullScalePeak()
    {
        // Attenuation can never clip, so a high peak must NOT cap a negative gain.
        Assert.Equal(-10.0, ReplayGain.AppliedGainDb(-10.0, peakLinear: 0.99, targetLufs: -18.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_BrickWalledMaster_GetsNoPositiveGain()
    {
        // peak == 1.0 (0 dBFS) → zero headroom → any positive desired gain is capped to 0.
        Assert.Equal(0.0, ReplayGain.AppliedGainDb(3.0, peakLinear: 1.0, targetLufs: -18.0), precision: 6);
    }

    [Fact]
    public void AppliedGainDb_BrickWalledMaster_ReturnsPositiveZero_NotNegativeZero()
    {
        // peak == 1.0 → peakDb == 0 → the cap (g = −peakDb) must not produce IEEE −0.0, which would
        // render as "-0.0" in the GAIN cell. It has to be a clean +0.0.
        var result = ReplayGain.AppliedGainDb(3.0, peakLinear: 1.0, targetLufs: -18.0);
        Assert.Equal(0.0, result);
        Assert.False(double.IsNegative(result), "must be +0.0, not IEEE −0.0 (would display as \"-0.0\")");
    }

    [Fact]
    public void AppliedGainDb_CapThenClamp_PositiveCeilingIsFiftyOne()
    {
        // A tiny peak (0.001 = −60 dBFS) permits a huge cap; the final clamp still bounds it to +51.
        Assert.Equal(51.0, ReplayGain.AppliedGainDb(100.0, peakLinear: 0.001, targetLufs: -18.0));
    }

    [Fact]
    public void AppliedGainDb_NegativeFloorIsClampedToMinusFiftyOne()
    {
        Assert.Equal(-51.0, ReplayGain.AppliedGainDb(-100.0, peakLinear: null, targetLufs: -18.0));
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
        Assert.Equal(9, restored!.Version);
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
