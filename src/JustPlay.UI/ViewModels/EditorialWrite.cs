using System;
using System.IO;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// READ -> OVERLAY -> WRITE WHAT CHANGED. The one shape an editorial tag write can have here, kept in
/// one place so the things that do it cannot drift apart.
///
/// <para><b>Only the changed fields are written.</b> <see cref="IMetadataWriter.WriteEditable"/>
/// takes a field mask, and the mask is exactly the fields whose value the edit changes in THIS file
/// (<see cref="EditableTags.Differences"/>). Everything else is not assigned, so its frame stays as
/// it is - which matters, because a value that went through a read is not always the value in the
/// file (ID3v2.3 splits "AC/DC" on '/'; a year is not a full date).</para>
///
/// <para><b>The read is the file's own, and lossless.</b> The overlay starts from
/// <see cref="IMetadataReader.ReadEditable"/>, which returns every value of a multi-value field - a
/// transform pointed at an artist works on "AC/DC", not on the "AC" a first-value read would hand it.
/// A file that cannot be read is not written at all.</para>
///
/// <para><b>Why the skip is not optional either.</b> A file whose values the edit does not actually
/// change is left completely alone: no new timestamp, no re-serialised ID3 tag. On a selection where
/// nine files in ten already read correctly, that is the difference between touching 37 files and
/// touching 4.</para>
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

    /// <summary>The same question asked of the editable snapshot - the lossless one a write overlays.</summary>
    public static string? Value(EditableTags m, TagField f) => f switch
    {
        TagField.Title       => Blank(m.Title),
        TagField.Artist      => Blank(m.Artist),
        TagField.Album       => Blank(m.Album),
        TagField.AlbumArtist => Blank(m.AlbumArtist),
        TagField.Genre       => Blank(m.Genre),
        TagField.Comment     => Blank(m.Comment),
        _                    => null,
    };

    /// <summary>A <see cref="TrackMetadata"/> in the shape a write takes. For hosts and test doubles
    /// that only have the full read; the real reader's <see cref="IMetadataReader.ReadEditable"/> is
    /// the lossless source an editorial write starts from.</summary>
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
        ReadFailed  = m.ReadFailed,
    };

    /// <summary>
    /// The file's tags with <paramref name="value"/> laid over the TEXT fields. It is handed each
    /// field and that field's current value, and returns what it should become - returning what it
    /// was given means "leave it". The two numbers are carried through untouched.
    /// </summary>
    public static EditableTags Over(EditableTags current, Func<TagField, string?, string?> value)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(value);

        string? V(TagField f) => Blank(value(f, Value(current, f)));

        return current with
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
    /// One file, start to finish: read it, lay <paramref name="value"/> over its text fields, and
    /// write the fields that changes - and nothing at all when it changes none. Returns null for
    /// "nothing to do".
    ///
    /// <para>The write goes through <paramref name="execute"/>, the host's own route to the file - so
    /// a file that is PLAYING is deferred rather than failed, exactly as a save from the editor is.</para>
    ///
    /// <para>(!) The transform is applied to what the FILE says right now, not to whatever a preview
    /// captured earlier. The file is the truth, and it is read here anyway.</para>
    /// </summary>
    /// <exception cref="IOException">The file could not be read. Nothing is written to it: an overlay
    /// on an empty read would be an overlay on nothing.</exception>
    public static TagWriteOutcome? One(string path, IMetadataReader reader, IMetadataWriter writer,
                                       TagWriteExecutor execute, Func<TagField, string?, string?> value)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(execute);

        var current = reader.ReadEditable(path);
        if (current.ReadFailed) throw CouldNotRead(path);

        var next = Over(current, value);
        var fields = EditableTags.Differences(next, current);

        if (fields == EditableField.None) return null;

        return execute(path, p => writer.WriteEditable(p, next, fields, CoverAction.Keep, null, null));
    }

    /// <summary>The one wording for "this file could not be read, so it was not written" - a batch
    /// counts it as failed and shows the first reason.</summary>
    public static IOException CouldNotRead(string path) =>
        new($"Could not read \"{Path.GetFileName(path)}\" - it was not written.");

    /// <summary>Null and "" are the same thing in a tag field - a box someone emptied and a box that
    /// was never set must not read as a change.</summary>
    internal static bool TextEquals(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
