namespace JustPlay.Core.Models;

/// <summary>
/// The editorial fields a user can change in a tag editor (JUST TAG).
/// Analysis-computed fields (BPM, key, energy, JUSTPLAY blob, ReplayGain) are
/// intentionally absent — those travel via <see cref="TagWrite"/> / <see cref="TagRestore"/>.
/// </summary>
public sealed record EditableTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }

    /// <summary>Release year; 0 = unset (clears the tag on write).</summary>
    public uint Year { get; init; }

    /// <summary>Track number; 0 = unset (clears the tag on write).</summary>
    public uint TrackNumber { get; init; }

    /// <summary>Free-text comment; null/empty clears the comment tag.</summary>
    public string? Comment { get; init; }
}

/// <summary>Describes what to do with embedded cover art during a <see cref="IMetadataWriter.WriteEditable"/> call.</summary>
public enum CoverAction
{
    /// <summary>Leave the existing cover art (Pictures array) completely untouched.</summary>
    Keep,

    /// <summary>Remove all embedded pictures (Pictures = empty array).</summary>
    Remove,

    /// <summary>Replace the embedded pictures with a single front-cover image supplied as a byte array.</summary>
    Replace,
}
