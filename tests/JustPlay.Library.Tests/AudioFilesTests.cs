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

        // GetFileName!() — the paths come from an enumeration of real files, so a null name is not a
        // case this test can hit; without it the compiler infers string?[] and CS8631 fires on Equal.
        var found = AudioFiles.Enumerate(_root).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();

        Assert.Equal(["track.aiff", "track.flac", "track.mp3"], found);
    }

    [Theory]
    [InlineData(@"C:\m\a.mp3",   true)]
    [InlineData(@"C:\m\a.FLAC",  true)]
    [InlineData(@"C:\m\a.aiff",  true)]
    // .opus is here because the APP has always listed it. The two lists had drifted, and the day
    // the finder started reading from the index every .opus file would have silently vanished from
    // it. (Playing one still needs the bassopus decoder plugin, which is not vendored.)
    [InlineData(@"C:\m\a.opus",  true)]
    [InlineData(@"C:\m\._a.mp3", false)]
    [InlineData(@"C:\m\a.jpg",   false)]
    [InlineData(@"C:\m\a.m3u",   false)]
    public void IsAudio_agrees_with_the_enumeration(string path, bool expected) =>
        Assert.Equal(expected, AudioFiles.IsAudio(path));

    [Fact]
    public void EnumerateWithKeys_can_stay_in_one_folder()
    {
        var sub = Path.Combine(_root, "Sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_root, "here.mp3"), "x");
        File.WriteAllText(Path.Combine(sub, "below.mp3"), "x");

        var shallow = AudioFiles.EnumerateWithKeys(_root, recursive: false)
            .Select(f => Path.GetFileName(f.Path)).ToArray();
        var deep = AudioFiles.EnumerateWithKeys(_root)
            .Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray();

        Assert.Equal(["here.mp3"], shallow);
        Assert.Equal(["below.mp3", "here.mp3"], deep);
    }

    [Fact]
    public void EnumerateWithKeys_reports_size_and_mtime_without_a_second_look()
    {
        var path = Path.Combine(_root, "track.mp3");
        File.WriteAllText(path, "0123456789");

        var scanned = Assert.Single(AudioFiles.EnumerateWithKeys(_root));

        Assert.Equal(new FileInfo(path).Length, scanned.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(path), scanned.ModifiedUtc, TimeSpan.FromSeconds(1));
    }
}
