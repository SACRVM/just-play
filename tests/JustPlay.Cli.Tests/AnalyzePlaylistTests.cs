using JustPlay.Cli.Commands;
using JustPlay.Core.Playlists;

namespace JustPlay.Cli.Tests;

/// <summary>
/// Tests for <c>justplay analyze --playlist &lt;m3u&gt;</c>: the argument rules of the two input
/// forms, and the list loading that decides which listed files are analysed, which are reported as
/// MISSING, and which database (if any) the run may touch.
///
/// <para>Purely managed - the list is loaded and classified without opening a single audio file,
/// so no BASS and no DSP is involved here.</para>
/// </summary>
public sealed class AnalyzePlaylistTests
{
    // =====================================================================
    // Argument parsing - which form the command line asks for
    // =====================================================================

    [Fact]
    public void Parse_PlaylistWithoutRoot_IsAccepted_AndCarriesNoRoot()
    {
        var a = AnalyzeArgs.Parse(["--playlist", "redo.m3u", "--index", "lib.json"], out var error);

        Assert.Null(error);
        Assert.NotNull(a);
        Assert.True(a!.UsesPlaylist);
        Assert.Equal("redo.m3u", a.Playlist);
        Assert.Null(a.Root);                 // no root invented for the list
        Assert.Equal("lib.json", a.IndexPath);
    }

    [Fact]
    public void Parse_PlaylistWithRoot_KeepsBoth_PlaylistStillWins()
    {
        var a = AnalyzeArgs.Parse(
            ["--playlist", "redo.m3u", "--root", "D:\\Music", "--index", "lib.json"], out var error);

        Assert.Null(error);
        Assert.NotNull(a);
        Assert.True(a!.UsesPlaylist);        // the list selects the files
        Assert.Equal("D:\\Music", a.Root);   // the root only picks the database
    }

    [Fact]
    public void Parse_PositionalRoot_IsTheDirectoryForm()
    {
        var a = AnalyzeArgs.Parse(["D:\\Music", "--index", "lib.json", "--threads", "4"], out var error);

        Assert.Null(error);
        Assert.NotNull(a);
        Assert.False(a!.UsesPlaylist);
        Assert.Equal("D:\\Music", a.Root);
        Assert.Null(a.Playlist);
        Assert.Equal(4, a.Threads);
    }

    [Fact]
    public void Parse_PositionalRootPlusPlaylist_StillUsesThePlaylist()
    {
        var a = AnalyzeArgs.Parse(
            ["D:\\Music", "--playlist", "redo.m3u", "--index", "lib.json"], out var error);

        Assert.Null(error);
        Assert.NotNull(a);
        Assert.True(a!.UsesPlaylist);
        Assert.Equal("D:\\Music", a.Root);
    }

    [Fact]
    public void Parse_NeitherRootNorPlaylist_Fails()
    {
        var a = AnalyzeArgs.Parse(["--index", "lib.json"], out var error);

        Assert.Null(a);
        Assert.NotNull(error);
        Assert.Contains("--playlist", error!);
    }

    [Fact]
    public void Parse_WithoutIndex_Fails_InBothForms()
    {
        Assert.Null(AnalyzeArgs.Parse(["--playlist", "redo.m3u"], out var listError));
        Assert.Contains("--index", listError!);

        Assert.Null(AnalyzeArgs.Parse(["D:\\Music"], out var dirError));
        Assert.Contains("--index", dirError!);
    }

    [Fact]
    public void Parse_Switches_AndDefaults()
    {
        var bare = AnalyzeArgs.Parse(["--playlist", "p.m3u", "--index", "i.json"], out _)!;
        Assert.False(bare.Force);
        Assert.False(bare.NoDb);
        Assert.Null(bare.DbPath);
        Assert.Equal(int.MaxValue, bare.Limit);
        Assert.True(bare.Threads > 0);

        var loaded = AnalyzeArgs.Parse(
            ["--playlist", "p.m3u", "--index", "i.json", "--force", "--no-db",
             "--db", "x.db", "--limit", "12"], out _)!;
        Assert.True(loaded.Force);
        Assert.True(loaded.NoDb);
        Assert.Equal("x.db", loaded.DbPath);
        Assert.Equal(12, loaded.Limit);
    }

    // =====================================================================
    // Loading the list - nothing is dropped in silence
    // =====================================================================

    [Fact]
    public void LoadPlaylist_MissingFileIsReportedByName_NotDropped()
    {
        var dir = Directory.CreateTempSubdirectory("jp-analyze-list").FullName;
        try
        {
            var here  = Path.Combine(dir, "here.mp3");
            var gone  = Path.Combine(dir, "gone.mp3");
            File.WriteAllText(here, "");

            var m3u = Path.Combine(dir, "redo.m3u");
            File.WriteAllText(m3u, "#EXTM3U\r\n#EXTINF:1,x\r\nhere.mp3\r\ngone.mp3\r\n");

            var batch = AnalyzeCommand.LoadPlaylist(m3u);

            Assert.Equal(2, batch.Listed);                       // both counted as "from the list"
            Assert.Single(batch.Files);
            Assert.Equal(here, batch.Files[0].Path);
            Assert.Single(batch.Missing);
            Assert.Equal(gone, batch.Missing[0]);                // named, so she can go find it
            Assert.Empty(batch.NotAudio);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadPlaylist_CarriesTheCheapKey_SoResumeWorksAsInTheDirectoryForm()
    {
        var dir = Directory.CreateTempSubdirectory("jp-analyze-key").FullName;
        try
        {
            var track = Path.Combine(dir, "track.mp3");
            File.WriteAllBytes(track, new byte[1234]);

            var m3u = Path.Combine(dir, "one.m3u");
            File.WriteAllText(m3u, "track.mp3\r\n");

            var scanned = Assert.Single(AnalyzeCommand.LoadPlaylist(m3u).Files);
            Assert.Equal(1234, scanned.SizeBytes);
            Assert.Equal(DateTimeKind.Utc, scanned.ModifiedUtc.Kind);
            Assert.Equal(new FileInfo(track).LastWriteTimeUtc, scanned.ModifiedUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadPlaylist_UncPathSurvivesVerbatim_AndDuplicatesCollapse()
    {
        // A host that cannot exist - the point is that the SPELLING survives resolution, and an
        // unreachable path lands in MISSING rather than vanishing. Never a real share.
        const string unc = @"\\jp-no-such-host-9f2a\music\GENRES\Hardcore\Missing Track.mp3";

        var dir = Directory.CreateTempSubdirectory("jp-analyze-unc").FullName;
        try
        {
            var m3u = Path.Combine(dir, "unc.m3u");
            File.WriteAllText(m3u, $"#EXTM3U\r\n{unc}\r\n{unc}\r\n");

            var batch = AnalyzeCommand.LoadPlaylist(m3u);

            Assert.Equal(1, batch.Listed);                       // the duplicate line collapses
            Assert.Empty(batch.Files);
            Assert.Equal(unc, Assert.Single(batch.Missing));     // backslashes and all
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolvePaths_KeepsAUncLineExactly()
    {
        // The parse half of the same guarantee, with no file system involved at all.
        const string unc = @"\\nas\music\GENRES\Techno\Track.flac";

        var dir = Directory.CreateTempSubdirectory("jp-analyze-unc2").FullName;
        try
        {
            var m3u = Path.Combine(dir, "unc.m3u");
            File.WriteAllText(m3u, $"#EXTM3U\r\n{unc}\r\n");

            Assert.Equal(unc, Assert.Single(M3uPlaylist.ResolvePaths(m3u)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadPlaylist_EmptyList_YieldsNothingAndNoMissing()
    {
        var dir = Directory.CreateTempSubdirectory("jp-analyze-empty").FullName;
        try
        {
            var m3u = Path.Combine(dir, "empty.m3u");
            File.WriteAllText(m3u, "#EXTM3U\r\n\r\n");

            var batch = AnalyzeCommand.LoadPlaylist(m3u);

            Assert.Equal(0, batch.Listed);
            Assert.Empty(batch.Files);
            Assert.Empty(batch.Missing);
            Assert.Empty(batch.NotAudio);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadPlaylist_GarbageList_ClassifiesEveryLine_NeverThrows()
    {
        var dir = Directory.CreateTempSubdirectory("jp-analyze-garbage").FullName;
        try
        {
            var cover = Path.Combine(dir, "cover.jpg");     // exists, but is not audio
            File.WriteAllText(cover, "");

            var m3u = Path.Combine(dir, "garbage.m3u");
            File.WriteAllText(m3u,
                "this is not a path\r\n" +
                "cover.jpg\r\n" +
                "nope.mp3\r\n" +
                "\r\n" +
                "# a comment\r\n");

            var batch = AnalyzeCommand.LoadPlaylist(m3u);

            Assert.Equal(3, batch.Listed);                          // comment + blank are not entries
            Assert.Empty(batch.Files);                              // nothing analysable
            Assert.Equal(Path.Combine(dir, "nope.mp3"), Assert.Single(batch.Missing));
            Assert.Equal(2, batch.NotAudio.Count);                  // the prose line and the cover
            Assert.Contains(cover, batch.NotAudio);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadPlaylist_SortsByPath_SoLimitAndResumeAreDeterministic()
    {
        var dir = Directory.CreateTempSubdirectory("jp-analyze-order").FullName;
        try
        {
            foreach (var name in new[] { "c.mp3", "a.mp3", "b.mp3" })
                File.WriteAllText(Path.Combine(dir, name), "");

            var m3u = Path.Combine(dir, "order.m3u");
            File.WriteAllText(m3u, "c.mp3\r\na.mp3\r\nb.mp3\r\n");

            var batch = AnalyzeCommand.LoadPlaylist(m3u);

            Assert.Equal(
                ["a.mp3", "b.mp3", "c.mp3"],
                batch.Files.Select(f => Path.GetFileName(f.Path)));
        }
        finally { Directory.Delete(dir, true); }
    }

    // =====================================================================
    // Which library database a rootless list may touch
    // =====================================================================

    [Fact]
    public void RegistryRootCovering_AllFilesUnderOneRegisteredRoot_ReturnsThatRoot()
    {
        string[] roots = [@"\\nas\music\GENRES", @"D:\Local"];
        string[] files =
        [
            @"\\nas\music\GENRES\Techno\a.mp3",
            @"\\nas\music\GENRES\Hardcore\Sub\b.flac",
        ];

        Assert.Equal(@"\\nas\music\GENRES", AnalyzeCommand.RegistryRootCovering(files, roots));
    }

    [Fact]
    public void RegistryRootCovering_FilesSpanTwoRoots_ReturnsNull()
    {
        string[] roots = [@"\\nas\music\GENRES", @"D:\Local"];
        string[] files = [@"\\nas\music\GENRES\Techno\a.mp3", @"D:\Local\b.mp3"];

        // Two roots means two databases; picking one would put a folder's index out of step
        // with its own folder, so the run stays index-only.
        Assert.Null(AnalyzeCommand.RegistryRootCovering(files, roots));
    }

    [Fact]
    public void RegistryRootCovering_OneFileOutsideEveryRoot_ReturnsNull()
    {
        string[] roots = [@"\\nas\music\GENRES"];
        string[] files = [@"\\nas\music\GENRES\Techno\a.mp3", @"C:\Downloads\b.mp3"];

        Assert.Null(AnalyzeCommand.RegistryRootCovering(files, roots));
    }

    [Fact]
    public void RegistryRootCovering_NothingRegistered_ReturnsNull()
    {
        Assert.Null(AnalyzeCommand.RegistryRootCovering([@"\\nas\music\GENRES\a.mp3"], []));
        Assert.Null(AnalyzeCommand.RegistryRootCovering([], [@"\\nas\music\GENRES"]));
    }
}
