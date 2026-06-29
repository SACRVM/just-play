using System.Text.Json.Serialization;

namespace JustPlay.Cli.Reports;

/// <summary>
/// JSON report for <c>justplay squeeze --out report.json</c>. Captures the squeeze result plus
/// the resolved file paths so the run is reproducible / inspectable.
/// Serialized via <see cref="JustPlay.Cli.CliJsonContext"/> (source-gen, trim/AOT-safe).
/// </summary>
public sealed record SqueezeReport
{
    [JsonPropertyName("indexPath")]          public required string IndexPath          { get; init; }
    [JsonPropertyName("root")]               public string?         Root               { get; init; }
    [JsonPropertyName("playlist")]           public string?         Playlist           { get; init; }
    [JsonPropertyName("poolSize")]           public int             PoolSize           { get; init; }
    [JsonPropertyName("requestedKeep")]      public int             RequestedKeep      { get; init; }
    [JsonPropertyName("coherenceThreshold")] public double          CoherenceThreshold { get; init; }
    [JsonPropertyName("keptCount")]          public int             KeptCount          { get; init; }
    [JsonPropertyName("droppedCount")]       public int             DroppedCount       { get; init; }
    [JsonPropertyName("unanalyzedDropped")]  public int             UnanalyzedDropped  { get; init; }
    [JsonPropertyName("coherentCount")]      public int             CoherentCount      { get; init; }
    [JsonPropertyName("enoughCoherent")]     public bool            EnoughCoherent     { get; init; }
    [JsonPropertyName("meanCohesion")]       public double          MeanCohesion       { get; init; }
    [JsonPropertyName("minCohesion")]        public double          MinCohesion        { get; init; }
    [JsonPropertyName("seedPath")]           public string?         SeedPath           { get; init; }
    [JsonPropertyName("message")]            public string          Message            { get; init; } = "";
    /// <summary>Kept file paths, in play order (sequenced unless --no-sequence).</summary>
    [JsonPropertyName("kept")]               public List<string>    Kept               { get; init; } = [];
    /// <summary>Dropped file paths (outliers + unanalysable tracks).</summary>
    [JsonPropertyName("dropped")]            public List<string>    Dropped            { get; init; } = [];
}
