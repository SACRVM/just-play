using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// <see cref="JustPlay.Core.Models.TrackMetadata.ReadFailed"/> separates "could not read the file"
/// from "read it, nothing usable in it". Both return the same bare record, so without the flag a
/// locked file and an untagged file are indistinguishable - and a caller that persists the read
/// turns a momentary lock into blank columns or empty fields written back.
/// </summary>
public class ReadFailedTests : IDisposable
{
    private readonly string _tempFile =
        Path.Combine(Path.GetTempPath(), $"justplay_readfail_{Guid.NewGuid():N}.mp3");
    private readonly TagLibMetadataReader _reader = new();

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void A_file_locked_by_another_process_is_a_failed_read()
    {
        File.WriteAllBytes(_tempFile, [0xFF, 0xFB, 0x90, 0x00]);

        // FileShare.None is what an exclusive writer (another tag editor, a copy in progress) holds.
        using (new FileStream(_tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var meta = _reader.Read(_tempFile);

            Assert.True(meta.ReadFailed);
            Assert.Null(meta.Title);
        }
    }

    [Fact]
    public void A_vanished_file_is_a_failed_read()
    {
        var meta = _reader.Read(_tempFile);   // never created

        Assert.True(meta.ReadFailed);
    }

    [Fact]
    public void A_file_that_is_not_audio_is_read_fine_and_simply_carries_nothing()
    {
        // Unsupported / corrupt content is a property of the FILE, not of this moment - it must stay
        // indexable as "not analysed" rather than being retried and skipped forever.
        File.WriteAllText(_tempFile, "this is not an mp3");

        var meta = _reader.Read(_tempFile);

        Assert.False(meta.ReadFailed);
    }
}
