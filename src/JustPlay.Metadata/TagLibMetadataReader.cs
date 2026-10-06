using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Reads tags via TagLib#. Degrades gracefully: an unreadable or tagless file still
/// yields usable metadata (at least the file name) so it can always be played.
/// </summary>
public sealed class TagLibMetadataReader : IMetadataReader
{
    public TrackMetadata Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fallback = Path.GetFileNameWithoutExtension(filePath);

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            var props = file.Properties;

            var energyStr = TagCustomFields.Get(file, "ENERGY");
            int? taggedEnergy = int.TryParse(energyStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var en)
                ? en : null;

            return new TrackMetadata
            {
                FallbackName = fallback,
                Title = Clean(tag.Title),
                // The same full values the editor shows (see AllValues): a First* read cut an
                // ID3v2.3 "AC/DC" to "AC" and a Beatport genre at its first '/', and the index, the
                // track table and search all saw the cut version.
                Artist = Clean(AllValues(file, static t => t.Performers)),
                Album = Clean(tag.Album),
                AlbumArtist = Clean(AllValues(file, static t => t.AlbumArtists)),
                Genre = Clean(StoredV23Genre(filePath) ?? AllValues(file, static t => t.Genres)),
                Year = tag.Year == 0 ? null : tag.Year,
                TrackNumber = tag.Track == 0 ? null : tag.Track,
                Comment = Clean(tag.Comment),
                Grouping = Clean(tag.Grouping),
                Duration = props?.Duration ?? TimeSpan.Zero,
                Bitrate = props?.AudioBitrate is > 0 and var br ? br : null,
                SampleRate = props?.AudioSampleRate is > 0 and var sr ? sr : null,
                Channels = props?.AudioChannels is > 0 and var ch ? ch : null,
                TaggedBpm = tag.BeatsPerMinute == 0 ? null : tag.BeatsPerMinute,
                TaggedKey = Clean(tag.InitialKey),
                TaggedEnergy = taggedEnergy,
                StoredAnalysis = AnalysisStateCodec.TryParse(TagCustomFields.Get(file, "JUSTPLAY")),
                CoverArt = FirstPicture(tag),
                // Costs one 4-byte read of the file head, and only during a SYNC - so that the ID3
                // column can be painted straight out of the index instead of probing every visible
                // row at display time.
                Id3Version = Id3VersionProbe.Read(filePath) is { } major
                             ? Id3VersionProbe.ShortName(major)
                             : null,
                IsFavorite = ReadPopm(file),
            };
        }
        catch (Exception ex)
        {
            // Corrupt/unsupported tags must never stop playback - return the bare minimum. An IO
            // failure is flagged on top: the file may be perfectly tagged, we just could not see it.
            return new TrackMetadata
            {
                FallbackName = fallback,
                ReadFailed = ex is IOException or UnauthorizedAccessException,
            };
        }
    }

    public EditableTags ReadEditable(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            return new EditableTags
            {
                Title       = Clean(tag.Title),
                Artist      = Clean(AllValues(file, static t => t.Performers)),
                Album       = Clean(tag.Album),
                AlbumArtist = Clean(AllValues(file, static t => t.AlbumArtists)),
                Genre       = Clean(StoredV23Genre(filePath) ?? AllValues(file, static t => t.Genres)),
                Year        = tag.Year,
                TrackNumber = tag.Track,
                Comment     = Clean(tag.Comment),
            };
        }
        catch (Exception ex)
        {
            // Corrupt/unsupported file - return empty snapshot rather than surfacing an exception.
            // An IO failure is flagged on top, exactly as Read does: nothing was seen, and a write
            // built on this snapshot would be built on nothing.
            return new EditableTags { ReadFailed = ex is IOException or UnauthorizedAccessException };
        }
    }

    /// <summary>
    /// The genre of a leading ID3v2.3 tag as the frame stores it. TagLib# splits a v2.3 TCON on '/'
    /// AND ';', so no join can tell which one stood there - see <see cref="Id3v23Genre"/>. Left to
    /// TagLib# when the text leads with ID3v1 index references ("(17)"), which it maps to names.
    /// </summary>
    private static string? StoredV23Genre(string path) =>
        Id3v23Genre.Read(path) is { } s && !s.StartsWith('(') && !byte.TryParse(s, out _) ? s : null;

    /// <summary>
    /// Every value of a multi-value field, as ONE string - what a tag editor shows and what it writes
    /// back only if the user edits it. Two rules, because there are two reasons a field reads as
    /// several values:
    /// <list type="bullet">
    ///   <item>ID3v2.3 (and older): TagLib# SPLITS the frame on '/' - "AC/DC" reads as "AC" + "DC".
    ///     It keeps every character but the slash, so joining with "/" gives back the frame's text
    ///     exactly. (A v2.3 genre is split on ';' too, which no join can undo; ReadEditable reads
    ///     that one from the frame's bytes - see <see cref="StoredV23Genre"/>.)</item>
    ///   <item>Everything else (ID3v2.4 null-separated values, repeated Xiph fields in a FLAC, MP4):
    ///     the values really are separate. They are joined with "; " - foobar2000's display form -
    ///     so all of them are visible and none can be mistaken for part of another.</item>
    /// </list>
    /// The first tag that carries the field answers, in TagLib#'s own order - the same tag
    /// <c>FirstPerformer</c> and friends read from - so the rule is chosen by where the values came
    /// from, not by the file type.
    /// </summary>
    internal static string? AllValues(TagLib.File file, Func<TagLib.Tag, string[]?> pick)
    {
        IEnumerable<TagLib.Tag?> tags = file.Tag is TagLib.CombinedTag combined ? combined.Tags : [file.Tag];

        foreach (var t in tags)
        {
            if (t is null || pick(t) is not { Length: > 0 } values) continue;

            return t is TagLib.Id3v2.Tag { Version: < 4 }
                ? string.Join("/", values)
                : string.Join("; ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        }

        return null;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The cover bytes, if the tag carries real artwork. Which picture that IS - past
    /// Serato's and MIK's GEOB blobs in the same array - is decided ONCE, in
    /// <see cref="CoverProbe.Best"/>, because the "does it have a cover" probe has to agree with this
    /// or the tick column and the editor would disagree about the same file.</summary>
    private static byte[]? FirstPicture(TagLib.Tag tag)
    {
        var data = CoverProbe.Best(tag)?.Data;
        return data is { Count: > 0 } ? data.Data : null;
    }

    /// <summary>
    /// Read the POPM (Popularimeter) rating from the file's ID3v2 tag.
    /// Returns true when rating &gt; 0 (any non-zero value means "liked").
    /// Falls back gracefully to false for non-ID3 formats or missing frames.
    /// Also handles Xiph RATING comment fields (FLAC/OGG) where a value of
    /// "5" (1-5 stars) or "255" (0-255 raw) is treated as "liked".
    /// </summary>
    /// <remarks>
    /// Internal (not private): <see cref="TagLibWritePreview"/> reuses this exact logic to read
    /// the CURRENT rating for its dry-run plan, so the preview can never drift from what
    /// <see cref="Read"/> itself considers "liked".
    /// </remarks>
    internal static bool ReadPopm(TagLib.File file)
    {
        // ID3v2: POPM frame - the standard path for MP3.
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.PopularimeterFrame.Get(id3, PopmUser, false);
            return frame is not null && frame.Rating > 0;
        }

        // Xiph (FLAC/OGG): non-standard RATING field as a courtesy fallback.
        // Vorbis comment RATING is often 0-5 (star scale) or 0-255 (raw byte).
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            var raw = xiph.GetFirstField("RATING");
            if (!string.IsNullOrEmpty(raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                return r > 0;
        }

        return false;
    }

    /// <summary>POPM user identifier - an ASCII string stored in the ID3 frame to namespace the rating.</summary>
    internal const string PopmUser = "JustPlay";
}
