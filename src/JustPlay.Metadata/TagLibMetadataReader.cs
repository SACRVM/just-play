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

            return new TrackMetadata
            {
                FallbackName = fallback,
                Title = Clean(tag.Title),
                Artist = Clean(tag.FirstPerformer),
                Album = Clean(tag.Album),
                Genre = Clean(tag.FirstGenre),
                Year = tag.Year == 0 ? null : tag.Year,
                Duration = props?.Duration ?? TimeSpan.Zero,
                Bitrate = props?.AudioBitrate is > 0 and var br ? br : null,
                SampleRate = props?.AudioSampleRate is > 0 and var sr ? sr : null,
                Channels = props?.AudioChannels is > 0 and var ch ? ch : null,
                TaggedBpm = tag.BeatsPerMinute == 0 ? null : tag.BeatsPerMinute,
                TaggedKey = Clean(tag.InitialKey),
                CoverArt = FirstPicture(tag),
            };
        }
        catch
        {
            // Corrupt/unsupported tags must never stop playback — return the bare minimum.
            return new TrackMetadata { FallbackName = fallback };
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
}
