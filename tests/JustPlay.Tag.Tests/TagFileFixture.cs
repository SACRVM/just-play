using System.Text;

namespace JustPlay.Tag.Tests;

/// <summary>
/// Real tagged files for the editor's write tests, synthesised in memory through TagLib# (the
/// technique the Metadata tests use) so no binary fixture lives in the repo - plus a reader for the
/// ID3v2 frames as they sit on disk, which is what "left alone" has to be measured on.
/// </summary>
internal sealed class TagFileFixture : IDisposable
{
    private readonly List<string> _temps = [];

    public const string ItunNorm = " 0000044E 00000000 00002D1E 00000000 00000000";

    /// <summary>
    /// An MP3 carrying what a careless save destroys: slash values in TPE1 / TPE2 / TCON, a full TDRC
    /// date, iTunes' COMM next to a plain one and one in another language, and foreign frames.
    /// </summary>
    public string Mp3(byte major, string title = "Old Title", string album = "Bulk")
    {
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = major;
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        var path = Temp(".mp3", bare);

        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;
            id3.SetTextFrame("TIT2", title);
            id3.SetTextFrame("TALB", album);
            id3.SetTextFrame("TPE1", "AC/DC");
            id3.SetTextFrame("TPE2", "Various / Others");
            id3.SetTextFrame("TCON", "Techno (Peak Time / Driving)");
            id3.SetTextFrame("TDRC", "2019-05-03");
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("iTunNORM", "eng") { Text = ItunNorm });
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("", "eng") { Text = "main comment" });
            id3.AddFrame(new TagLib.Id3v2.CommentsFrame("", "deu") { Text = "zweiter Kommentar" });
            id3.AddFrame(new TagLib.Id3v2.PrivateFrame("TRAKTOR4", new TagLib.ByteVector([1, 2, 3, 4])));
            TagLib.Id3v2.UserTextInformationFrame.Get(id3, "EnergyLevel", true)!.Text = ["6"];
            file.Save();
        }

        return path;
    }

    /// <summary>A minimal FLAC with TWO ARTIST fields, a full date and a foreign field.</summary>
    public string Flac()
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
            xiph.SetField("SERATO_MARKERS_V2", "AQFDT0xPUgAAAAAEAP///w==");
            file.Save();
        }

        return path;
    }

    /// <summary>The ID3v2 frames exactly as they sit in the file: id plus the hex of the whole frame,
    /// in file order. TagLib# writes no extended header and no unsynchronisation.</summary>
    public static List<string> Id3Frames(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b[0] == 'I' && b[1] == 'D' && b[2] == '3', "no ID3v2 tag at the head");
        Assert.Equal(0, b[5]);

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

    /// <summary>The frames other than the ones named - what an edit of those must leave identical.</summary>
    public static List<string> Except(List<string> frames, params string[] ids) =>
        frames.Where(f => !ids.Any(id => f.StartsWith(id + ":", StringComparison.Ordinal))).ToList();

    /// <summary>Every Xiph field with every value, by name.</summary>
    public static Dictionary<string, string[]> XiphFields(string path)
    {
        using var file = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, false)!;
        return xiph.ToDictionary(name => name, name => xiph.GetField(name));
    }

    private static int Syncsafe(byte[] b, int at) =>
        (b[at] << 21) | (b[at + 1] << 14) | (b[at + 2] << 7) | b[at + 3];

    private string Temp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justtag_save_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _temps.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var t in _temps)
        {
            try { File.Delete(t); } catch (IOException) { }
        }
    }
}
