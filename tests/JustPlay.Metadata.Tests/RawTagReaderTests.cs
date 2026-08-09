using System.Security.Cryptography;
using JustPlay.Core.Models;
using Xunit.Abstractions;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Tests for <see cref="TagLibRawTagReader"/> against both synthetic fixtures (built in-process,
/// no binary blobs in the repo) and Chloe's real ID3 corpus at <see cref="CorpusDir"/> - 8 real MP3s
/// carrying real Serato/Traktor/Mixed In Key frames, measured once already in
/// <c>.claude/night-reports/2026-07-31-L3-taglib-bytes.md</c>. The corpus is gitignored
/// (<c>testdata/</c>) and outside this repo; tests that need it skip gracefully when it is absent,
/// the same pattern <c>JustPlay.Analysis.Tests.BeatEnergyProbeTests</c> uses for its own external
/// fixtures. Nothing here writes to the corpus - see the read-only tests below, which hash every
/// corpus file before and after a read and assert byte-identity, mirroring how the L3 measurement
/// itself proved the write path safe.
/// </summary>
public sealed class RawTagReaderTests : IDisposable
{
    private const string CorpusDir = @"D:\repos\just-edit\testdata\id3-corpus";

    private static readonly string[] CorpusFiles =
    [
        "A_serato_full_anaritzz.mp3",
        "B_serato_full_adanatwins.mp3",
        "C_serato_full_farhigh.mp3",
        "D_popm_cortes.mp3",
        "E_popm_alanoak.mp3",
        "F_mik_armstrong.mp3",
        "G_priv_crownless.mp3",
        "H_serato_common_malaa.mp3",
    ];

    private readonly ITestOutputHelper _out;
    private readonly TagLibRawTagReader _reader = new();
    private readonly List<string> _tempFiles = [];

    public RawTagReaderTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists)) File.Delete(f);
    }

    private static string Corpus(string fileName) => Path.Combine(CorpusDir, fileName);

    private bool SkipIfCorpusMissing()
    {
        if (Directory.Exists(CorpusDir)) return false;
        _out.WriteLine($"SKIP (corpus not found): {CorpusDir}");
        return true;
    }

    // ===========================================================================================
    // Read-only guarantee - the hard constraint. Hashes real corpus files AND a synthetic fixture
    // carrying every frame type this reader recognises, before and after reading, and asserts
    // byte-identity. Mirrors TagLibWritePreviewTests.Preview_NeverModifiesFileOnDisk's pattern for
    // the write-preview path, and the corpus-hashing method the L3 measurement itself used.
    // ===========================================================================================

    [Fact]
    public void Read_NeverModifiesRealCorpusFiles()
    {
        if (SkipIfCorpusMissing()) return;

        foreach (var name in CorpusFiles)
        {
            var path = Corpus(name);
            if (!File.Exists(path)) { _out.WriteLine($"SKIP (missing from corpus): {name}"); continue; }

            var before = SHA256.HashData(File.ReadAllBytes(path));
            var result = _reader.Read(path);
            var after = SHA256.HashData(File.ReadAllBytes(path));

            Assert.True(result.Success, $"{name}: expected a successful read, got '{result.FailureReason}'");
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void Read_NeverModifiesSyntheticFixture()
    {
        var path = CreateVendorFixtureMp3();
        var before = SHA256.HashData(File.ReadAllBytes(path));

        // Read multiple times - a caching/lazy-load bug that only shows up on a second pass must
        // not slip through a single-call test.
        _reader.Read(path);
        _reader.Read(path);
        _reader.Read(path);

        var after = SHA256.HashData(File.ReadAllBytes(path));
        Assert.Equal(before, after);
    }

    // ===========================================================================================
    // Corpus: vendor recognition on real files. Numbers below were verified against this reader's
    // actual output on the corpus (not copied blind from the L3 doc), though they agree with it -
    // see the per-file frame-count table there for cross-reference.
    // ===========================================================================================

    [Fact]
    public void Read_Corpus_A_RecognizesSeratoAndMixedInKeyGeobFrames()
    {
        if (SkipIfCorpusMissing()) return;

        var result = _reader.Read(Corpus("A_serato_full_anaritzz.mp3"));
        Assert.True(result.Success);
        Assert.Equal(31, result.TotalEntryCount);

        var id3 = Assert.Single(result.Containers);
        Assert.Equal(RawTagContainerKind.Id3v2, id3.Kind);
        Assert.Equal("ID3v2.3", id3.Label);

        var markers2 = Assert.Single(id3.Entries, e => e.Id == "GEOB" && e.Descriptor == "Serato Markers2");
        Assert.Equal(RawTagVendor.Serato, markers2.Vendor);
        Assert.Equal(513, markers2.SizeBytes);

        var mikKey = Assert.Single(id3.Entries, e => e.Id == "GEOB" && e.Descriptor == "Key");
        Assert.Equal(RawTagVendor.MixedInKey, mikKey.Vendor);

        // "Multiple GEOB/TXXX frames on one file" - real, not contrived: this one file alone
        // carries 10 GEOB frames and 6 TXXX frames.
        Assert.Equal(10, id3.Entries.Count(e => e.Id == "GEOB"));
        Assert.Equal(6, id3.Entries.Count(e => e.Id == "TXXX"));

        // Binary entries never show raw bytes - only a size (and mime, where known) summary.
        var apic = Assert.Single(id3.Entries, e => e.Id == "APIC");
        Assert.Equal("image, 226,578 bytes (image/jpeg)", apic.Summary);
    }

    [Fact]
    public void Read_Corpus_D_RecognizesOwnPopmAndMixedInKeyCuePoints()
    {
        if (SkipIfCorpusMissing()) return;

        var result = _reader.Read(Corpus("D_popm_cortes.mp3"));
        Assert.True(result.Success);

        var id3 = Assert.Single(result.Containers);
        var popm = Assert.Single(id3.Entries, e => e.Id == "POPM");
        Assert.Equal("JustPlay", popm.Descriptor);
        Assert.Equal(RawTagVendor.JustPlay, popm.Vendor);
        Assert.Equal("rating 255/255, played 0x", popm.Summary);

        var cuePoints = Assert.Single(id3.Entries, e => e.Id == "GEOB" && e.Descriptor == "CuePoints");
        Assert.Equal(RawTagVendor.MixedInKey, cuePoints.Vendor);
    }

    [Fact]
    public void Read_Corpus_F_And_G_RecognizeTraktorPrivFrame()
    {
        if (SkipIfCorpusMissing()) return;

        var f = _reader.Read(Corpus("F_mik_armstrong.mp3"));
        var fId3 = Assert.Single(f.Containers);
        Assert.Equal("ID3v2.4", fId3.Label); // F is the corpus's one v2.4 tag alongside G
        var fPriv = Assert.Single(fId3.Entries, e => e.Id == "PRIV");
        Assert.Equal("TRAKTOR4", fPriv.Descriptor);
        Assert.Equal(RawTagVendor.Traktor, fPriv.Vendor);
        Assert.Equal(85166, fPriv.SizeBytes);

        var g = _reader.Read(Corpus("G_priv_crownless.mp3"));
        Assert.Equal(4, g.TotalEntryCount);
        var gPriv = Assert.Single(g.Containers.Single().Entries, e => e.Id == "PRIV");
        Assert.Equal(RawTagVendor.Traktor, gPriv.Vendor);
        Assert.Equal(26323, gPriv.SizeBytes);
    }

    [Fact]
    public void Read_Corpus_VendorEntries_SurfacesOnlyRecognizedRows()
    {
        if (SkipIfCorpusMissing()) return;

        var result = _reader.Read(Corpus("A_serato_full_anaritzz.mp3"));
        // Standard frames (TIT2, TPE1, TKEY, TBPM, COMM, APIC, ...) must NOT show up as "vendor"
        // entries - only the ones this reader can actually attribute to a tool.
        Assert.All(result.VendorEntries, e => Assert.NotEqual(RawTagVendor.Unknown, e.Vendor));
        Assert.True(result.VendorEntries.Count() < result.TotalEntryCount);
        Assert.Contains(result.VendorEntries, e => e.Vendor == RawTagVendor.Serato);
        Assert.Contains(result.VendorEntries, e => e.Vendor == RawTagVendor.MixedInKey);
        Assert.Contains(result.VendorEntries, e => e.Vendor == RawTagVendor.JustPlay);
    }

    // ===========================================================================================
    // Synthetic fixtures - multiple frames of the same type on one file, and every container kind
    // this reader implements or explicitly reports as unsupported.
    // ===========================================================================================

    [Fact]
    public void Read_Id3v2_HandlesMultipleGeobTxxxPrivFramesOnOneFile()
    {
        var path = CreateVendorFixtureMp3();
        var result = _reader.Read(path);

        Assert.True(result.Success);
        var id3 = Assert.Single(result.Containers, c => c.Kind == RawTagContainerKind.Id3v2);

        Assert.Equal(2, id3.Entries.Count(e => e.Id == "GEOB"));
        Assert.Equal(2, id3.Entries.Count(e => e.Id == "TXXX"));
        Assert.Equal(2, id3.Entries.Count(e => e.Id == "PRIV"));

        var seratoGeob = Assert.Single(id3.Entries, e => e.Id == "GEOB" && e.Descriptor == "Serato Markers2");
        Assert.Equal(RawTagVendor.Serato, seratoGeob.Vendor);

        var mikGeob = Assert.Single(id3.Entries, e => e.Id == "GEOB" && e.Descriptor == "Energy");
        Assert.Equal(RawTagVendor.MixedInKey, mikGeob.Vendor);

        var traktorPriv = Assert.Single(id3.Entries, e => e.Id == "PRIV" && e.Descriptor == "TRAKTOR4");
        Assert.Equal(RawTagVendor.Traktor, traktorPriv.Vendor);

        var otherPriv = Assert.Single(id3.Entries, e => e.Id == "PRIV" && e.Descriptor == "org.example.other");
        Assert.Equal(RawTagVendor.Unknown, otherPriv.Vendor);

        var mikTxxx = Assert.Single(id3.Entries, e => e.Id == "TXXX" && e.Descriptor == "EnergyLevel");
        Assert.Equal(RawTagVendor.MixedInKey, mikTxxx.Vendor);
        Assert.Equal("6", mikTxxx.Summary);

        var plainTxxx = Assert.Single(id3.Entries, e => e.Id == "TXXX" && e.Descriptor == "PLAINFIELD");
        Assert.Equal(RawTagVendor.Unknown, plainTxxx.Vendor);

        var popm = Assert.Single(id3.Entries, e => e.Id == "POPM");
        Assert.Equal(RawTagVendor.JustPlay, popm.Vendor);
        Assert.Equal("rating 196/255, played 2x", popm.Summary);

        var comm = Assert.Single(id3.Entries, e => e.Id == "COMM");
        Assert.Equal("test comment", comm.Summary);
    }

    [Fact]
    public void Read_Id3v1Only_ReportsUnsupportedContainer_NotAnEmptyId3v2Container()
    {
        var path = CreateId3v1OnlyMp3();
        var result = _reader.Read(path);

        Assert.True(result.Success);
        var container = Assert.Single(result.Containers);
        Assert.Equal(RawTagContainerKind.Id3v1, container.Kind);
        Assert.Empty(container.Entries);
        Assert.False(string.IsNullOrWhiteSpace(container.UnsupportedReason));

        // The TagLib# eager-empty-Id3v1-tag gotcha (TagContractComplianceTests.AssertNoId3v1Bytes)
        // must not make a clean file report a phantom container of any OTHER kind.
        Assert.DoesNotContain(result.Containers, c => c.Kind == RawTagContainerKind.Id3v2);
    }

    [Fact]
    public void Read_Xiph_RecognizesSeratoAndOwnFields()
    {
        var path = CreateFlacFixture();
        var result = _reader.Read(path);

        Assert.True(result.Success);
        var xiph = Assert.Single(result.Containers);
        Assert.Equal(RawTagContainerKind.Xiph, xiph.Kind);

        var serato = Assert.Single(xiph.Entries, e => e.Id == "SERATO_MARKERS_V2");
        Assert.Equal(RawTagVendor.Serato, serato.Vendor);
        Assert.Null(serato.Descriptor); // Xiph has no separate descriptor axis - the key IS the id

        var ours = Assert.Single(xiph.Entries, e => e.Id == "JUSTPLAY");
        Assert.Equal(RawTagVendor.JustPlay, ours.Vendor);

        // Xiph normalises field keys to upper case - EnergyLevel written, ENERGYLEVEL read back.
        var mik = Assert.Single(xiph.Entries, e => e.Id == "ENERGYLEVEL");
        Assert.Equal(RawTagVendor.MixedInKey, mik.Vendor);

        var artist = Assert.Single(xiph.Entries, e => e.Id == "ARTIST");
        Assert.Equal(RawTagVendor.Unknown, artist.Vendor);
        Assert.Equal("Test Artist", artist.Summary);
    }

    [Fact]
    public void Read_Ape_RecognizesSeratoField_AndReportsBinaryItemsBySizeOnly()
    {
        var path = CreateVendorFixtureMp3(); // also carries an APE tag - see the builder
        var result = _reader.Read(path);

        var ape = Assert.Single(result.Containers, c => c.Kind == RawTagContainerKind.Ape);
        var serato = Assert.Single(ape.Entries, e => e.Id == "SERATO_TESTFIELD");
        Assert.Equal(RawTagVendor.Serato, serato.Vendor);
        Assert.Equal("abc", serato.Summary);

        var plain = Assert.Single(ape.Entries, e => e.Id == "PLAIN_APE_FIELD");
        Assert.Equal(RawTagVendor.Unknown, plain.Vendor);

        // TagLib# mirrors the ID3v2 pictures into the APE tag too (binary items) - never a
        // hexdump, just a size summary.
        Assert.Contains(ape.Entries, e => e.Summary.EndsWith(" bytes", StringComparison.Ordinal));
    }

    // ===========================================================================================
    // Never throws - a file this reader cannot open or parse comes back as a failed result.
    // ===========================================================================================

    [Fact]
    public void Read_CorruptFile_ReturnsFailureReason_NeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_rawtag_corrupt_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]); // not a recognisable audio format
        _tempFiles.Add(path);

        var result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Containers);
    }

    [Fact]
    public void Read_MissingFile_ReturnsFailureReason_NeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_rawtag_missing_{Guid.NewGuid():N}.mp3");

        var result = _reader.Read(path);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.Containers);
    }

    // ===========================================================================================
    // Fixture builders - synthesised in-process, matching the pattern TagContractComplianceTests /
    // EditableTagsRoundTripTests already use (bare ID3v2.3 header + one MPEG sync). Frames are
    // added directly via TagLib# (fixture setup, not the code under test) so this test file needs
    // no binary blobs.
    // ===========================================================================================

    private string CreateBareMp3()
    {
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3; // v2.3
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        return WriteTemp(".mp3", bare);
    }

    /// <summary>
    /// One MP3 carrying: 2 GEOB (Serato + Mixed In Key), 2 TXXX (Mixed In Key's EnergyLevel + a
    /// plain field), 2 PRIV (Traktor + an unrecognised owner), our own POPM, a COMM, a real cover
    /// APIC, and - because requesting the APE tag on save writes one alongside ID3v2 - an APEv2
    /// tag with a Serato-prefixed and a plain field. One fixture, every container kind this reader
    /// implements in detail.
    /// </summary>
    private string CreateVendorFixtureMp3()
    {
        var path = CreateBareMp3();

        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;

            // GEOB frames go through Tag.Pictures (NOT Tag.AddFrame): TagLib# represents GEOB and
            // APIC with the same AttachmentFrame type, and assigning Tag.Pictures REPLACES every
            // AttachmentFrame on the tag - the exact clear-all-destroys-GEOB trap
            // TagLibMetadataWriter.KeepNonPictureAttachments already guards against on the write
            // path. Build the full picture set in one assignment instead of two.
            var seratoGeob = new TagLib.Id3v2.AttachmentFrame
            {
                Description = "Serato Markers2",
                MimeType = "application/octet-stream",
                Type = TagLib.PictureType.NotAPicture,
                Data = new TagLib.ByteVector(new byte[20]),
            };
            var mikGeob = new TagLib.Id3v2.AttachmentFrame
            {
                Description = "Energy",
                MimeType = "application/json",
                Type = TagLib.PictureType.NotAPicture,
                Data = new TagLib.ByteVector(new byte[5]),
            };
            var cover = new TagLib.Picture(new TagLib.ByteVector(new byte[50]))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
            };
            file.Tag.Pictures = [seratoGeob, mikGeob, cover];

            TagLib.Id3v2.UserTextInformationFrame.Get(id3, "EnergyLevel", true)!.Text = ["6"];
            TagLib.Id3v2.UserTextInformationFrame.Get(id3, "PLAINFIELD", true)!.Text = ["plain value"];

            TagLib.Id3v2.PrivateFrame.Get(id3, "TRAKTOR4", true)!.PrivateData = new TagLib.ByteVector(new byte[8]);
            TagLib.Id3v2.PrivateFrame.Get(id3, "org.example.other", true)!.PrivateData = new TagLib.ByteVector(new byte[3]);

            var popm = TagLib.Id3v2.PopularimeterFrame.Get(id3, "JustPlay", true)!;
            popm.Rating = 196;
            popm.PlayCount = 2;

            file.Tag.Comment = "test comment";

            var ape = (TagLib.Ape.Tag)file.GetTag(TagLib.TagTypes.Ape, true)!;
            ape.SetValue("SERATO_TESTFIELD", "abc");
            ape.SetValue("PLAIN_APE_FIELD", "xyz");

            file.Save();
        }

        return path;
    }

    /// <summary>A minimal FLAC (mirrors TagContractComplianceTests.CreateFlac) with a Xiph comment
    /// carrying a Serato field, our own JUSTPLAY field, Mixed In Key's EnergyLevel, and one plain
    /// standard field.</summary>
    private string CreateFlacFixture()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("fLaC"u8.ToArray());
        w.Write((byte)0x80); // last-block flag | type 0 (STREAMINFO)
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)34);
        var si = new byte[34];
        si[0] = 0x10; si[1] = 0x00; // min block size 4096
        si[2] = 0x10; si[3] = 0x00; // max block size 4096
        si[10] = 0x0A; si[11] = 0xC4; si[12] = 0x42; si[13] = 0xF0; // 44100 Hz / 2ch / 16bit
        w.Write(si);
        var path = WriteTemp(".flac", ms.ToArray());

        using (var file = TagLib.File.Create(path))
        {
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true)!;
            xiph.SetField("ARTIST", "Test Artist");
            xiph.SetField("SERATO_MARKERS_V2", "base64garbageherelongstring==");
            xiph.SetField("JUSTPLAY", "{\"v\":9}");
            xiph.SetField("EnergyLevel", "7");
            file.Save();
        }

        return path;
    }

    /// <summary>An MP3 with a hand-built 128-byte ID3v1 trailer and NO ID3v2 header - the
    /// "explicitly unsupported" container this reader is required to report gracefully.</summary>
    private string CreateId3v1OnlyMp3()
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xFB, 0x90, 0x00]); // minimal MPEG sync, no leading ID3v2 tag

        var v1 = new byte[128];
        v1[0] = (byte)'T'; v1[1] = (byte)'A'; v1[2] = (byte)'G';
        WriteAscii(v1, 3, 30, "Test Title");
        WriteAscii(v1, 33, 30, "Test Artist");
        WriteAscii(v1, 63, 30, "Test Album");
        WriteAscii(v1, 93, 4, "2024");
        WriteAscii(v1, 97, 30, "Test Comment");
        v1[127] = 255; // genre: unknown
        ms.Write(v1);

        return WriteTemp(".mp3", ms.ToArray());
    }

    private static void WriteAscii(byte[] buffer, int offset, int length, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
    }

    private string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_rawtag_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }
}
