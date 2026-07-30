using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Library;

namespace JustPlay.App.Services;

/// <summary>
/// The app's handle on THIS machine's library index (0.6).
///
/// <para>The database is per library root and is opened lazily, so a user who never picks a root —
/// or never leaves FOLDER scope — pays nothing. Changing the root closes the old handle and opens
/// the new one; everything else in the app just asks for query results.</para>
/// </summary>
public interface ILibraryIndexService
{
    /// <summary>The library root this index belongs to, or null before one is picked.</summary>
    string? Root { get; }

    /// <summary>True when a database is open and can be queried.</summary>
    bool IsReady { get; }

    /// <summary>Rows in the index (0 when it has never been scanned).</summary>
    int Count { get; }

    /// <summary>Points the service at a library root. Cheap and idempotent for the same root.</summary>
    void UseRoot(string? root);

    /// <summary>Runs a query. Returns empty when no index is open — never throws for that reason.</summary>
    IReadOnlyList<TrackIndexEntry> Query(LibraryQuery query);

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

    public bool IsReady
    {
        get { lock (_gate) return _db is not null; }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                try { return _db?.Count ?? 0; }
                catch (Exception) { return 0; }
            }
        }
    }

    public event EventHandler? Changed;

    public void UseRoot(string? root)
    {
        lock (_gate)
        {
            if (string.Equals(Root, root, StringComparison.OrdinalIgnoreCase) && _db is not null)
                return;

            _db?.Dispose();
            _db  = null;
            Root = root;

            if (string.IsNullOrWhiteSpace(root)) return;

            try
            {
                _db = LibraryDb.OpenForRoot(root);
            }
            catch (Exception ex)
            {
                // A busy or unwritable database must never stop the finder from browsing folders.
                Console.WriteLine($"[Library] cannot open index for {root}: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<TrackIndexEntry> Query(LibraryQuery query)
    {
        lock (_gate)
        {
            if (_db is null) return [];
            try { return _db.Query(query); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Library] query failed: {ex.Message}");
                return [];
            }
        }
    }

    public async Task<SyncReport?> SyncAsync(
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return null;

        LibraryDb? db;
        lock (_gate) db = _db;
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
