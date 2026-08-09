namespace JustPlay.Core.Models;

/// <summary>
/// Tag-level information read from the file. Purely descriptive - no analysis.
/// </summary>
public sealed record TrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>Album artist tag (ID3v2 TPE2 / Xiph ALBUMARTIST).</summary>
    public string? AlbumArtist { get; init; }

    public string? Genre { get; init; }
    public uint? Year { get; init; }

    /// <summary>Track number tag; null when unset (zero in the raw tag).</summary>
    public uint? TrackNumber { get; init; }

    /// <summary>Free-text comment tag (where some tools, e.g. Mixed In Key, stash the key/energy).</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Content Group / Grouping tag (ID3v2 TIT1, MP4 (c)grp, FLAC GROUPING).
    /// Used by JustPlay's batch tagger to store the JP vibe string in a field
    /// that DJ software surfaces in the Grouping column.
    /// </summary>
    public string? Grouping { get; init; }

    /// <summary>Total track length.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Audio bitrate in kbit/s, if known.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Sample rate in Hz, if known.</summary>
    public int? SampleRate { get; init; }

    public int? Channels { get; init; }

    /// <summary>Embedded cover art bytes, if present.</summary>
    public byte[]? CoverArt { get; init; }

    /// <summary>
    /// The file's ID3v2 major version as a display string ("2.3", "2.4"), or null when it carries no
    /// leading ID3v2 tag at all - a FLAC, an MP4, or an MP3 that has never been tagged.
    ///
    /// <para>It rides along on the metadata read so the LIBRARY INDEX can store it, which is the whole
    /// point: the ID3 column used to be a per-row live probe of every visible file, and the table must
    /// not touch the disk to paint itself - zero live file accesses at display time, everything a row
    /// can show must already be sitting in the index. The reader fills it from the shared <c>Id3VersionProbe</c> - the SAME code the
    /// tag editor's write-format notice uses, so the two can never disagree about one file.</para>
    /// </summary>
    public string? Id3Version { get; init; }

    /// <summary>
    /// BPM stored in the file's tags, if the producer/labeler wrote one.
    /// This is the *claimed* BPM - our own analysis may override it.
    /// </summary>
    public double? TaggedBpm { get; init; }

    /// <summary>Key stored in the file's tags, if present (raw string).</summary>
    public string? TaggedKey { get; init; }

    /// <summary>Energy stored in the file's tags (TXXX:ENERGY), if present. 1..10.</summary>
    public int? TaggedEnergy { get; init; }

    /// <summary>
    /// JustPlay's own analysis record parsed from the JUSTPLAY tag, if this file was
    /// analysed by us before. Lets the app reproduce the queue picture without
    /// re-analysing. Null when the file carries no (valid) JustPlay blob.
    /// </summary>
    public TrackAnalysisState? StoredAnalysis { get; init; }

    /// <summary>
    /// Whether this track is marked as a favourite, read from the ID3v2 POPM
    /// (Popularimeter) frame. True when the POPM rating byte is &gt; 0. For non-ID3
    /// formats that carry no POPM equivalent the field is silently false (no crash).
    /// </summary>
    public bool IsFavorite { get; init; }

    /// <summary>Fallback display name when no title tag exists (the file name).</summary>
    public required string FallbackName { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FallbackName : Title!;
}
