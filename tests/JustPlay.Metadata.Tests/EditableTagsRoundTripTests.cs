using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Round-trip tests for the editorial tag editing surface:
/// <see cref="IMetadataWriter.WriteEditable"/> -> <see cref="IMetadataReader.ReadEditable"/> /
/// <see cref="IMetadataReader.Read"/>. All tests use a throwaway temp MP3 synthesised in memory
/// via TagLib# so no audio fixture is needed in the repo. The key regression guard also verifies
/// that a JustPlay analysis blob written via the existing <see cref="IMetadataWriter.Write"/>
/// survives a subsequent <see cref="IMetadataWriter.WriteEditable"/> call.
/// </summary>
public sealed class EditableTagsRoundTripTests : IDisposable
{
    private readonly string _tempFile;
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();

    public EditableTagsRoundTripTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"justplay_edit_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(_tempFile, BuildMinimalMp3());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    // -- Editorial field round-trips -------------------------------------------

    [Fact]
    public void WriteEditable_AllFields_RoundTripViaReadEditable()
    {
        var tags = new EditableTags
        {
            Title       = "Test Title",
            Artist      = "Test Artist",
            Album       = "Test Album",
            AlbumArtist = "Test Album Artist",
            Genre       = "Techno",
            Year        = 2024,
            TrackNumber = 7,
            Comment     = "Test comment",
        };

        _writer.WriteEditable(_tempFile, tags, CoverAction.Keep, null, null);

        var back = _reader.ReadEditable(_tempFile);
        Assert.Equal("Test Title",        back.Title);
        Assert.Equal("Test Artist",       back.Artist);
        Assert.Equal("Test Album",        back.Album);
        Assert.Equal("Test Album Artist", back.AlbumArtist);
        Assert.Equal("Techno",            back.Genre);
        Assert.Equal(2024u,               back.Year);
        Assert.Equal(7u,                  back.TrackNumber);
        Assert.Equal("Test comment",      back.Comment);
    }

    [Fact]
    public void WriteEditable_AllFields_AlsoRoundTripViaRead()
    {
        var tags = new EditableTags
        {
            Title       = "Via Read",
            Artist      = "Read Artist",
            Album       = "Read Album",
            AlbumArtist = "Read Album Artist",
            Genre       = "House",
            Year        = 2023,
            TrackNumber = 3,
        };

        _writer.WriteEditable(_tempFile, tags, CoverAction.Keep, null, null);

        var back = _reader.Read(_tempFile);
        Assert.Equal("Via Read",          back.Title);
        Assert.Equal("Read Artist",       back.Artist);
        Assert.Equal("Read Album",        back.Album);
        Assert.Equal("Read Album Artist", back.AlbumArtist);
        Assert.Equal("House",             back.Genre);
        Assert.Equal(2023u,               back.Year);
        Assert.Equal(3u,                  back.TrackNumber);
    }

    // -- Year / TrackNumber zero clears ----------------------------------------

    [Fact]
    public void WriteEditable_YearZero_ClearsYearTag()
    {
        // Seed a year first.
        _writer.WriteEditable(_tempFile, new EditableTags { Year = 1999 }, CoverAction.Keep, null, null);
        Assert.Equal(1999u, _reader.ReadEditable(_tempFile).Year);

        // Year = 0 must clear.
        _writer.WriteEditable(_tempFile, new EditableTags { Year = 0 }, CoverAction.Keep, null, null);
        var back = _reader.ReadEditable(_tempFile);
        Assert.Equal(0u, back.Year);

        // And Read() should also return null (Year == 0 -> null in TrackMetadata).
        Assert.Null(_reader.Read(_tempFile).Year);
    }

    [Fact]
    public void WriteEditable_TrackNumberZero_ClearsTrackTag()
    {
        // Seed a track number first.
        _writer.WriteEditable(_tempFile, new EditableTags { TrackNumber = 5 }, CoverAction.Keep, null, null);
        Assert.Equal(5u, _reader.ReadEditable(_tempFile).TrackNumber);

        // TrackNumber = 0 must clear.
        _writer.WriteEditable(_tempFile, new EditableTags { TrackNumber = 0 }, CoverAction.Keep, null, null);
        var back = _reader.ReadEditable(_tempFile);
        Assert.Equal(0u, back.TrackNumber);

        // And Read() should return null (Track == 0 -> null in TrackMetadata).
        Assert.Null(_reader.Read(_tempFile).TrackNumber);
    }

    // -- Cover art: Replace -> read returns bytes -------------------------------

    [Fact]
    public void WriteEditable_CoverReplace_ReadReturnsTheSameBytes()
    {
        // A minimal valid JPEG (SOI + EOI marker).
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };

        _writer.WriteEditable(_tempFile, new EditableTags(), CoverAction.Replace, fakeJpeg, "image/jpeg");

        var back = _reader.Read(_tempFile);
        Assert.NotNull(back.CoverArt);
        Assert.Equal(fakeJpeg, back.CoverArt);
    }

    // -- Cover art: Remove clears cover ---------------------------------------

    [Fact]
    public void WriteEditable_CoverRemove_ClearsCoverArt()
    {
        // First embed a cover.
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        _writer.WriteEditable(_tempFile, new EditableTags(), CoverAction.Replace, fakeJpeg, "image/jpeg");
        Assert.NotNull(_reader.Read(_tempFile).CoverArt);

        // Now remove it.
        _writer.WriteEditable(_tempFile, new EditableTags(), CoverAction.Remove, null, null);
        Assert.Null(_reader.Read(_tempFile).CoverArt);
    }

    // -- Cover art: Keep preserves existing cover ------------------------------

    [Fact]
    public void WriteEditable_CoverKeep_PreservesExistingCover()
    {
        // Embed a cover first.
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        _writer.WriteEditable(_tempFile, new EditableTags(), CoverAction.Replace, fakeJpeg, "image/jpeg");
        Assert.NotNull(_reader.Read(_tempFile).CoverArt);

        // A subsequent WriteEditable with Keep must leave the cover alone.
        _writer.WriteEditable(_tempFile, new EditableTags { Title = "New Title" }, CoverAction.Keep, null, null);

        var back = _reader.Read(_tempFile);
        Assert.Equal("New Title", back.Title);
        Assert.NotNull(back.CoverArt);
        Assert.Equal(fakeJpeg, back.CoverArt);
    }

    // -- Regression: analysis blob written by Write() survives WriteEditable() -

    [Fact]
    public void WriteEditable_AfterWrite_PreservesAnalysisBlob()
    {
        var aMinor = new MusicalKey(9, KeyMode.Minor); // A minor

        // Write an analysis blob (BPM + key + energy + JUSTPLAY state).
        var analysisWrite = new TagWrite
        {
            Bpm    = 140.0,
            Key    = aMinor,
            Energy = 8,
            State  = new TrackAnalysisState
            {
                Version  = TrackAnalysisState.CurrentVersion,
                Detected = new AnalysisResult
                {
                    Bpm    = 140.0,
                    Key    = aMinor,
                    Energy = 8,
                },
            },
        };
        _writer.Write(_tempFile, analysisWrite);

        // Confirm the blob was written.
        var before = _reader.Read(_tempFile);
        Assert.NotNull(before.StoredAnalysis);
        Assert.Equal(140.0, before.TaggedBpm);
        Assert.Equal(8,     before.TaggedEnergy);

        // Now do an editorial write - must not touch analysis fields.
        _writer.WriteEditable(_tempFile, new EditableTags { Title = "Edited" }, CoverAction.Keep, null, null);

        var after = _reader.Read(_tempFile);
        Assert.Equal("Edited", after.Title);

        // Analysis fields must be fully preserved.
        Assert.NotNull(after.StoredAnalysis);
        Assert.Equal(140.0,            after.TaggedBpm);
        Assert.Equal(8,                after.TaggedEnergy);
        Assert.Equal(before.TaggedKey, after.TaggedKey);
        Assert.Equal(before.StoredAnalysis!.Version,
                     after.StoredAnalysis!.Version);
    }

    // -- ReadEditable degrades gracefully on corrupt/missing file -------------

    [Fact]
    public void ReadEditable_OnNonExistentFile_ReturnsEmptyRatherThanThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no_such_file_{Guid.NewGuid():N}.mp3");
        var result = Record.Exception(() => _reader.ReadEditable(missing));
        // Must not throw - graceful degradation.
        Assert.Null(result);
    }

    // -- Minimal MP3 factory (same technique as DjCommentRoundTripTests) -------

    private static byte[] BuildMinimalMp3()
    {
        var buf = Path.Combine(Path.GetTempPath(), $"justplay_edit_seed_{Guid.NewGuid():N}.mp3");
        try
        {
            // Bare ID3v2.3 header (10 bytes) + one silent MPEG frame header (4 bytes).
            var bare = new byte[14];
            bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
            bare[3] = 3; bare[4] = 0; bare[5] = 0; // ID3v2.3, no flags
            bare[6] = 0; bare[7] = 0; bare[8] = 0; bare[9] = 0; // 0-byte tag body
            // MPEG1 Layer3 128kbps 44100Hz stereo sync header.
            bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
            File.WriteAllBytes(buf, bare);

            // Round-trip through TagLib# so it expands to a properly-formed ID3v2 file.
            using var f = TagLib.File.Create(buf);
            f.Tag.Title = "seed";
            f.Save();

            return File.ReadAllBytes(buf);
        }
        finally
        {
            if (File.Exists(buf)) File.Delete(buf);
        }
    }
}
