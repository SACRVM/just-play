using System.IO;
using System.Linq;
using JustPlay.Core.Models;

namespace JustPlay.Library.Tests;

/// <summary>
/// What the finder's LIBRARY scope stands on: a recursive path filter, and turning a stored row
/// back into the app's models without opening the file.
/// </summary>
public sealed class LibraryScopeTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "jp-scope-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDb _db;

    public LibraryScopeTests()
    {
        Directory.CreateDirectory(_tmp);
        _db = LibraryDb.Open(Path.Combine(_tmp, "index.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static TrackIndexEntry Track(string path, double? bpm = 128) => new()
    {
        FilePath = path,
        ContentHash = "",
        AnalysedAt = "2026-07-30T12:00:00.0000000Z",
        DetectionVersion = TrackIndex.CurrentDetectionVersion,
        FileSizeBytes = 1000,
        Success = true,
        Bpm = bpm,
    };

    // ── Recursive scope ───────────────────────────────────────────────────────

    [Fact]
    public void PathPrefix_takes_everything_below_a_folder()
    {
        _db.UpsertMany([
            Track(@"\\nas\music\GENRES\Techno\a.mp3"),
            Track(@"\\nas\music\GENRES\Techno\Sub\b.mp3"),
            Track(@"\\nas\music\GENRES\House\c.mp3"),
            Track(@"\\nas\music\SETS\d.mp3"),
        ]);

        var techno = _db.Query(new LibraryQuery { PathPrefix = @"\\nas\music\GENRES\Techno" });
        Assert.Equal(2, techno.Count);

        var genres = _db.Query(new LibraryQuery { PathPrefix = @"\\nas\music\GENRES" });
        Assert.Equal(3, genres.Count);

        var all = _db.Query(new LibraryQuery { PathPrefix = @"\\nas\music" });
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void PathPrefix_tolerates_a_trailing_separator()
    {
        _db.Upsert(Track(@"C:\music\Techno\a.mp3"));

        Assert.Single(_db.Query(new LibraryQuery { PathPrefix = @"C:\music\Techno" }));
        Assert.Single(_db.Query(new LibraryQuery { PathPrefix = @"C:\music\Techno\" }));
    }

    [Fact]
    public void PathPrefix_is_case_insensitive_like_the_filesystem()
    {
        _db.Upsert(Track(@"\\nas\music\GENRES\a.mp3"));

        // The finder's root comes from settings, the paths come from enumeration — their spelling
        // must not have to match.
        Assert.Single(_db.Query(new LibraryQuery { PathPrefix = @"\\NAS\Music\genres" }));
    }

    [Fact]
    public void PathPrefix_does_not_leak_into_a_sibling_with_the_same_start()
    {
        _db.UpsertMany([
            Track(@"C:\music\Techno\a.mp3"),
            Track(@"C:\music\TechnoHard\b.mp3"),   // NOT below "Techno"
        ]);

        var hits = _db.Query(new LibraryQuery { PathPrefix = @"C:\music\Techno" });

        Assert.Single(hits);
        Assert.EndsWith(@"Techno\a.mp3", hits[0].FilePath);
    }

    [Fact]
    public void An_underscore_in_a_folder_name_is_not_a_wildcard()
    {
        _db.UpsertMany([
            Track(@"C:\music\Hard_Techno\a.mp3"),
            Track(@"C:\music\HardXTechno\b.mp3"),
        ]);

        var hits = _db.Query(new LibraryQuery { PathPrefix = @"C:\music\Hard_Techno" });

        Assert.Single(hits);
        Assert.EndsWith(@"Hard_Techno\a.mp3", hits[0].FilePath);
    }

    [Fact]
    public void An_underscore_in_the_search_text_is_not_a_wildcard_either()
    {
        _db.UpsertMany([
            Track(@"C:\a.mp3") with { Title = "Hard_Mix" },
            Track(@"C:\b.mp3") with { Title = "HardXMix" },
        ]);

        var hits = _db.Query(new LibraryQuery { Text = "Hard_Mix" });

        Assert.Single(hits);
        Assert.Equal("Hard_Mix", hits[0].Title);
    }

    [Fact]
    public void Scope_and_filters_combine()
    {
        _db.UpsertMany([
            Track(@"C:\music\Techno\slow.mp3", bpm: 120),
            Track(@"C:\music\Techno\fast.mp3", bpm: 150),
            Track(@"C:\music\House\fast.mp3",  bpm: 150),
        ]);

        var hits = _db.Query(new LibraryQuery
        {
            PathPrefix = @"C:\music\Techno",
            BpmMin = 140,
        });

        Assert.Single(hits);
        Assert.EndsWith(@"Techno\fast.mp3", hits[0].FilePath);
    }

    // ── Row → app models, without opening the file ────────────────────────────

    [Fact]
    public void An_entry_rebuilds_into_the_analysis_the_app_shows()
    {
        var entry = Track(@"C:\music\a.mp3", bpm: 145) with
        {
            KeyCamelot = "8A", KeyConfidence = 0.9, Energy = 8,
            LoudnessLufs = -7.2, ReplayGainDb = -3.8, Peak = 0.99,
            BeatType = "4x4-driving", FourOnFloor = 0.9, OffbeatEnergy = 0.3,
            Swing = 0.02, Syncopation = 0.1, HalfTimeFeel = 0.05,
            Dark = 0.7, Hypnotic = 0.6, BassPunch = 0.8, BassGroove = 0.4,
            Harshness = 0.25, RawEnergyScore = 0.72, SpectralFlatness = 0.2,
            AcfSharpness = 0.66, GridConfidence = 0.81,
        };

        var analysis = TrackIndexMapping.ToAnalysisResult(entry);

        Assert.Equal(145, analysis.Bpm);
        Assert.Equal("8A", analysis.Key?.Camelot);
        Assert.Equal(8, analysis.Energy);
        Assert.Equal(0.81, analysis.GridConfidence);
        Assert.Equal("4x4-driving", analysis.Rhythm?.BeatType);
        Assert.Equal(0.7, analysis.Dark);
        // The index keeps only the scalar summary, not the fingerprint's float arrays.
        Assert.Null(analysis.Fingerprint);
    }

    [Fact]
    public void A_partial_rhythm_block_yields_no_rhythm_rather_than_zeroes()
    {
        // BeatType present but the scalars missing (an older entry) — inventing 0.0 for the
        // missing ones would put a fake "perfectly straight" track into the beat filters.
        var entry = Track(@"C:\music\a.mp3") with { BeatType = "breaks" };

        Assert.Null(TrackIndexMapping.ToAnalysisResult(entry).Rhythm);
    }

    [Fact]
    public void An_entry_rebuilds_into_the_metadata_a_row_displays()
    {
        var entry = Track(@"C:\music\Artist - Title.mp3", bpm: 145.4) with
        {
            Title = "Title", Artist = "Artist", Album = "Album", Genre = "Techno",
            Year = 2026, DurationSec = 372.5, BitrateKbps = 320, KeyCamelot = "8A", Energy = 8,
        };

        var meta = TrackIndexMapping.ToMetadata(entry);

        Assert.Equal("Title", meta.Title);
        Assert.Equal("Artist", meta.Artist);
        Assert.Equal(2026u, meta.Year);
        Assert.Equal(372.5, meta.Duration.TotalSeconds);
        Assert.Equal(320, meta.Bitrate);
        Assert.Equal(145u, meta.TaggedBpm);          // rounded for the tag-style field
        Assert.Equal("8A", meta.TaggedKey);
        Assert.Equal(8, meta.TaggedEnergy);
        Assert.Equal("Artist - Title", meta.FallbackName);
    }

    [Fact]
    public void A_row_with_no_analysis_still_carries_its_tags()
    {
        var entry = TrackIndexMapping.NotAnalysed(
            @"C:\music\new.mp3", 1000, DateTime.UtcNow,
            new TrackMetadata { FallbackName = "new", Title = "New Track", Artist = "Someone" });

        Assert.True(entry.NeedsAnalysis);
        Assert.Equal("New Track", entry.Title);
        Assert.Null(TrackIndexMapping.ToAnalysisResult(entry).Bpm);
    }
}
