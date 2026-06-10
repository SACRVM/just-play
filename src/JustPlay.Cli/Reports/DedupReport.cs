using System.Text.Json.Serialization;

namespace JustPlay.Cli.Reports;

/// <summary>
/// Output of the <c>justplay dedup</c> command.
/// JSON-serializable via <see cref="CliJsonContext"/>.
/// </summary>
public sealed record DedupReport
{
    [JsonPropertyName("root")]              public required string Root                { get; init; }
    [JsonPropertyName("scannedFiles")]      public int    ScannedFiles      { get; init; }
    [JsonPropertyName("exactDupeSets")]     public int    ExactDupeSets     { get; init; }
    [JsonPropertyName("exactDupeFiles")]    public int    ExactDupeFiles    { get; init; }
    [JsonPropertyName("wastedBytes")]       public long   WastedBytes       { get; init; }
    [JsonPropertyName("wastedSizeGb")]      public double WastedSizeGb      { get; init; }
    [JsonPropertyName("nearDupeSets")]      public int    NearDupeSets      { get; init; }
    [JsonPropertyName("nearDupeFiles")]     public int    NearDupeFiles     { get; init; }
    [JsonPropertyName("exactGroups")]       public List<DedupGroup> ExactGroups  { get; init; } = [];
    [JsonPropertyName("nearGroups")]        public List<DedupGroup> NearGroups   { get; init; } = [];
}

/// <summary>
/// A group of files that are exact or near duplicates of each other.
/// </summary>
public sealed record DedupGroup
{
    /// <summary>
    /// For exact dupes: the SHA-256 hex digest.
    /// For near dupes: "artist|title" (normalised).
    /// </summary>
    [JsonPropertyName("key")]      public required string Key      { get; init; }
    [JsonPropertyName("filePaths")] public List<string> FilePaths  { get; init; } = [];
    /// <summary>Bytes wasted (all copies minus one) — only set for exact dupes.</summary>
    [JsonPropertyName("wastedBytes")] public long WastedBytes     { get; init; }
}
