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

    /// <summary>Fallback display name when no title tag exists (the file name).</summary>
    public required string FallbackName { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FallbackName : Title!;
}
