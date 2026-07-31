using System.Security.Cryptography;
using System.Text;
using JustPlay.Core.Storage;
using Microsoft.Data.Sqlite;

namespace JustPlay.Library;

/// <summary>
/// The per-machine library index: a local SQLite database over a library root.
///
/// <para><b>Why local and not on the share (Chloe, 2026-07-30).</b> The music lives on
/// <c>\\nas\music</c> and two machines use it. A shared database file is not an option: SQLite's
/// own documentation says <i>"WAL does not work over a network filesystem"</i> and <i>"All
/// processes using a database must be on the same host computer"</i>, and for rollback mode over
/// SMB, <i>"SQLite relies on exclusive locks for write operations, and those have been known to
/// operate incorrectly for some network filesystems. This has led to database corruption."</i></para>
///
/// <para><b>So the FILES are the truth and this is only an index onto them.</b> Each machine
/// syncs its own database from the tags in the files (the JUSTPLAY blob), which means no machine
/// is blind and no DSP is ever run twice: whoever analyses a track first writes the result into
/// the file, and the other machine imports it with a tag read. That also splits cleanly:
/// <b>WAL solves two processes</b> (JUST PLAY + JUST TAG + the CLI on one machine — one writer at
/// a time, readers never blocked), <b>the files solve two machines</b>.</para>
///
/// <para>Threading: one connection, guarded. Writes are short and the DSP dwarfs them; a query
/// over the whole library is sub-millisecond. If read contention ever shows up, open a second
/// read-only connection — WAL readers never block the writer.</para>
/// </summary>
public sealed class LibraryDb : IDisposable
{
    /// <summary>Schema version stored in SQLite's <c>user_version</c>. Bump + migrate when columns change.</summary>
    private const int SchemaVersion = 2;

    private readonly SqliteConnection _cx;
    private readonly Lock _gate = new();

    private LibraryDb(SqliteConnection cx) => _cx = cx;

    // ── Location ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Where this machine keeps its index for <paramref name="libraryRoot"/>:
    /// <c>%LOCALAPPDATA%\JUST\library\music-ab12cd34.db</c> (and the platform equivalent on macOS).
    /// Suite-wide folder — JUST PLAY, JUST TAG and the CLI on one machine open the same file.
    /// The name carries a readable prefix plus a hash of the root, so two roots never collide.
    /// </summary>
    public static string DefaultPathFor(string libraryRoot)
    {
        var normalized = libraryRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];

        // NOT Path.GetFileName: for a UNC share root like \\nas\music — exactly the shape of
        // Chloe's library root — that returns "" (the share IS the root), and every library
        // would end up called "library-<hash>". Take the last non-empty segment instead.
        var lastSegment = normalized
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .LastOrDefault(s => s.Length > 0) ?? "";

        var label = new string(lastSegment
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (label.Length == 0) label = "library";

        return JustDataPaths.Combine("JUST", "library", $"{label}-{hash}.db");
    }

    // ── Open / schema ────────────────────────────────────────────────────────

    /// <summary>Opens (creating if needed) the index for <paramref name="libraryRoot"/>.</summary>
    public static LibraryDb OpenForRoot(string libraryRoot) => Open(DefaultPathFor(libraryRoot));

    /// <summary>Opens (creating if needed) the database at <paramref name="dbPath"/>.</summary>
    public static LibraryDb Open(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var cx = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared across JUST PLAY / JUST TAG / CLI on this machine — cross-process locking
            // is SQLite's job, and with WAL that is exactly what it is good at.
            Cache = SqliteCacheMode.Default,
        }.ToString());

        cx.Open();

        // WAL: one writer, readers never blocked. Only legal because the file is LOCAL.
        Execute(cx, "PRAGMA journal_mode=WAL;");
        // A second process holding the write lock is normal, not an error — wait it out.
        Execute(cx, "PRAGMA busy_timeout=5000;");
        // NORMAL is the documented safe pairing with WAL: durable across app crashes, and only
        // at risk of losing the last transactions if the OS itself dies.
        Execute(cx, "PRAGMA synchronous=NORMAL;");

        var db = new LibraryDb(cx);
        db.Migrate();
        return db;
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;") ?? 0);
        if (version >= SchemaVersion) return;

        if (version == 0)
        {
            Execute(_cx, Schema);
            Execute(_cx, FolderSchema);
        }
        else if (version == 1)
        {
            // v2 (2026-07-30): per-folder fingerprints, so the finder can paint a directory from
            // the index and verify it behind the user's back instead of enumerating first.
            Execute(_cx, FolderSchema);
        }

        Execute(_cx, $"PRAGMA user_version={SchemaVersion};");
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS tracks (
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
        CREATE INDEX IF NOT EXISTS ix_tracks_bpm     ON tracks(bpm);
        CREATE INDEX IF NOT EXISTS ix_tracks_key     ON tracks(key_camelot);
        CREATE INDEX IF NOT EXISTS ix_tracks_energy  ON tracks(energy);
        CREATE INDEX IF NOT EXISTS ix_tracks_missing ON tracks(missing);
        CREATE INDEX IF NOT EXISTS ix_tracks_artist  ON tracks(artist);
        """;

    /// <summary>
    /// What the last look at a folder found: how many tracks were in it and the newest modification
    /// time among them. Comparing this against the live listing is what tells the finder whether the
    /// rows it just painted from the index are still true.
    /// </summary>
    private const string FolderSchema = """
        CREATE TABLE IF NOT EXISTS folders (
            path        TEXT PRIMARY KEY COLLATE NOCASE,
            file_count  INTEGER NOT NULL,
            max_mtime   TEXT,
            checked_at  TEXT NOT NULL
        );
        """;

    // ── Write ────────────────────────────────────────────────────────────────

    private const string UpsertSql = """
        INSERT INTO tracks (
            path, content_hash, analysed_at, detection_version, file_size, modified_utc,
            seen_at, missing,
            title, artist, album, genre, year, duration_sec, bitrate_kbps,
            success, error, bpm, key_name, key_camelot, key_confidence, energy,
            loudness_lufs, replay_gain_db, peak, danceability,
            beat_type, four_on_floor, offbeat_energy, swing, syncopation, half_time_feel,
            raw_energy_score, spectral_flatness, harshness, bass_punch, bass_groove, dark, hypnotic,
            acf_sharpness, grid_confidence
        ) VALUES (
            $path, $content_hash, $analysed_at, $detection_version, $file_size, $modified_utc,
            $seen_at, 0,
            $title, $artist, $album, $genre, $year, $duration_sec, $bitrate_kbps,
            $success, $error, $bpm, $key_name, $key_camelot, $key_confidence, $energy,
            $loudness_lufs, $replay_gain_db, $peak, $danceability,
            $beat_type, $four_on_floor, $offbeat_energy, $swing, $syncopation, $half_time_feel,
            $raw_energy_score, $spectral_flatness, $harshness, $bass_punch, $bass_groove, $dark, $hypnotic,
            $acf_sharpness, $grid_confidence
        )
        ON CONFLICT(path) DO UPDATE SET
            content_hash=excluded.content_hash, analysed_at=excluded.analysed_at,
            detection_version=excluded.detection_version, file_size=excluded.file_size,
            modified_utc=excluded.modified_utc, seen_at=excluded.seen_at, missing=0,
            title=excluded.title, artist=excluded.artist, album=excluded.album,
            genre=excluded.genre, year=excluded.year, duration_sec=excluded.duration_sec,
            bitrate_kbps=excluded.bitrate_kbps,
            success=excluded.success, error=excluded.error, bpm=excluded.bpm,
            key_name=excluded.key_name, key_camelot=excluded.key_camelot,
            key_confidence=excluded.key_confidence, energy=excluded.energy,
            loudness_lufs=excluded.loudness_lufs, replay_gain_db=excluded.replay_gain_db,
            peak=excluded.peak, danceability=excluded.danceability,
            beat_type=excluded.beat_type, four_on_floor=excluded.four_on_floor,
            offbeat_energy=excluded.offbeat_energy, swing=excluded.swing,
            syncopation=excluded.syncopation, half_time_feel=excluded.half_time_feel,
            raw_energy_score=excluded.raw_energy_score, spectral_flatness=excluded.spectral_flatness,
            harshness=excluded.harshness, bass_punch=excluded.bass_punch,
            bass_groove=excluded.bass_groove, dark=excluded.dark, hypnotic=excluded.hypnotic,
            acf_sharpness=excluded.acf_sharpness, grid_confidence=excluded.grid_confidence;
        """;

    /// <summary>Inserts or updates one track. Clears the <c>missing</c> flag — we just saw it.</summary>
    public void Upsert(TrackIndexEntry entry) => UpsertMany([entry]);

    /// <summary>
    /// Inserts or updates many tracks in ONE transaction. A scan calls this in batches — a
    /// transaction per track would fsync per track and turn a scan into a crawl.
    /// </summary>
    public void UpsertMany(IEnumerable<TrackIndexEntry> entries)
    {
        lock (_gate)
        {
            using var tx  = _cx.BeginTransaction();
            using var cmd = _cx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertSql;

            var seenAt = DateTime.UtcNow.ToString("o");
            foreach (var e in entries)
            {
                cmd.Parameters.Clear();
                Bind(cmd, e, seenAt);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static void Bind(SqliteCommand cmd, TrackIndexEntry e, string seenAt)
    {
        void P(string name, object? value) =>
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        P("$path", e.FilePath);
        P("$content_hash", e.ContentHash);
        P("$analysed_at", e.AnalysedAt);
        P("$detection_version", e.DetectionVersion);
        P("$file_size", e.FileSizeBytes);
        P("$modified_utc", e.ModifiedUtc);
        P("$seen_at", seenAt);

        P("$title", e.Title);
        P("$artist", e.Artist);
        P("$album", e.Album);
        P("$genre", e.Genre);
        P("$year", e.Year);
        P("$duration_sec", e.DurationSec);
        P("$bitrate_kbps", e.BitrateKbps);

        P("$success", e.Success ? 1 : 0);
        P("$error", e.Error);
        P("$bpm", e.Bpm);
        P("$key_name", e.KeyName);
        P("$key_camelot", e.KeyCamelot);
        P("$key_confidence", e.KeyConfidence);
        P("$energy", e.Energy);
        P("$loudness_lufs", e.LoudnessLufs);
        P("$replay_gain_db", e.ReplayGainDb);
        P("$peak", e.Peak);
        P("$danceability", e.Danceability);

        P("$beat_type", e.BeatType);
        P("$four_on_floor", e.FourOnFloor);
        P("$offbeat_energy", e.OffbeatEnergy);
        P("$swing", e.Swing);
        P("$syncopation", e.Syncopation);
        P("$half_time_feel", e.HalfTimeFeel);

        P("$raw_energy_score", e.RawEnergyScore);
        P("$spectral_flatness", e.SpectralFlatness);
        P("$harshness", e.Harshness);
        P("$bass_punch", e.BassPunch);
        P("$bass_groove", e.BassGroove);
        P("$dark", e.Dark);
        P("$hypnotic", e.Hypnotic);

        P("$acf_sharpness", e.AcfSharpness);
        P("$grid_confidence", e.GridConfidence);
    }

    /// <summary>
    /// Flags tracks the sync could not find on disk. They stay in the index and stay queryable
    /// via <see cref="LibraryQuery.IncludeMissing"/> — a track is never silently dropped just
    /// because a share was offline or a folder got renamed.
    /// </summary>
    public int MarkMissing(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            using var tx  = _cx.BeginTransaction();
            using var cmd = _cx.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE tracks SET missing=1 WHERE path=$path;";

            var affected = 0;
            foreach (var path in paths)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$path", path);
                affected += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return affected;
        }
    }

    /// <summary>Clears the missing flag for a file that turned up again (cheap — no re-read).</summary>
    public bool ClearMissing(string path)
    {
        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET missing=0 WHERE path=$path AND missing=1;";
            cmd.Parameters.AddWithValue("$path", path);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The whole index reduced to what a sync pass has to compare: size, mtime and the missing
    /// flag. One query instead of a lookup per file, and small enough that a very large library
    /// still fits in memory (a few tens of bytes per track).
    /// </summary>
    public Dictionary<string, SyncKey> LoadSyncState()
    {
        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = "SELECT path, file_size, modified_utc, missing, success FROM tracks;";

            var map = new Dictionary<string, SyncKey>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                map[r.GetString(0)] = new SyncKey(
                    r.GetInt64(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetInt32(3) != 0,
                    r.GetInt32(4) != 0);
            }
            return map;
        }
    }

    /// <summary>Removes an entry outright (used when a file is confirmed gone, not just unseen).</summary>
    public bool Remove(string path)
    {
        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE path=$path;";
            cmd.Parameters.AddWithValue("$path", path);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    private const string SelectColumns = """
        SELECT path, content_hash, analysed_at, detection_version, file_size, modified_utc,
               title, artist, album, genre, year, duration_sec, bitrate_kbps,
               success, error, bpm, key_name, key_camelot, key_confidence, energy,
               loudness_lufs, replay_gain_db, peak, danceability,
               beat_type, four_on_floor, offbeat_energy, swing, syncopation, half_time_feel,
               raw_energy_score, spectral_flatness, harshness, bass_punch, bass_groove, dark, hypnotic,
               acf_sharpness, grid_confidence
        FROM tracks
        """;

    /// <summary>The entry for <paramref name="path"/>, or null. Path matching is case-insensitive.</summary>
    public TrackIndexEntry? TryGet(string path)
    {
        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = SelectColumns + " WHERE path=$path;";
            cmd.Parameters.AddWithValue("$path", path);

            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    /// <summary>
    /// Point lookups for a known set of paths, in one lock. The finder uses this when it lists a
    /// single folder: the DISK says which files are there, this says what we already know about
    /// them, and only the leftovers cost a tag read.
    /// </summary>
    public Dictionary<string, TrackIndexEntry> LookupMany(IEnumerable<string> paths)
    {
        var found = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = SelectColumns + " WHERE path=$path;";
            var p = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);

            foreach (var path in paths)
            {
                p.Value = path;
                using var r = cmd.ExecuteReader();
                if (r.Read()) found[path] = Read(r);
            }
        }

        return found;
    }

    // ── Folder fingerprints ──────────────────────────────────────────────────

    /// <summary>What the last check found in <paramref name="folder"/>, or null if it never looked.</summary>
    public FolderFingerprint? FolderState(string folder)
    {
        var key = NormalizeFolder(folder);

        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = "SELECT file_count, max_mtime FROM folders WHERE path=$path;";
            cmd.Parameters.AddWithValue("$path", key);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            DateTime? newest = null;
            if (!r.IsDBNull(1) &&
                DateTime.TryParse(r.GetString(1), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                newest = parsed.ToUniversalTime();

            return new FolderFingerprint(r.GetInt32(0), newest);
        }
    }

    /// <summary>Records what a check found in <paramref name="folder"/>.</summary>
    public void SetFolderState(string folder, FolderFingerprint state)
    {
        var key = NormalizeFolder(folder);

        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = """
                INSERT INTO folders (path, file_count, max_mtime, checked_at)
                VALUES ($path, $count, $mtime, $checked)
                ON CONFLICT(path) DO UPDATE SET
                    file_count=excluded.file_count,
                    max_mtime=excluded.max_mtime,
                    checked_at=excluded.checked_at;
                """;
            cmd.Parameters.AddWithValue("$path", key);
            cmd.Parameters.AddWithValue("$count", state.FileCount);
            cmd.Parameters.AddWithValue("$mtime",
                state.MaxModifiedUtc is { } m ? TrackIndexEntry.FormatUtc(m) : DBNull.Value);
            cmd.Parameters.AddWithValue("$checked", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Forgets a folder's fingerprint, so the next look re-verifies it from scratch.</summary>
    public void ForgetFolderState(string folder)
    {
        var key = NormalizeFolder(folder);

        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = "DELETE FROM folders WHERE path=$path;";
            cmd.Parameters.AddWithValue("$path", key);
            cmd.ExecuteNonQuery();
        }
    }

    private static string NormalizeFolder(string folder) =>
        folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Total rows, including entries flagged missing.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM tracks;") ?? 0);
        }
    }

    /// <summary>Runs a <see cref="LibraryQuery"/>. This is the set-building path.</summary>
    public IReadOnlyList<TrackIndexEntry> Query(LibraryQuery q)
    {
        lock (_gate)
        {
            using var cmd = _cx.CreateCommand();
            var where = new List<string>();

            if (!q.IncludeMissing) where.Add("missing=0");
            if (q.SuccessOnly)     where.Add("success=1");

            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                // LIKE is case-insensitive for ASCII, which is what a search box wants. '%' and
                // '_' in the typed text must be escaped or "Hard_Techno" also matches
                // "HardXTechno". '\' is safe as the escape char HERE because this matches tag
                // text, not paths.
                where.Add(@"(title LIKE $text ESCAPE '\' OR artist LIKE $text ESCAPE '\'
                             OR album LIKE $text ESCAPE '\')");
                cmd.Parameters.AddWithValue("$text", "%" + EscapeLike(q.Text.Trim()) + "%");
            }

            if (!string.IsNullOrWhiteSpace(q.PathPrefix))
            {
                // Deliberately NOT LIKE: on Windows the escape character would be '\', which is
                // also the path separator — the two cannot coexist in one pattern. An exact
                // prefix comparison costs a scan instead of an index seek, which at library scale
                // is still far below the cost of the tag reads this whole design exists to avoid.
                var prefix = q.PathPrefix.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                // COLLATE NOCASE must be spelled out: substr() is a function call, not a column
                // reference, so it does NOT inherit the path column's NOCASE collation and the
                // comparison would silently turn case-sensitive.
                where.Add("substr(path, 1, $prefixLen) = $prefix COLLATE NOCASE");
                cmd.Parameters.AddWithValue("$prefix", prefix);
                cmd.Parameters.AddWithValue("$prefixLen", prefix.Length);

                if (!q.Recursive)
                {
                    // No further separator after the prefix = a direct child of that folder.
                    where.Add("instr(substr(path, $prefixLen + 1), $sep) = 0");
                    cmd.Parameters.AddWithValue("$sep", Path.DirectorySeparatorChar.ToString());
                }
            }

            if (q.BpmMin is { } bpmMin) { where.Add("bpm >= $bpmMin"); cmd.Parameters.AddWithValue("$bpmMin", bpmMin); }
            if (q.BpmMax is { } bpmMax) { where.Add("bpm <= $bpmMax"); cmd.Parameters.AddWithValue("$bpmMax", bpmMax); }

            if (q.Camelot is { Count: > 0 })
            {
                var names = new List<string>();
                var i = 0;
                foreach (var code in q.Camelot)
                {
                    var name = $"$camelot{i++}";
                    names.Add(name);
                    cmd.Parameters.AddWithValue(name, code);
                }
                where.Add($"key_camelot IN ({string.Join(", ", names)})");
            }

            if (q.EnergyMin is { } eMin) { where.Add("energy >= $eMin"); cmd.Parameters.AddWithValue("$eMin", eMin); }
            if (q.EnergyMax is { } eMax) { where.Add("energy <= $eMax"); cmd.Parameters.AddWithValue("$eMax", eMax); }

            if (!string.IsNullOrWhiteSpace(q.BeatType))
            {
                where.Add("beat_type = $beatType");
                cmd.Parameters.AddWithValue("$beatType", q.BeatType);
            }

            if (q.MinGridConfidence is { } grid)
            {
                where.Add("grid_confidence >= $grid");
                cmd.Parameters.AddWithValue("$grid", grid);
            }

            if (q.MaxHarshness is { } harsh)
            {
                where.Add("harshness <= $harsh");
                cmd.Parameters.AddWithValue("$harsh", harsh);
            }

            var order = q.Sort switch
            {
                LibrarySort.Artist     => "artist",
                LibrarySort.Title      => "title",
                LibrarySort.Bpm        => "bpm",
                LibrarySort.Key        => "key_camelot",
                LibrarySort.Energy     => "energy",
                LibrarySort.Duration   => "duration_sec",
                LibrarySort.AnalysedAt => "analysed_at",
                _                      => "path",
            };

            cmd.CommandText = SelectColumns
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + $" ORDER BY {order} {(q.Descending ? "DESC" : "ASC")}"
                + (q.Limit > 0 ? $" LIMIT {q.Limit}" : "")
                + ";";

            var results = new List<TrackIndexEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) results.Add(Read(r));
            return results;
        }
    }

    private static TrackIndexEntry Read(SqliteDataReader r)
    {
        string? S(int i)  => r.IsDBNull(i) ? null : r.GetString(i);
        double? D(int i)  => r.IsDBNull(i) ? null : r.GetDouble(i);
        int?    I(int i)  => r.IsDBNull(i) ? null : r.GetInt32(i);

        return new TrackIndexEntry
        {
            FilePath         = r.GetString(0),
            ContentHash      = r.GetString(1),
            AnalysedAt       = r.GetString(2),
            DetectionVersion = r.GetInt32(3),
            FileSizeBytes    = r.GetInt64(4),
            ModifiedUtc      = S(5),

            Title       = S(6),
            Artist      = S(7),
            Album       = S(8),
            Genre       = S(9),
            Year        = r.IsDBNull(10) ? null : (uint)r.GetInt64(10),
            DurationSec = r.IsDBNull(11) ? 0 : r.GetDouble(11),
            BitrateKbps = I(12),

            Success       = r.GetInt32(13) != 0,
            Error         = S(14),
            Bpm           = D(15),
            KeyName       = S(16),
            KeyCamelot    = S(17),
            KeyConfidence = D(18),
            Energy        = I(19),
            LoudnessLufs  = D(20),
            ReplayGainDb  = D(21),
            Peak          = D(22),
            Danceability  = r.IsDBNull(23) ? null : (float)r.GetDouble(23),

            BeatType      = S(24),
            FourOnFloor   = D(25),
            OffbeatEnergy = D(26),
            Swing         = D(27),
            Syncopation   = D(28),
            HalfTimeFeel  = D(29),

            RawEnergyScore   = D(30),
            SpectralFlatness = D(31),
            Harshness        = D(32),
            BassPunch        = D(33),
            BassGroove       = D(34),
            Dark             = D(35),
            Hypnotic         = D(36),

            AcfSharpness   = D(37),
            GridConfidence = D(38),
        };
    }

    // ── Interchange with the CLI's index JSON ────────────────────────────────

    /// <summary>
    /// Imports a <see cref="TrackIndex"/> JSON file (the CLI's <c>--index</c> format).
    /// This is how a fresh machine can be seeded from an existing analysis run instead of
    /// re-reading every tag header, and how <c>JustPlayCLI analyze</c> output lands in the app.
    /// Returns the number of entries written.
    /// </summary>
    public int ImportJson(string indexPath)
    {
        var index = TrackIndex.Load(indexPath);
        UpsertMany(index.Entries.Values);
        return index.Entries.Count;
    }

    /// <summary>
    /// Exports the index to the CLI's JSON format — a portable snapshot, written wherever the
    /// caller asks for it (⛔ never automatically next to her music).
    /// </summary>
    public int ExportJson(string indexPath, LibraryQuery? filter = null)
    {
        var entries = Query(filter ?? new LibraryQuery { SuccessOnly = false, IncludeMissing = true });
        var index   = new TrackIndex();
        foreach (var e in entries) index.Entries[e.FilePath] = e;
        index.Save(indexPath);
        return entries.Count;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Escapes the LIKE wildcards in user-typed text so a search for "Hard_Techno" does not also
    /// match "HardXTechno". Pairs with <c>ESCAPE '\'</c> in the SQL.
    /// </summary>
    private static string EscapeLike(string s) => s
        .Replace(@"\", @"\\")
        .Replace("%", @"\%")
        .Replace("_", @"\_");

    private static void Execute(SqliteConnection cx, string sql)
    {
        using var cmd = cx.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _cx.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cx.Close();
            _cx.Dispose();
        }
    }
}
