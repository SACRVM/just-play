namespace JustPlay.Core.Models;

/// <summary>
/// Tag-level information read from the file. Purely descriptive — no analysis.
/// </summary>
public sealed record TrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public uint? Year { get; init; }

    /// <summary>Free-text comment tag (where some tools, e.g. Mixed In Key, stash the key/energy).</summary>
    public string? Comment { get; init; }

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
    /// BPM stored in the file's tags, if the producer/labeler wrote one.
    /// This is the *claimed* BPM — our own analysis may override it.
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
