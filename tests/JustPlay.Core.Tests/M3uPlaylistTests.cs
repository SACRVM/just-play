using System;
using System.IO;
using System.Linq;
using JustPlay.Core.Playlists;
using Xunit;

namespace JustPlay.Core.Tests;

public class M3uPlaylistTests
{
    [Theory]
    [InlineData("set.m3u8", true)]
    [InlineData("SET.M3U", true)]
    [InlineData("song.mp3", false)]
    [InlineData(null, false)]
    public void IsPlaylist_DetectsByExtension(string? path, bool expected)
        => Assert.Equal(expected, M3uPlaylist.IsPlaylist(path));

    [Fact]
    public void Write_AlwaysRelativeForwardSlash_IncludingParent_CrlfAndRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("jp-m3u").FullName;
        var outsideDir = Directory.CreateTempSubdirectory("jp-m3u-out").FullName;
        try
        {
            // One track UNDER the save folder, one in a SIBLING folder (the real SETS→GENRES case).
            var under = Path.Combine(dir, "a.mp3");
            var outside = Path.Combine(outsideDir, "b.mp3");
            File.WriteAllText(under, "");
            File.WriteAllText(outside, "");

            var m3u = Path.Combine(dir, "set.m3u8");
            M3uPlaylist.Write(m3u,
            [
                new M3uPlaylist.Entry(under, TimeSpan.FromSeconds(100), "Artist - A"),
                new M3uPlaylist.Entry(outside, null, "Artist - B"),
            ]);

            var text = File.ReadAllText(m3u);
            Assert.Contains("\r\n", text);                              // CRLF line endings (Traktor/Mac)
            var lines = text.Replace("\r", "").Split('\n');
            Assert.Equal("#EXTM3U", lines[0]);
            Assert.Equal("a.mp3", lines[2]);                            // under → relative
            // sibling → RELATIVE with "../" + forward slashes (NEVER absolute) — the fix.
            var expectedSibling = Path.GetRelativePath(dir, outside).Replace('\\', '/');
            Assert.StartsWith("../", expectedSibling);
            Assert.Equal(expectedSibling, lines[4]);
            Assert.DoesNotContain("\\", lines[4]);                      // forward slashes only

            // Read back: both resolve to their absolute locations, order preserved.
            var paths = M3uPlaylist.ReadPaths(m3u);
            Assert.Equal(2, paths.Count);
            Assert.Equal(Path.GetFullPath(under), paths[0]);
            Assert.Equal(Path.GetFullPath(outside), paths[1]);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(outsideDir, true);
        }
    }

    [Fact]
    public void ReadPaths_SkipsCommentsBlanksAndMissingFiles()
    {
        var dir = Directory.CreateTempSubdirectory("jp-m3u2").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3");
            File.WriteAllText(real, "");
            var m3u = Path.Combine(dir, "p.m3u8");
            File.WriteAllText(m3u, $"#EXTM3U\n\n#EXTINF:1,x\nreal.mp3\nmissing.mp3\n");

            var paths = M3uPlaylist.ReadPaths(m3u);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
