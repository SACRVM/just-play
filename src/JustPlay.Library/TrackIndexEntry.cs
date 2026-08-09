using System.Text.Json.Serialization;

namespace JustPlay.Library;

/// <summary>
/// One track's full feature set in the library index.
/// Keyed by <see cref="FilePath"/> + <see cref="ContentHash"/>.
/// JSON-serializable via <see cref="LibraryJsonContext"/> (source-gen, trim/AOT-safe).
///
/// <para>0.6 (THE LIBRARY): moved here from <c>JustPlay.Cli.Index</c> unchanged, so every index
/// file written by the CLI so far loads as-is. The only addition is <see cref="ModifiedUtc"/>,
/// the cheap-key partner to <see cref="FileSizeBytes"/> - older entries simply have it null.</para>
/// </summary>
public sealed record TrackIndexEntry
{
    // -- Identity -------------------------------------------------------------
    [JsonPropertyName("filePath")]       public required string FilePath       { get; init; }
    /// <summary>SHA-256 hex digest of the file bytes - used to detect file replacement.</summary>
    [JsonPropertyName("contentHash")]    public required string ContentHash    { get; init; }
    /// <summary>
    /// ISO-8601 UTC timestamp of when this entry was analysed. (!) May be
    /// <see cref="UnknownAnalysedAt"/> - see that field's doc before trusting this as a real date.
    /// </summary>
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

    // -- Tag metadata (read without DSP) --------------------------------------
    [JsonPropertyName("title")]          public string?  Title       { get; init; }
    [JsonPropertyName("artist")]         public string?  Artist      { get; init; }
    [JsonPropertyName("album")]          public string?  Album       { get; init; }
    [JsonPropertyName("genre")]          public string?  Genre       { get; init; }
    [JsonPropertyName("year")]           public uint?    Year        { get; init; }
    [JsonPropertyName("durationSec")]    public double   DurationSec { get; init; }
    [JsonPropertyName("bitrateKbps")]    public int?     BitrateKbps { get; init; }

    // -- The rest of what a track table SHOWS (0.6.1) -------------------------
    // 2026-08-07: zero live file accesses at display time - everything a row can show must already
    // be sitting in the index. Measured the night before (C1): album artist and comment were not in
    // the index at ALL, so every list that showed them read the tag off the NAS per row - ~12
    // minutes single-threaded for 2,000 files. Cover
    // presence and the ID3 version were worse: a second and third file open per visible row, through
    // CoverProbe.Has() and Id3VersionProbe.Read(). All five are now written once, at sync time, from
    // the one read the sync already does.
    [JsonPropertyName("albumArtist")]    public string?  AlbumArtist { get; init; }
    [JsonPropertyName("comment")]        public string?  Comment     { get; init; }
    [JsonPropertyName("trackNo")]        public uint?    TrackNo     { get; init; }
    /// <summary>Whether the file carries real artwork - the COV tick. Null = never established.</summary>
    [JsonPropertyName("hasCover")]       public bool?    HasCover    { get; init; }
    /// <summary>"2.3" / "2.4", or null for a file with no leading ID3v2 tag (FLAC, MP4, untagged MP3).</summary>
    [JsonPropertyName("id3Version")]     public string?  Id3Version  { get; init; }

    /// <summary>
    /// Which SHAPE of tag extraction produced this row - bumped whenever the set of tag fields the
    /// index stores grows, and compared by <see cref="LooksUnchanged(long, DateTime)"/>.
    ///
    /// <para><b>Why this exists, and why it is not optional.</b> The cheap key (size + mtime) asks
    /// "did the FILE change". It cannot ask "did WE start wanting more out of it" - so when the five
    /// fields above were added, every existing row would have kept its old, incomplete data forever:
    /// a re-sync skips unchanged files by design, which is exactly the trap night task C2 measured on
    /// 2026-08-07 (an unchanged file's <c>analysed_at</c> is frozen at its first import stamp, so
    /// L7's staleness fix could never reach the 957 files it was built for). A row from an older
    /// shape reads as changed, is re-read once, and is then quiet again.</para>
    ///
    /// <para>0 = pre-0.6.1 (title/artist/album/genre/year only). Old JSON without the key
    /// deserialises to 0, which is the correct answer for it.</para>
    /// </summary>
    [JsonPropertyName("tagRev")]         public int      TagRev      { get; init; }

    /// <summary>The tag shape this build writes. (!) Bump when a tag field is ADDED to this record -
    /// that is what re-reads the library once and fills the new column in.</summary>
    public const int CurrentTagRev = 1;

    // -- DSP analysis ----------------------------------------------------------
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

    // -- Rhythm (RhythmPattern) ------------------------------------------------
    [JsonPropertyName("beatType")]       public string?  BeatType       { get; init; }
    [JsonPropertyName("fourOnFloor")]    public double?  FourOnFloor    { get; init; }
    [JsonPropertyName("offbeatEnergy")]  public double?  OffbeatEnergy  { get; init; }
    [JsonPropertyName("swing")]          public double?  Swing          { get; init; }
    [JsonPropertyName("syncopation")]    public double?  Syncopation    { get; init; }
    [JsonPropertyName("halfTimeFeel")]   public double?  HalfTimeFeel   { get; init; }

    // -- Vibe quartet + fatigue flag (v8+) -------------------------------------
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

    // -- Grid-confidence (v9+) -------------------------------------------------
    /// <summary>ACF peak sharpness ratio [0,1]: 1 = sharp/unambiguous, 0 = broad/competing peaks.</summary>
    [JsonPropertyName("acfSharpness")]     public double?  AcfSharpness     { get; init; }
    /// <summary>Composite beat-grid confidence [0,1]. (!) threshold 0.45 (grid-soft), 0.25 (grid-fail).</summary>
    [JsonPropertyName("gridConfidence")]   public double?  GridConfidence   { get; init; }

    // -- Cheap-key helpers (0.6) -----------------------------------------------

    /// <summary>Formats a UTC timestamp the way the index stores them (round-trip "o").</summary>
    public static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("o");

    /// <summary>
    /// The <see cref="AnalysedAt"/> value used when we genuinely do not know when a row was
    /// analysed - a blob imported from a file's tags that carries no
    /// <c>TrackAnalysisState.AnalysedAtUtc</c> of its own (see
    /// <see cref="TrackIndexMapping.FromStoredBlob"/>).
    ///
    /// <para><b>Why the Unix epoch and not "now".</b> Measured 2026-08-01 (night report, L6):
    /// stamping an unknown analysed-at with the IMPORT moment made
    /// <c>StaleRule.FlacMonoDecodeBug()</c> report 0 stale entries against a real 957-file debt -
    /// every synced row looked freshly analysed because "now" is indistinguishable from a genuine
    /// fresh timestamp to any "analysed before date X" rule. The epoch is the opposite of that: it
    /// is always earlier than any real cutoff a <see cref="StaleRule.AnalysedBefore"/>-shaped rule
    /// will ever use, so "we don't know" reads as "assume maximally stale" instead of "assume
    /// clean" - the honest answer when the true date is unrecoverable. This is also why it is a
    /// dedicated sentinel value rather than a new boolean column: the local SQLite index
    /// (<c>LibraryDb</c>) has a fixed, hand-written column list, so a new field on this record
    /// would silently NOT survive a DB round-trip, while this string keeps flowing through the
    /// existing <c>analysed_at</c> TEXT column, the JSON index, AND the DB unchanged.</para>
    /// </summary>
    public static readonly DateTime UnknownAnalysedAt = DateTime.UnixEpoch;

    /// <summary>
    /// Filesystem timestamps are not equal across filesystems: FAT/exFAT round to 2 s and SMB
    /// clients have been seen off by a hair. Two seconds is the standard tolerance and is far
    /// tighter than any real edit.
    /// </summary>
    private static readonly TimeSpan MTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The CHEAP freshness check: same size and (within tolerance) same last-write time as what
    /// we recorded. Returns false when <see cref="ModifiedUtc"/> is missing (pre-0.6 entry) or
    /// unparsable - the caller then falls back to <see cref="ContentHash"/>.
    ///
    /// <para>This is the one thing that makes a rescan cheap: hashing 6.8k tracks over SMB pulls
    /// the entire library through the network, a directory listing does not.</para>
    /// </summary>
    /// <para>(!!) This asks ONLY "is the file unchanged". It deliberately does NOT consider
    /// <see cref="TagRev"/> - <c>AnalyzeCommand</c> uses this same method to decide whether to skip
    /// DSP, and folding a tag-shape bump in here would re-ANALYSE the whole library (hours of DSP)
    /// when all that is needed is re-READING its tags (minutes). Sync pairs it with
    /// <see cref="TagsAreCurrent"/>; analysis does not.</para>
    public bool LooksUnchanged(long sizeBytes, DateTime modifiedUtc) =>
        LooksUnchanged(FileSizeBytes, ModifiedUtc, sizeBytes, modifiedUtc);

    /// <summary>
    /// Whether this row's TAG fields were extracted by the current shape - see <see cref="TagRev"/>.
    /// A sync skips a file only when it is BOTH unchanged and current; anything else re-reads the
    /// tags, which costs one file open and no DSP.
    /// </summary>
    [JsonIgnore]
    public bool TagsAreCurrent => TagRev >= CurrentTagRev;

    /// <summary>
    /// The same comparison against a bare recorded key, so a sync pass can hold a few million
    /// (size, mtime) pairs in memory without materialising whole entries. ONE implementation -
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

    /// <summary>Known file, no analysis yet - queue it, do not treat it as a failure.</summary>
    [JsonIgnore]
    public bool NeedsAnalysis => !Success && Error is null;

    /// <summary>
    /// Adopts a pre-0.6 entry into the cheap-key world: same size, so we trust the existing
    /// analysis and just record the mtime we can now see. Without this, upgrading would hash
    /// every file in the library once - 60 GB of NAS traffic to learn nothing new.
    ///
    /// <para>Deliberate: a stale blob is trusted as-is, never silently re-analysed
    /// (see the analysis-tag-persistence design).</para>
    /// </summary>
    public TrackIndexEntry AdoptedWith(DateTime modifiedUtc) =>
        this with { ModifiedUtc = FormatUtc(modifiedUtc) };
}
