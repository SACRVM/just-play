using System.Text.Json.Serialization;

namespace JustPlay.Engine.Dtos;

/// <summary>
/// Aggregate result of <see cref="ITrackEngine.AnalyzeLibraryAsync"/> — the per-track
/// analysis outcomes for every audio file found under the requested folder.
/// JSON-serializable via <see cref="EngineJsonContext"/> (source-generated, trim/AOT-safe).
/// </summary>
public sealed record LibraryAnalysisResult
{
    [JsonPropertyName("folder")]       public required string Folder      { get; init; }
    [JsonPropertyName("totalFiles")]   public int TotalFiles   { get; init; }
    [JsonPropertyName("succeeded")]    public int Succeeded    { get; init; }
    [JsonPropertyName("failed")]       public int Failed       { get; init; }
    [JsonPropertyName("tracks")]       public IReadOnlyList<TrackAnalysisDto> Tracks { get; init; } = [];
}
