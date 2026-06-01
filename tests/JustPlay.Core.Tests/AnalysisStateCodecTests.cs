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
}
