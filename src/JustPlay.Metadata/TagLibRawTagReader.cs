using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// TagLib# implementation of <see cref="IRawTagReader"/>. Read-only: this class never calls
/// <c>TagLib.File.Save()</c> or any other persist path - the handle opened here is used purely to
/// walk the already-parsed frame/field objects. <see cref="RawTagReaderTests.Read_NeverModifiesFileOnDisk"/>
/// pins this with a before/after SHA-256 comparison, the same pattern
/// <c>TagLibWritePreviewTests.Preview_NeverModifiesFileOnDisk</c> uses for the write-preview path.
///
/// <para>
/// Handles ID3v2 in full (every frame, including multiple TXXX/GEOB/PRIV on one file) plus Xiph
/// comments (FLAC/OGG) and APEv2 items - both are flat key/value tag systems and cost little beyond
/// ID3v2's frame walk. ID3v1 and MP4/Apple atoms come back as an explicit, documented
/// <see cref="RawTagContainer.UnsupportedReason"/> rather than a silent empty table (ID3v1 has no
/// vendor extension mechanism at all; MP4 atom enumeration needs the freeform '----' box tree
/// resolved and is out of scope for the MP3-only corpus this reader ships against tonight).
/// </para>
///
/// <para>
/// Reused rather than reimplemented: <see cref="Id3VersionProbe.Name"/> for the ID3v2 container
/// label (the same string the library index's ID3 column shows), <see cref="TagLibMetadataReader.PopmUser"/>
/// for recognising our own POPM rating, and the exact custom-field key literals
/// <see cref="TagCustomFields"/> writes ("ENERGY", "JUSTPLAY") for recognising our own TXXX/Xiph
/// fields - see the "Vendor recognition" section below.
/// </para>
/// </summary>
public sealed class TagLibRawTagReader : IRawTagReader
{
    public RawTagReadResult Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            // Read-only use of TagLib#: this handle is never Save()d.
            using var file = TagLib.File.Create(filePath);

            // file.TagTypesOnDisk, NOT file.TagTypes or "GetTag(..) != null": TagLib# eagerly
            // materialises an empty in-memory ID3v1 tag on every MP3 open (see
            // TagContractComplianceTests.AssertNoId3v1Bytes's comment on this exact gotcha), so any
            // other presence check reports a phantom ID3v1 container on every clean file.
            var present = file.TagTypesOnDisk;
            var containers = new List<RawTagContainer>();

            if (present.HasFlag(TagLib.TagTypes.Id3v2) &&
                file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
                containers.Add(ReadId3v2(id3));

            if (present.HasFlag(TagLib.TagTypes.Xiph) &&
                file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
                containers.Add(ReadXiph(xiph));

            if (present.HasFlag(TagLib.TagTypes.Ape) &&
                file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
                containers.Add(ReadApe(ape));

            if (present.HasFlag(TagLib.TagTypes.Id3v1))
                containers.Add(Unsupported(RawTagContainerKind.Id3v1, "ID3v1",
                    "ID3v1 is a fixed 30-byte text tag (title/artist/album/year/comment/genre) with " +
                    "no vendor extension mechanism - those fields are already surfaced by " +
                    "IMetadataReader, and no DJ tool has ever put cue/key/energy data in it."));

            if (present.HasFlag(TagLib.TagTypes.Apple))
                containers.Add(Unsupported(RawTagContainerKind.Mp4, "MP4 atoms",
                    "MP4/M4A atom enumeration is not implemented yet - TagLib# exposes named " +
                    "properties for known atoms only, not a generic walk of the box tree (a freeform " +
                    "'----' atom needs its mean/name sub-boxes resolved to get a readable name). " +
                    "Out of scope for the MP3/ID3v2 corpus this reader was built and measured " +
                    "against; see IRawTagReader's remarks for the documented gap."));

            return new RawTagReadResult { FilePath = filePath, Containers = containers };
        }
        catch (Exception ex)
        {
            // A file this reader cannot open or parse must come back as a failed result, never an
            // exception - the same "never crash" rule every other reader in this project follows.
            return new RawTagReadResult
            {
                FilePath = filePath,
                Containers = [],
                FailureReason = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // -------------------------------------------------------------------------
    // Container builders
    // -------------------------------------------------------------------------

    private static RawTagContainer ReadId3v2(TagLib.Id3v2.Tag id3)
    {
        var entries = id3.GetFrames().Select(FromId3Frame).ToList();
        return new RawTagContainer
        {
            Kind = RawTagContainerKind.Id3v2,
            // Reused, not re-derived: the same label logic the library index's ID3 column shows,
            // so a raw-tags window and the track table can never disagree about one file's version.
            Label = Id3VersionProbe.Name(id3.Version),
            Entries = entries,
        };
    }

    private static RawTagContainer ReadXiph(TagLib.Ogg.XiphComment xiph)
    {
        var entries = new List<RawTagEntry>();
        foreach (var key in xiph)
        {
            var joined = string.Join(" / ", xiph.GetField(key));
            entries.Add(new RawTagEntry
            {
                Id = key,
                Descriptor = null, // Xiph has no separate descriptor axis - the key IS the handle.
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(joined),
                Summary = Truncate(joined),
                Vendor = RecognizeTextKey(key),
            });
        }

        return new RawTagContainer
        {
            Kind = RawTagContainerKind.Xiph,
            Label = "Xiph (Vorbis comments)",
            Entries = entries,
        };
    }

    private static RawTagContainer ReadApe(TagLib.Ape.Tag ape)
    {
        var entries = new List<RawTagEntry>();
        foreach (var key in ape)
        {
            if (ape.GetItem(key) is not { } item) continue;

            var isText = item.Type == TagLib.Ape.ItemType.Text;
            entries.Add(new RawTagEntry
            {
                Id = key,
                Descriptor = null, // APE has no separate descriptor axis either.
                SizeBytes = item.Size,
                Summary = isText ? Truncate(item.ToString()) : BinarySummary(item.Size, mimeType: null),
                Vendor = RecognizeTextKey(key),
            });
        }

        return new RawTagContainer
        {
            Kind = RawTagContainerKind.Ape,
            Label = "APEv2",
            Entries = entries,
        };
    }

    private static RawTagContainer Unsupported(RawTagContainerKind kind, string label, string reason) =>
        new() { Kind = kind, Label = label, Entries = [], UnsupportedReason = reason };

    // -------------------------------------------------------------------------
    // ID3v2 frame -> entry. One branch per frame TYPE (not per frame ID string) so multiple
    // TXXX/GEOB/PRIV frames on one file - explicitly required - fall out naturally: GetFrames()
    // already returns every frame, duplicates included, in file order.
    // -------------------------------------------------------------------------

    private static RawTagEntry FromId3Frame(TagLib.Id3v2.Frame frame)
    {
        var id = frame.FrameId.ToString();
        var size = (int)frame.Size; // size as last stored on disk - no re-render, no guessing

        // GEOB and APIC/PIC share one runtime type in TagLib# 2.3.0 (AttachmentFrame); GEOB is
        // told apart from a real picture by its frame id, not by a different C# type. This is the
        // exact type CoverProbe.cs already filters on via PictureType.NotAPicture.
        if (frame is TagLib.Id3v2.AttachmentFrame att)
        {
            var isGeob = id == "GEOB";
            var isRealPicture = !isGeob && att.Type != TagLib.PictureType.NotAPicture;
            var body = BinarySummary(size, att.MimeType);
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(att.Description),
                SizeBytes = size,
                Summary = isRealPicture ? $"image, {body}" : body,
                Vendor = isGeob ? RecognizeGeob(att.Description) : RawTagVendor.Unknown,
            };
        }

        if (frame is TagLib.Id3v2.PrivateFrame priv)
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(priv.Owner),
                SizeBytes = size,
                Summary = BinarySummary(size, mimeType: null),
                Vendor = RecognizePrivOwner(priv.Owner),
            };

        if (frame is TagLib.Id3v2.UniqueFileIdentifierFrame ufid)
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(ufid.Owner),
                SizeBytes = size,
                Summary = BinarySummary(size, mimeType: null),
            };

        if (frame is TagLib.Id3v2.PopularimeterFrame popm)
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(popm.User),
                SizeBytes = size,
                Summary = $"rating {popm.Rating}/255, played {popm.PlayCount}x",
                Vendor = RecognizePopmUser(popm.User),
            };

        if (frame is TagLib.Id3v2.CommentsFrame comm)
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(comm.Description),
                SizeBytes = size,
                Summary = Truncate(comm.Text),
            };

        // UserTextInformationFrame (TXXX) extends TextInformationFrame - the more specific check
        // must run first, or every TXXX would be reported as a plain text frame with no descriptor.
        if (frame is TagLib.Id3v2.UserTextInformationFrame txxx)
            return new RawTagEntry
            {
                Id = id,
                Descriptor = NullIfEmpty(txxx.Description),
                SizeBytes = size,
                Summary = Truncate(string.Join(" / ", txxx.Text)),
                Vendor = RecognizeTextKey(txxx.Description),
            };

        if (frame is TagLib.Id3v2.TextInformationFrame text)
            return new RawTagEntry
            {
                Id = id,
                SizeBytes = size,
                Summary = Truncate(string.Join(" / ", text.Text)),
            };

        // Anything else (ETCO, CHAP, RVA2, an unrecognised/custom frame, ...) - report its size
        // honestly rather than guessing at a text decode that might not apply.
        return new RawTagEntry { Id = id, SizeBytes = size, Summary = BinarySummary(size, mimeType: null) };
    }

    // -------------------------------------------------------------------------
    // Vendor recognition - a small, explicit, documented table. Every entry here is backed by a
    // specific measurement or spec citation, never a guess:
    //   - .claude/night-reports/2026-07-31-L3-taglib-bytes.md - measured on the 8-file real-library
    //     ID3 corpus (full per-file frame inventory in the linked skill doc).
    //   - ~/.claude/skills/dj-metadata-interop/references/serato-in-file-geob.md,
    //     mik-beatport-coexistence.md, db-centric-apps.md, write-safety-measured.md.
    // rekordbox is deliberately absent: confirmed (db-centric-apps.md Sec.2) to leave no
    // distinguishing frame in the file at all - see RawTagVendor.Rekordbox's remarks.
    // -------------------------------------------------------------------------

    private static RawTagVendor RecognizeGeob(string? description)
    {
        if (string.IsNullOrEmpty(description)) return RawTagVendor.Unknown;

        // "Serato Analysis" / "Serato Autotags" / "Serato Markers_" / "Serato Markers2" /
        // "Serato BeatGrid" / "Serato Overview" / "Serato Offsets_" - measured on 6 of 8 corpus files.
        if (description.StartsWith("Serato ", StringComparison.Ordinal)) return RawTagVendor.Serato;

        // Mixed In Key's GEOB set - measured on the corpus (write-safety-measured.md: "Mixed In Key |
        // Key / Energy / CuePoints"). MIK 11 no longer writes these, but plenty of already-tagged
        // files still carry them from earlier versions / pool pipelines.
        if (description is "Key" or "Energy" or "CuePoints") return RawTagVendor.MixedInKey;

        return RawTagVendor.Unknown;
    }

    private static RawTagVendor RecognizePrivOwner(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return RawTagVendor.Unknown;

        // Measured on F_mik_armstrong / G_priv_crownless: owner "TRAKTOR4".
        return owner.StartsWith("TRAKTOR", StringComparison.Ordinal) ? RawTagVendor.Traktor : RawTagVendor.Unknown;
    }

    private static RawTagVendor RecognizePopmUser(string? user)
    {
        if (string.IsNullOrEmpty(user)) return RawTagVendor.Unknown;

        // Reused constant, not a duplicated literal: the exact user string TagLibMetadataReader's
        // own like-button rating reads/writes.
        if (string.Equals(user, TagLibMetadataReader.PopmUser, StringComparison.Ordinal))
            return RawTagVendor.JustPlay;

        // db-centric-apps.md Sec.1: "traktor@native-instruments.de|xxx|0" - the email is the User
        // field, "xxx" the rating byte and "0" the play count (the pipe-joined form is the doc's own
        // shorthand for the three POPM fields, not the literal User string).
        if (string.Equals(user, "traktor@native-instruments.de", StringComparison.OrdinalIgnoreCase))
            return RawTagVendor.Traktor;

        return RawTagVendor.Unknown;
    }

    /// <summary>
    /// Shared by ID3v2 TXXX descriptors, Xiph field keys and APE item keys. One table covers all
    /// three because <see cref="TagCustomFields"/> writes the exact same literals ("ENERGY",
    /// "JUSTPLAY") regardless of which tag system a file uses - see its Get/Set.
    /// </summary>
    private static RawTagVendor RecognizeTextKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return RawTagVendor.Unknown;
        if (key is "JUSTPLAY" or "ENERGY") return RawTagVendor.JustPlay;

        // "EnergyLevel" is the commonly observed casing; some tooling reports "ENERGYLEVEL"
        // (mik-beatport-coexistence.md Sec.1) - compare case-insensitively for that reason alone.
        if (string.Equals(key, "EnergyLevel", StringComparison.OrdinalIgnoreCase)) return RawTagVendor.MixedInKey;

        // TXXX:SERATO_PLAYCOUNT (measured on the corpus) and the SERATO_* Xiph field family
        // (serato-in-file-geob.md Sec.1: SERATO_OVERVIEW / SERATO_ANALYSIS / SERATO_MARKERS_V2 / ...).
        if (key.StartsWith("SERATO_", StringComparison.Ordinal)) return RawTagVendor.Serato;

        return RawTagVendor.Unknown;
    }

    // -------------------------------------------------------------------------
    // Formatting helpers
    // -------------------------------------------------------------------------

    private const int MaxSummaryChars = 300;

    /// <summary>Binary entries show a size (and mime type, if it says something beyond the generic
    /// Serato/GEOB default) - never their raw bytes.</summary>
    private static string BinarySummary(int sizeBytes, string? mimeType)
    {
        // Invariant culture, not the machine's current culture: a size like "226,578 bytes" must
        // read the same regardless of which DJ's PC this runs on (same reasoning as every other
        // ToString(CultureInfo.InvariantCulture) call in this project).
        var size = sizeBytes.ToString("N0", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(mimeType) || string.Equals(mimeType, "application/octet-stream", StringComparison.Ordinal)
            ? $"{size} bytes"
            : $"{size} bytes ({mimeType})";
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxSummaryChars ? flat : string.Concat(flat.AsSpan(0, MaxSummaryChars), "...");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
