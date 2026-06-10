using System.Text.Json.Serialization;

namespace JustPlay.Cli.Reports;

/// <summary>
/// Output of the <c>justplay scan</c> command.
/// JSON-serializable via <see cref="CliJsonContext"/>.
/// </summary>
public sealed record ScanReport
{
    [JsonPropertyName("root")]         public required string Root          { get; init; }
    [JsonPropertyName("totalFiles")]   public int    TotalFiles   { get; init; }
    [JsonPropertyName("totalBytes")]   public long   TotalBytes   { get; init; }
    [JsonPropertyName("totalSizeGb")]  public double TotalSizeGb  { get; init; }
    /// <summary>Key = extension (lower-case including dot), Value = file count.</summary>
    [JsonPropertyName("byFormat")]     public Dictionary<string, int> ByFormat { get; init; } = [];
    /// <summary>Key = folder path, Value = file count in that folder (non-recursive).</summary>
    [JsonPropertyName("byFolder")]     public Dictionary<string, int> ByFolder { get; init; } = [];
}
