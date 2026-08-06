using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Library;

namespace JustPlay.App.Services;

/// <summary>
/// The app's handle on THIS machine's library index (0.6).
///
/// <para><b>Indexing is opt-in and must stay opt-in.</b> Nothing here touches the disk until
/// somebody asks for library data: reads open the database only if it already exists, and the file
/// is CREATED by exactly one action — an explicit scan. A user who browses folders and never turns
/// on "+ subfolders" ends up with no index file at all, and the finder behaves exactly as it did
/// in 0.5.</para>
///
/// <para>The database is per library root; changing the root just drops the handle.</para>
/// </summary>
public interface ILibraryIndexService
{
    /// <summary>The library root this index belongs to, or null before one is picked.</summary>
    string? Root { get; }

    /// <summary>True when this root has an index on disk. False = never scanned; nothing was created.</summary>
    bool HasIndex { get; }

    /// <summary>Rows in the index (0 when it has never been scanned).</summary>
    int Count { get; }

    /// <summary>
    /// Points the service at a library root. Records it and drops any previous handle — it does NOT
    /// open or create anything, so merely opening the finder never leaves an index file behind.
    /// </summary>
    void UseRoot(string? root);

    /// <summary>Runs a query. Returns empty when no index is open — never throws for that reason.</summary>
    IReadOnlyList<TrackIndexEntry> Query(LibraryQuery query);

    /// <summary>
    /// What the index knows about a specific set of files. Empty when there is no index — the
    /// caller then falls back to reading tags, per row.
    /// </summary>
    IReadOnlyDictionary<string, TrackIndexEntry> LookupMany(IReadOnlyList<string> paths);

    /// <summary>
    /// The tracks the index holds for exactly this folder (not its sub-folders). Empty means the
    /// folder was never indexed — the caller then reads it from disk.
    /// </summary>
    IReadOnlyList<TrackIndexEntry> QueryFolder(string folder);

    /// <summary>
    /// Checks one folder against the disk and repairs the index if it drifted. Cheap when nothing
    /// changed (a fingerprint comparison, no writes). Meant to run BEHIND a listing that was
    /// already painted from the index, never in front of it.
    /// </summary>
    Task<FolderVerifyResult?> VerifyFolderAsync(string folder, CancellationToken ct = default);

    /// <summary>
    /// The same check for an explicit list of tracks — what a playlist needs, since it has no folder
    /// to fingerprint. Also meant to run behind an already-painted list.
    /// </summary>
    Task<TrackVerifyResult?> VerifyTracksAsync(IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Forgets a folder's stored fingerprint, so the next check cannot early-out on it. This is what
    /// an explicit Refresh does — the escape hatch for anything the cheap comparison cannot see.
    /// </summary>
    void ForgetFolderState(string folder);

    /// <summary>
    /// The genres this library actually uses, most-used first — what the tag editor suggests from
    /// before falling back to a canned list. Empty when there is no index yet (never creates one).
    /// </summary>
    IReadOnlyList<string> Genres();

    /// <summary>
    /// Brings the index in line with the disk. Long-running on a first run (measured ~13.5 min for
    /// 14k files over SMB — the tag reads dominate), near-instant afterwards, because an unchanged
    /// file is recognised by its directory entry and never opened.
    /// </summary>
    Task<SyncReport?> SyncAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Raised after a sync changed the index, so open views can refresh.</summary>
    event EventHandler? Changed;
}

/// <inheritdoc/>
public sealed class LibraryIndexService(IMetadataReader metadata) : ILibraryIndexService, IDisposable
{
    private readonly Lock _gate = new();
    private LibraryDb? _db;

    public string? Root { get; private set; }

    public bool HasIndex
    {
        get
        {
            lock (_gate)
            {
                if (_db is not null) return true;
                return Root is { Length: > 0 } root && File.Exists(LibraryDb.DefaultPathFor(root));
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                try { return Open(createIfMissing: false)?.Count ?? 0; }
                catch (Exception) { return 0; }
            }
        }
    }

    public event EventHandler? Changed;

    public void UseRoot(string? root)
    {
        lock (_gate)
        {
            if (string.Equals(Root, root, StringComparison.OrdinalIgnoreCase)) return;

            _db?.Dispose();
            _db  = null;
            Root = root;

            // Announce the root to the suite as soon as it is set — but only when an index FILE really
            // exists for it. Doing it here (and not only on the first open) is what lets JUST TAG know
            // about the index without JUST PLAY's finder having been opened once in this session; the
            // File.Exists guard is what stops a root that was never scanned from claiming to be indexed.
            if (root is { Length: > 0 } r && File.Exists(LibraryDb.DefaultPathFor(r)))
                LibraryIndexRegistry.Register(r);
        }
    }

    /// <summary>
    /// The open database for the current root, or null.
    ///
    /// <para><paramref name="createIfMissing"/> is the opt-in gate: readers pass false, so browsing
    /// a library that was never scanned reports "no index" instead of quietly creating one. Only an
    /// explicit scan passes true. Caller holds <see cref="_gate"/>.</para>
    /// </summary>
    private LibraryDb? Open(bool createIfMissing)
    {
        if (_db is not null) return _db;
        if (Root is not { Length: > 0 } root) return null;

        var path = LibraryDb.DefaultPathFor(root);
        if (!createIfMissing && !File.Exists(path)) return null;

        try
        {
            _db = LibraryDb.Open(path);
            // This machine now has an index for this root — record it where the whole suite can see
            // it, so JUST TAG (and anything after it) can answer "is this folder indexed?" without
            // reading JUST PLAY's settings file. Only here, where an index actually EXISTS or was
            // just created by an explicit scan; browsing alone never registers anything.
            LibraryIndexRegistry.Register(root);
        }
        catch (Exception ex)
        {
            // A busy or unwritable database must never stop the finder from browsing folders.
            Console.WriteLine($"[Library] cannot open index for {root}: {ex.Message}");
        }

        return _db;
    }

    public IReadOnlyList<TrackIndexEntry> Query(LibraryQuery query)
    {
        lock (_gate)
        {
            var db = Open(createIfMissing: false);
            if (db is null) return [];
            try { return db.Query(query); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Library] query failed: {ex.Message}");
                return [];
            }
        }
    }

    public IReadOnlyDictionary<string, TrackIndexEntry> LookupMany(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return new Dictionary<string, TrackIndexEntry>();

        lock (_gate)
        {
            var db = Open(createIfMissing: false);
            if (db is null) return new Dictionary<string, TrackIndexEntry>();
            try { return db.LookupMany(paths); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Library] lookup failed: {ex.Message}");
                return new Dictionary<string, TrackIndexEntry>();
            }
        }
    }

    public IReadOnlyList<TrackIndexEntry> QueryFolder(string folder) =>
        Query(new LibraryQuery
        {
            PathPrefix     = folder,
            Recursive      = false,
            SuccessOnly    = false,   // un-analysed tracks are part of the folder too
            IncludeMissing = false,
        });

    public async Task<FolderVerifyResult?> VerifyFolderAsync(string folder, CancellationToken ct = default)
    {
        LibraryDb? db;
        lock (_gate) db = Open(createIfMissing: false);
        if (db is null) return null;

        try
        {
            return await Task.Run(
                () => new LibrarySync(db, metadata).VerifyFolder(folder, options: null, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return null;   // navigated away — a newer check owns the pane
        }
        catch (Exception ex)
        {
            // A flaky share must never break browsing; the listing that is already painted stands.
            Console.WriteLine($"[Library] folder check failed for {folder}: {ex.Message}");
            return null;
        }
    }

    public void ForgetFolderState(string folder)
    {
        lock (_gate)
        {
            try { Open(createIfMissing: false)?.ForgetFolderState(folder); }
            catch (Exception ex) { Console.WriteLine($"[Library] forget folder failed: {ex.Message}"); }
        }
    }

    public IReadOnlyList<string> Genres()
    {
        lock (_gate)
        {
            try { return Open(createIfMissing: false)?.DistinctGenres() ?? []; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Library] genre list failed: {ex.Message}");
                return [];
            }
        }
    }

    public async Task<TrackVerifyResult?> VerifyTracksAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return null;

        LibraryDb? db;
        lock (_gate) db = Open(createIfMissing: false);
        if (db is null) return null;

        try
        {
            return await Task.Run(
                () => new LibrarySync(db, metadata).VerifyTracks(paths, options: null, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Library] track check failed: {ex.Message}");
            return null;
        }
    }

    public async Task<SyncReport?> SyncAsync(
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return null;

        // The one place allowed to bring the index into existence.
        LibraryDb? db;
        lock (_gate) db = Open(createIfMissing: true);
        if (db is null) return null;

        var report = await Task.Run(
            () => new LibrarySync(db, metadata).Reconcile(root, options: null, progress, ct), ct);

        Changed?.Invoke(this, EventArgs.Empty);
        return report;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
        }
    }
}
