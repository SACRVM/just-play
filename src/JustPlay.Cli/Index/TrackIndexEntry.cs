using System.Text.Json.Serialization;

namespace JustPlay.Cli.Index;

/// <summary>
/// One track's full feature set in the sidecar analysis index.
/// Keyed by <see cref="FilePath"/> + <see cref="ContentHash"/>.
/// JSON-serializable via <see cref="CliJsonContext"/> (source-gen, trim/AOT-safe).
/// </summary>
public sealed record TrackIndexEntry
{
    // ── Identity ─────────────────────────────────────────────────────────────
    [JsonPropertyName("filePath")]       public required string FilePath       { get; init; }
    /// <summary>SHA-256 hex digest of the file bytes — used to detect file replacement.</summary>
    [JsonPropertyName("contentHash")]    public required string ContentHash    { get; init; }
    /// <summary>ISO-8601 UTC timestamp of when this entry was written.</summary>
    [JsonPropertyName("analysedAt")]     public required string AnalysedAt     { get; init; }
    /// <summary>Version token of the detection stack at analysis time (bump when DSP changes).</summary>
    [JsonPropertyName("detectionVersion")] public int DetectionVersion         { get; init; }
    [JsonPropertyName("fileSizeBytes")]  public long FileSizeBytes             { get; init; }

    // ── Tag metadata (read without DSP) ──────────────────────────────────────
    [JsonPropertyName("title")]          public string?  Title       { get; init; }
    [JsonPropertyName("artist")]         public string?  Artist      { get; init; }
    [JsonPropertyName("album")]          public string?  Album       { get; init; }
    [JsonPropertyName("genre")]          public string?  Genre       { get; init; }
    [JsonPropertyName("year")]           public uint?    Year        { get; init; }
    [JsonPropertyName("durationSec")]    public double   DurationSec { get; init; }
    [JsonPropertyName("bitrateKbps")]    public int?     BitrateKbps { get; init; }

    // ── DSP analysis ──────────────────────────────────────────────────────────
    [JsonPropertyName("success")]        public bool     Success     { get; init; }
    [JsonPropertyName("error")]          public string?  Error       { get; init; }
    [JsonPropertyName("bpm")]            public double?  Bpm         { get; init; }
    [JsonPropertyName("keyName")]        public string?  KeyName     { get; init; }
    [JsonPropertyName("keyCamelot")]     public string?  KeyCamelot  { get; init; }
    [JsonPropertyName("keyConfidence")]  public double?  KeyConfidence { get; init; }
    [JsonPropertyName("energy")]         public int?     Energy      { get; init; }
    [JsonPropertyName("loudnessLufs")]   public double?  LoudnessLufs { get; init; }
    [JsonPropertyName("replayGainDb")]   public double?  ReplayGainDb { get; init; }
    [JsonPropertyName("peak")]           public double?  Peak        { get; init; }
    [JsonPropertyName("danceability")]   public float?   Danceability { get; init; }

    // ── Rhythm (RhythmPattern — from other agent, read defensively) ───────────
    // These fields are nullable; they'll be null until the other agent's rhythm
    // DSP lands. The struct fields map to: RhythmPattern.BeatType, .FourOnFloor, etc.
    [JsonPropertyName("beatType")]       public string?  BeatType       { get; init; }
    [JsonPropertyName("fourOnFloor")]    public double?  FourOnFloor    { get; init; }
    [JsonPropertyName("offbeatEnergy")]  public double?  OffbeatEnergy  { get; init; }
    [JsonPropertyName("swing")]          public double?  Swing          { get; init; }
    [JsonPropertyName("syncopation")]    public double?  Syncopation    { get; init; }
    [JsonPropertyName("halfTimeFeel")]   public double?  HalfTimeFeel   { get; init; }

    // ── Vibe quartet + fatigue flag (v8+; v7 had character label which is now dropped) ───────
    [JsonPropertyName("rawEnergyScore")]   public double?  RawEnergyScore   { get; init; }
    [JsonPropertyName("spectralFlatness")] public double?  SpectralFlatness { get; init; }
    /// <summary>Noisy fatigue flag [0,1]: high = wall-of-noise/schranz. Kept separate from the vibe quartet.</summary>
    [JsonPropertyName("harshness")]        public double?  Harshness        { get; init; }
    /// <summary>Vibe quartet PUNCH [0,1]: bass transient sharpness.</summary>
    [JsonPropertyName("bassPunch")]        public double?  BassPunch        { get; init; }
    /// <summary>Vibe quartet GROOVE [0,1]: swung/off-grid bass feel.</summary>
    [JsonPropertyName("bassGroove")]       public double?  BassGroove       { get; init; }
    /// <summary>Vibe quartet DARK [0,1]: 1 = dark/dull (low HF content); 0 = bright.</summary>
    [JsonPropertyName("dark")]             public double?  Dark             { get; init; }
    /// <summary>Vibe quartet HYPNOTIC [0,1]: 1 = minimal/looping; 0 = evolving/progressive.</summary>
    [JsonPropertyName("hypnotic")]         public double?  Hypnotic         { get; init; }

    // ── Grid-confidence (v9+) ─────────────────────────────────────────────────
    /// <summary>ACF peak sharpness ratio [0,1]: 1 = sharp/unambiguous, 0 = broad/competing peaks.</summary>
    [JsonPropertyName("acfSharpness")]     public double?  AcfSharpness     { get; init; }
    /// <summary>Composite beat-grid confidence [0,1]. ⚠ threshold 0.45 (grid-soft), 0.25 (grid-fail).</summary>
    [JsonPropertyName("gridConfidence")]   public double?  GridConfidence   { get; init; }
}
