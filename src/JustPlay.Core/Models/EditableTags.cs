namespace JustPlay.Core.Models;

/// <summary>
/// The editorial fields a user can change in a tag editor (JUST TAG).
/// Analysis-computed fields (BPM, key, energy, JUSTPLAY blob, ReplayGain) are
/// intentionally absent - those travel via <see cref="TagWrite"/> / <see cref="TagRestore"/>.
/// <para>
/// A snapshot of VALUES, not an instruction: which of them a write actually puts into the file is
/// the <see cref="EditableField"/> mask handed to <see cref="Abstractions.IMetadataWriter.WriteEditable"/>
/// next to it. A value outside the mask is never written, whatever it holds.
/// </para>
/// </summary>
public sealed record EditableTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }

    /// <summary>Release year; 0 = unset (clears the tag when written).</summary>
    public uint Year { get; init; }

    /// <summary>Track number; 0 = unset (clears the tag when written).</summary>
    public uint TrackNumber { get; init; }

    /// <summary>Free-text comment; null/empty clears the comment tag when written.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// The file could not be READ (locked, share unreachable, vanished) - the same meaning as
    /// <see cref="TrackMetadata.ReadFailed"/>. Every value above is then empty because nothing was
    /// seen, not because the file carries nothing, and no write may be built on top of it.
    /// </summary>
    public bool ReadFailed { get; init; }

    /// <summary>
    /// The fields whose values differ between two snapshots - the one comparison every editorial
    /// write uses to decide what it writes. Text is compared trimmed and ordinal, with null, empty
    /// and whitespace all meaning "nothing"; a box someone emptied and a box never set are the same.
    /// </summary>
    public static EditableField Differences(EditableTags a, EditableTags b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var d = EditableField.None;
        if (!SameText(a.Title, b.Title))             d |= EditableField.Title;
        if (!SameText(a.Artist, b.Artist))           d |= EditableField.Artist;
        if (!SameText(a.Album, b.Album))             d |= EditableField.Album;
        if (!SameText(a.AlbumArtist, b.AlbumArtist)) d |= EditableField.AlbumArtist;
        if (!SameText(a.Genre, b.Genre))             d |= EditableField.Genre;
        if (a.Year != b.Year)                        d |= EditableField.Year;
        if (a.TrackNumber != b.TrackNumber)          d |= EditableField.TrackNumber;
        if (!SameText(a.Comment, b.Comment))         d |= EditableField.Comment;
        return d;
    }

    private static bool SameText(string? a, string? b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);

    private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
}

/// <summary>
/// Which editorial fields a <see cref="Abstractions.IMetadataWriter.WriteEditable"/> call writes.
/// A field outside the mask is not assigned at all, so its frame stays exactly as the file has it -
/// including the values a re-assignment would lose (an ID3v2.3 "AC/DC" that TagLib# splits on '/',
/// a full TDRC date behind a bare year, iTunes' COMM frames, a second ARTIST field in a FLAC).
/// </summary>
[Flags]
public enum EditableField
{
    None        = 0,
    Title       = 1 << 0,
    Artist      = 1 << 1,
    Album       = 1 << 2,
    AlbumArtist = 1 << 3,
    Genre       = 1 << 4,
    Year        = 1 << 5,
    TrackNumber = 1 << 6,
    Comment     = 1 << 7,

    All = Title | Artist | Album | AlbumArtist | Genre | Year | TrackNumber | Comment,
}

/// <summary>Describes what to do with embedded cover art during a <see cref="Abstractions.IMetadataWriter.WriteEditable"/> call.</summary>
public enum CoverAction
{
    /// <summary>Leave the existing cover art (Pictures array) completely untouched.</summary>
    Keep,

    /// <summary>Remove all embedded pictures (Pictures = empty array).</summary>
    Remove,

    /// <summary>Replace the embedded pictures with a single front-cover image supplied as a byte array.</summary>
    Replace,
}
