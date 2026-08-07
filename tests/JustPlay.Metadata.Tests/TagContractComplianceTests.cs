using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Regression guard for the library tag contract (NAS CLAUDE.md Sec.4) across all three
/// container types the library holds. Empirically found violated by the 2026-07-16
/// ingest of 4,166 files (repaired by library-import/fix_contract.py); these tests pin
/// the writer so the bugs cannot come back:
///
///   1. TKEY (MP3/AIFF) / initialkey+key+tkey (FLAC) hold the CAMELOT code ("6A"),
///      never the musical name ("Gm").
///   2. The comment is rebuilt from the SAME detected key/energy that TKEY and
///      TXXX:ENERGY carry - one source, no floor-vs-round or okpc-vs-kpc drift.
///   3. MP3: EXACTLY ONE COMM frame (lang="eng", desc="") and NO ID3v1 shadow tag
///      (which readers render as a second "ID3v1 Comment" frame).
///   4. FLAC: bpm + comment are materialized as Xiph fields (TagLib#'s defaults write
///      TEMPO/DESCRIPTION, which the contract - and DJ tools - do not read).
///
/// Fixtures are synthesised in-memory (see EditableTagsRoundTripTests) - no binary
/// blobs in the repo.
/// </summary>
public sealed class TagContractComplianceTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly TagLibMetadataWriter _writer = new();

    // G minor: pitch class 7, minor -> Camelot "6A" (the report's own example: the old
    // writer stamped "Gm"). Energy 8 / BPM 128 round-trip as plain integers.
    private static readonly MusicalKey Key = new(7, KeyMode.Minor);
    private const int Energy = 8;
    private const double Bpm = 128.0;
    private const string ExpectedComment = "6A - Energy 8";

    private TagWrite ContractWrite() => new()
    {
        Bpm     = Bpm,
        Key     = Key,
        Energy  = Energy,
        // The comment is built from the SAME values the standard tags get - mirrors
        // PromoteCommand's contract write.
        Comment = DjCommentBuilder.Build(Key, Energy, existingComment: null),
    };

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists)) File.Delete(f);
    }

    // -- MP3 -------------------------------------------------------------------

    [Fact]
    public void Mp3_Write_StampsCamelotAndSingleEngComm_AndStripsId3v1()
    {
        var path = CreateMp3();

        // Seed the legacy mess the ingest found: an ID3v1 shadow tag and an old-style
        // comment (which TagLib mirrors into v1) plus a musical TKEY.
        using (var seed = TagLib.File.Create(path))
        {
            if (seed.GetTag(TagLib.TagTypes.Id3v1, true) is TagLib.Id3v1.Tag v1)
                seed.Tag.Comment = "legacy comment";
            seed.Tag.InitialKey = "Gm";
            seed.Save();
        }

        _writer.Write(path, ContractWrite());

        using var file = TagLib.File.Create(path);
        var id3 = Assert.IsType<TagLib.Id3v2.Tag>(file.GetTag(TagLib.TagTypes.Id3v2, false));

        Assert.Equal("6A", id3.InitialKey);                       // 1. Camelot, not "Gm"
        Assert.Equal(128u, id3.BeatsPerMinute);
        Assert.Equal("8", GetTxxx(id3, "ENERGY"));

        var comms = id3.GetFrames<TagLib.Id3v2.CommentsFrame>().ToList();
        var comm = Assert.Single(comms);                          // 3. exactly ONE COMM
        Assert.Equal("eng", comm.Language);                       //    lang eng, not "ivl"
        Assert.Equal("", comm.Description);                       //    desc empty, no "ID3v1 Comment"
        Assert.Equal(ExpectedComment, comm.Text);                 // 2. same key/energy as TKEY/ENERGY

        // 3. the v1 shadow tag is gone from the FILE BYTES - the check mutagen-based
        // verification uses. (TagLib# eagerly materialises an empty in-memory Id3v1
        // object on every MP3 open, so GetTag(Id3v1) is non-null even for clean files.)
        AssertNoId3v1Bytes(path);
    }

    [Fact]
    public void Mp3_Rewrite_StaysSingleComm()
    {
        var path = CreateMp3();

        _writer.Write(path, ContractWrite());
        _writer.Write(path, ContractWrite()); // idempotence - a re-promote must not double frames

        using var file = TagLib.File.Create(path);
        var id3 = Assert.IsType<TagLib.Id3v2.Tag>(file.GetTag(TagLib.TagTypes.Id3v2, false));
        var comm = Assert.Single(id3.GetFrames<TagLib.Id3v2.CommentsFrame>().ToList());
        Assert.Equal(ExpectedComment, comm.Text);
        AssertNoId3v1Bytes(path);
    }

    // -- AIFF (ID3v2 chunk inside the container) -------------------------------

    [Fact]
    public void Aiff_Write_StampsCamelotAndSingleEngComm()
    {
        var path = CreateAiff();

        _writer.Write(path, ContractWrite());

        using var file = TagLib.File.Create(path);
        var id3 = Assert.IsType<TagLib.Id3v2.Tag>(file.GetTag(TagLib.TagTypes.Id3v2, false));

        Assert.Equal("6A", id3.InitialKey);
        Assert.Equal(128u, id3.BeatsPerMinute);
        Assert.Equal("8", GetTxxx(id3, "ENERGY"));

        var comm = Assert.Single(id3.GetFrames<TagLib.Id3v2.CommentsFrame>().ToList());
        Assert.Equal("eng", comm.Language);
        Assert.Equal("", comm.Description);
        Assert.Equal(ExpectedComment, comm.Text);

        // The FORM container size must span the appended "ID3 " chunk - TagLib# 2.3.0
        // leaves it stale, which makes the whole tag invisible to strict parsers
        // (mutagen - the contract's verification tool). See RepairAiffFormSize.
        var bytes = File.ReadAllBytes(path);
        var declared = (uint)((bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7]);
        Assert.Equal((uint)(bytes.Length - 8), declared);
    }

    // -- FLAC (Xiph) -----------------------------------------------------------

    [Fact]
    public void Flac_Write_MaterializesFullContractFieldSet()
    {
        var path = CreateFlac();

        _writer.Write(path, ContractWrite());

        using var file = TagLib.File.Create(path);
        var xiph = Assert.IsType<TagLib.Ogg.XiphComment>(file.GetTag(TagLib.TagTypes.Xiph, false));

        // 1./4. the Camelot triple + bpm + energy + comment, all as the fields the
        // contract (and mutagen verification) reads - not TEMPO/DESCRIPTION.
        Assert.Equal("6A", xiph.GetFirstField("INITIALKEY"));
        Assert.Equal("6A", xiph.GetFirstField("KEY"));
        Assert.Equal("6A", xiph.GetFirstField("TKEY"));
        Assert.Equal("128", xiph.GetFirstField("BPM"));
        Assert.Equal("8", xiph.GetFirstField("ENERGY"));
        Assert.Equal(ExpectedComment, xiph.GetFirstField("COMMENT"));
        Assert.True((xiph.GetField("DESCRIPTION")?.Length ?? 0) == 0,
            "DESCRIPTION must not carry a second comment copy");
    }

    // -- fixture builders ------------------------------------------------------

    private string CreateMp3()
    {
        // Bare ID3v2.3 header + one MPEG1 Layer3 sync header - the same minimal shape
        // EditableTagsRoundTripTests uses; TagLib parses it as a taggable MP3.
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3;                                     // v2.3
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        return WriteTemp(".mp3", bare);
    }

    private string CreateAiff()
    {
        // Minimal AIFF: FORM/AIFF + COMM (2ch, 16 bit, 44100 Hz as 80-bit float) + empty SSND.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Be32(uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        void Be16(ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }

        w.Write("FORM"u8.ToArray()); Be32(4 + 8 + 18 + 8 + 8); w.Write("AIFF"u8.ToArray());
        w.Write("COMM"u8.ToArray()); Be32(18);
        Be16(2);            // channels
        Be32(0);            // frame count
        Be16(16);           // bits per sample
        // 44100 Hz as IEEE 754 80-bit extended: exponent 0x400E, mantissa 0xAC44<<48.
        Be16(0x400E); Be32(0xAC440000); Be32(0);
        w.Write("SSND"u8.ToArray()); Be32(8); Be32(0); Be32(0);
        return WriteTemp(".aiff", ms.ToArray());
    }

    private string CreateFlac()
    {
        // Minimal FLAC: "fLaC" + a last-block STREAMINFO (34 bytes) - enough for TagLib
        // to open the file and add a VORBIS_COMMENT block on save.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("fLaC"u8.ToArray());
        w.Write((byte)0x80);                       // last-block flag | type 0 (STREAMINFO)
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)34);
        var si = new byte[34];
        si[0] = 0x10; si[1] = 0x00;                // min block size 4096
        si[2] = 0x10; si[3] = 0x00;                // max block size 4096
        // sample rate 44100 (20 bit), channels-1 = 1 (3 bit), bps-1 = 15 (5 bit):
        // 44100 = 0x0AC44 -> bytes 10..12: 0x0A 0xC4 0x4_, then ((1)<<1)|(15>>4) ...
        si[10] = 0x0A; si[11] = 0xC4; si[12] = 0x42; si[13] = 0xF0;
        w.Write(si);
        return WriteTemp(".flac", ms.ToArray());
    }

    private string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_contract_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    // -- assertion helpers -----------------------------------------------------

    private static string? GetTxxx(TagLib.Id3v2.Tag id3, string desc)
        => TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, false)?.Text is { Length: > 0 } t
            ? t[0] : null;

    private static void AssertNoId3v1Bytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasV1 = bytes.Length >= 128
                    && bytes[^128] == 'T' && bytes[^127] == 'A' && bytes[^126] == 'G';
        Assert.False(hasV1, "file must not end in an ID3v1 shadow tag");
    }
}
