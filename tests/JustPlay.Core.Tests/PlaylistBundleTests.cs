using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using JustPlay.Core.Playlists;
using Xunit;

namespace JustPlay.Core.Tests;

public class PlaylistBundleTests
{
    [Fact]
    public void WriteToFolder_NumbersInSetOrder_CollisionFree_AndM3uRoundTrips()
    {
        var srcA = Directory.CreateTempSubdirectory("jp-bundle-a").FullName;
        var srcB = Directory.CreateTempSubdirectory("jp-bundle-b").FullName;
        var destParent = Directory.CreateTempSubdirectory("jp-bundle-dest").FullName;
        try
        {
            // Two files with the SAME filename in different folders - would collide without numbering.
            var a = Path.Combine(srcA, "track.mp3"); File.WriteAllText(a, "AAA");
            var b = Path.Combine(srcB, "track.mp3"); File.WriteAllText(b, "BBB");

            var dest = Path.Combine(destParent, "set");
            var n = PlaylistBundle.WriteToFolder(dest, "MySet",
            [
                new PlaylistBundle.Entry(a, TimeSpan.FromSeconds(60), "Artist - A"),
                new PlaylistBundle.Entry(b, null, "Artist - B"),
            ]);

            Assert.Equal(2, n);
            // Numbered, collision-free, set order preserved - and the bytes are the right file's.
            Assert.Equal("AAA", File.ReadAllText(Path.Combine(dest, "01 - track.mp3")));
            Assert.Equal("BBB", File.ReadAllText(Path.Combine(dest, "02 - track.mp3")));

            // m3u8 written next to them, round-trips to the copied files in order.
            var m3u = Path.Combine(dest, "MySet.m3u8");
            Assert.True(File.Exists(m3u));
            var paths = M3uPlaylist.ReadPaths(m3u);
            Assert.Equal(2, paths.Count);
            Assert.Equal(Path.GetFullPath(Path.Combine(dest, "01 - track.mp3")), paths[0]);
            Assert.Equal(Path.GetFullPath(Path.Combine(dest, "02 - track.mp3")), paths[1]);
        }
        finally
        {
            Directory.Delete(srcA, true);
            Directory.Delete(srcB, true);
            Directory.Delete(destParent, true);
        }
    }

    [Fact]
    public void WriteToFolder_PadsToThreeDigits_ForHundredTracks()
    {
        var src = Directory.CreateTempSubdirectory("jp-pad-src").FullName;
        var destParent = Directory.CreateTempSubdirectory("jp-pad-dest").FullName;
        try
        {
            var entries = new List<PlaylistBundle.Entry>();
            for (var i = 0; i < 100; i++)
            {
                var f = Path.Combine(src, $"s{i}.mp3");
                File.WriteAllText(f, "x");
                entries.Add(new PlaylistBundle.Entry(f, null, $"T{i}"));
            }

            var dest = Path.Combine(destParent, "big");
            var n = PlaylistBundle.WriteToFolder(dest, "big", entries);

            Assert.Equal(100, n);
            Assert.True(File.Exists(Path.Combine(dest, "001 - s0.mp3")));   // padded to three digits
            Assert.True(File.Exists(Path.Combine(dest, "100 - s99.mp3")));
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(destParent, true);
        }
    }

    [Fact]
    public void WriteZip_ContainsNumberedAudio_PlusPlaylist()
    {
        var src = Directory.CreateTempSubdirectory("jp-zip-src").FullName;
        var destDir = Directory.CreateTempSubdirectory("jp-zip-dest").FullName;
        try
        {
            var a = Path.Combine(src, "one.mp3"); File.WriteAllText(a, "11");
            var b = Path.Combine(src, "two.mp3"); File.WriteAllText(b, "22");
            var zipPath = Path.Combine(destDir, "set.zip");

            var n = PlaylistBundle.WriteZip(zipPath, "set",
            [
                new PlaylistBundle.Entry(a, null, "A"),
                new PlaylistBundle.Entry(b, null, "B"),
            ]);

            Assert.Equal(2, n);
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("01 - one.mp3", names);
            Assert.Contains("02 - two.mp3", names);
            Assert.Contains("set.m3u8", names);
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(destDir, true);
        }
    }

    [Fact]
    public void WriteToFolder_SkipsMissingSources()
    {
        var src = Directory.CreateTempSubdirectory("jp-miss-src").FullName;
        var destParent = Directory.CreateTempSubdirectory("jp-miss-dest").FullName;
        try
        {
            var real = Path.Combine(src, "real.mp3"); File.WriteAllText(real, "r");
            var gone = Path.Combine(src, "gone.mp3"); // never created

            var dest = Path.Combine(destParent, "set");
            var n = PlaylistBundle.WriteToFolder(dest, "set",
            [
                new PlaylistBundle.Entry(gone, null, "missing"),
                new PlaylistBundle.Entry(real, null, "real"),
            ]);

            Assert.Equal(1, n);
            // Only the present file is numbered (01), and the playlist references just it.
            Assert.True(File.Exists(Path.Combine(dest, "01 - real.mp3")));
            Assert.Single(M3uPlaylist.ReadPaths(Path.Combine(dest, "set.m3u8")));
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(destParent, true);
        }
    }
}
