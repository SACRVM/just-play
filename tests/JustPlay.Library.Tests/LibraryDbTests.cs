using System.IO;
using System.Linq;

namespace JustPlay.Library.Tests;

/// <summary>
/// The per-machine index: round-trip, upsert semantics, the set-building query surface, the
/// never-drop-a-track rule, and interchange with the CLI's index JSON.
/// </summary>
public sealed class LibraryDbTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "jp-db-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDb _db;

    public LibraryDbTests()
    {
        Directory.CreateDirectory(_tmp);
        _db = LibraryDb.Open(Path.Combine(_tmp, "index.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static TrackIndexEntry Track(
        string path,
        double? bpm = 128, string? camelot = "8A", int? energy = 7,
        string? artist = "Artist", string? title = "Title",
        double? harshness = 0.2, double? grid = 0.8, bool success = true) =>
        new()
        {
            FilePath         = path,
            ContentHash      = "hash-" + Path.GetFileNameWithoutExtension(path),
            AnalysedAt       = "2026-07-30T12:00:00.0000000Z",
            DetectionVersion = TrackIndex.CurrentDetectionVersion,
            FileSizeBytes    = 9_000_000,
            ModifiedUtc      = "2026-07-29T08:30:00.0000000Z",
            Artist           = artist,
            Title            = title,
            Success          = success,
            Bpm              = bpm,
            KeyCamelot       = camelot,
            Energy           = energy,
            Harshness        = harshness,
            GridConfidence   = grid,
        };

    // ── Storage ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_field_survives_a_round_trip()
    {
        var entry = Track(@"C:\music\a.mp3") with
        {
            Album = "Album", Genre = "Hard Techno", Year = 2026,
            DurationSec = 372.5, BitrateKbps = 320,
            KeyName = "Am", KeyConfidence = 0.91,
            LoudnessLufs = -7.4, ReplayGainDb = -3.6, Peak = 0.98, Danceability = 0.5f,
            BeatType = "4x4-driving", FourOnFloor = 0.93, OffbeatEnergy = 0.31,
            Swing = 0.02, Syncopation = 0.17, HalfTimeFeel = 0.05,
            RawEnergyScore = 0.77, SpectralFlatness = 0.12,
            BassPunch = 0.66, BassGroove = 0.41, Dark = 0.72, Hypnotic = 0.55,
            AcfSharpness = 0.62,
        };

        _db.Upsert(entry);
        var loaded = _db.TryGet(@"C:\music\a.mp3");

        Assert.NotNull(loaded);
        Assert.Equal(entry, loaded);   // record equality: every column, or this fails
    }

    [Fact]
    public void Upsert_updates_in_place_instead_of_duplicating()
    {
        _db.Upsert(Track(@"C:\music\a.mp3", bpm: 128));
        _db.Upsert(Track(@"C:\music\a.mp3", bpm: 145));

        Assert.Equal(1, _db.Count);
        Assert.Equal(145, _db.TryGet(@"C:\music\a.mp3")!.Bpm);
    }

    [Fact]
    public void Paths_match_case_insensitively_like_the_filesystem()
    {
        _db.Upsert(Track(@"C:\music\A.mp3"));

        Assert.NotNull(_db.TryGet(@"c:\MUSIC\a.MP3"));
        _db.Upsert(Track(@"c:\music\a.mp3"));
        Assert.Equal(1, _db.Count);   // same track, not a second row
    }

    [Fact]
    public void Reopening_the_file_keeps_the_data_and_the_schema()
    {
        var path = Path.Combine(_tmp, "reopen.db");
        using (var db = LibraryDb.Open(path))
            db.Upsert(Track(@"C:\music\a.mp3"));

        using var reopened = LibraryDb.Open(path);
        Assert.Equal(1, reopened.Count);
        Assert.NotNull(reopened.TryGet(@"C:\music\a.mp3"));
    }

    // ── Query: the set-building surface ───────────────────────────────────────

    [Fact]
    public void Query_filters_a_mix_window_out_of_the_library()
    {
        _db.UpsertMany([
            Track(@"C:\m\fits.mp3",      bpm: 128, camelot: "8A", energy: 8, harshness: 0.2, grid: 0.8),
            Track(@"C:\m\too-slow.mp3",  bpm: 118, camelot: "8A", energy: 8),
            Track(@"C:\m\wrong-key.mp3", bpm: 128, camelot: "3B", energy: 8),
            Track(@"C:\m\too-calm.mp3",  bpm: 128, camelot: "9A", energy: 3),
            Track(@"C:\m\harsh.mp3",     bpm: 128, camelot: "8A", energy: 8, harshness: 0.9),
            Track(@"C:\m\grid-soft.mp3", bpm: 128, camelot: "8A", energy: 8, grid: 0.3),
            Track(@"C:\m\also-fits.mp3", bpm: 130, camelot: "9A", energy: 7, harshness: 0.1, grid: 0.7),
        ]);

        var hits = _db.Query(new LibraryQuery
        {
            BpmMin = 126, BpmMax = 132,
            Camelot = ["8A", "7A", "9A", "8B"],
            EnergyMin = 7,
            MaxHarshness = 0.5,
            MinGridConfidence = 0.45,
        });

        Assert.Equal(
            ["also-fits.mp3", "fits.mp3"],
            hits.Select(h => Path.GetFileName(h.FilePath)).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Query_text_matches_artist_title_and_album_case_insensitively()
    {
        _db.UpsertMany([
            Track(@"C:\m\1.mp3", artist: "Klaas",   title: "Rise Up"),
            Track(@"C:\m\2.mp3", artist: "Someone", title: "klaas Edit"),
            Track(@"C:\m\3.mp3", artist: "Other",   title: "Nothing"),
        ]);

        var hits = _db.Query(new LibraryQuery { Text = "KLAAS" });
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Query_sorts_and_limits()
    {
        _db.UpsertMany([
            Track(@"C:\m\a.mp3", bpm: 150),
            Track(@"C:\m\b.mp3", bpm: 128),
            Track(@"C:\m\c.mp3", bpm: 174),
        ]);

        var fastest = _db.Query(new LibraryQuery { Sort = LibrarySort.Bpm, Descending = true, Limit = 2 });

        Assert.Equal([174, 150], fastest.Select(t => t.Bpm).ToArray());
    }

    [Fact]
    public void Failed_analyses_are_hidden_by_default_but_reachable()
    {
        _db.UpsertMany([
            Track(@"C:\m\ok.mp3"),
            Track(@"C:\m\broken.mp3", success: false),
        ]);

        Assert.Single(_db.Query(new LibraryQuery()));
        Assert.Equal(2, _db.Query(new LibraryQuery { SuccessOnly = false }).Count);
    }

    // ── Never leave a song behind ─────────────────────────────────────────────

    [Fact]
    public void A_missing_file_is_flagged_not_deleted()
    {
        _db.UpsertMany([Track(@"C:\m\here.mp3"), Track(@"C:\m\gone.mp3")]);

        Assert.Equal(1, _db.MarkMissing([@"C:\m\gone.mp3"]));

        Assert.Single(_db.Query(new LibraryQuery()));                          // hidden by default
        Assert.Equal(2, _db.Query(new LibraryQuery { IncludeMissing = true }).Count);
        Assert.Equal(2, _db.Count);                                            // still in the index
        Assert.NotNull(_db.TryGet(@"C:\m\gone.mp3"));                          // and still readable
    }

    [Fact]
    public void Seeing_a_missing_file_again_clears_the_flag()
    {
        _db.Upsert(Track(@"C:\m\flaky.mp3"));
        _db.MarkMissing([@"C:\m\flaky.mp3"]);
        Assert.Empty(_db.Query(new LibraryQuery()));

        _db.Upsert(Track(@"C:\m\flaky.mp3"));   // the share came back
        Assert.Single(_db.Query(new LibraryQuery()));
    }

    // ── Interchange with the CLI ──────────────────────────────────────────────

    [Fact]
    public void Json_import_and_export_round_trip_through_the_CLI_format()
    {
        var jsonPath = Path.Combine(_tmp, "exported.index.json");
        _db.UpsertMany([Track(@"C:\m\a.mp3", bpm: 128), Track(@"C:\m\b.mp3", bpm: 145)]);

        Assert.Equal(2, _db.ExportJson(jsonPath));

        using var fresh = LibraryDb.Open(Path.Combine(_tmp, "fresh.db"));
        Assert.Equal(2, fresh.ImportJson(jsonPath));
        Assert.Equal(145, fresh.TryGet(@"C:\m\b.mp3")!.Bpm);
    }

    // ── Where the file lives ──────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\nas\music", @"\\nas\music\")]
    [InlineData(@"\\nas\music", @"\\NAS\Music")]
    public void DefaultPathFor_is_stable_across_spelling_of_the_same_root(string a, string b) =>
        Assert.Equal(LibraryDb.DefaultPathFor(a), LibraryDb.DefaultPathFor(b));

    [Fact]
    public void DefaultPathFor_separates_different_roots()
    {
        var music = LibraryDb.DefaultPathFor(@"\\nas\music");
        var sets  = LibraryDb.DefaultPathFor(@"\\nas\production");

        Assert.NotEqual(music, sets);
        Assert.EndsWith(".db", music);
        Assert.Contains("music-", Path.GetFileName(music));
    }
}
