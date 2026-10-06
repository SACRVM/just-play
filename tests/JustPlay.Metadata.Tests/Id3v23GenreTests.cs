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

    // -- fixtures ---------------------------------------------------------------------------------

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
