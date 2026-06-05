using System.Text.Json.Serialization;

namespace JustPlay.Engine.Dtos;

/// <summary>
/// Fields to write into a track's file tags, passed to <see cref="ITrackEngine.WriteTagsAsync"/>.
/// Null means "leave this field untouched"; set a value to write it.
/// JSON-serializable via <see cref="EngineJsonContext"/> (source-generated, trim/AOT-safe).
/// </summary>
public sealed record WriteTagsRequest
{
    /// <summary>BPM to write into the standard tempo tag (rounded to nearest integer in the file).</summary>
    [JsonPropertyName("bpm")]     public double? Bpm    { get; init; }
    /// <summary>Musical key to write, in any format accepted by MusicalKey.TryParse
    /// (Camelot e.g. "8A", or musical e.g. "Am", "F#m", "C major").</summary>
    [JsonPropertyName("key")]     public string? Key    { get; init; }
    /// <summary>Perceived energy (1..10) to write into the ENERGY custom tag.</summary>
    [JsonPropertyName("energy")]  public int?    Energy { get; init; }
    /// <summary>Mark/unmark the track as a favourite (ID3 POPM / Xiph RATING).
    /// Null = leave untouched.</summary>
    [JsonPropertyName("favorite")]public bool?   Favorite { get; init; }
}

/// <summary>
/// Result of a <see cref="ITrackEngine.WriteTagsAsync"/> call.
/// JSON-serializable via <see cref="EngineJsonContext"/> (source-generated, trim/AOT-safe).
/// </summary>
public sealed record WriteTagsResult
{
    [JsonPropertyName("filePath")] public required string FilePath { get; init; }
    [JsonPropertyName("success")]  public bool    Success  { get; init; }
    [JsonPropertyName("error")]    public string? Error    { get; init; }
}
