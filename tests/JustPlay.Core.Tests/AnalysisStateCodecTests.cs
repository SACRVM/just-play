using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

public class AnalysisStateCodecTests
{
    [Fact]
    public void RoundTrips_AllFields()
    {
        var state = new TrackAnalysisState
        {
            Version = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm = 127.98,
                Key = new MusicalKey(9, KeyMode.Minor), // 8A
                KeyConfidence = 0.82,
                Energy = 7,
            },
            BpmDecision = FieldDecision.Applied,
            KeyDecision = FieldDecision.Pending,
            EnergyDecision = FieldDecision.Kept,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(state.Version, restored!.Version);
        Assert.Equal(127.98, restored.Detected.Bpm);
        Assert.Equal(new MusicalKey(9, KeyMode.Minor), restored.Detected.Key);
        Assert.Equal("8A", restored.Detected.Key!.Value.Camelot);
        Assert.Equal(0.82, restored.Detected.KeyConfidence);
        Assert.Equal(7, restored.Detected.Energy);
        Assert.Equal(FieldDecision.Applied, restored.BpmDecision);
        Assert.Equal(FieldDecision.Pending, restored.KeyDecision);
        Assert.Equal(FieldDecision.Kept, restored.EnergyDecision);
    }

    [Fact]
    public void RoundTrips_Original_ForReversibleWrites()
    {
        // We overwrote a foreign BPM (128) and key (9A) with our detected values; the originals
        // are stashed so the write can be undone. Energy was never overwritten → no original.
        var state = new TrackAnalysisState
        {
            Detected = new AnalysisResult { Bpm = 127.98, Key = new MusicalKey(9, KeyMode.Minor), Energy = 7 }, // 8A
            Original = new AnalysisResult { Bpm = 128, Key = new MusicalKey(11, KeyMode.Minor) },            // 10A
            BpmDecision = FieldDecision.Applied,
            KeyDecision = FieldDecision.Applied,
            EnergyDecision = FieldDecision.Pending,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Original);
        Assert.Equal(128, restored.Original!.Bpm);
        Assert.Equal("10A", restored.Original.Key!.Value.Camelot);
        Assert.Null(restored.Original.Energy);
    }

    [Fact]
    public void Original_IsNull_WhenNothingOverwritten()
    {
        var state = new TrackAnalysisState
        {
            Detected = new AnalysisResult { Bpm = 120, Energy = 5 },
            BpmDecision = FieldDecision.Pending,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Null(restored!.Original);
    }

    [Fact]
    public void RoundTrips_PartialResult()
    {
        // BPM only — key/energy not yet detected; nulls must survive.
        var state = new TrackAnalysisState
        {
            Detected = new AnalysisResult { Bpm = 120 },
            BpmDecision = FieldDecision.Pending,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(120, restored!.Detected.Bpm);
        Assert.Null(restored.Detected.Key);
        Assert.Null(restored.Detected.Energy);
        Assert.Null(restored.Detected.KeyConfidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"v\":")]
    public void TryParse_ReturnsNull_ForAbsentOrCorrupt(string? blob)
    {
        Assert.Null(AnalysisStateCodec.TryParse(blob));
    }

    // ── AnalysedAtUtc (L7, post-L6 night report) ────────────────────────────────

    [Fact]
    public void RoundTrips_AnalysedAtUtc()
    {
        var state = new TrackAnalysisState
        {
            Version = TrackAnalysisState.CurrentVersion,
            AnalysedAtUtc = new DateTime(2026, 7, 31, 22, 5, 12, DateTimeKind.Utc),
            Detected = new AnalysisResult { Bpm = 128 },
            BpmDecision = FieldDecision.Applied,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(state.AnalysedAtUtc, restored!.AnalysedAtUtc);
        Assert.Equal(DateTimeKind.Utc, restored.AnalysedAtUtc!.Value.Kind);
    }

    [Fact]
    public void AnalysedAtUtc_IsNull_WhenNeverSet()
    {
        // Default construction — same shape every pre-L7 caller already produces.
        var state = new TrackAnalysisState
        {
            Detected = new AnalysisResult { Bpm = 120 },
        };

        Assert.Null(state.AnalysedAtUtc);

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));
        Assert.NotNull(restored);
        Assert.Null(restored!.AnalysedAtUtc);
    }

    [Fact]
    public void OldBlob_WithNoAatKey_StillParses_WithAnalysedAtUtcNull()
    {
        // Verbatim shape of a blob written before AnalysedAtUtc existed: no "aat" key at all.
        // Backward-compat is non-negotiable — this blob lives in Chloe's actual music files.
        const string oldBlob =
            "{\"v\":9,\"bpm\":127.98,\"kpc\":9,\"kmd\":\"min\",\"kcf\":0.82,\"nrg\":7," +
            "\"abpm\":\"A\",\"akey\":\"P\",\"anrg\":\"K\"}";

        var restored = AnalysisStateCodec.TryParse(oldBlob);

        Assert.NotNull(restored);
        Assert.Null(restored!.AnalysedAtUtc);
        Assert.Equal(9, restored.Version);
        Assert.Equal(127.98, restored.Detected.Bpm);
        Assert.Equal(new MusicalKey(9, KeyMode.Minor), restored.Detected.Key);
        Assert.Equal(7, restored.Detected.Energy);
        Assert.Equal(FieldDecision.Applied, restored.BpmDecision);
        Assert.Equal(FieldDecision.Pending, restored.KeyDecision);
        Assert.Equal(FieldDecision.Kept, restored.EnergyDecision);
    }
}
