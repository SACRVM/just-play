using System.Text;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// An editorial write touches the fields it is told to and NOTHING else - the frames of every other
/// field stay byte-identical. And the editable read shows a multi-value field whole.
///
/// <para>The data loss this pins: the writer used to assign every editorial field on every save,
/// from a read that kept only the FIRST value. TagLib# splits an ID3v2.3 TPE1/TCON on '/', so
/// fixing a title truncated "AC/DC" to "AC" and the Beatport genre "Techno (Peak Time / Driving)" to
/// "Techno (Peak Time"; the year turned a TDRC "2019-05-03" into "2019"; the comment helper dropped
/// iTunes' COMM frames and the other-language ones; a FLAC lost its second ARTIST. ID3v2.4 keeps the
/// strings whole, which is why both versions are pinned.</para>
/// </summary>
public sealed class EditableWriteMaskTests : IDisposable
{
    private readonly List<string> _temps = [];
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();

    public void Dispose()
    {
        foreach (var t in _temps)
            if (File.Exists(t)) File.Delete(t);
    }

    // -- MP3: the frames of untouched fields are byte-identical -----------------------------------

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void EditingOnlyTheTitle_LeavesEveryOtherFrameByteIdentical(byte major)
    {
        var path = Mp3Fixture(major);
        var before = Id3Frames(path);

        var tags = _reader.ReadEditable(path) with { Title = "New Title" };
        _writer.WriteEditable(path, tags, EditableField.Title, CoverAction.Keep, null, null);

        var after = Id3Frames(path);
        Assert.Equal(Without(before, "TIT2"), Without(after, "TIT2"));
        Assert.Equal("New Title", _reader.ReadEditable(path).Title);
    }

    /// <summary>The same save, asserted value by value - what the review measured, in its words.</summary>
    [Fact]
    public void EditingOnlyTheTitle_OnId3v23_KeepsTheSlashValues_TheDate_AndEveryComment()
    {
        var path = Mp3Fixture(3);

        var tags = _reader.ReadEditable(path) with { Title = "New Title" };
        _writer.WriteEditable(path, tags, EditableField.Title, CoverAction.Keep, null, null);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false)!;

        Assert.Equal("AC/DC", string.Join("/", file.Tag.Performers));
        Assert.Equal("Techno (Peak Time / Driving)", string.Join("/", file.Tag.Genres));
        Assert.Equal("2019-05-03", TextOf(id3, "TDRC"));

        var comments = id3.GetFrames<TagLib.Id3v2.CommentsFrame>("COMM")
                          .Select(c => $"{c.Language}|{c.Description}|{c.Text}")
                          .ToList();
        Assert.Equal(3, comments.Count);
        Assert.Contains($"eng|iTunNORM|{ItunNorm}", comments);
        Assert.Contains("eng||main comment", comments);
        Assert.Contains("deu||zweiter Kommentar", comments);
    }

    [Fact]
    public void AnEmptyMask_WritesNoTextField()
    {
        var path = Mp3Fixture(3);
        var before = Id3Frames(path);

        // Every value different from the file - and none of them named.
        var everythingElse = new EditableTags
        {
            Title = "x", Artist = "x", Album = "x", AlbumArtist = "x", Genre = "x",
            Year = 1, TrackNumber = 1, Comment = "x",
        };
        _writer.WriteEditable(path, everythingElse, EditableField.None, CoverAction.Keep, null, null);

        Assert.Equal(before, Id3Frames(path));
    }

    /// <summary>The other half of the contract: a field that IS edited is written as one value.</summary>
    [Fact]
    public void AnEditedArtist_IsWrittenAsOneValue()
    {
        var path = Mp3Fixture(3);

        _writer.WriteEditable(path, new EditableTags { Artist = "Ansome" }, EditableField.Artist,
                              CoverAction.Keep, null, null);

        using var file = TagLib.File.Create(path);
        Assert.Equal(["Ansome"], file.Tag.Performers);
        Assert.Equal("Techno (Peak Time / Driving)", string.Join("/", file.Tag.Genres));   // untouched
    }

    // -- ID3v2.3 genre: shown and kept as the frame stores it -------------------------------------

    /// <summary>TagLib# splits a v2.3 genre on ';' as well, so a "/" join would show "House; Techno"
    /// as "House/ Techno". The editable read takes the frame's own text instead, and a save that
    /// does not touch the genre leaves that text alone.</summary>
    [Fact]
    public void ASemicolonGenre_OnId3v23_IsShownAndKeptAsStored()
    {
        var path = Mp3Fixture(3, id3 => id3.SetTextFrame("TCON", "House; Techno"));
        Assert.Equal("House; Techno", _reader.ReadEditable(path).Genre);

        var before = Only(Id3Frames(path), "TCON");
        var tags = _reader.ReadEditable(path) with { Title = "New Title" };
        _writer.WriteEditable(path, tags, EditableField.Title, CoverAction.Keep, null, null);

        Assert.Equal(before, Only(Id3Frames(path), "TCON"));
    }

    /// <summary>Writing the genre is still a write: the guard steps aside for the field being set.</summary>
    [Fact]
    public void AnEditedGenre_OnId3v23_IsWritten()
    {
        var path = Mp3Fixture(3);

        _writer.WriteEditable(path, new EditableTags { Genre = "Hard Techno" }, EditableField.Genre,
                              CoverAction.Keep, null, null);

        Assert.Equal("Hard Techno", _reader.ReadEditable(path).Genre);
    }

    // -- FLAC: two ARTIST fields survive a title edit ---------------------------------------------

    [Fact]
    public void EditingOnlyTheTitle_OnFlac_KeepsBothArtistFields_AndEveryOtherField()
    {
        var path = FlacFixture();
        var before = XiphFields(path);

        var tags = _reader.ReadEditable(path) with { Title = "New Title" };
        _writer.WriteEditable(path, tags, EditableField.Title, CoverAction.Keep, null, null);

        var after = XiphFields(path);
        Assert.Equal(["Artist One", "Artist Two"], after["ARTIST"]);
        Assert.Equal(["New Title"], after["TITLE"]);

        before.Remove("TITLE");
        after.Remove("TITLE");
        Assert.Equal(before, after);
    }

    // -- The editable read shows every value ------------------------------------------------------

    [Fact]
    public void ReadEditable_OnId3v23_RejoinsTheSlashSplit_Exactly()
    {
        var path = Mp3Fixture(3);

        var tags = _reader.ReadEditable(path);

        Assert.Equal("AC/DC", tags.Artist);
        Assert.Equal("Various / Others", tags.AlbumArtist);
        Assert.Equal("Techno (Peak Time / Driving)", tags.Genre);
        Assert.Equal(2019u, tags.Year);
    }

    [Fact]
    public void ReadEditable_OnId3v24_KeepsASlashValueWhole()
    {
        var path = Mp3Fixture(4);

        var tags = _reader.ReadEditable(path);

        Assert.Equal("AC/DC", tags.Artist);
        Assert.Equal("Techno (Peak Time / Driving)", tags.Genre);
    }

    /// <summary>Genuinely separate values are shown with "; " between them - all of them visible,
    /// none mistakable for part of another.</summary>
    [Fact]
    public void ReadEditable_ShowsGenuineMultiValues_SeparatedBySemicolons()
    {
        var mp3 = Mp3Fixture(4, id3 => id3.SetTextFrame("TPE1", "Artist One", "Artist Two"));
        Assert.Equal("Artist One; Artist Two", _reader.ReadEditable(mp3).Artist);

        var flac = FlacFixture();
        Assert.Equal("Artist One; Artist Two", _reader.ReadEditable(flac).Artist);
    }

    [Fact]
    public void ReadEditable_OnALockedFile_SaysTheReadFailed()
    {
        var path = Mp3Fixture(3);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var tags = _reader.ReadEditable(path);
            Assert.True(tags.ReadFailed);
            Assert.Null(tags.Artist);
        }

        Assert.False(_reader.ReadEditable(path).ReadFailed);
    }

    // -- The shared comparison --------------------------------------------------------------------

    [Fact]
    public void Differences_NamesExactlyTheFieldsThatDiffer()
    {
        var a = new EditableTags { Title = "t", Artist = "AC/DC", Year = 2019, Comment = "" };

        Assert.Equal(EditableField.None, EditableTags.Differences(a, a with { Comment = null, Title = " t " }));
        Assert.Equal(EditableField.Title | EditableField.Year,
                     EditableTags.Differences(a, a with { Title = "u", Year = 2020 }));
    }

    // -- fixtures ---------------------------------------------------------------------------------

    private const string ItunNorm = " 0000044E 00000000 00002D1E 00000000 00000000";

    /// <summary>
    /// An MP3 carrying everything a careless save destroys: slash values in TPE1 / TPE2 / TCON, a
    /// full TDRC date, iTunes' COMM next to a plain one and one in another language, and foreign
    /// frames (Traktor PRIV, Mixed In Key TXXX, a Serato GEOB). Built in memory via TagLib# - the
    /// technique of <see cref="FavoriteRoundTripTests"/> - so no binary fixture lives in the repo.
    /// </summary>
    private string Mp3Fixture(byte major, Action<TagLib.Id3v2.Tag>? extra = null)
    {
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = major;
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        var path = Temp(".mp3", bare);

        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;
            id3.SetTextFrame("TIT2", "Old Title");
            id3.SetTextFrame("TPE1", "AC/DC");
            id3.SetTextFrame("TPE2", "Various / Others");
            id3.SetTextFrame("TCON", "Techno (Peak Time / Driving)");
            id3.SetTextFrame("TDRC", "2019-05-03");
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("iTunNORM", "eng") { Text = ItunNorm });
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("", "eng") { Text = "main comment" });
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("", "deu") { Text = "zweiter Kommentar" });
            id3.AddFrame(new TagLib.Id3v2.PrivateFrame("TRAKTOR4", new TagLib.ByteVector([1, 2, 3, 4])));
            TagLib.Id3v2.UserTextInformationFrame.Get(id3, "EnergyLevel", true)!.Text = ["6"];
            file.Tag.Pictures =
            [
                new TagLib.Id3v2.AttachmentFrame
                {
                    Description = "Serato Markers2",
                    MimeType = "application/octet-stream",
                    Type = TagLib.PictureType.NotAPicture,
                    Data = new TagLib.ByteVector(new byte[16]),
                },
            ];
            extra?.Invoke(id3);
            file.Save();
        }

        return path;
    }

    /// <summary>A minimal FLAC (the shape TagContractComplianceTests uses) with TWO ARTIST fields, a
    /// full date, a comment and a foreign field.</summary>
    private string FlacFixture()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("fLaC"u8.ToArray());
        w.Write((byte)0x80);                       // last-block flag | type 0 (STREAMINFO)
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)34);
        var si = new byte[34];
        si[0] = 0x10; si[1] = 0x00;
        si[2] = 0x10; si[3] = 0x00;
        si[10] = 0x0A; si[11] = 0xC4; si[12] = 0x42; si[13] = 0xF0;
        w.Write(si);
        var path = Temp(".flac", ms.ToArray());

        using (var file = TagLib.File.Create(path))
        {
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true)!;
            xiph.SetField("TITLE", "Old Title");
            xiph.SetField("ARTIST", "Artist One", "Artist Two");
            xiph.SetField("GENRE", "Techno (Peak Time / Driving)");
            xiph.SetField("DATE", "2019-05-03");
            xiph.SetField("COMMENT", "main comment");
            xiph.SetField("SERATO_MARKERS_V2", "AQFDT0xPUgAAAAAEAP///w==");
            file.Save();
        }

        return path;
    }

    private string Temp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_mask_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _temps.Add(path);
        return path;
    }

    // -- raw readers ------------------------------------------------------------------------------

    /// <summary>
    /// The ID3v2 frames exactly as they sit in the file - id plus the hex of the whole frame, header
    /// included, in file order. Walks the bytes itself rather than asking TagLib#, which would hand
    /// back its own re-rendering of what it parsed. TagLib# writes no extended header and no
    /// unsynchronisation, so a frame list is all there is.
    /// </summary>
    private static List<string> Id3Frames(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b[0] == 'I' && b[1] == 'D' && b[2] == '3', "no ID3v2 tag at the head");
        Assert.Equal(0, b[5]);                                  // no flags: no ext header, no unsync

        var major = b[3];
        var end = 10 + Syncsafe(b, 6);
        var frames = new List<string>();

        for (var pos = 10; pos + 10 <= end && b[pos] != 0;)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = major >= 4
                ? Syncsafe(b, pos + 4)
                : (b[pos + 4] << 24) | (b[pos + 5] << 16) | (b[pos + 6] << 8) | b[pos + 7];
            frames.Add($"{id}:{Convert.ToHexString(b, pos, 10 + size)}");
            pos += 10 + size;
        }

        return frames;
    }

    private static int Syncsafe(byte[] b, int at) =>
        (b[at] << 21) | (b[at + 1] << 14) | (b[at + 2] << 7) | b[at + 3];

    private static List<string> Without(List<string> frames, string id) =>
        frames.Where(f => !f.StartsWith(id + ":", StringComparison.Ordinal)).ToList();

    private static List<string> Only(List<string> frames, string id) =>
        frames.Where(f => f.StartsWith(id + ":", StringComparison.Ordinal)).ToList();

    private static string? TextOf(TagLib.Id3v2.Tag id3, string id) =>
        id3.GetFrames<TagLib.Id3v2.TextInformationFrame>(id).FirstOrDefault()?.Text.FirstOrDefault();

    /// <summary>Every Xiph field with every value, by name.</summary>
    private static Dictionary<string, string[]> XiphFields(string path)
    {
        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, false)!;
        return xiph.ToDictionary(name => name, name => xiph.GetField(name));
    }
}
