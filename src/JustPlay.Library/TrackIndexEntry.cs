using System.Text.Json.Serialization;

namespace JustPlay.Library;

/// <summary>
/// One track's full feature set in the library index.
/// Keyed by <see cref="FilePath"/> + <see cref="ContentHash"/>.
/// JSON-serializable via <see cref="LibraryJsonContext"/> (source-gen, trim/AOT-safe).
///
/// <para>0.6 (THE LIBRARY): moved here from <c>JustPlay.Cli.Index</c> unchanged, so every index
/// file written by the CLI so far loads as-is. The only addition is <see cref="ModifiedUtc"/>,
/// the cheap-key partner to <see cref="FileSizeBytes"/> — older entries simply have it null.</para>
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
    /// <summary>
    /// ISO-8601 UTC last-write time of the file when this entry was written (0.6+).
    /// Together with <see cref="FileSizeBytes"/> this is the CHEAP key: it costs one directory
    /// entry, whereas <see cref="ContentHash"/> costs reading the whole file over the network.
    /// Null on entries written before 0.6.
    /// </summary>
    [JsonPropertyName("modifiedUtc")]    public string?  ModifiedUtc  { get; init; }

    // ── Tag metadata (read without DSP) ──────────────────────────────────────
    [JsonPropertyName("title")]          public string?  Title       { get; init; }
    [JsonPropertyName("artist")]         public string?  Artist      { get; init; }
    [JsonPropertyName("album")]          public string?  Album       { get; init; }
    [JsonPropertyName("genre")]          public string?  Genre       { get; init; }
    [JsonPropertyName("year")]           public uint?    Year        { get; init; }
    [JsonPropertyName("durationSec")]    public double   DurationSec { get; init; }
    [JsonPropertyName("bitrateKbps")]    public int?     BitrateKbps { get; init; }

    // ── DSP analysis ──────────────────────────────────────────────────────────
    /// <summary>
    /// True when a DSP analysis produced these values. Three states, no magic strings:
    /// <c>Success=true</c> = analysed; <c>false</c> + <see cref="Error"/> set = the analysis
    /// failed; <c>false</c> + <see cref="Error"/> null = known file, never analysed
    /// (see <see cref="NeedsAnalysis"/>).
    /// </summary>
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

    // ── Rhythm (RhythmPattern) ────────────────────────────────────────────────
    [JsonPropertyName("beatType")]       public string?  BeatType       { get; init; }
    [JsonPropertyName("fourOnFloor")]    public double?  FourOnFloor    { get; init; }
    [JsonPropertyName("offbeatEnergy")]  public double?  OffbeatEnergy  { get; init; }
    [JsonPropertyName("swing")]          public double?  Swing          { get; init; }
    [JsonPropertyName("syncopation")]    public double?  Syncopation    { get; init; }
    [JsonPropertyName("halfTimeFeel")]   public double?  HalfTimeFeel   { get; init; }

    // ── Vibe quartet + fatigue flag (v8+) ─────────────────────────────────────
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

    // ── Cheap-key helpers (0.6) ───────────────────────────────────────────────

    /// <summary>Formats a UTC timestamp the way the index stores them (round-trip "o").</summary>
    public static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("o");

    /// <summary>
    /// Filesystem timestamps are not equal across filesystems: FAT/exFAT round to 2 s and SMB
    /// clients have been seen off by a hair. Two seconds is the standard tolerance and is far
    /// tighter than any real edit.
    /// </summary>
    private static readonly TimeSpan MTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The CHEAP freshness check: same size and (within tolerance) same last-write time as what
    /// we recorded. Returns false when <see cref="ModifiedUtc"/> is missing (pre-0.6 entry) or
    /// unparsable — the caller then falls back to <see cref="ContentHash"/>.
    ///
    /// <para>This is the one thing that makes a rescan cheap: hashing 6.8k tracks over SMB pulls
    /// the entire library through the network, a directory listing does not.</para>
    /// </summary>
    public bool LooksUnchanged(long sizeBytes, DateTime modifiedUtc) =>
        LooksUnchanged(FileSizeBytes, ModifiedUtc, sizeBytes, modifiedUtc);

    /// <summary>
    /// The same comparison against a bare recorded key, so a sync pass can hold a few million
    /// (size, mtime) pairs in memory without materialising whole entries. ONE implementation —
    /// the instance method delegates here.
    /// </summary>
    public static bool LooksUnchanged(
        long recordedSize, string? recordedModifiedUtc, long sizeBytes, DateTime modifiedUtc)
    {
        if (recordedSize != sizeBytes) return false;
        if (string.IsNullOrEmpty(recordedModifiedUtc)) return false;
        if (!DateTime.TryParse(recordedModifiedUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var recorded))
            return false;

        var delta = recorded.ToUniversalTime() - modifiedUtc.ToUniversalTime();
        return delta.Duration() <= MTimeTolerance;
    }

    /// <summary>Known file, no analysis yet — queue it, do not treat it as a failure.</summary>
    [JsonIgnore]
    public bool NeedsAnalysis => !Success && Error is null;

    /// <summary>
    /// Adopts a pre-0.6 entry into the cheap-key world: same size, so we trust the existing
    /// analysis and just record the mtime we can now see. Without this, upgrading would hash
    /// every file in the library once — 60 GB of NAS traffic to learn nothing new.
    ///
    /// <para>Deliberate: a stale blob is trusted as-is, never silently re-analysed
    /// (see the analysis-tag-persistence design).</para>
    /// </summary>
    public TrackIndexEntry AdoptedWith(DateTime modifiedUtc) =>
        this with { ModifiedUtc = FormatUtc(modifiedUtc) };
}
