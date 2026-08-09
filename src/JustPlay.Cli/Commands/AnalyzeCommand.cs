using JustPlay.Core.Models;
using JustPlay.Core.Playlists;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay analyze &lt;root&gt; --index &lt;path&gt; [--threads N] [--limit N] [--db &lt;path&gt;|--no-db] [--force]</c>
/// <c>justplay analyze --playlist &lt;m3u&gt; --index &lt;path&gt; [--root &lt;dir&gt;] [--threads N] [--limit N] [--db &lt;path&gt;|--no-db] [--force]</c>
///
/// PHASE 1 - the heavy resumable analysis pass. For each audio file (under <c>root</c>, or listed
/// in the playlist):
///   1. Cheap key (size + mtime, straight from the directory listing): unchanged and already
///      analysed -> skip without opening the file.
///   2. Read tag metadata. A JUSTPLAY blob at the CURRENT detection version is imported as-is -
///      that analysis has already been paid for, on this machine or the other one.
///   3. Otherwise run the full DSP pipeline (BPM -> key -> energy -> loudness -> beat fingerprint
///      -> RhythmPattern) and hash the file (for dedup).
///   4. Write the entry to the sidecar index AND to this machine's library database.
///
/// <para><b>What changed in 0.6 and why.</b> This command used to SHA-256 every file first, with
/// the comment "cheap - no decode needed". Measured 2026-07-30 against <c>\\nas\music\GENRES</c>,
/// it is not cheap over SMB: hashing reads every byte, i.e. the whole library through the network
/// on every run, while the directory entry that answers "did this change?" costs 0.23 ms. Files
/// are now hashed only when they are actually analysed.</para>
///
/// <para><b>The two input forms.</b> A ROOT is the whole tree. A PLAYLIST is an explicit list of
/// files - the form to use when a KNOWN subset must be re-run (e.g. the files a decoder fix
/// invalidated): pointing <c>--force</c> at the common root would redo the other thousands for
/// nothing. Both forms produce identical index/database rows; they differ only in which files are
/// fed in, and the playlist form additionally reports every listed file that is no longer on disk.
/// The list is processed in path order, so <c>--limit</c> and an interrupted run behave exactly as
/// they do for a directory.</para>
///
/// <para><b>READ-ONLY on the audio library</b> - no tags are written here (that is <c>promote</c>).
/// Outputs are the index file and the local database.</para>
/// </summary>
internal static class AnalyzeCommand
{
    private const int FlushEveryN = 50;

    /// <summary>
    /// The directory form: analyse every audio file under <paramref name="root"/>.
    ///
    /// <para><paramref name="root"/> feeds THREE things and no others: the enumeration, the
    /// default library-database file (<see cref="LibraryDb.DefaultPathFor"/>), and the
    /// registration of that database in <see cref="LibraryIndexRegistry"/>. It does NOT decide
    /// where the index goes (<c>--index</c> is explicit) and it is not part of any index key -
    /// entries are keyed by absolute file path.</para>
    /// </summary>
    public static int Run(
        string root,
        string indexPath,
        int threads,
        int limit,
        string? dbPath = null,
        bool noDb = false,
        bool force = false)
    {
        root      = Path.GetFullPath(root);
        indexPath = Path.GetFullPath(indexPath);

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[analyze] ERROR: directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[analyze] Root      : {root}");

        // One enumeration, carrying size + mtime - a follow-up FileInfo per file would be a
        // second round-trip each over SMB.
        var files = AudioFiles.EnumerateWithKeys(root)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The directory form OWNS the root: it is the pass that indexes it, so it may create the
        // database and register the root. The playlist form may not (see RunPlaylist).
        return Analyze(files, root, indexPath, threads, limit, dbPath, noDb, force,
                       mayCreateRootDb: true, list: null);
    }

    /// <summary>
    /// The playlist form: analyse exactly the files an .m3u/.m3u8 lists (paths resolved against the
    /// playlist's own folder; an absolute or UNC line is used verbatim).
    ///
    /// <para><b>What "root" becomes here.</b> A list of 957 files scattered over many folders has
    /// no single root, and an INFERRED common ancestor would be a guess that quietly picks the
    /// wrong database. So the three jobs the directory form's root does are answered separately:
    /// <list type="bullet">
    ///   <item>ENUMERATION - replaced by the list. No root needed.</item>
    ///   <item>INDEX LOCATION - never came from the root; <c>--index</c> is explicit either way,
    ///         and entries are keyed by absolute path, so a list run writes into exactly the same
    ///         index the directory run does.</item>
    ///   <item>THE LIBRARY DATABASE - the one thing that genuinely still wants a root. It is taken
    ///         from <c>--root</c> when given, else from the registry: the registered root that
    ///         contains EVERY listed file (see <see cref="RegistryRootCovering"/>). If neither
    ///         answers, the run continues index-only and says so.</item>
    /// </list>
    /// (!) In this form the database is opened only when it ALREADY EXISTS - never created. A
    /// created-and-then-registered empty database would read as "this folder is indexed" forever
    /// (the hazard <see cref="LibraryIndexRegistry.OpenFor"/> exists to prevent), and analysing a
    /// hand-written list is not the act that establishes a library root. Run the directory form
    /// once to establish one.</para>
    /// </summary>
    public static int RunPlaylist(
        string playlistPath,
        string? root,
        string indexPath,
        int threads,
        int limit,
        string? dbPath = null,
        bool noDb = false,
        bool force = false)
    {
        playlistPath = Path.GetFullPath(playlistPath);
        indexPath    = Path.GetFullPath(indexPath);

        if (!File.Exists(playlistPath))
        {
            Console.Error.WriteLine($"[analyze] ERROR: playlist not found: {playlistPath}");
            return 1;
        }

        if (root is not null)
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"[analyze] ERROR: directory not found: {root}");
                return 1;
            }
        }

        var list = LoadPlaylist(playlistPath);

        Console.WriteLine($"[analyze] Playlist  : {playlistPath}");
        Console.WriteLine($"[analyze] Listed    : {list.Listed:N0}");
        if (list.Missing.Count > 0)
            Console.WriteLine($"[analyze] MISSING   : {list.Missing.Count:N0}  (listed but not on disk - named below)");
        if (list.NotAudio.Count > 0)
            Console.WriteLine($"[analyze] Not audio : {list.NotAudio.Count:N0}  (named below)");

        if (list.Files.Count == 0)
        {
            Console.Error.WriteLine("[analyze] ERROR: the list contains no audio file that exists on disk.");
            PrintLeftBehind(list);
            return 1;
        }

        // The database root: explicit --root, else the registered root that covers the whole list.
        var dbRoot = root ?? RegistryRootCovering(
            list.Files.Select(f => f.Path).ToList(), LibraryIndexRegistry.Roots());

        return Analyze(list.Files, dbRoot, indexPath, threads, limit, dbPath, noDb, force,
                       mayCreateRootDb: false, list: list);
    }

    // -- The list -------------------------------------------------------------------

    /// <summary>
    /// What a playlist run was handed: the files it will analyse (with their cheap keys), plus
    /// everything it will NOT analyse, by name. Nothing is dropped silently - a track that moved
    /// or was deleted has to be visible in the output, or the run quietly does less than it says.
    /// </summary>
    internal readonly record struct PlaylistBatch(
        int Listed,
        List<AudioFiles.ScannedFile> Files,
        List<string> Missing,
        List<string> NotAudio);

    /// <summary>
    /// Resolve the playlist and stat every entry once. Sorted by path so the work order matches the
    /// directory form's, which is what keeps <c>--limit</c> and resume deterministic.
    /// </summary>
    internal static PlaylistBatch LoadPlaylist(string playlistPath)
    {
        // ResolvePaths does NOT filter by existence - that is the point here (ReadPaths would drop
        // the gone files before we could name them).
        var referenced = M3uPlaylist.ResolvePaths(playlistPath);

        var files    = new List<AudioFiles.ScannedFile>(referenced.Count);
        var missing  = new List<string>();
        var notAudio = new List<string>();

        foreach (var path in referenced)
        {
            if (!AudioFiles.IsAudio(path))
            {
                notAudio.Add(path);
                continue;
            }

            long size;
            DateTime modifiedUtc;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    missing.Add(path);
                    continue;
                }
                size        = info.Length;
                modifiedUtc = info.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                // Unreachable share, illegal characters, permissions: from the list's point of
                // view the file is not there. Reported, never dropped.
                missing.Add(path);
                continue;
            }

            files.Add(new AudioFiles.ScannedFile(path, size, modifiedUtc));
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new PlaylistBatch(referenced.Count, files, missing, notAudio);
    }

    /// <summary>
    /// The single registered root that contains EVERY one of <paramref name="paths"/>, or null when
    /// the paths span several roots (or reach outside all of them). Null means "no database this
    /// run" - writing rows into a database whose root does not contain them would put a folder's
    /// index out of step with its own folder, and picking one of several roots would be a guess.
    /// Pure so the containment rule is testable without the machine's registry file.
    /// </summary>
    internal static string? RegistryRootCovering(IReadOnlyList<string> paths, IReadOnlyList<string> roots)
    {
        if (paths.Count == 0 || roots.Count == 0) return null;

        string? common = null;
        foreach (var path in paths)
        {
            var root = LibraryIndexRegistry.RootFor(roots, Path.GetDirectoryName(path));
            if (root is null) return null;
            if (common is null) common = root;
            else if (!string.Equals(common, root, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return common;
    }

    // -- The pass -------------------------------------------------------------------

    private static int Analyze(
        List<AudioFiles.ScannedFile> files,
        string? root,
        string indexPath,
        int threads,
        int limit,
        string? dbPath,
        bool noDb,
        bool force,
        bool mayCreateRootDb,
        PlaylistBatch? list)
    {
        // -- The local library database (this machine's index) ----------------
        var (db, dbLocation) = OpenLibraryDb(root, dbPath, noDb, mayCreateRootDb);
        using var _ = db;

        Console.WriteLine($"[analyze] Index     : {indexPath}");
        Console.WriteLine($"[analyze] Library db: {dbLocation}");
        Console.WriteLine($"[analyze] Threads   : {threads}");
        Console.WriteLine($"[analyze] Detection : v{TrackIndex.CurrentDetectionVersion}" +
                          (force ? "  (--force: re-running DSP on everything)" : ""));
        if (limit < int.MaxValue)
            Console.WriteLine($"[analyze] Limit     : {limit}");

        // Load existing index for resumability
        var index = TrackIndex.Load(indexPath);
        Console.WriteLine($"[analyze] Existing entries: {index.Entries.Count:N0}");

        var allFiles = limit < files.Count ? files.Take(limit).ToList() : files;

        Console.WriteLine($"[analyze] Files to process: {allFiles.Count:N0}");

        var dbState = db?.LoadSyncState();

        // -- Compose the analysis stack once ---------------------------------
        using var composer = EngineComposer.Build();

        var startTime = DateTime.UtcNow;
        var processed = 0;
        var skipped   = 0;
        var imported  = 0;
        var analysed  = 0;
        var failed    = 0;
        var since     = 0; // files since last flush
        var lockObj   = new object();
        var pending   = new List<TrackIndexEntry>(FlushEveryN);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
        };

        Parallel.ForEach(allFiles, parallelOptions, file =>
        {
            var filePath = file.Path;
            try
            {
                // -- 1. Cheap-key resume: no file is opened to answer this ----
                if (!force && IsAlreadyDone(file, index, dbState, lockObj))
                {
                    lock (lockObj)
                    {
                        skipped++;
                        PrintProgress(++processed, allFiles.Count, startTime, skipped, failed, "[skip]");
                    }
                    return;
                }

                // -- 2. Tags (needed either way) ------------------------------
                TrackMetadata? meta = null;
                try
                {
                    meta = composer.MetadataReader.Read(filePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[analyze] WARN: tag read failed for {Path.GetFileName(filePath)}: {ex.Message}");
                    // Continue - DSP can still run without tags.
                }

                // -- 3. Already measured at the current version? Import it. ---
                TrackIndexEntry? entry = null;
                var status = "  ok ";

                if (!force && meta is not null)
                {
                    entry = TrackIndexMapping.FromStoredBlob(
                        filePath, file.SizeBytes, file.ModifiedUtc, meta,
                        minVersion: TrackIndex.CurrentDetectionVersion);

                    if (entry is not null)
                    {
                        status = "[tags]";
                        Interlocked.Increment(ref imported);
                    }
                }

                // -- 4. Otherwise: hash + full DSP ---------------------------
                if (entry is null)
                {
                    string hash;
                    try
                    {
                        hash = AudioFiles.Sha256(filePath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[analyze] WARN: cannot hash {filePath}: {ex.Message}");
                        Interlocked.Increment(ref failed);
                        return;
                    }

                    AnalysisResult? result = null;
                    string?         error  = null;
                    try
                    {
                        using var cts = new CancellationTokenSource();
                        result = composer.AnalysisService.AnalyzeAsync(filePath, null, cts.Token)
                                         .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        Console.Error.WriteLine($"[analyze] WARN: analysis failed for {Path.GetFileName(filePath)}: {ex.Message}");
                        Interlocked.Increment(ref failed);
                        status = " FAIL";
                    }

                    if (error is null) Interlocked.Increment(ref analysed);

                    entry = TrackIndexMapping.ToIndexEntry(
                        filePath, hash, file.SizeBytes, file.ModifiedUtc, meta, result,
                        detectionVersion: TrackIndex.CurrentDetectionVersion, error: error);
                }

                // -- 5. Persist (serialised) ---------------------------------
                lock (lockObj)
                {
                    index.Entries[filePath] = entry;
                    pending.Add(entry);
                    since++;
                    PrintProgress(++processed, allFiles.Count, startTime, skipped, failed, status);

                    // Flush periodically for durability
                    if (since >= FlushEveryN)
                    {
                        index.Save(indexPath);
                        db?.UpsertMany(pending);
                        pending.Clear();
                        since = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[analyze] ERROR (unexpected): {filePath}: {ex.Message}");
                Interlocked.Increment(ref failed);
            }
        });

        // Final save
        index.Save(indexPath);
        if (pending.Count > 0) db?.UpsertMany(pending);

        Console.WriteLine();
        if (list is { } batch)
        {
            Console.WriteLine($"[analyze] Done. FromList={batch.Listed}  Analysed={analysed}  " +
                              $"ImportedFromTags={imported}  Skipped={skipped}  Failed={failed}  " +
                              $"Missing={batch.Missing.Count}" +
                              (batch.NotAudio.Count > 0 ? $"  NotAudio={batch.NotAudio.Count}" : ""));
            PrintLeftBehind(batch);
        }
        else
        {
            Console.WriteLine($"[analyze] Done. Analysed={analysed}  ImportedFromTags={imported}  " +
                              $"Skipped={skipped}  Failed={failed}");
        }
        Console.WriteLine($"[analyze] Index entries total: {index.Entries.Count:N0}");
        Console.WriteLine($"[analyze] Index written to: {indexPath}");
        if (db is not null)
            Console.WriteLine($"[analyze] Library db rows: {db.Count:N0}");

        return failed > 0 ? 2 : 0; // exit 2 = partial failure (some files failed)
    }

    /// <summary>
    /// Name every listed file this run did not analyse. Counts are for the eye, names are for the
    /// fix - a count alone cannot tell her WHICH track went missing, and a track she cannot name
    /// is a track that stays lost.
    /// </summary>
    private static void PrintLeftBehind(PlaylistBatch batch)
    {
        if (batch.Missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[analyze] MISSING - listed but not on disk ({batch.Missing.Count:N0}):");
            foreach (var path in batch.Missing)
                Console.WriteLine($"    {path}");
        }

        if (batch.NotAudio.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[analyze] NOT AUDIO - listed but not a format we analyse ({batch.NotAudio.Count:N0}):");
            foreach (var path in batch.NotAudio)
                Console.WriteLine($"    {path}");
        }
    }

    /// <summary>
    /// Pick this machine's library database for the run, and say in one string what happened - the
    /// caller prints it, so "no database" is never silent.
    ///
    /// <para><paramref name="mayCreateRootDb"/> is the guard: only a run that OWNS the root (the
    /// directory form) may bring its database into existence and register it. An explicit
    /// <c>--db &lt;path&gt;</c> is a file the user named, so it is created either way and never
    /// registered - it is a one-off, not a claim on the root.</para>
    /// </summary>
    private static (LibraryDb? Db, string Location) OpenLibraryDb(
        string? root, string? dbPath, bool noDb, bool mayCreateRootDb)
    {
        if (noDb) return (null, "(none: --no-db)");

        string location;
        if (dbPath is not null)
        {
            location = Path.GetFullPath(dbPath);
        }
        else if (root is null)
        {
            return (null, "(none: no root for this list - pass --root <dir> or --db <path> to also "
                        + "update a library database; the index is written either way)");
        }
        else
        {
            location = LibraryDb.DefaultPathFor(root);
            if (!mayCreateRootDb && !File.Exists(location))
                return (null, $"(none: {root} has no library database yet - run 'analyze <root>' "
                            + "once to build one; the index is written either way)");
        }

        try
        {
            var db = LibraryDb.Open(location);
            // Only the DEFAULT location means "this root's index" - an explicit --db-path is a
            // one-off file and must not claim the root for the whole suite.
            if (dbPath is null) LibraryIndexRegistry.Register(root);
            return (db, location);
        }
        catch (Exception ex)
        {
            // A locked or unwritable database must not cost her the analysis run.
            Console.Error.WriteLine($"[analyze] WARN: cannot open library db ({ex.Message}) - index only.");
            return (null, $"(failed: {location})");
        }
    }

    /// <summary>
    /// True if an existing entry - in the sidecar index or in this machine's database - still
    /// matches the file's cheap key AND holds a finished analysis. Opens nothing.
    ///
    /// <para>Entries written before 0.6 have no recorded mtime, so they never match here and fall
    /// through to a tag read. That costs ~57 ms each and usually ends in a blob import, which is
    /// still vastly cheaper than the SHA-256 pass the old resume check performed on every file.</para>
    /// </summary>
    private static bool IsAlreadyDone(
        AudioFiles.ScannedFile file,
        TrackIndex index,
        Dictionary<string, SyncKey>? dbState,
        object lockObj)
    {
        if (dbState is not null &&
            dbState.TryGetValue(file.Path, out var key) &&
            key.Analysed &&
            key.Matches(file.SizeBytes, file.ModifiedUtc))
            return true;

        lock (lockObj)
        {
            return index.Entries.TryGetValue(file.Path, out var entry)
                && entry.Success
                && entry.LooksUnchanged(file.SizeBytes, file.ModifiedUtc);
        }
    }

    private static void PrintProgress(int n, int total, DateTime start, int skipped, int failed, string status)
    {
        var pct     = total > 0 ? (double)n / total * 100.0 : 0;
        var elapsed = DateTime.UtcNow - start;
        var eta     = n > 0 && total > n
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds / n * (total - n))
            : TimeSpan.Zero;
        Console.Write($"\r  {status} {n,6}/{total,-6} ({pct,5:F1}%)  " +
                      $"skip={skipped,-5} fail={failed,-5} " +
                      $"elapsed={elapsed:hh\\:mm\\:ss}  ETA={eta:hh\\:mm\\:ss}   ");
    }
}

/// <summary>
/// The parsed <c>analyze</c> command line. It lives next to the command (not inside Program's
/// top-level statements) because the verb now has TWO mutually exclusive input forms, and the rule
/// for which one wins - plus what is still required in each - is logic worth testing rather than
/// prose in a help text.
/// </summary>
internal sealed record AnalyzeArgs(
    string? Root,
    string? Playlist,
    string  IndexPath,
    int     Threads,
    int     Limit,
    string? DbPath,
    bool    NoDb,
    bool    Force)
{
    /// <summary>True when this run analyses a LIST of files rather than a directory tree. The
    /// playlist wins whenever it is present - a root given alongside it only picks the database.</summary>
    public bool UsesPlaylist => Playlist is not null;

    /// <summary>
    /// Parse <c>analyze</c>'s arguments, or return null with the message to print.
    ///
    /// <para>A leading token that is not a flag is the directory form's <c>&lt;root&gt;</c>;
    /// <c>--root</c> says the same thing and is the only way to give a root alongside
    /// <c>--playlist</c>. When both a playlist and a root are present the PLAYLIST decides which
    /// files are analysed (same precedence as <c>tag clean</c> and <c>squeeze</c>) and the root is
    /// used only to pick the library database.</para>
    /// </summary>
    public static AnalyzeArgs? Parse(string[] args, out string? error)
    {
        var positionalRoot = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : null;
        var flags = positionalRoot is null ? args : args[1..];

        var playlist = CliArgs.ParseStringFlag(flags, "--playlist");
        var root     = positionalRoot ?? CliArgs.ParseStringFlag(flags, "--root");

        if (playlist is null && root is null)
        {
            error = "analyze requires a <root> directory or --playlist <m3u>.";
            return null;
        }

        var indexPath = CliArgs.ParseStringFlag(flags, "--index");
        if (indexPath is null)
        {
            error = "analyze requires --index <path>.";
            return null;
        }

        error = null;
        return new AnalyzeArgs(
            Root:      root,
            Playlist:  playlist,
            IndexPath: indexPath,
            Threads:   CliArgs.ParseIntFlag(flags, "--threads", Environment.ProcessorCount),
            Limit:     CliArgs.ParseIntFlag(flags, "--limit", int.MaxValue),
            DbPath:    CliArgs.ParseStringFlag(flags, "--db"),
            NoDb:      CliArgs.ParseBoolFlag(flags, "--no-db"),
            Force:     CliArgs.ParseBoolFlag(flags, "--force"));
    }
}
