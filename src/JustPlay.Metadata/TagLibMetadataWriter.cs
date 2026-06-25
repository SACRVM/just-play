using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Writes analysis values back via TagLib#. BPM → standard tempo tag (rounded; the
/// exact value is preserved in the JUSTPLAY blob), key → standard key tag as an ID3
/// key string (e.g. "Am"), energy + the JustPlay state blob → custom fields. Camelot
/// is intentionally NOT written (derived from the key on read) so the user's comment
/// is never clobbered.
/// </summary>
public sealed class TagLibMetadataWriter : IMetadataWriter
{
    private static readonly string[] PitchNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public void Write(string filePath, TagWrite write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        if (write.Bpm is { } bpm)
            tag.BeatsPerMinute = (uint)Math.Clamp(Math.Round(bpm), 0, 999);

        if (write.Key is { } key)
            tag.InitialKey = ToId3Key(key);

        if (write.Energy is { } energy)
            TagCustomFields.Set(file, "ENERGY", energy.ToString(CultureInfo.InvariantCulture));

        if (write.State is { } state)
            TagCustomFields.Set(file, "JUSTPLAY", AnalysisStateCodec.Serialize(state));

        if (write.Comment is { } comment)
        {
            var clean = comment.Length == 0 ? null : comment;

            // N21 CLEAN SLATE: collapse the multiple COMM frames legacy taggers left
            // (COMM::'' / COMM:'ID3v1 Comment' / blob frames in various languages) to
            // exactly ONE clean frame. Two traps the earlier attempt hit:
            //   1. the combined file.Tag.Comment setter ALSO writes the ID3v1 tag, which
            //      TagLib# then renders back as a second COMM frame (desc="ID3v1 Comment")
            //      — the source of the duplicate;
            //   2. removing frames one-by-one is fragile; RemoveFrames(ident) clears all.
            // So: wipe every COMM frame, write the comment DIRECTLY on the Id3v2 tag, and
            // clear the ID3v1 comment so nothing re-mirrors. Result: a single COMM frame.
            if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3ForComm)
            {
                id3ForComm.RemoveFrames("COMM");
                id3ForComm.Comment = clean;
                if (file.GetTag(TagLib.TagTypes.Id3v1, false) is TagLib.Id3v1.Tag id3v1)
                    id3v1.Comment = null;
            }
            else
            {
                // Non-ID3 container (e.g. FLAC/Xiph) — single comment field, no dup issue.
                tag.Comment = clean;
            }
        }

        if (write.Grouping is { } grouping)
            tag.Grouping = grouping.Length == 0 ? null : grouping;

        if (write.Favorite is { } favorite)
            SetPopm(file, favorite);

        if (write.ReplayGainDb is { } g)
            TagCustomFields.Set(file, "REPLAYGAIN_TRACK_GAIN",
                g.ToString("0.00", CultureInfo.InvariantCulture) + " dB");

        if (write.Peak is { } pk)
            TagCustomFields.Set(file, "REPLAYGAIN_TRACK_PEAK",
                pk.ToString("0.000000", CultureInfo.InvariantCulture));

        file.Save();
    }

    public void Restore(string filePath, TagRestore restore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        // null = the field was empty before the undone write → clear it (the inverse of Write,
        // which leaves null fields untouched).
        tag.BeatsPerMinute = restore.Bpm is { } bpm ? (uint)Math.Clamp(Math.Round(bpm), 0, 999) : 0;
        tag.InitialKey = restore.Key is { } key ? ToId3Key(key) : null;

        if (restore.Energy is { } energy)
            TagCustomFields.Set(file, "ENERGY", energy.ToString(CultureInfo.InvariantCulture));
        else
            TagCustomFields.Remove(file, "ENERGY");

        if (restore.State is { } state)
            TagCustomFields.Set(file, "JUSTPLAY", AnalysisStateCodec.Serialize(state));
        else
            TagCustomFields.Remove(file, "JUSTPLAY");

        if (restore.ReplayGainDb is { } rg)
            TagCustomFields.Set(file, "REPLAYGAIN_TRACK_GAIN",
                rg.ToString("0.00", CultureInfo.InvariantCulture) + " dB");
        else
            TagCustomFields.Remove(file, "REPLAYGAIN_TRACK_GAIN");

        if (restore.Peak is { } pk)
            TagCustomFields.Set(file, "REPLAYGAIN_TRACK_PEAK",
                pk.ToString("0.000000", CultureInfo.InvariantCulture));
        else
            TagCustomFields.Remove(file, "REPLAYGAIN_TRACK_PEAK");

        // Only touch the comment if it was explicitly captured before the write; null CommentCaptured
        // means the DJ comment feature was off — leave the user's comment alone.
        if (restore.CommentCaptured)
            tag.Comment = string.IsNullOrEmpty(restore.Comment) ? null : restore.Comment;

        file.Save();
    }

    /// <summary>MusicalKey → ID3v2 TKEY string ("A","C#","Am","F#m", …).</summary>
    private static string ToId3Key(MusicalKey key)
        => PitchNames[((key.PitchClass % 12) + 12) % 12] + (key.Mode == KeyMode.Minor ? "m" : "");

    /// <summary>
    /// Set or clear the favourite rating in the file's tag system.
    /// MP3 / ID3v2: POPM frame, rating 255 (liked) or frame removed (not liked).
    /// FLAC / Xiph: non-standard RATING field ("5" liked, removed when not liked).
    /// Other formats: silently ignored (no crash).
    /// </summary>
    private static void SetPopm(TagLib.File file, bool liked)
    {
        // ID3v2 path — POPM is the standard rating frame for MP3.
        if (file.GetTag(TagLib.TagTypes.Id3v2, liked) is TagLib.Id3v2.Tag id3)
        {
            if (liked)
            {
                // create:true — creates the frame if missing.
                var frame = TagLib.Id3v2.PopularimeterFrame.Get(id3, TagLibMetadataReader.PopmUser, true)!;
                frame.Rating = 255; // 255 = "loved" / 5-star in WMP convention.
            }
            else
            {
                // create:false — returns null when the frame doesn't exist.
                var frame = TagLib.Id3v2.PopularimeterFrame.Get(id3, TagLibMetadataReader.PopmUser, false);
                if (frame is not null) id3.RemoveFrame(frame);
            }
            return;
        }

        // Xiph fallback for FLAC/OGG.
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            if (liked)
                xiph.SetField("RATING", "5");
            else
                xiph.RemoveField("RATING");
        }
        // Other formats (MP4/AAC, WMA, …): no standard rating tag — silently do nothing.
    }
}
