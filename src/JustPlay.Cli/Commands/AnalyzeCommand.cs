using JustPlay.Core.Models;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay analyze &lt;root&gt; --index &lt;path&gt; [--threads N] [--limit N] [--db &lt;path&gt;|--no-db] [--force]</c>
///
/// PHASE 1 — the heavy resumable analysis pass. For each audio file under <c>root</c>:
///   1. Cheap key (size + mtime, straight from the directory listing): unchanged and already
///      analysed → skip without opening the file.
///   2. Read tag metadata. A JUSTPLAY blob at the CURRENT detection version is imported as-is —
///      that analysis has already been paid for, on this machine or the other one.
///   3. Otherwise run the full DSP pipeline (BPM → key → energy → loudness → beat fingerprint
///      → RhythmPattern) and hash the file (for dedup).
///   4. Write the entry to the sidecar index AND to this machine's library database.
///
/// <para><b>What changed in 0.6 and why.</b> This command used to SHA-256 every file first, with
/// the comment "cheap — no decode needed". Measured 2026-07-30 against <c>\\nas\music\GENRES</c>,
/// it is not cheap over SMB: hashing reads every byte, i.e. the whole library through the network
/// on every run, while the directory entry that answers "did this change?" costs 0.23 ms. Files
/// are now hashed only when they are actually analysed.</para>
///
/// <para><b>READ-ONLY on the audio library</b> — no tags are written here (that is <c>promote</c>).
/// Outputs are the index file and the local database.</para>
/// </summary>
internal static class AnalyzeCommand
{
    private const int FlushEveryN = 50;

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

        // ── The local library database (this machine's index) ────────────────
        LibraryDb? db = null;
        var dbLocation = "(none)";
        if (!noDb)
        {
            dbLocation = dbPath is not null
                ? Path.GetFullPath(dbPath)
                : LibraryDb.DefaultPathFor(root);
            try
            {
                db = LibraryDb.Open(dbLocation);
                // Only the DEFAULT location means "this root's index" — an explicit --db-path is a
                // one-off file and must not claim the root for the whole suite.
                if (dbPath is null) LibraryIndexRegistry.Register(root);
            }
            catch (Exception ex)
            {
                // A locked or unwritable database must not cost her the analysis run.
                Console.Error.WriteLine($"[analyze] WARN: cannot open library db ({ex.Message}) — index only.");
                dbLocation = $"(failed: {dbLocation})";
            }
        }

        using var _ = db;

        Console.WriteLine($"[analyze] Root      : {root}");
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

        // One enumeration, carrying size + mtime — a follow-up FileInfo per file would be a
        // second round-trip each over SMB.
        var allFiles = AudioFiles.EnumerateWithKeys(root)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (limit < allFiles.Count)
            allFiles = allFiles.Take(limit).ToList();

        Console.WriteLine($"[analyze] Files to process: {allFiles.Count:N0}");

        var dbState = db?.LoadSyncState();

        // ── Compose the analysis stack once ─────────────────────────────────
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
                // ── 1. Cheap-key resume: no file is opened to answer this ────
                if (!force && IsAlreadyDone(file, index, dbState, lockObj))
                {
                    lock (lockObj)
                    {
                        skipped++;
                        PrintProgress(++processed, allFiles.Count, startTime, skipped, failed, "[skip]");
                    }
                    return;
                }

                // ── 2. Tags (needed either way) ──────────────────────────────
                TrackMetadata? meta = null;
                try
                {
                    meta = composer.MetadataReader.Read(filePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[analyze] WARN: tag read failed for {Path.GetFileName(filePath)}: {ex.Message}");
                    // Continue — DSP can still run without tags.
                }

                // ── 3. Already measured at the current version? Import it. ───
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

                // ── 4. Otherwise: hash + full DSP ───────────────────────────
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

                // ── 5. Persist (serialised) ─────────────────────────────────
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
        Console.WriteLine($"[analyze] Done. Analysed={analysed}  ImportedFromTags={imported}  " +
                          $"Skipped={skipped}  Failed={failed}");
        Console.WriteLine($"[analyze] Index entries total: {index.Entries.Count:N0}");
        Console.WriteLine($"[analyze] Index written to: {indexPath}");
        if (db is not null)
            Console.WriteLine($"[analyze] Library db rows: {db.Count:N0}");

        return failed > 0 ? 2 : 0; // exit 2 = partial failure (some files failed)
    }

    /// <summary>
    /// True if an existing entry — in the sidecar index or in this machine's database — still
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
