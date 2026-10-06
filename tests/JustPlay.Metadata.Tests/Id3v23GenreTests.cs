using System.Text;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// An ID3v2.3 genre survives a save that does not write it - byte for byte.
///
/// <para>What was wrong: TagLib# 2.3 parses a v2.3 TCON by splitting it on '/' AND ';' and renders
/// it back joined with ';'. It re-renders a text frame on every save whose encoding differs from the
/// process-global default - in practice every v2.3 frame - so the analysis write JUST PLAY makes
/// after measuring turned the Beatport genre "Techno (Peak Time / Driving)" into
/// "Techno (Peak Time ; Driving)", which is what Traktor and Serato then show. Measured on a save that
/// assigned nothing at all.</para>
/// </summary>
public sealed class Id3v23GenreTests : IDisposable
{
    private readonly List<string> _temps = [];
    private readonly TagLibMetadataWriter _writer = new();

    public void Dispose()
    {
        foreach (var t in _temps)
            if (File.Exists(t)) File.Delete(t);
    }

    [Theory]
    [InlineData("Techno (Peak Time / Driving)")]
    [InlineData("House; Techno")]
    [InlineData("Drum & Bass/Jungle")]
    public void AnAnalysisWrite_KeepsTheGenreFrameByteIdentical(string genre)
    {
        var path = Mp3(genre);
        var before = Tcon(path);

        _writer.Write(path, new TagWrite { Bpm = 128 });

        Assert.Equal(before, Tcon(path));
    }

    [Fact]
    public void ARestore_KeepsTheGenreFrameByteIdentical()
    {
        var path = Mp3("Techno (Peak Time / Driving)");
        var before = Tcon(path);

        _writer.Restore(path, new TagRestore());

        Assert.Equal(before, Tcon(path));
    }

    /// <summary>The guard reads the frame from the file's own bytes and never guesses: a v2.3 file with
    /// no genre gets none.</summary>
    [Fact]
    public void AFileWithoutAGenre_GetsNone()
    {
        var path = Mp3(null);

        _writer.Write(path, new TagWrite { Bpm = 128 });

        Assert.Null(Tcon(path));
    }

    /// <summary>
    /// The READ side the index, the track table and search see. It used First* values, so a v2.3
    /// genre or artist with a slash reached all of them cut at the slash - "Techno (Peak Time" as the
    /// genre column, "AC" as the artist.
    /// </summary>
    [Theory]
    [InlineData("Techno (Peak Time / Driving)")]
    [InlineData("Hard Dance / Hardcore / Neo Rave")]
    [InlineData("House; Techno")]
    public void Read_ReturnsTheWholeGenre(string genre)
    {
        var path = Mp3(genre);

        Assert.Equal(genre, new TagLibMetadataReader().Read(path).Genre);
    }

    [Fact]
    public void Read_ReturnsTheWholeArtist()
    {
        var path = Mp3(null, artist: "AC/DC");

        Assert.Equal("AC/DC", new TagLibMetadataReader().Read(path).Artist);
    }

    /// <summary>
    /// The same guard for a tag that does not lead the file: an AIFF keeps its ID3 tag in an "ID3 "
    /// chunk after the audio, a WAV in an "id3 " chunk. Lossless DJ downloads are mostly AIFF, so a
    /// guard that only looked at offset 0 left exactly those genres to TagLib#'s rewrite.
    /// </summary>
    [Theory]
    [InlineData(".aiff", "Techno (Peak Time / Driving)")]
    [InlineData(".aiff", "Hard Dance / Hardcore / Neo Rave")]
    [InlineData(".wav", "Techno (Peak Time / Driving)")]
    public void AnAnalysisWrite_KeepsAChunkedGenre(string ext, string genre)
    {
        var path = ext == ".aiff" ? Aiff(genre) : Wav(genre);

        _writer.Write(path, new TagWrite { Bpm = 128 });

        // The frame's own text, not a reader's: a reader that joins TagLib#'s split parts with '/'
        // turns a rewritten "A ; B" back into "A / B" and would hide exactly this defect.
        Assert.Equal(genre, ChunkTconText(path));
    }

    // -- fixtures ---------------------------------------------------------------------------------

    /// <summary>A minimal AIFF (the TagContractComplianceTests shape) carrying an ID3v2.3 chunk whose
    /// genre is stored as ONE string.</summary>
    private string Aiff(string genre)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Be32(uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        void Be16(ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }

        w.Write("FORM"u8.ToArray()); Be32(4 + 8 + 18 + 8 + 8); w.Write("AIFF"u8.ToArray());
        w.Write("COMM"u8.ToArray()); Be32(18);
        Be16(2); Be32(0); Be16(16);
        Be16(0x400E); Be32(0xAC440000); Be32(0);   // 44100 Hz as an 80-bit extended
        w.Write("SSND"u8.ToArray()); Be32(8); Be32(0); Be32(0);

        return Tagged(".aiff", ms.ToArray(), genre);
    }

    /// <summary>A minimal PCM WAV carrying an ID3v2.3 chunk whose genre is stored as ONE string.</summary>
    private string Wav(string genre)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);   // little-endian, as RIFF wants

        w.Write("RIFF"u8.ToArray()); w.Write(4u + 8 + 16 + 8 + 4); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16u);
        w.Write((ushort)1); w.Write((ushort)2); w.Write(44100u); w.Write(44100u * 4); w.Write((ushort)4); w.Write((ushort)16);
        w.Write("data"u8.ToArray()); w.Write(4u); w.Write(0u);

        return Tagged(".wav", ms.ToArray(), genre);
    }

    private string Tagged(string ext, byte[] container, string genre)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_v23genre_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, container);
        _temps.Add(path);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;
        id3.Version = 3;
        id3.SetTextFrame("TIT2", "Title");
        id3.SetTextFrame("TCON", genre);
        file.Save();

        // The fixture itself must hold the genre as one v2.3 string, or the test proves nothing.
        Assert.Equal(genre, ChunkTconText(path));
        return path;
    }

    /// <summary>The text of the TCON frame in an AIFF "ID3 " / WAV "id3 " chunk, read from the bytes
    /// (UTF-16 or Latin-1, cut at the first NUL). Fails when the chunk is not ID3v2.3.</summary>
    private static string? ChunkTconText(string path)
    {
        var b = File.ReadAllBytes(path);
        var aiff = Encoding.ASCII.GetString(b, 0, 4) == "FORM";
        int Size(int at) => aiff
            ? (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]
            : b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        for (var pos = 12; pos + 8 <= b.Length;)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = Size(pos + 4);
            if (id is "ID3 " or "id3 ")
            {
                var t = pos + 8;
                Assert.True(b[t] == 'I' && b[t + 1] == 'D' && b[t + 2] == '3' && b[t + 3] == 3, "not an ID3v2.3 chunk");
                var end = t + 10 + ((b[t + 6] << 21) | (b[t + 7] << 14) | (b[t + 8] << 7) | b[t + 9]);
                for (var f = t + 10; f + 10 <= end && b[f] != 0;)
                {
                    var fsz = (b[f + 4] << 24) | (b[f + 5] << 16) | (b[f + 6] << 8) | b[f + 7];
                    if (Encoding.ASCII.GetString(b, f, 4) == "TCON")
                    {
                        var body = b.AsSpan(f + 11, fsz - 1);
                        var text = b[f + 10] == 1
                            ? Encoding.Unicode.GetString(body.Length >= 2 && body[0] == 0xFF ? body[2..] : body)
                            : Encoding.Latin1.GetString(body);
                        var nul = text.IndexOf('\0');
                        return nul >= 0 ? text[..nul] : text;
                    }
                    f += 10 + fsz;
                }
                return null;
            }
            pos += 8 + size + (size & 1);
        }
        return null;
    }

    /// <summary>A minimal ID3v2.3 MP3 (the TagContractComplianceTests shape) with a title and, when
    /// given, a genre and an artist each stored as ONE string - the way other taggers write them.</summary>
    private string Mp3(string? genre, string? artist = null)
    {
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3;
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        var path = Path.Combine(Path.GetTempPath(), $"justplay_v23genre_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, bare);
        _temps.Add(path);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;
        id3.SetTextFrame("TIT2", "Title");
        if (genre is not null) id3.SetTextFrame("TCON", genre);
        if (artist is not null) id3.SetTextFrame("TPE1", artist);
        file.Save();

        return path;
    }

    /// <summary>The TCON frame exactly as it sits in the file (header included, as hex), or null.
    /// Walks the v2.3 frames itself - TagLib# would hand back its own re-rendering.</summary>
    private static string? Tcon(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b[0] == 'I' && b[1] == 'D' && b[2] == '3' && b[3] == 3, "no ID3v2.3 tag at the head");

        var end = 10 + ((b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9]);
        for (var pos = 10; pos + 10 <= end && b[pos] != 0;)
        {
            var size = (b[pos + 4] << 24) | (b[pos + 5] << 16) | (b[pos + 6] << 8) | b[pos + 7];
            if (Encoding.ASCII.GetString(b, pos, 4) == "TCON")
                return Convert.ToHexString(b, pos, 10 + size);
            pos += 10 + size;
        }

        return null;
    }
}
