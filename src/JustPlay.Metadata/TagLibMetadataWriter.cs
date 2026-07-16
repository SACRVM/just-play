using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Writes analysis values back via TagLib#, in the shape of the library tag contract
/// (NAS CLAUDE.md §4): BPM → standard tempo tag (rounded; the exact value is preserved
/// in the JUSTPLAY blob), key → standard key tag as the CAMELOT code ("6A", the
/// DJ-readable form Traktor/rekordbox sort by — <see cref="MusicalKey"/> parses it
/// back on read), energy + the JustPlay state blob → custom fields. FLAC additionally
/// materialises <c>key</c>/<c>tkey</c>/<c>bpm</c> Xiph fields, MP3 loses any legacy
/// ID3v1 shadow tag, and the comment is exactly ONE <c>eng</c> COMM frame.
/// </summary>
public sealed class TagLibMetadataWriter : IMetadataWriter
{
    public void Write(string filePath, TagWrite write)
    {
        WriteCore(filePath, write);
        RepairAiffFormSize(filePath);
    }

    private static void WriteCore(string filePath, TagWrite write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        // Contract §4: no ID3v1 shadow tag, ever. TagLib# happily keeps updating an
        // existing v1 tag on save, which readers then surface as a second COMM
        // ("ID3v1 Comment") with truncated 30-char values — strip it up front
        // (the TagLib# equivalent of mutagen's save(v1=0)). No-op for AIFF/FLAC.
        file.RemoveTags(TagLib.TagTypes.Id3v1);

        if (write.Bpm is { } bpm)
            SetBpm(file, tag, bpm);

        if (write.Key is { } key)
            SetContractKey(file, tag, key);

        if (write.Energy is { } energy)
            TagCustomFields.Set(file, "ENERGY", energy.ToString(CultureInfo.InvariantCulture));

        if (write.State is { } state)
            TagCustomFields.Set(file, "JUSTPLAY", AnalysisStateCodec.Serialize(state));

        if (write.Comment is { } comment)
            ApplyCleanComment(file, comment.Length == 0 ? null : comment);

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
        RestoreCore(filePath, restore);
        RepairAiffFormSize(filePath);
    }

    private static void RestoreCore(string filePath, TagRestore restore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        // null = the field was empty before the undone write → clear it (the inverse of Write,
        // which leaves null fields untouched).
        if (restore.Bpm is { } bpm)
        {
            SetBpm(file, tag, bpm);
        }
        else
        {
            tag.BeatsPerMinute = 0;
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment x)
                x.RemoveField("BPM");
        }

        if (restore.Key is { } key)
        {
            SetContractKey(file, tag, key);
        }
        else
        {
            tag.InitialKey = null;
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment x)
            {
                x.RemoveField("KEY");
                x.RemoveField("TKEY");
            }
        }

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

    public void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
        byte[]? newCover, string? coverMimeType)
    {
        WriteEditableCore(filePath, tags, coverAction, newCover, coverMimeType);
        RepairAiffFormSize(filePath);
    }

    private static void WriteEditableCore(string filePath, EditableTags tags, CoverAction coverAction,
        byte[]? newCover, string? coverMimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        // Contract §4: no ID3v1 shadow tag (see Write).
        file.RemoveTags(TagLib.TagTypes.Id3v1);

        // Title / Artist / Album / AlbumArtist / Genre — null/empty clears.
        tag.Title       = string.IsNullOrEmpty(tags.Title)       ? null : tags.Title;
        tag.Performers  = string.IsNullOrWhiteSpace(tags.Artist) ? [] : [tags.Artist];
        tag.Album       = string.IsNullOrEmpty(tags.Album)       ? null : tags.Album;
        tag.AlbumArtists = string.IsNullOrWhiteSpace(tags.AlbumArtist) ? [] : [tags.AlbumArtist];
        tag.Genres      = string.IsNullOrWhiteSpace(tags.Genre)  ? [] : [tags.Genre];

        // Year / TrackNumber — 0 clears (TagLib convention).
        tag.Year  = tags.Year;
        tag.Track = tags.TrackNumber;

        // Comment — reuse the single-COMM-frame helper (same de-dup logic as Write).
        ApplyCleanComment(file, tags.Comment);

        // Cover art. NEVER assign a fresh array without carrying the NotAPicture
        // attachments over: Serato / Mixed In Key store cues, beatgrids and energy as
        // GEOB frames that TagLib# surfaces in Pictures[] with Type=NotAPicture —
        // a blind clear-all would silently destroy a DJ's performance data (the golden
        // interop rule: never clear-all-rewrite foreign frames).
        switch (coverAction)
        {
            case CoverAction.Remove:
                tag.Pictures = KeepNonPictureAttachments(tag.Pictures);
                break;

            case CoverAction.Replace when newCover is { Length: > 0 }:
                tag.Pictures =
                [
                    .. KeepNonPictureAttachments(tag.Pictures),
                    new TagLib.Picture(new TagLib.ByteVector(newCover))
                    {
                        Type        = TagLib.PictureType.FrontCover,
                        MimeType    = coverMimeType ?? "image/jpeg",
                        Description = string.Empty,
                    },
                ];
                break;

            // CoverAction.Keep — do nothing; tag.Pictures is left exactly as TagLib# read it.
        }

        file.Save();
    }

    /// <summary>
    /// Set the global ID3v2 write version + encoding (mp3tag's three modes). <c>ForceDefault*</c> is the
    /// key bit: without it TagLib# PRESERVES an existing tag's version/encoding on save, so a file already
    /// in v2.4/UTF-8 would stay that way — we force-normalise instead (research dossier §"version" + §"encoding").
    /// TagLib#'s own defaults are v2.3 / <see cref="TagLib.StringType.UTF8"/>, so encoding always needs forcing.
    /// </summary>
    public void ConfigureId3WriteFormat(Id3WriteFormat format)
    {
        switch (format)
        {
            case Id3WriteFormat.Id3v23Latin1:
                TagLib.Id3v2.Tag.DefaultVersion = 3;
                TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.Latin1;
                break;
            case Id3WriteFormat.Id3v24Utf8:
                TagLib.Id3v2.Tag.DefaultVersion = 4;
                TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.UTF8;
                break;
            default: // Id3v23Utf16 — the safe, mp3tag-default, max-compatibility mode
                TagLib.Id3v2.Tag.DefaultVersion = 3;
                TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.UTF16;
                break;
        }
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        TagLib.Id3v2.Tag.ForceDefaultEncoding = true;
    }

    /// <summary>
    /// TagLib# 2.3.0 leaves the top-level FORM chunk size STALE when it saves the trailing
    /// <c>ID3 </c> chunk of an AIFF (verified: append AND in-place update). TagLib itself
    /// re-finds the chunk by scanning, but strict RIFF/IFF parsers — mutagen, i.e. the
    /// library's contract verification — stop at the declared FORM end and see NO tag at
    /// all. A well-formed single-FORM AIFF always has FORM size == file length - 8, so
    /// patch the 4 size bytes after every save when they disagree. No-op for non-AIFF.
    /// </summary>
    private static void RepairAiffFormSize(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
        if (fs.Length < 12) return;
        Span<byte> head = stackalloc byte[12];
        fs.ReadExactly(head);
        if (!head[..4].SequenceEqual("FORM"u8) || !head[8..12].SequenceEqual("AIFF"u8)) return;

        var actual = (uint)(fs.Length - 8);
        var declared = (uint)((head[4] << 24) | (head[5] << 16) | (head[6] << 8) | head[7]);
        if (declared == actual) return;

        fs.Position = 4;
        fs.Write([(byte)(actual >> 24), (byte)(actual >> 16), (byte)(actual >> 8), (byte)actual]);
    }

    /// <summary>
    /// Contract key write: the CAMELOT code ("6A"), never the musical name ("Gm") — Traktor
    /// and the whole library sort by Camelot (NAS CLAUDE.md §4; the musical form was the old
    /// behaviour and is still accepted on READ via <see cref="MusicalKey.TryParse"/>).
    /// ID3 (MP3/AIFF): TKEY via InitialKey. Xiph (FLAC): the contract triple
    /// <c>initialkey</c> + <c>key</c> + <c>tkey</c>, all Camelot.
    /// </summary>
    private static void SetContractKey(TagLib.File file, TagLib.Tag tag, MusicalKey key)
    {
        var cam = key.Camelot;
        tag.InitialKey = cam; // ID3 TKEY / Xiph INITIALKEY
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("KEY", cam);
            xiph.SetField("TKEY", cam);
        }
    }

    /// <summary>
    /// Contract BPM write: standard tempo tag everywhere, PLUS an explicit Xiph <c>BPM</c>
    /// field on FLAC — TagLib# maps <see cref="TagLib.Tag.BeatsPerMinute"/> to <c>TEMPO</c>
    /// there, which DJ tools (and the contract) don't read.
    /// </summary>
    private static void SetBpm(TagLib.File file, TagLib.Tag tag, double bpm)
    {
        var rounded = (uint)Math.Clamp(Math.Round(bpm), 0, 999);
        tag.BeatsPerMinute = rounded;
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            xiph.SetField("BPM", rounded.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Write a comment to the file using the single-COMM-frame strategy (ID3v2) or the
    /// plain comment field (Xiph/other). Shared by <see cref="Write"/> and
    /// <see cref="WriteEditable"/> so the de-dup logic lives in exactly one place.
    /// </summary>
    /// <remarks>
    /// N21 CLEAN SLATE + §4 contract: collapse the multiple COMM frames legacy taggers
    /// left (COMM::'' / COMM:'ID3v1 Comment' / blob frames in various languages) to
    /// exactly ONE clean frame with <b>lang="eng", desc=""</b>. Three traps:
    ///   1. the combined file.Tag.Comment setter ALSO writes the ID3v1 tag, which
    ///      TagLib# then renders back as a second COMM frame (desc="ID3v1 Comment")
    ///      — Write/WriteEditable therefore strip ID3v1 entirely before saving;
    ///   2. removing frames one-by-one is fragile; RemoveFrames(ident) clears all;
    ///   3. the id3.Comment SETTER stamps TagLib#'s default language onto the frame —
    ///      readers saw the invalid code "ivl" (the NAS-documented bug). Contract says
    ///      "eng", so the frame is constructed explicitly.
    /// FLAC/Xiph: the contract reads the <c>COMMENT</c> field; TagLib#'s Comment property
    /// prefers <c>DESCRIPTION</c>, so both are set/cleared explicitly and kept in sync.
    /// </remarks>
    private static void ApplyCleanComment(TagLib.File file, string? comment)
    {
        var clean = string.IsNullOrEmpty(comment) ? null : comment;

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            id3.RemoveFrames("COMM");
            if (clean is not null)
                id3.AddFrame(new TagLib.Id3v2.CommentsFrame(description: "", language: "eng")
                {
                    Text = clean,
                });
        }
        else if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            if (clean is null)
            {
                xiph.RemoveField("COMMENT");
                xiph.RemoveField("DESCRIPTION");
            }
            else
            {
                xiph.SetField("COMMENT", clean);
                xiph.RemoveField("DESCRIPTION"); // one canonical comment field, not two
            }
        }
        else
        {
            // Other containers (MP4 etc.) — single comment field, no dup issue.
            file.Tag.Comment = clean;
        }
    }

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

    /// <summary>
    /// The Pictures[] entries that are NOT pictures: GEOB attachments (Serato Markers,
    /// Mixed In Key Key/Energy, beatgrids, cue points) that must survive every cover
    /// operation. See the cover-art switch in <see cref="WriteEditable"/>.
    /// </summary>
    private static TagLib.IPicture[] KeepNonPictureAttachments(TagLib.IPicture[]? pics)
        => pics is null
            ? []
            : Array.FindAll(pics, static p => p.Type == TagLib.PictureType.NotAPicture);
}
