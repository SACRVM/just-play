using System;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// READ -> OVERLAY -> WRITE. The one shape an editorial tag write can have here, kept in one place
/// so the two things that do it cannot drift apart.
///
/// <para><b>Why the read is not optional.</b> <see cref="IMetadataWriter.WriteEditable"/> takes a
/// COMPLETE set of tags and clears whatever it is not given. So "write only the genre" cannot be
/// expressed as a write - it can only be expressed as "read this file's tags, lay the new genre over
/// them, write the result back". Every editorial write in the suite is therefore per file, and every
/// one of them has to build the whole set.</para>
///
/// <para><b>Why the skip is not optional either.</b> A file whose values the edit does not actually
/// change is left completely alone: no new timestamp, no re-serialised ID3 tag, no tidied comment
/// frame over a value it already carried. On a selection where nine files in ten already read
/// correctly, that is the difference between touching 37 files and touching 4.</para>
/// </summary>
public static class EditorialWrite
{
    /// <summary>
    /// The text fields a bulk edit can be pointed at. Not YEAR or TRACK #: those are numbers, and
    /// "lowercase the year" is not a thing anybody means.
    /// </summary>
    public static readonly TagField[] TextFields =
    [
        TagField.Artist, TagField.Title, TagField.Album, TagField.AlbumArtist,
        TagField.Genre, TagField.Comment,
    ];

    /// <summary>The field's name as the UI writes it - one spelling, used by every window.</summary>
    public static string Label(TagField f) => f switch
    {
        TagField.Title       => "TITLE",
        TagField.Artist      => "ARTIST",
        TagField.Album       => "ALBUM",
        TagField.AlbumArtist => "ALBUM ARTIST",
        TagField.Genre       => "GENRE",
        TagField.Comment     => "COMMENT",
        TagField.Year        => "YEAR",
        TagField.Track       => "TRACK #",
        _                    => "",
    };

    /// <summary>
    /// What the file carries in one text field today, normalised the way a write normalises it -
    /// trimmed, and empty read as nothing. Comparing against anything else is how a value that only
    /// differs by a space becomes a write nobody asked for.
    /// </summary>
    public static string? Value(TrackMetadata m, TagField f) => f switch
    {
        TagField.Title       => Blank(m.Title),
        TagField.Artist      => Blank(m.Artist),
        TagField.Album       => Blank(m.Album),
        TagField.AlbumArtist => Blank(m.AlbumArtist),
        TagField.Genre       => Blank(m.Genre),
        TagField.Comment     => Blank(m.Comment),
        _                    => null,
    };

    /// <summary>What the file holds today, in the shape a write takes - the thing an edit overlays.</summary>
    public static EditableTags From(TrackMetadata m) => new()
    {
        Title       = Blank(m.Title),
        Artist      = Blank(m.Artist),
        Album       = Blank(m.Album),
        AlbumArtist = Blank(m.AlbumArtist),
        Genre       = Blank(m.Genre),
        Comment     = Blank(m.Comment),
        Year        = m.Year ?? 0u,
        TrackNumber = m.TrackNumber ?? 0u,
    };

    /// <summary>
    /// The file's tags with <paramref name="value"/> laid over the TEXT fields. It is handed each
    /// field and that field's current value, and returns what it should become - returning what it
    /// was given means "leave it". The two numbers are carried through untouched.
    /// </summary>
    public static EditableTags Over(TrackMetadata current, Func<TagField, string?, string?> value)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(value);

        string? V(TagField f) => Blank(value(f, Value(current, f)));

        return From(current) with
        {
            Title       = V(TagField.Title),
            Artist      = V(TagField.Artist),
            Album       = V(TagField.Album),
            AlbumArtist = V(TagField.AlbumArtist),
            Genre       = V(TagField.Genre),
            Comment     = V(TagField.Comment),
        };
    }

    /// <summary>
    /// Would writing <paramref name="next"/> actually change what the file holds? The question the
    /// skip hangs off - see the class summary for why it is worth asking.
    /// </summary>
    public static bool Changes(EditableTags next, TrackMetadata current)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(current);

        return !TextEquals(next.Title, Blank(current.Title))
            || !TextEquals(next.Artist, Blank(current.Artist))
            || !TextEquals(next.Album, Blank(current.Album))
            || !TextEquals(next.AlbumArtist, Blank(current.AlbumArtist))
            || !TextEquals(next.Genre, Blank(current.Genre))
            || !TextEquals(next.Comment, Blank(current.Comment))
            || next.Year != (current.Year ?? 0u)
            || next.TrackNumber != (current.TrackNumber ?? 0u);
    }

    /// <summary>
    /// One file, start to finish: read it, lay <paramref name="value"/> over its text fields, and
    /// write only if that changes something. Returns null for "nothing to do".
    ///
    /// <para>The write goes through <paramref name="execute"/>, the host's own route to the file - so
    /// a file that is PLAYING is deferred rather than failed, exactly as a save from the editor is.</para>
    ///
    /// <para>(!) The transform is applied to what the FILE says right now, not to whatever a preview
    /// captured earlier. The file is the truth, and it is read here anyway.</para>
    /// </summary>
    public static TagWriteOutcome? One(string path, IMetadataReader reader, IMetadataWriter writer,
                                       TagWriteExecutor execute, Func<TagField, string?, string?> value)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(execute);

        var current = reader.Read(path);
        var next = Over(current, value);

        if (!Changes(next, current)) return null;

        return execute(path, p => writer.WriteEditable(p, next, CoverAction.Keep, null, null));
    }

    /// <summary>Null and "" are the same thing in a tag field - a box someone emptied and a box that
    /// was never set must not read as a change.</summary>
    internal static bool TextEquals(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
