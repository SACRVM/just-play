using JustPlay.Core.Abstractions;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Round-trip tests for the POPM (Popularimeter) favourite feature:
/// write -> read -> assert; unfavourite -> read -> assert false; verify other
/// tags are preserved. All tests use a real (temp-file) MP3 synthesised
/// in memory via TagLib# so no audio fixture is needed in the repo.
/// </summary>
public class FavoriteRoundTripTests : IDisposable
{
    private readonly string _tempFile;
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();

    public FavoriteRoundTripTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"justplay_fav_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(_tempFile, MinimalMp3WithTitle("Test Track"));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void WriteFavoriteTrue_ThenRead_ReturnsIsFavoriteTrue()
    {
        var before = _reader.Read(_tempFile);
        Assert.False(before.IsFavorite);

        _writer.Write(_tempFile, new TagWrite { Favorite = true });

        var after = _reader.Read(_tempFile);
        Assert.True(after.IsFavorite);
    }

    [Fact]
    public void WriteFavoriteFalse_AfterLiking_ReturnsIsFavoriteFalse()
    {
        // Like it first.
        _writer.Write(_tempFile, new TagWrite { Favorite = true });
        Assert.True(_reader.Read(_tempFile).IsFavorite);

        // Unlike it.
        _writer.Write(_tempFile, new TagWrite { Favorite = false });
        Assert.False(_reader.Read(_tempFile).IsFavorite);
    }

    [Fact]
    public void WriteFavoriteTrue_PreservesExistingTitle()
    {
        // The test file is seeded with title "Test Track" (set in MinimalMp3WithTitle).
        var before = _reader.Read(_tempFile);
        Assert.Equal("Test Track", before.Title);

        _writer.Write(_tempFile, new TagWrite { Favorite = true });

        var after = _reader.Read(_tempFile);
        Assert.Equal("Test Track", after.Title);
        Assert.True(after.IsFavorite);
    }

    [Fact]
    public void WriteFavoriteNull_LeavesPopmUntouched()
    {
        // Like it, then write with Favorite = null - POPM must not be cleared.
        _writer.Write(_tempFile, new TagWrite { Favorite = true });
        Assert.True(_reader.Read(_tempFile).IsFavorite);

        // A write with Favorite = null must leave the existing POPM frame alone.
        _writer.Write(_tempFile, new TagWrite { Bpm = 128 });
        Assert.True(_reader.Read(_tempFile).IsFavorite);
    }

    [Fact]
    public void WriteUnliked_OnFileWithNoPopm_DoesNotCrash()
    {
        // File has no POPM frame. Removing a non-existent frame must not throw.
        var ex = Record.Exception(() => _writer.Write(_tempFile, new TagWrite { Favorite = false }));
        Assert.Null(ex);
        Assert.False(_reader.Read(_tempFile).IsFavorite);
    }

    // -- Synthesise a minimal valid ID3v2-tagged MP3 with a title -------------
    // Same technique as DjCommentRoundTripTests: ask TagLib# to write a title tag
    // so we don't hand-craft frame bytes. The MPEG audio content is irrelevant.

    private static byte[] MinimalMp3WithTitle(string title)
    {
        var buf = Path.Combine(Path.GetTempPath(), $"justplay_fav_seed_{Guid.NewGuid():N}.mp3");
        try
        {
            // Bare ID3v2.3 header + one silent MPEG frame header.
            var bare = new byte[10 + 4];
            bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
            bare[3] = 3; bare[4] = 0; bare[5] = 0;
            bare[6] = 0; bare[7] = 0; bare[8] = 0; bare[9] = 0;
            bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
            File.WriteAllBytes(buf, bare);

            using var f = TagLib.File.Create(buf);
            f.Tag.Title = title;
            f.Save();

            return File.ReadAllBytes(buf);
        }
        finally
        {
            if (File.Exists(buf)) File.Delete(buf);
        }
    }
}
