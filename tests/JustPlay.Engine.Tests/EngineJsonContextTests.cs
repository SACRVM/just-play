using System.Text.Json;
using JustPlay.Engine;
using JustPlay.Engine.Dtos;

namespace JustPlay.Engine.Tests;

/// <summary>
/// Verifies that all facade DTOs round-trip correctly through the source-generated
/// <see cref="EngineJsonContext"/>. No BASS, no Avalonia, no file I/O — pure JSON.
/// </summary>
public class EngineJsonContextTests
{
    // ── TrackAnalysisDto ──────────────────────────────────────────────────────

    [Fact]
    public void TrackAnalysisDto_RoundTrips_AllFields()
    {
        var original = new TrackAnalysisDto
        {
            FilePath     = "/music/test.mp3",
            Bpm          = 128.4,
            KeyName      = "A minor",
            KeyCamelot   = "8A",
            KeyConfidence = 0.87,
            Energy       = 7,
            Danceability = 1.23f,
            Success      = true,
            Error        = null,
        };

        var json = JsonSerializer.Serialize(original, EngineJsonContext.Default.TrackAnalysisDto);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.TrackAnalysisDto);

        Assert.NotNull(restored);
        Assert.Equal(original.FilePath,      restored!.FilePath);
        Assert.Equal(original.Bpm,           restored.Bpm);
        Assert.Equal(original.KeyName,       restored.KeyName);
        Assert.Equal(original.KeyCamelot,    restored.KeyCamelot);
        Assert.Equal(original.KeyConfidence, restored.KeyConfidence);
        Assert.Equal(original.Energy,        restored.Energy);
        Assert.Equal(original.Danceability,  restored.Danceability);
        Assert.Equal(original.Success,       restored.Success);
        Assert.Null(restored.Error);
    }

    [Fact]
    public void TrackAnalysisDto_NullFields_OmittedFromJson()
    {
        var dto = new TrackAnalysisDto
        {
            FilePath = "/music/sparse.mp3",
            Success  = false,
            Error    = "Analysis failed",
        };

        var json = JsonSerializer.Serialize(dto, EngineJsonContext.Default.TrackAnalysisDto);

        // Source-gen option WhenWritingNull — nullable fields must not appear.
        Assert.DoesNotContain("\"bpm\"",          json);
        Assert.DoesNotContain("\"keyName\"",      json);
        Assert.DoesNotContain("\"keyCamelot\"",   json);
        Assert.DoesNotContain("\"energy\"",       json);
        Assert.DoesNotContain("\"danceability\"", json);
        Assert.Contains("\"error\"",              json);
    }

    // ── TrackTagsDto ──────────────────────────────────────────────────────────

    [Fact]
    public void TrackTagsDto_RoundTrips_AllFields()
    {
        var original = new TrackTagsDto
        {
            FilePath          = "/music/track.mp3",
            Title             = "Test Track",
            Artist            = "DJ Test",
            Album             = "Test Album",
            Genre             = "Techno",
            Year              = 2024,
            Comment           = "8A 128 BPM",
            DurationSec       = 360.5,
            BitrateKbps       = 320,
            SampleRate        = 44100,
            Channels          = 2,
            TaggedBpm         = 128.0,
            TaggedKey         = "Am",
            TaggedEnergy      = 7,
            IsFavorite        = true,
            HasStoredAnalysis = true,
        };

        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.TrackTagsDto);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.TrackTagsDto);

        Assert.NotNull(restored);
        Assert.Equal(original.FilePath,          restored!.FilePath);
        Assert.Equal(original.Title,             restored.Title);
        Assert.Equal(original.Artist,            restored.Artist);
        Assert.Equal(original.DurationSec,       restored.DurationSec);
        Assert.Equal(original.TaggedBpm,         restored.TaggedBpm);
        Assert.Equal(original.TaggedKey,         restored.TaggedKey);
        Assert.Equal(original.TaggedEnergy,      restored.TaggedEnergy);
        Assert.Equal(original.IsFavorite,        restored.IsFavorite);
        Assert.Equal(original.HasStoredAnalysis, restored.HasStoredAnalysis);
    }

    // ── WriteTagsRequest ──────────────────────────────────────────────────────

    [Fact]
    public void WriteTagsRequest_RoundTrips()
    {
        var original = new WriteTagsRequest
        {
            Bpm      = 128.0,
            Key      = "8A",
            Energy   = 7,
            Favorite = true,
        };

        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.WriteTagsRequest);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WriteTagsRequest);

        Assert.NotNull(restored);
        Assert.Equal(original.Bpm,      restored!.Bpm);
        Assert.Equal(original.Key,      restored.Key);
        Assert.Equal(original.Energy,   restored.Energy);
        Assert.Equal(original.Favorite, restored.Favorite);
    }

    [Fact]
    public void WriteTagsRequest_AllNulls_RoundTrips()
    {
        var original = new WriteTagsRequest();
        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.WriteTagsRequest);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WriteTagsRequest);

        Assert.NotNull(restored);
        Assert.Null(restored!.Bpm);
        Assert.Null(restored.Key);
        Assert.Null(restored.Energy);
        Assert.Null(restored.Favorite);
    }

    // ── WriteTagsResult ──────────────────────────────────────────────────────

    [Fact]
    public void WriteTagsResult_Success_RoundTrips()
    {
        var original = new WriteTagsResult { FilePath = "/music/track.mp3", Success = true };
        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.WriteTagsResult);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WriteTagsResult);

        Assert.NotNull(restored);
        Assert.Equal(original.FilePath, restored!.FilePath);
        Assert.True(restored.Success);
        Assert.Null(restored.Error);
    }

    [Fact]
    public void WriteTagsResult_Failure_RoundTrips()
    {
        var original = new WriteTagsResult
        {
            FilePath = "/music/locked.mp3",
            Success  = false,
            Error    = "Access denied",
        };
        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.WriteTagsResult);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WriteTagsResult);

        Assert.NotNull(restored);
        Assert.False(restored!.Success);
        Assert.Equal("Access denied", restored.Error);
    }

    // ── LibraryAnalysisResult ─────────────────────────────────────────────────

    [Fact]
    public void LibraryAnalysisResult_RoundTrips()
    {
        var original = new LibraryAnalysisResult
        {
            Folder     = "/music",
            TotalFiles = 2,
            Succeeded  = 1,
            Failed     = 1,
            Tracks     =
            [
                new TrackAnalysisDto { FilePath = "/music/a.mp3", Bpm = 130.0, Success = true  },
                new TrackAnalysisDto { FilePath = "/music/b.mp3", Success = false, Error = "Read error" },
            ],
        };

        var json     = JsonSerializer.Serialize(original, EngineJsonContext.Default.LibraryAnalysisResult);
        var restored = JsonSerializer.Deserialize(json, EngineJsonContext.Default.LibraryAnalysisResult);

        Assert.NotNull(restored);
        Assert.Equal(original.Folder,     restored!.Folder);
        Assert.Equal(original.TotalFiles, restored.TotalFiles);
        Assert.Equal(original.Succeeded,  restored.Succeeded);
        Assert.Equal(original.Failed,     restored.Failed);
        Assert.Equal(2, restored.Tracks.Count);
        Assert.Equal("/music/a.mp3", restored.Tracks[0].FilePath);
        Assert.Equal(130.0,          restored.Tracks[0].Bpm);
        Assert.True(restored.Tracks[0].Success);
        Assert.False(restored.Tracks[1].Success);
        Assert.Equal("Read error",   restored.Tracks[1].Error);
    }
}
