namespace JustPlay.Metadata;

/// <summary>
/// Reads/writes JustPlay's non-standard fields (ENERGY, JUSTPLAY blob) in whatever
/// tag system the file uses - ID3v2 <c>TXXX</c> frames (MP3) or Xiph comment fields
/// (FLAC/OGG). Formats with neither (e.g. some MP4s) degrade gracefully: the custom
/// field is simply not persisted and the track falls back to re-analysis.
/// <para>
/// The key is matched CASE-INSENSITIVELY, in both containers. Xiph field names are
/// case-insensitive by specification (TagLib# upper-cases them), but a TXXX description is
/// free text, and other taggers spell the same field differently - foobar2000 writes
/// <c>replaygain_track_gain</c>, we write <c>REPLAYGAIN_TRACK_GAIN</c>. A case-sensitive lookup
/// missed theirs, so a write ADDED a second frame next to it (two gains, one of them stale) and
/// the write preview called an overwrite a fresh write.
/// </para>
/// </summary>
internal static class TagCustomFields
{
    public static string? Get(TagLib.File file, string key)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            return NullIfBlank(xiph.GetFirstField(key));

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            return Preferred(Frames(id3, key), key)?.Text is { Length: > 0 } t ? NullIfBlank(t[0]) : null;

        return null;
    }

    /// <summary>Set the field, leaving exactly ONE of it under <paramref name="key"/>'s own spelling.
    /// Every other case variant is removed; the one that stays is updated in place, so it keeps its
    /// position and text encoding, and a file without any gets a new frame.</summary>
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
            var frames = Frames(id3, key);
            var keep = Preferred(frames, key);
            foreach (var other in frames)
                if (!ReferenceEquals(other, keep)) id3.RemoveFrame(other);

            keep ??= TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true);
            keep.Description = key;
            keep.Text = [value];
        }
    }

    /// <summary>Remove a custom field entirely, in every spelling (used by Undo to restore a file
    /// that had none).</summary>
    public static void Remove(TagLib.File file, string key)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.RemoveField(key);
            return;
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            RemoveAll(id3, key);
    }

    /// <summary>Every TXXX frame whose description is <paramref name="key"/> in any casing.</summary>
    private static List<TagLib.Id3v2.UserTextInformationFrame> Frames(TagLib.Id3v2.Tag id3, string key) =>
        id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>("TXXX")
           .Where(f => string.Equals(f.Description, key, StringComparison.OrdinalIgnoreCase))
           .ToList();

    /// <summary>Our own spelling when it is there, otherwise the first variant.</summary>
    private static TagLib.Id3v2.UserTextInformationFrame? Preferred(
        List<TagLib.Id3v2.UserTextInformationFrame> frames, string key) =>
        frames.FirstOrDefault(f => string.Equals(f.Description, key, StringComparison.Ordinal))
        ?? frames.FirstOrDefault();

    private static void RemoveAll(TagLib.Id3v2.Tag id3, string key)
    {
        foreach (var frame in Frames(id3, key)) id3.RemoveFrame(frame);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
