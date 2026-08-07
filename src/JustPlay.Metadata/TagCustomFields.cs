namespace JustPlay.Metadata;

/// <summary>
/// Reads/writes JustPlay's non-standard fields (ENERGY, JUSTPLAY blob) in whatever
/// tag system the file uses - ID3v2 <c>TXXX</c> frames (MP3) or Xiph comment fields
/// (FLAC/OGG). Formats with neither (e.g. some MP4s) degrade gracefully: the custom
/// field is simply not persisted and the track falls back to re-analysis.
/// </summary>
internal static class TagCustomFields
{
    public static string? Get(TagLib.File file, string key)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            return NullIfBlank(xiph.GetFirstField(key));

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
            return frame?.Text is { Length: > 0 } t ? NullIfBlank(t[0]) : null;
        }

        return null;
    }

    public static void Set(TagLib.File file, string key, string value)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField(key, value);
            return;
        }

        // Default to ID3v2 (covers MP3, including tagless - created on demand).
        if (file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true);
            frame.Text = [value];
        }
    }

    /// <summary>Remove a custom field entirely (used by Undo to restore a file that had none).</summary>
    public static void Remove(TagLib.File file, string key)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.RemoveField(key);
            return;
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
            if (frame is not null) id3.RemoveFrame(frame);
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
