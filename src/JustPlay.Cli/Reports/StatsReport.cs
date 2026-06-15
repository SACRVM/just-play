using System.Text.Json.Serialization;

namespace JustPlay.Cli.Reports;

/// <summary>
/// Output of the <c>justplay stats</c> command: histograms derived from the analysis index.
/// JSON-serializable via <see cref="CliJsonContext"/>.
/// </summary>
public sealed record StatsReport
{
    [JsonPropertyName("indexPath")]      public required string IndexPath   { get; init; }
    [JsonPropertyName("totalIndexed")]   public int    TotalIndexed   { get; init; }
    [JsonPropertyName("analysedCount")]  public int    AnalysedCount  { get; init; }
    [JsonPropertyName("failedCount")]    public int    FailedCount    { get; init; }

    /// <summary>BPM distribution in 10-BPM decade bands (e.g. "120-129": 312).</summary>
    [JsonPropertyName("bpmBuckets")]     public List<BpmBucket>           BpmBuckets    { get; init; } = [];
    /// <summary>Energy histogram: key = "1".."10", value = file count.</summary>
    [JsonPropertyName("energyHist")]     public Dictionary<string, int>   EnergyHist    { get; init; } = [];
    /// <summary>BeatType distribution from RhythmPattern (if available).</summary>
    [JsonPropertyName("beatTypeHist")]   public Dictionary<string, int>   BeatTypeHist  { get; init; } = [];
    /// <summary>Average FourOnFloor, OffbeatEnergy, Swing, Syncopation, HalfTimeFeel across the indexed set.</summary>
    [JsonPropertyName("rhythmAverages")] public Dictionary<string, double> RhythmAverages { get; init; } = [];
    /// <summary>Danceability histogram: bucketed by 0.5 increments.</summary>
    [JsonPropertyName("danceabilityHist")] public Dictionary<string, int> DanceabilityHist { get; init; } = [];

    // ── Vibe quartet + fatigue flag (v8+) ────────────────────────────────────
    /// <summary>Histogram of harshness/noisy-flag scores (0.1 buckets, e.g. "0.0-0.1": 42).</summary>
    [JsonPropertyName("harshnessHist")]   public Dictionary<string, int>   HarshnessHist    { get; init; } = [];
    /// <summary>Histogram of punch (BassPunch) scores (0.1 buckets).</summary>
    [JsonPropertyName("basePunchHist")]   public Dictionary<string, int>   BasePunchHist    { get; init; } = [];
    /// <summary>Histogram of groove (BassGroove) scores (0.1 buckets).</summary>
    [JsonPropertyName("bassGrooveHist")]  public Dictionary<string, int>   BassGrooveHist   { get; init; } = [];
    /// <summary>Histogram of dark scores (0.1 buckets; 1=dark/dull, 0=bright).</summary>
    [JsonPropertyName("darkHist")]        public Dictionary<string, int>   DarkHist         { get; init; } = [];
    /// <summary>Histogram of hypnotic scores (0.1 buckets; 1=looping/minimal, 0=evolving).</summary>
    [JsonPropertyName("hypnoticHist")]    public Dictionary<string, int>   HypnoticHist     { get; init; } = [];
    /// <summary>Histogram of rawEnergyScore values (0.1 buckets).</summary>
    [JsonPropertyName("rawEnergyHist")]   public Dictionary<string, int>   RawEnergyHist    { get; init; } = [];

    // ── Grid-confidence (v9+) ─────────────────────────────────────────────────
    /// <summary>Histogram of GridConfidence values (0.1 buckets). GridConfidence &lt; 0.45 = grid-soft.</summary>
    [JsonPropertyName("gridConfHist")]    public Dictionary<string, int>   GridConfHist     { get; init; } = [];
    /// <summary>Number of successfully analysed tracks with GridConfidence &lt; 0.45 (grid-soft warning threshold).</summary>
    [JsonPropertyName("gridSoftCount")]   public int                        GridSoftCount    { get; init; }
}

public sealed record BpmBucket
{
    [JsonPropertyName("label")] public required string Label { get; init; }  // e.g. "120-129"
    [JsonPropertyName("min")]   public int    Min   { get; init; }
    [JsonPropertyName("max")]   public int    Max   { get; init; }
    [JsonPropertyName("count")] public int    Count { get; init; }
}
