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
                Artist = Clean(tag.FirstPerformer),
                Album = Clean(tag.Album),
                AlbumArtist = Clean(tag.FirstAlbumArtist),
                Genre = Clean(tag.FirstGenre),
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
                IsFavorite = ReadPopm(file),
            };
        }
        catch
        {
            // Corrupt/unsupported tags must never stop playback — return the bare minimum.
            return new TrackMetadata { FallbackName = fallback };
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
                Artist      = Clean(tag.FirstPerformer),
                Album       = Clean(tag.Album),
                AlbumArtist = Clean(tag.FirstAlbumArtist),
                Genre       = Clean(tag.FirstGenre),
                Year        = tag.Year,
                TrackNumber = tag.Track,
                Comment     = Clean(tag.Comment),
            };
        }
        catch
        {
            // Corrupt/unsupported file — return empty snapshot rather than surfacing an exception.
            return new EditableTags();
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static byte[]? FirstPicture(TagLib.Tag tag)
    {
        var pics = tag.Pictures;
        if (pics is null || pics.Length == 0) return null;
        var data = pics[0].Data;
        return data.Count > 0 ? data.Data : null;
    }

    /// <summary>
    /// Read the POPM (Popularimeter) rating from the file's ID3v2 tag.
    /// Returns true when rating &gt; 0 (any non-zero value means "liked").
    /// Falls back gracefully to false for non-ID3 formats or missing frames.
    /// Also handles Xiph RATING comment fields (FLAC/OGG) where a value of
    /// "5" (1–5 stars) or "255" (0–255 raw) is treated as "liked".
    /// </summary>
    private static bool ReadPopm(TagLib.File file)
    {
        // ID3v2: POPM frame — the standard path for MP3.
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

    /// <summary>POPM user identifier — an ASCII string stored in the ID3 frame to namespace the rating.</summary>
    internal const string PopmUser = "JustPlay";
}
