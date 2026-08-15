using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace JustPlay.Library.Tests;

/// <summary>
/// Schema v3 (2026-08-07): the index carries everything a track table SHOWS, so no list has to open
/// a file to paint a row - the rule is zero live file reads, everything goes into the index. Before
/// this, album artist and comment were not indexed at all (a tag read per row over the NAS), and the
/// COV tick and the ID3 column each cost a SECOND and THIRD file open per visible row.
///
/// <para>These tests guard the two things that can go quietly wrong: an existing index on disk
/// must survive the migration, and the re-read that fills the new columns must not turn into a
/// re-ANALYSIS.</para>
/// </summary>
public sealed class IndexTagColumnsTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "jp-tagcols-" + Guid.NewGuid().ToString("N")[..8]);

    public IndexTagColumnsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private string DbPath => Path.Combine(_tmp, "index.db");

    private static TrackIndexEntry Entry(string path) => new()
    {
        FilePath         = path,
        ContentHash      = "hash",
        AnalysedAt       = "2026-08-01T12:00:00.0000000Z",
        DetectionVersion = TrackIndex.CurrentDetectionVersion,
        FileSizeBytes    = 5_000_000,
        ModifiedUtc      = TrackIndexEntry.FormatUtc(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc)),
        Success          = true,
    };

    // -- Round-trip ----------------------------------------------------------------------------

    [Fact]
    public void The_new_tag_columns_survive_a_round_trip()
    {
        using var db = LibraryDb.Open(DbPath);

        db.Upsert(Entry(@"C:\m\a.mp3") with
        {
            AlbumArtist = "Various Artists",
            Comment     = "8A - Energy 7",
            TrackNo     = 4,
            HasCover    = true,
            Id3Version  = "2.3",
            TagRev      = TrackIndexEntry.CurrentTagRev,
        });

        var back = db.TryGet(@"C:\m\a.mp3");

        Assert.NotNull(back);
        Assert.Equal("Various Artists", back!.AlbumArtist);
        Assert.Equal("8A - Energy 7", back.Comment);
        Assert.Equal(4u, back.TrackNo);
        Assert.True(back.HasCover);
        Assert.Equal("2.3", back.Id3Version);
        Assert.Equal(TrackIndexEntry.CurrentTagRev, back.TagRev);
    }

    [Fact]
    public void Has_cover_keeps_its_three_states()
    {
        using var db = LibraryDb.Open(DbPath);

        db.Upsert(Entry(@"C:\m\yes.mp3")  with { HasCover = true });
        db.Upsert(Entry(@"C:\m\no.mp3")   with { HasCover = false });
        db.Upsert(Entry(@"C:\m\dunno.mp3") with { HasCover = null });

        // "no cover" and "never looked" are different answers and the tick column shows them
        // differently - collapsing null to false would claim knowledge we do not have.
        Assert.True(db.TryGet(@"C:\m\yes.mp3")!.HasCover);
        Assert.False(db.TryGet(@"C:\m\no.mp3")!.HasCover);
        Assert.Null(db.TryGet(@"C:\m\dunno.mp3")!.HasCover);
    }

    // -- The migration, on an index that already exists ----------------------------------------

    [Fact]
    public void A_v2_index_is_upgraded_in_place_and_keeps_its_rows()
    {
        // A real pre-v3 database: build it with the OLD schema, exactly as a shipped build left it.
        CreateV2Database(DbPath, @"C:\m\old.mp3");

        using var db = LibraryDb.Open(DbPath);   // <- runs Migrate()
        var row = db.TryGet(@"C:\m\old.mp3");

        Assert.NotNull(row);
        Assert.Equal("Old Artist", row!.Artist);          // the data that was there is still there
        Assert.Equal(128, row.Bpm);                       // including the expensive half: the analysis
        Assert.Null(row.AlbumArtist);                     // the new columns exist and are empty
        Assert.Null(row.Comment);
        Assert.Null(row.HasCover);
        Assert.Equal(0, row.TagRev);                      // => the next sync re-reads it once
    }

    [Fact]
    public void The_upgraded_index_can_be_written_to_and_read_back()
    {
        CreateV2Database(DbPath, @"C:\m\old.mp3");

        using (var db = LibraryDb.Open(DbPath))
        {
            db.Upsert(Entry(@"C:\m\old.mp3") with
            {
                Artist = "Old Artist", Comment = "filled in later",
                TagRev = TrackIndexEntry.CurrentTagRev,
            });
        }

        using var reopened = LibraryDb.Open(DbPath);
        var row = reopened.TryGet(@"C:\m\old.mp3");

        Assert.Equal("filled in later", row!.Comment);
        Assert.Equal(TrackIndexEntry.CurrentTagRev, row.TagRev);
    }

    [Fact]
    public void Migrating_twice_is_harmless()
    {
        CreateV2Database(DbPath, @"C:\m\old.mp3");

        using (var first = LibraryDb.Open(DbPath)) { }
        using var second = LibraryDb.Open(DbPath);   // must not try to ADD COLUMN again

        Assert.NotNull(second.TryGet(@"C:\m\old.mp3"));
    }

    // -- The rule that keeps the re-read cheap -------------------------------------------------

    [Fact]
    public void An_old_row_is_not_tag_current_so_a_sync_re_reads_it()
    {
        var stale   = Entry(@"C:\m\a.mp3") with { TagRev = 0 };
        var current = Entry(@"C:\m\a.mp3") with { TagRev = TrackIndexEntry.CurrentTagRev };

        Assert.False(stale.TagsAreCurrent);
        Assert.True(current.TagsAreCurrent);
    }

    [Fact]
    public void But_looks_unchanged_still_ignores_the_tag_revision()
    {
        // (!!) THE REGRESSION THIS GUARDS: AnalyzeCommand uses LooksUnchanged to decide whether to skip
        // DSP. If a tag-shape bump made this return false, bumping it would re-ANALYSE the whole
        // library - hours of DSP - when the only thing needed is re-READING tags, which is minutes.
        // "Did the file change" and "did we start storing more of it" are two different questions.
        var size     = 5_000_000L;
        var modified = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var oldShape = Entry(@"C:\m\a.mp3") with { TagRev = 0 };

        Assert.True(oldShape.LooksUnchanged(size, modified));
    }

    [Fact]
    public void A_changed_file_is_re_read_whatever_its_tag_revision_says()
    {
        var current = Entry(@"C:\m\a.mp3") with { TagRev = TrackIndexEntry.CurrentTagRev };

        Assert.False(current.LooksUnchanged(
            5_000_001, new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc)));   // grew by a byte
    }

    /// <summary>
    /// Writes a database in the SHIPPED v2 shape - the old column list, the old
    /// <c>user_version</c> - so the migration is exercised against what is really on her disk
    /// rather than against a freshly created v3 file.
    /// </summary>
    private static void CreateV2Database(string path, string trackPath)
    {
        using var cx = new SqliteConnection($"Data Source={path}");
        cx.Open();

        using (var cmd = cx.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE tracks (
                    path              TEXT PRIMARY KEY COLLATE NOCASE,
                    content_hash      TEXT NOT NULL,
                    analysed_at       TEXT NOT NULL,
                    detection_version INTEGER NOT NULL,
                    file_size         INTEGER NOT NULL,
                    modified_utc      TEXT,
                    seen_at           TEXT,
                    missing           INTEGER NOT NULL DEFAULT 0,
                    title TEXT, artist TEXT, album TEXT, genre TEXT, year INTEGER,
                    duration_sec REAL, bitrate_kbps INTEGER,
                    success INTEGER NOT NULL, error TEXT,
                    bpm REAL, key_name TEXT, key_camelot TEXT, key_confidence REAL,
                    energy INTEGER, loudness_lufs REAL, replay_gain_db REAL, peak REAL,
                    danceability REAL,
                    beat_type TEXT, four_on_floor REAL, offbeat_energy REAL, swing REAL,
                    syncopation REAL, half_time_feel REAL,
                    raw_energy_score REAL, spectral_flatness REAL, harshness REAL,
                    bass_punch REAL, bass_groove REAL, dark REAL, hypnotic REAL,
                    acf_sharpness REAL, grid_confidence REAL
                );
                CREATE TABLE folders (
                    path        TEXT PRIMARY KEY COLLATE NOCASE,
                    file_count  INTEGER NOT NULL,
                    max_mtime   TEXT,
                    checked_at  TEXT NOT NULL
                );
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = cx.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tracks (path, content_hash, analysed_at, detection_version,
                                    file_size, modified_utc, seen_at, missing,
                                    artist, success, bpm)
                VALUES ($p, 'hash', '2026-08-01T12:00:00.0000000Z', 9,
                        5000000, '2026-08-01T10:00:00.0000000Z', '2026-08-01T12:00:00.0000000Z', 0,
                        'Old Artist', 1, 128.0);
                """;
            cmd.Parameters.AddWithValue("$p", trackPath);
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearPool(cx);
    }
}
