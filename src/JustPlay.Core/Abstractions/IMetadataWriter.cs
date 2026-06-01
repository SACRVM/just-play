using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Writes analysis values back into a file's tags. The counterpart to
/// <see cref="IMetadataReader"/>; implemented in the metadata adapter (TagLib#).
/// Only ever called on an explicit user action — JustPlay never auto-writes files.
/// </summary>
public interface IMetadataWriter
{
    /// <summary>
    /// Apply <paramref name="write"/> to the file and save. Only the non-null
    /// standard fields are written; <see cref="TagWrite.State"/>, when set, is
    /// always written as the JUSTPLAY blob. Other tags (comment, title, …) are
    /// left untouched.
    /// </summary>
    void Write(string filePath, TagWrite write);
}

/// <summary>
/// Describes a single write: which standard fields to set (null = leave as-is) and
/// the full <see cref="TrackAnalysisState"/> blob to stamp. Camelot is NOT written —
/// it is derived from the musical key on read, so the comment stays the user's.
/// </summary>
public sealed record TagWrite
{
    /// <summary>Write BPM into the standard tempo tag (rounded; the exact value lives in <see cref="State"/>).</summary>
    public double? Bpm { get; init; }

    /// <summary>Write the musical key into the standard key tag (e.g. "Am").</summary>
    public MusicalKey? Key { get; init; }

    /// <summary>Write perceived energy (1..10) into the non-standard ENERGY tag.</summary>
    public int? Energy { get; init; }

    /// <summary>Stamp the JustPlay analysis record (detected values + version + per-field decisions).</summary>
    public TrackAnalysisState? State { get; init; }
}
