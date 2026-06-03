using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Integration tests for the "DJ Software compatible" comment feature — round-trip
/// through a real (temp) file via <see cref="TagLibMetadataWriter"/> and
/// <see cref="TagLibMetadataReader"/>, verifying idempotency and undo (Restore).
///
/// A minimal valid ID3v2-tagged MP3 is synthesised in memory so no audio fixture is
/// needed in the repo. TagLib# only needs a parseable tag header; the MPEG audio
/// frames that follow are silent/padding and are never decoded in these tests.
/// </summary>
public class DjCommentRoundTripTests : IDisposable
{
    private readonly string _tempFile;
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();

    public DjCommentRoundTripTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"justplay_test_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(_tempFile, MinimalMp3WithComment("my note"));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    // ── Helper: A minor = pitch class 9 → Camelot "8A" ───────────────────

    private static readonly MusicalKey AMinor = new(9, KeyMode.Minor);

    private static TagWrite WriteWithDjComment(MusicalKey? key, int? energy, string? existingComment)
    {
        var newComment = DjCommentBuilder.Build(key, energy, existingComment);
        return new TagWrite
        {
            Key    = key,
            Energy = energy,
            Comment = newComment,
        };
    }

    // ── Round-trip: write → reload → assert comment ────────────────────────

    [Fact]
    public void Write_WithDjCommentOn_PrependsCamelotAndEnergy()
    {
        // Existing comment in the file is "my note" (set in constructor).
        var before = _reader.Read(_tempFile);
        Assert.Equal("my note", before.Comment);

        var write = WriteWithDjComment(AMinor, 7, before.Comment);
        _writer.Write(_tempFile, write);

        var after = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", after.Comment);
    }

    [Fact]
    public void Write_Idempotent_SecondWriteDoesNotGrowComment()
    {
        // First write
        var md1 = _reader.Read(_tempFile);
        _writer.Write(_tempFile, WriteWithDjComment(AMinor, 7, md1.Comment));

        var md2 = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", md2.Comment);

        // Second write (identical analysis) must not change the comment.
        _writer.Write(_tempFile, WriteWithDjComment(AMinor, 7, md2.Comment));

        var md3 = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", md3.Comment);
    }

    [Fact]
    public void Restore_AfterWrite_RestoresOriginalComment()
    {
        // Capture pre-write state (as SnapshotOf does in the ViewModel).
        var before = _reader.Read(_tempFile);
        Assert.Equal("my note", before.Comment);

        var snapshot = new TagRestore
        {
            Comment = before.Comment,
            CommentCaptured = true,
        };

        // Write with DJ comment.
        _writer.Write(_tempFile, WriteWithDjComment(AMinor, 7, before.Comment));

        var afterWrite = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", afterWrite.Comment);

        // Undo (Restore).
        _writer.Restore(_tempFile, snapshot);

        var afterRestore = _reader.Read(_tempFile);
        Assert.Equal("my note", afterRestore.Comment);
    }

    [Fact]
    public void Restore_CommentNotCaptured_LeavesCommentUntouched()
    {
        // Write DJ comment.
        var before = _reader.Read(_tempFile);
        _writer.Write(_tempFile, WriteWithDjComment(AMinor, 7, before.Comment));

        var afterWrite = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", afterWrite.Comment);

        // Restore with CommentCaptured = false (feature was off) — comment must not change.
        var snapshot = new TagRestore { CommentCaptured = false };
        _writer.Restore(_tempFile, snapshot);

        var afterRestore = _reader.Read(_tempFile);
        Assert.Equal("8A - Energy 7 | my note", afterRestore.Comment);
    }

    [Fact]
    public void Write_SettingOff_CommentUntouched()
    {
        // When WriteDjComment is off, Comment in TagWrite is null → file comment untouched.
        var before = _reader.Read(_tempFile);
        Assert.Equal("my note", before.Comment);

        // Simulate setting OFF: no Comment in TagWrite.
        var write = new TagWrite { Key = AMinor, Energy = 7 }; // Comment = null
        _writer.Write(_tempFile, write);

        var after = _reader.Read(_tempFile);
        Assert.Equal("my note", after.Comment);
    }

    // ── Synthesise a minimal valid ID3v2-tagged MP3 ────────────────────────
    // Structure:
    //   ID3v2.3 header (10 bytes) + ID3v2 COMM frame with "my note"
    //   + zero-padded to fill declared tag size + a silent MPEG frame.
    //
    // TagLib# is lenient on the audio content; it reads tags first and only
    // probes audio properties on demand — which we never trigger here.

    private static byte[] MinimalMp3WithComment(string commentText)
    {
        // We'll build a proper ID3v2 tag using TagLib# itself so we don't have
        // to hand-craft frame bytes. Write to a temp buffer file, read it back.
        var buf = Path.Combine(Path.GetTempPath(), $"justplay_seed_{Guid.NewGuid():N}.mp3");
        try
        {
            // Start with a minimal MP3: ID3v2 header + one silent MPEG frame header.
            // MPEG1 Layer3 128kbps 44100Hz stereo = sync word FF FB 90 00 (4 bytes).
            var bare = new byte[10 + 4];
            // ID3v2 header: "ID3" + version 2.3.0 + flags 0 + syncsafe size (0 = 0 bytes of frames)
            bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
            bare[3] = 3; bare[4] = 0; bare[5] = 0; // version 2.3, rev 0, flags 0
            // syncsafe int: 0 (no frames yet — TagLib will extend on Save)
            bare[6] = 0; bare[7] = 0; bare[8] = 0; bare[9] = 0;
            // One silent MPEG frame header (sync + layer3 + 128kbps + 44100 + stereo)
            bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
            File.WriteAllBytes(buf, bare);

            // Now use TagLib to write the comment, producing a proper file.
            using var f = TagLib.File.Create(buf);
            f.Tag.Comment = commentText;
            f.Save();

            return File.ReadAllBytes(buf);
        }
        finally
        {
            if (File.Exists(buf)) File.Delete(buf);
        }
    }
}
