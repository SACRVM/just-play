using System.Text.Json.Serialization;

namespace JustPlay.Engine.Dtos;

/// <summary>
/// Tag-level metadata for one track, returned by <see cref="ITrackEngine.ReadTagsAsync"/>.
/// All fields are nullable so a partially-tagged file is still fully representable.
/// JSON-serializable via <see cref="EngineJsonContext"/> (source-generated, trim/AOT-safe).
/// </summary>
public sealed record TrackTagsDto
{
    [JsonPropertyName("filePath")]    public required string FilePath  { get; init; }
    [JsonPropertyName("title")]       public string?  Title      { get; init; }
    [JsonPropertyName("artist")]      public string?  Artist     { get; init; }
    [JsonPropertyName("album")]       public string?  Album      { get; init; }
    [JsonPropertyName("genre")]       public string?  Genre      { get; init; }
    [JsonPropertyName("year")]        public uint?    Year       { get; init; }
    [JsonPropertyName("comment")]     public string?  Comment    { get; init; }
    /// <summary>Content Group / Grouping tag (ID3v2 TIT1, MP4 ©grp, FLAC GROUPING).</summary>
    [JsonPropertyName("grouping")]    public string?  Grouping   { get; init; }
    [JsonPropertyName("durationSec")] public double   DurationSec { get; init; }
    [JsonPropertyName("bitrateKbps")] public int?     BitrateKbps { get; init; }
    [JsonPropertyName("sampleRate")]  public int?     SampleRate  { get; init; }
    [JsonPropertyName("channels")]    public int?     Channels    { get; init; }
    [JsonPropertyName("taggedBpm")]   public double?  TaggedBpm  { get; init; }
    [JsonPropertyName("taggedKey")]   public string?  TaggedKey  { get; init; }
    [JsonPropertyName("taggedEnergy")]public int?     TaggedEnergy { get; init; }
    [JsonPropertyName("isFavorite")]  public bool     IsFavorite { get; init; }
    /// <summary>True when the file carries a valid JustPlay analysis blob (previously analysed).</summary>
    [JsonPropertyName("hasStoredAnalysis")] public bool HasStoredAnalysis { get; init; }
}
