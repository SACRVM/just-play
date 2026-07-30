using System.IO;
using System.Linq;

namespace JustPlay.Library.Tests;

/// <summary>
/// What counts as a track. The app, the finder, the CLI and JUST TAG share this enumeration —
/// if it drifts, two front-ends disagree about the size of the same library.
/// </summary>
public sealed class AudioFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-files-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public AudioFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Enumerate_takes_audio_recursively_and_skips_the_rest()
    {
        var genres = Path.Combine(_root, "GENRES", "Techno");
        Directory.CreateDirectory(genres);

        File.WriteAllText(Path.Combine(genres, "track.mp3"), "x");
        File.WriteAllText(Path.Combine(genres, "track.flac"), "x");
        File.WriteAllText(Path.Combine(genres, "track.aiff"), "x");
        File.WriteAllText(Path.Combine(genres, "._track.aiff"), "x");  // macOS resource fork
        File.WriteAllText(Path.Combine(genres, "cover.jpg"), "x");
        File.WriteAllText(Path.Combine(_root, "list.m3u"), "x");

        var found = AudioFiles.Enumerate(_root).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        Assert.Equal(["track.aiff", "track.flac", "track.mp3"], found);
    }

    [Theory]
    [InlineData(@"C:\m\a.mp3",   true)]
    [InlineData(@"C:\m\a.FLAC",  true)]
    [InlineData(@"C:\m\a.aiff",  true)]
    [InlineData(@"C:\m\._a.mp3", false)]
    [InlineData(@"C:\m\a.jpg",   false)]
    [InlineData(@"C:\m\a.m3u",   false)]
    public void IsAudio_agrees_with_the_enumeration(string path, bool expected) =>
        Assert.Equal(expected, AudioFiles.IsAudio(path));
}
