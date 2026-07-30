using System.Diagnostics;
using JustPlay.Core.Abstractions;

namespace JustPlay.Library;

/// <summary>
/// Brings this machine's index in line with what is actually on disk.
///
/// <para><b>The rule the whole pass is built around:</b> never open a file you do not have to.
/// Measured 2026-07-30 on <c>\\nas\music\GENRES</c> — a directory entry costs 0.23 ms, opening a
/// file to read its tags costs 57.5 ms, and re-reads are just as slow as cold reads (the wall is
/// the per-file open, not bandwidth: parallelism buys ~15 %). So the sweep enumerates, compares
/// the cheap key, and touches only what actually changed.</para>
///
/// <para><b>Why it needs no DSP.</b> The analysis lives in the file's JUSTPLAY tag. Whichever
/// machine analysed a track first wrote it there, so the other machine imports the full
/// <see cref="Core.Models.AnalysisResult"/> with a tag read and runs nothing. On Chloe's library
/// that covered 400/400 sampled files. Files with no blob are RETURNED for analysis rather than
/// analysed here — DSP needs the BASS adapter, and this project stays platform-agnostic.</para>
///
/// <para>A file that vanished is flagged, never deleted (<see cref="LibraryDb.MarkMissing"/>) —
/// an offline share or a renamed folder must not silently shrink the library.</para>
/// </summary>
public sealed class LibrarySync(LibraryDb db, IMetadataReader metadata)
{
    /// <summary>
    /// Reconciles <paramref name="root"/> into the index. Cheap by design: on an unchanged
    /// library this opens ZERO files.
    /// </summary>
    public SyncReport Reconcile(
        string root,
        SyncOptions? options = null,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var opt   = options ?? SyncOptions.Default;
        var total = Stopwatch.StartNew();

        // ── 1. Enumerate: the only pass over the whole tree ──────────────────
        progress?.Report(new SyncProgress(SyncPhase.Enumerating, 0, 0, null));

        var enumerateWatch = Stopwatch.StartNew();
        var onDisk = AudioFiles.EnumerateWithKeys(root).ToList();
        enumerateWatch.Stop();

        ct.ThrowIfCancellationRequested();

        // ── 2. Compare against the index in memory ───────────────────────────
        progress?.Report(new SyncProgress(SyncPhase.Comparing, 0, onDisk.Count, null));

        var known = db.LoadSyncState();
        var seen  = new HashSet<string>(known.Count, StringComparer.OrdinalIgnoreCase);

        var changed   = new List<AudioFiles.ScannedFile>();
        var unchanged = 0;
        var recovered = 0;

        foreach (var file in onDisk)
        {
            seen.Add(file.Path);

            if (known.TryGetValue(file.Path, out var key) &&
                key.Matches(file.SizeBytes, file.ModifiedUtc))
            {
                unchanged++;
                // It was flagged missing and is back — a one-statement UPDATE, no re-read.
                if (key.Missing && db.ClearMissing(file.Path)) recovered++;
                continue;
            }

            changed.Add(file);
        }

        // ── 3. Only now do we open anything ──────────────────────────────────
        var tagWatch  = Stopwatch.StartNew();
        var imported  = 0;
        var queued    = new List<string>();
        var tagFailed = 0;
        var batch     = new List<TrackIndexEntry>(opt.BatchSize);
        var done      = 0;

        foreach (var file in changed)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress(SyncPhase.ReadingTags, done, changed.Count, file.Path));

            TrackIndexEntry? entry = null;
            try
            {
                var meta = metadata.Read(file.Path);
                entry = TrackIndexMapping.FromStoredBlob(
                    file.Path, file.SizeBytes, file.ModifiedUtc, meta, opt.MinBlobVersion);

                if (entry is not null)
                {
                    imported++;
                }
                else
                {
                    // No usable blob → this one needs real DSP. Index it anyway (tags only) so a
                    // track on disk is never invisible in the library just because it is new.
                    queued.Add(file.Path);
                    if (opt.IndexUnanalysedFiles)
                        entry = TrackIndexMapping.NotAnalysed(
                            file.Path, file.SizeBytes, file.ModifiedUtc, meta);
                }
            }
            catch (Exception)
            {
                // A locked or unreadable file is not a reason to abort a library sweep.
                tagFailed++;
                if (!queued.Contains(file.Path)) queued.Add(file.Path);
            }

            if (entry is not null) batch.Add(entry);

            if (batch.Count >= opt.BatchSize)
            {
                db.UpsertMany(batch);
                batch.Clear();
            }

            done++;
        }

        if (batch.Count > 0) db.UpsertMany(batch);
        tagWatch.Stop();

        // ── 4. What disappeared ──────────────────────────────────────────────
        progress?.Report(new SyncProgress(SyncPhase.Finishing, changed.Count, changed.Count, null));

        var markedMissing = 0;
        if (opt.MarkMissing)
        {
            var gone = known
                .Where(kv => !kv.Value.Missing && !seen.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            if (gone.Count > 0) markedMissing = db.MarkMissing(gone);
        }

        total.Stop();

        return new SyncReport
        {
            Root              = root,
            Scanned           = onDisk.Count,
            Unchanged         = unchanged,
            Recovered         = recovered,
            ImportedFromTags  = imported,
            QueuedForAnalysis = queued,
            TagReadFailed     = tagFailed,
            MarkedMissing     = markedMissing,
            EnumerateElapsed  = enumerateWatch.Elapsed,
            TagReadElapsed    = tagWatch.Elapsed,
            TotalElapsed      = total.Elapsed,
        };
    }

    /// <summary>
    /// Checks ONE folder against the index and repairs it if it drifted — the background half of
    /// painting a directory from the index.
    ///
    /// <para>The early-out is Chloe's fingerprint (2026-07-30): the file count plus the newest
    /// modification time among the folder's tracks. It moves for an added, deleted, renamed OR
    /// retagged file, which the folder's own timestamp does not — measured, <c>GENRES\Bass_House</c>
    /// held a file modified 07-29 while the directory still read 07-17. When it matches, this
    /// returns having written nothing.</para>
    ///
    /// <para>Cheap enough to run behind every folder open: 0.12–0.25 ms per file, measured over SMB
    /// (276 ms for the 1,092-file folder, ~18 ms for a normal one) — but only because it is NOT on
    /// the path that paints the list.</para>
    /// </summary>
    public FolderVerifyResult VerifyFolder(
        string folder, SyncOptions? options = null, CancellationToken ct = default)
    {
        var opt = options ?? SyncOptions.Default;

        var files = AudioFiles.EnumerateWithKeys(folder, recursive: false).ToList();
        var live  = FolderFingerprint.FromFiles(files);

        ct.ThrowIfCancellationRequested();

        if (db.FolderState(folder) is { } stored && stored.Matches(live))
            return new FolderVerifyResult { Folder = folder, Changed = false };

        // Something moved. Compare per file — the fingerprint says THAT it changed, not WHAT.
        var known = db.LookupMany(files.Select(f => f.Path));
        var seen  = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);

        var batch    = new List<TrackIndexEntry>();
        var queued   = new List<string>();
        var added    = 0;
        var updated  = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(file.Path);

            var existing = known.GetValueOrDefault(file.Path);
            if (existing is not null && existing.LooksUnchanged(file.SizeBytes, file.ModifiedUtc))
                continue;   // this one is fine; something else in the folder moved

            TrackIndexEntry? entry = null;
            try
            {
                var meta = metadata.Read(file.Path);
                entry = TrackIndexMapping.FromStoredBlob(
                    file.Path, file.SizeBytes, file.ModifiedUtc, meta, opt.MinBlobVersion);

                if (entry is null)
                {
                    queued.Add(file.Path);
                    if (opt.IndexUnanalysedFiles)
                        entry = TrackIndexMapping.NotAnalysed(
                            file.Path, file.SizeBytes, file.ModifiedUtc, meta);
                }
            }
            catch (Exception)
            {
                if (!queued.Contains(file.Path)) queued.Add(file.Path);
            }

            if (entry is null) continue;

            batch.Add(entry);
            if (existing is null) added++; else updated++;
        }

        if (batch.Count > 0) db.UpsertMany(batch);

        var markedMissing = 0;
        if (opt.MarkMissing)
        {
            var gone = db
                .Query(new LibraryQuery
                {
                    PathPrefix = folder, Recursive = false,
                    SuccessOnly = false, IncludeMissing = false,
                })
                .Select(e => e.FilePath)
                .Where(p => !seen.Contains(p))
                .ToList();

            if (gone.Count > 0) markedMissing = db.MarkMissing(gone);
        }

        db.SetFolderState(folder, live);

        return new FolderVerifyResult
        {
            Folder            = folder,
            Changed           = added > 0 || updated > 0 || markedMissing > 0,
            Added             = added,
            Updated           = updated,
            MarkedMissing     = markedMissing,
            QueuedForAnalysis = queued,
        };
    }

    /// <summary>
    /// Entries that are on disk and unchanged but whose stored analysis a rule rejects — the
    /// FLAC mono-decode debt, a failed run worth retrying, a never-analysed file. Paged so a
    /// very large library does not materialise at once.
    ///
    /// <para>Separate from <see cref="Reconcile"/> on purpose: reconciliation is about the
    /// FILESYSTEM, staleness is about our own past measurements. Mixing them would force the
    /// cheap pass to load full entries it does not need.</para>
    /// </summary>
    public IReadOnlyList<string> FindStale(StalenessPolicy policy)
    {
        var stale = new List<string>();
        foreach (var entry in db.Query(new LibraryQuery { SuccessOnly = false, IncludeMissing = false }))
            if (policy.StaleReason(entry) is not null)
                stale.Add(entry.FilePath);

        return stale;
    }
}

/// <summary>What a <see cref="LibrarySync.VerifyFolder"/> check found.</summary>
public sealed record FolderVerifyResult
{
    public required string Folder { get; init; }

    /// <summary>False = the fingerprint matched, nothing was read or written.</summary>
    public bool Changed { get; init; }

    public int Added { get; init; }
    public int Updated { get; init; }
    public int MarkedMissing { get; init; }

    /// <summary>Files in this folder that still need a real analysis run.</summary>
    public IReadOnlyList<string> QueuedForAnalysis { get; init; } = [];
}

/// <summary>Knobs for a <see cref="LibrarySync.Reconcile"/> pass.</summary>
public sealed record SyncOptions
{
    /// <summary>
    /// Lowest stored blob version to trust. 0 = trust any blob (the default: a stale blob is
    /// trusted as-is and re-analysis is asked for by a <see cref="StaleRule"/>, not by the sweep).
    /// </summary>
    public int MinBlobVersion { get; init; }

    /// <summary>Index files that have no analysis yet, so they are visible but marked. Default true.</summary>
    public bool IndexUnanalysedFiles { get; init; } = true;

    /// <summary>Flag index entries the sweep did not find on disk. Default true.</summary>
    public bool MarkMissing { get; init; } = true;

    /// <summary>Entries per write transaction. One transaction per file would fsync per file.</summary>
    public int BatchSize { get; init; } = 200;

    public static readonly SyncOptions Default = new();
}

/// <summary>What a <see cref="LibrarySync.Reconcile"/> pass did.</summary>
public sealed record SyncReport
{
    public required string Root { get; init; }

    /// <summary>Audio files found on disk.</summary>
    public int Scanned { get; init; }

    /// <summary>Matched the cheap key — not one of these was opened.</summary>
    public int Unchanged { get; init; }

    /// <summary>Previously flagged missing, found again.</summary>
    public int Recovered { get; init; }

    /// <summary>New/changed files whose analysis came straight out of their tags — no DSP.</summary>
    public int ImportedFromTags { get; init; }

    /// <summary>Files that need a real analysis run (no usable blob, or unreadable).</summary>
    public IReadOnlyList<string> QueuedForAnalysis { get; init; } = [];

    /// <summary>Files whose tags could not be read at all.</summary>
    public int TagReadFailed { get; init; }

    /// <summary>Index entries flagged missing by this pass.</summary>
    public int MarkedMissing { get; init; }

    public TimeSpan EnumerateElapsed { get; init; }
    public TimeSpan TagReadElapsed { get; init; }
    public TimeSpan TotalElapsed { get; init; }

    /// <summary>Files this pass actually had to open.</summary>
    public int Opened => ImportedFromTags + QueuedForAnalysis.Count;

    /// <summary>One line for the log / library panel.</summary>
    public override string ToString() =>
        $"{Scanned:N0} on disk · {Unchanged:N0} unchanged · {ImportedFromTags:N0} imported from tags · " +
        $"{QueuedForAnalysis.Count:N0} need analysis · {MarkedMissing:N0} missing · " +
        $"enumerate {EnumerateElapsed.TotalSeconds:F1}s, tags {TagReadElapsed.TotalSeconds:F1}s";
}

public enum SyncPhase
{
    Enumerating,
    Comparing,
    ReadingTags,
    Finishing,
}

/// <param name="Phase">Which stage the pass is in.</param>
/// <param name="Done">Items processed in this stage.</param>
/// <param name="Total">Items in this stage (0 when not yet known — enumeration cannot predict).</param>
/// <param name="CurrentPath">File being read, when applicable.</param>
public readonly record struct SyncProgress(SyncPhase Phase, int Done, int Total, string? CurrentPath);
