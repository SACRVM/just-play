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

    /// <summary>
    /// Restore a file to a previously-captured tag state — the inverse of <see cref="Write"/>,
    /// used by "Undo last write". Unlike <see cref="Write"/>, a null field here means CLEAR (the
    /// field had no value before the undone write): BPM → 0, key → blank, and the ENERGY /
    /// JUSTPLAY custom fields are removed. Non-null fields are written back verbatim.
    /// </summary>
    void Restore(string filePath, TagRestore restore);

    /// <summary>
    /// Write the editorial fields from <paramref name="tags"/> to the file and save.
    /// <para>
    /// Analysis fields (BPM, key, energy, JUSTPLAY blob, ReplayGain, POPM) are
    /// intentionally NOT touched — this method is purely editorial.
    /// </para>
    /// </summary>
    /// <param name="filePath">Path to the audio file to update.</param>
    /// <param name="tags">New editorial tag values.</param>
    /// <param name="coverAction">
    /// What to do with embedded cover art: <see cref="CoverAction.Keep"/> leaves the
    /// Pictures array untouched; <see cref="CoverAction.Remove"/> clears it;
    /// <see cref="CoverAction.Replace"/> replaces it with the supplied image.
    /// </param>
    /// <param name="newCover">
    /// Raw cover image bytes; only used when <paramref name="coverAction"/> is
    /// <see cref="CoverAction.Replace"/>.
    /// </param>
    /// <param name="coverMimeType">
    /// MIME type of <paramref name="newCover"/> (e.g. "image/jpeg"); defaults to
    /// "image/jpeg" when null.
    /// </param>
    void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
        byte[]? newCover, string? coverMimeType);

    /// <summary>
    /// Set the ID3v2 version + text encoding used when SAVING MP3 tags — process-global (TagLib#
    /// static config). Call once at startup and whenever the user changes the preference. Non-MP3
    /// containers (FLAC / MP4 / Ogg) are unaffected. The default <see cref="Id3WriteFormat.Id3v23Utf16"/>
    /// matches mp3tag and is the most broadly readable; see <see cref="Id3WriteFormat"/>.
    /// </summary>
    void ConfigureId3WriteFormat(Id3WriteFormat format);
}

/// <summary>
/// Describes a single write: which standard fields to set (null = leave as-is) and
/// the full <see cref="TrackAnalysisState"/> blob to stamp. Camelot is NOT written —
/// it is derived from the musical key on read, so the comment stays the user's
/// unless <see cref="Comment"/> is explicitly set (opt-in DJ Software compatible mode).
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

    /// <summary>
    /// When non-null, write this exact string as the file's comment tag. Used by the
    /// "DJ Software compatible" comment feature: the caller builds the comment (Camelot +
    /// energy prefix + preserved user text) and passes it here. Null = leave comment untouched.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// When non-null, set or clear the POPM favourite flag. True → write POPM rating 255
    /// ("loved", the Windows Media Player convention); false → remove the POPM frame.
    /// Null = leave the POPM frame untouched.
    /// </summary>
    public bool? Favorite { get; init; }

    /// <summary>
    /// When non-null, write this value as the <c>REPLAYGAIN_TRACK_GAIN</c> custom field
    /// (e.g. <c>"-6.35 dB"</c>). Null = leave the field untouched.
    /// </summary>
    public double? ReplayGainDb { get; init; }

    /// <summary>
    /// When non-null, write this value as the <c>REPLAYGAIN_TRACK_PEAK</c> custom field
    /// (e.g. <c>"0.988553"</c>). Null = leave the field untouched.
    /// </summary>
    public double? Peak { get; init; }

    /// <summary>
    /// When non-null, write this string into the file's Content Group (Grouping) tag.
    /// ID3v2: TIT1 frame; MP4: ©grp; FLAC: GROUPING comment.
    /// Used by the CLI batch tagger to stamp the JP vibe tag where DJ software
    /// surfaces the Grouping column.  Null = leave the field untouched.
    /// </summary>
    public string? Grouping { get; init; }
}

/// <summary>
/// A snapshot of the tag fields JustPlay touches, captured before a write so the write can be
/// undone. Null means the field was empty before — <see cref="IMetadataWriter.Restore"/> clears
/// it rather than leaving it (the key difference from <see cref="TagWrite"/>).
/// </summary>
public sealed record TagRestore
{
    /// <summary>Previous standard tempo tag; null → clear (BPM 0).</summary>
    public double? Bpm { get; init; }

    /// <summary>Previous standard key tag; null → clear.</summary>
    public MusicalKey? Key { get; init; }

    /// <summary>Previous ENERGY custom field; null → remove the field.</summary>
    public int? Energy { get; init; }

    /// <summary>Previous JUSTPLAY blob (parsed); null → remove the field.</summary>
    public TrackAnalysisState? State { get; init; }

    /// <summary>
    /// Previous comment field value, captured only when the "DJ Software compatible" comment
    /// feature was active at write time. Null = the feature was off, or the comment was not
    /// captured — <see cref="IMetadataWriter.Restore"/> leaves the comment untouched in that case.
    /// A non-null value (including empty string) is written back verbatim so even a previously-
    /// empty comment is correctly restored.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// True when the comment was explicitly captured before the write (distinguishes "feature off,
    /// nothing captured" from "feature was on, existing comment was empty/null"). Only when this
    /// is true will <see cref="IMetadataWriter.Restore"/> touch the comment field.
    /// </summary>
    public bool CommentCaptured { get; init; }

    /// <summary>
    /// Previous <c>REPLAYGAIN_TRACK_GAIN</c> value; null → remove the field on restore.
    /// </summary>
    public double? ReplayGainDb { get; init; }

    /// <summary>
    /// Previous <c>REPLAYGAIN_TRACK_PEAK</c> value; null → remove the field on restore.
    /// </summary>
    public double? Peak { get; init; }
}
