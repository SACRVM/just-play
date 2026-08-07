using System;
using System.IO;
using JustPlay.Core.Playlists;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// N25: PLS + XSPF playlist import, mirroring the coverage <see cref="M3uPlaylistTests"/> has for M3U -
/// happy path with order preserved, relative-path resolution against the playlist's own folder,
/// malformed/blank entries skipped, "file://" URIs (incl. UNC) resolved to local paths, "http(s)://"
/// entries skipped (LOCAL library importer only), empty playlist -> empty list, and the
/// <see cref="Playlist"/> facade dispatching each extension to the right parser.
/// </summary>
public class PlaylistImportTests
{
    // -- IsPlaylist / facade extension detection -----------------------------

    [Theory]
    [InlineData("set.pls", true)]
    [InlineData("SET.PLS", true)]
    [InlineData("song.mp3", false)]
    [InlineData(null, false)]
    public void Pls_IsPlaylist_DetectsByExtension(string? path, bool expected)
        => Assert.Equal(expected, PlsPlaylist.IsPlaylist(path));

    [Theory]
    [InlineData("set.xspf", true)]
    [InlineData("SET.XSPF", true)]
    [InlineData("song.mp3", false)]
    [InlineData(null, false)]
    public void Xspf_IsPlaylist_DetectsByExtension(string? path, bool expected)
        => Assert.Equal(expected, XspfPlaylist.IsPlaylist(path));

    [Theory]
    [InlineData("set.m3u8", true)]
    [InlineData("set.m3u", true)]
    [InlineData("set.pls", true)]
    [InlineData("set.xspf", true)]
    [InlineData("song.mp3", false)]
    [InlineData(null, false)]
    public void Facade_IsPlaylist_DetectsAnyRecognizedFormat(string? path, bool expected)
        => Assert.Equal(expected, Playlist.IsPlaylist(path));

    // -- PLS ------------------------------------------------------------------

    [Fact]
    public void Pls_ReadPaths_OrderIsByFileIndex_NotLineOrder()
    {
        var dir = Directory.CreateTempSubdirectory("jp-pls").FullName;
        try
        {
            var a = Path.Combine(dir, "a.mp3"); File.WriteAllText(a, "");
            var b = Path.Combine(dir, "b.mp3"); File.WriteAllText(b, "");
            var c = Path.Combine(dir, "c.mp3"); File.WriteAllText(c, "");

            // Lines deliberately out of order on disk (File3, then File1, then File2) - N must win.
            var pls = Path.Combine(dir, "set.pls");
            File.WriteAllText(pls,
                "[playlist]\n" +
                "NumberOfEntries=3\n" +
                "File3=c.mp3\n" +
                "Title3=C\n" +
                "File1=a.mp3\n" +
                "Title1=A\n" +
                "File2=b.mp3\n" +
                "Title2=B\n" +
                "Version=2\n");

            var paths = PlsPlaylist.ReadPaths(pls);
            Assert.Equal(3, paths.Count);
            Assert.Equal(Path.GetFullPath(a), paths[0]);
            Assert.Equal(Path.GetFullPath(b), paths[1]);
            Assert.Equal(Path.GetFullPath(c), paths[2]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pls_ReadPaths_SkipsBlankMalformedHttpAndMissingFiles()
    {
        var dir = Directory.CreateTempSubdirectory("jp-pls2").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");

            var pls = Path.Combine(dir, "set.pls");
            File.WriteAllText(pls,
                "[playlist]\n" +
                "NumberOfEntries=3\n" +
                "File1=real.mp3\n" +
                "\n" +                                   // blank line
                "this line has no equals sign\n" +        // malformed - no key=value shape
                "File2=http://example.com/stream.mp3\n" + // remote - skipped
                "Title2=Radio\n" +
                "File3=missing.mp3\n" +                   // never created - filtered like M3U does
                "Version=2\n");

            var paths = PlsPlaylist.ReadPaths(pls);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pls_ReadPaths_FileUri_ResolvesToLocalPath()
    {
        var dir = Directory.CreateTempSubdirectory("jp-pls3").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");
            var fileUri = new Uri(real).AbsoluteUri; // e.g. file:///C:/Users/.../real.mp3

            var pls = Path.Combine(dir, "set.pls");
            File.WriteAllText(pls, $"[playlist]\nNumberOfEntries=1\nFile1={fileUri}\nVersion=2\n");

            var paths = PlsPlaylist.ReadPaths(pls);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0], ignoreCase: true);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pls_ReadPaths_EmptyPlaylist_ReturnsEmptyList()
    {
        var dir = Directory.CreateTempSubdirectory("jp-pls4").FullName;
        try
        {
            var pls = Path.Combine(dir, "empty.pls");
            File.WriteAllText(pls, "[playlist]\nNumberOfEntries=0\nVersion=2\n");

            Assert.Empty(PlsPlaylist.ReadPaths(pls));
        }
        finally { Directory.Delete(dir, true); }
    }

    // -- XSPF -----------------------------------------------------------------

    [Fact]
    public void Xspf_ReadPaths_HappyPath_OrderPreserved()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf").FullName;
        try
        {
            var a = Path.Combine(dir, "a.mp3"); File.WriteAllText(a, "");
            var b = Path.Combine(dir, "b.mp3"); File.WriteAllText(b, "");
            var c = Path.Combine(dir, "c.mp3"); File.WriteAllText(c, "");

            var xspf = Path.Combine(dir, "set.xspf");
            File.WriteAllText(xspf,
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <playlist version="1" xmlns="http://xspf.org/ns/0/">
                  <trackList>
                    <track><location>a.mp3</location></track>
                    <track><location>b.mp3</location></track>
                    <track><location>c.mp3</location></track>
                  </trackList>
                </playlist>
                """);

            var paths = XspfPlaylist.ReadPaths(xspf);
            Assert.Equal(3, paths.Count);
            Assert.Equal(Path.GetFullPath(a), paths[0]);
            Assert.Equal(Path.GetFullPath(b), paths[1]);
            Assert.Equal(Path.GetFullPath(c), paths[2]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_SkipsBlankMalformedHttpAndMissingFiles()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf2").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");

            var xspf = Path.Combine(dir, "set.xspf");
            File.WriteAllText(xspf,
                """
                <playlist xmlns="http://xspf.org/ns/0/">
                  <trackList>
                    <track><location>real.mp3</location></track>
                    <track><location></location></track>
                    <track><title>No location element in this one</title></track>
                    <track><location>http://example.com/stream.mp3</location></track>
                    <track><location>missing.mp3</location></track>
                  </trackList>
                </playlist>
                """);

            var paths = XspfPlaylist.ReadPaths(xspf);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_TakesFirstLocationOnly()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf3").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");
            var other = Path.Combine(dir, "other.mp3"); File.WriteAllText(other, "");

            var xspf = Path.Combine(dir, "set.xspf");
            File.WriteAllText(xspf,
                """
                <playlist xmlns="http://xspf.org/ns/0/">
                  <trackList>
                    <track>
                      <location>real.mp3</location>
                      <location>other.mp3</location>
                    </track>
                  </trackList>
                </playlist>
                """);

            var paths = XspfPlaylist.ReadPaths(xspf);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_FileUri_ResolvesToLocalPath()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf4").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");
            var fileUri = new Uri(real).AbsoluteUri;

            var xspf = Path.Combine(dir, "set.xspf");
            File.WriteAllText(xspf,
                $"""
                <playlist xmlns="http://xspf.org/ns/0/">
                  <trackList>
                    <track><location>{fileUri}</location></track>
                  </trackList>
                </playlist>
                """);

            var paths = XspfPlaylist.ReadPaths(xspf);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0], ignoreCase: true);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_EmptyPlaylist_ReturnsEmptyList()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf5").FullName;
        try
        {
            var xspf = Path.Combine(dir, "empty.xspf");
            File.WriteAllText(xspf,
                """<playlist xmlns="http://xspf.org/ns/0/"><trackList></trackList></playlist>""");

            Assert.Empty(XspfPlaylist.ReadPaths(xspf));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_NamespaceTolerant_NoXmlnsDeclared()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf6").FullName;
        try
        {
            var real = Path.Combine(dir, "real.mp3"); File.WriteAllText(real, "");

            var xspf = Path.Combine(dir, "set.xspf");
            // No xmlns at all - a lazy/older exporter. Must still resolve by local-name matching.
            File.WriteAllText(xspf,
                """<playlist><trackList><track><location>real.mp3</location></track></trackList></playlist>""");

            var paths = XspfPlaylist.ReadPaths(xspf);
            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(real), paths[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Xspf_ReadPaths_MalformedXml_ReturnsEmptyList_NeverThrows()
    {
        var dir = Directory.CreateTempSubdirectory("jp-xspf7").FullName;
        try
        {
            var xspf = Path.Combine(dir, "broken.xspf");
            // Missing closing tags - not well-formed XML.
            File.WriteAllText(xspf, "<playlist><trackList><track><location>real.mp3</location>");

            var ex = Record.Exception(() => XspfPlaylist.ReadPaths(xspf));
            Assert.Null(ex);
            Assert.Empty(XspfPlaylist.ReadPaths(xspf));
        }
        finally { Directory.Delete(dir, true); }
    }

    // -- PlaylistUriResolver (internal - direct coverage of the URI edge cases) -

    [Fact]
    public void UriResolver_UncFileUri_ResolvesToUncPath()
    {
        // file://host/share/track.mp3 -> \\host\share\track.mp3 (Uri.LocalPath's own documented UNC
        // behaviour). Tested directly since we don't need a REAL reachable UNC host to exercise it -
        // ResolveLocalPath itself does no existence check (only the callers filter by File.Exists).
        var resolved = PlaylistUriResolver.ResolveLocalPath("file://host/share/track.mp3", baseDir: "");
        Assert.Equal(@"\\host\share\track.mp3", resolved);
    }

    [Fact]
    public void UriResolver_HttpUri_ReturnsNull()
    {
        Assert.Null(PlaylistUriResolver.ResolveLocalPath("https://example.com/stream.mp3", baseDir: ""));
        Assert.Null(PlaylistUriResolver.ResolveLocalPath("http://example.com/stream.mp3", baseDir: ""));
    }

    [Fact]
    public void UriResolver_RelativePath_ResolvesAgainstBaseDir()
    {
        var baseDir = @"C:\Music\GENRES";
        var resolved = PlaylistUriResolver.ResolveLocalPath("track.mp3", baseDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "track.mp3")), resolved);
    }

    // -- Facade dispatch ------------------------------------------------------

    [Fact]
    public void Facade_ReadPaths_DispatchesEachExtensionToItsParser()
    {
        var dir = Directory.CreateTempSubdirectory("jp-facade").FullName;
        try
        {
            var track = Path.Combine(dir, "track.mp3"); File.WriteAllText(track, "");

            var m3u = Path.Combine(dir, "set.m3u8");
            File.WriteAllText(m3u, "#EXTM3U\ntrack.mp3\n");

            var pls = Path.Combine(dir, "set.pls");
            File.WriteAllText(pls, "[playlist]\nNumberOfEntries=1\nFile1=track.mp3\nVersion=2\n");

            var xspf = Path.Combine(dir, "set.xspf");
            File.WriteAllText(xspf,
                """<playlist xmlns="http://xspf.org/ns/0/"><trackList><track><location>track.mp3</location></track></trackList></playlist>""");

            var expected = Path.GetFullPath(track);
            Assert.Equal(new[] { expected }, Playlist.ReadPaths(m3u));
            Assert.Equal(new[] { expected }, Playlist.ReadPaths(pls));
            Assert.Equal(new[] { expected }, Playlist.ReadPaths(xspf));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Facade_ReadPaths_UnrecognizedExtension_ReturnsEmptyList_NeverThrows()
    {
        var dir = Directory.CreateTempSubdirectory("jp-facade2").FullName;
        try
        {
            var notAPlaylist = Path.Combine(dir, "notes.txt");
            File.WriteAllText(notAPlaylist, "just some text\n");

            var ex = Record.Exception(() => Playlist.ReadPaths(notAPlaylist));
            Assert.Null(ex);
            Assert.Empty(Playlist.ReadPaths(notAPlaylist));
        }
        finally { Directory.Delete(dir, true); }
    }
}
