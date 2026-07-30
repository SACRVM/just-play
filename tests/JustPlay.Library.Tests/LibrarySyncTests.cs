using System.IO;
using System.Linq;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Library.Tests;

/// <summary>
/// The sync pass. The load-bearing assertion in here is the cheap one: an unchanged library must
/// cause ZERO file opens — measured 2026-07-30, an open costs ~250× a directory entry.
/// </summary>
public sealed class LibrarySyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-sync-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDb _db;
    private readonly FakeReader _tags = new();
    private readonly LibrarySync _sync;

    public LibrarySyncTests()
    {
        Directory.CreateDirectory(_root);
        _db   = LibraryDb.Open(Path.Combine(_root, ".db", "index.db"));
        _sync = new LibrarySync(_db, _tags);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Test doubles / helpers ────────────────────────────────────────────────

    private sealed class FakeReader : IMetadataReader
    {
        public readonly Dictionary<string, TrackMetadata> ByPath = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Opened = [];
        public readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase);

        public TrackMetadata Read(string filePath)
        {
            Opened.Add(filePath);
            if (Unreadable.Contains(filePath)) throw new IOException("file is locked");
            return ByPath.TryGetValue(filePath, out var m)
                ? m
                : new TrackMetadata { FallbackName = Path.GetFileNameWithoutExtension(filePath) };
        }

        public EditableTags ReadEditable(string filePath) => new();
    }

    private static TrackAnalysisState Blob(int version = TrackAnalysisState.CurrentVersion) =>
        new()
        {
            Version = version,
            Detected = new AnalysisResult
            {
                Bpm = 145.0,
                Key = new MusicalKey(9, KeyMode.Minor),   // A minor → 8A
                Energy = 8,
                GridConfidence = 0.81,
                Harshness = 0.2,
            },
        };

    /// <summary>Creates a file with deterministic content and returns its path.</summary>
    private string MakeFile(string relative, string content = "audio", TrackAnalysisState? blob = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        _tags.ByPath[path] = new TrackMetadata
        {
            FallbackName = Path.GetFileNameWithoutExtension(path),
            Title = Path.GetFileNameWithoutExtension(path),
            Artist = "Artist",
            Duration = TimeSpan.FromMinutes(6),
            StoredAnalysis = blob,
        };

        return path;
    }

    // ── The cheap-key promise ─────────────────────────────────────────────────

    [Fact]
    public void An_unchanged_library_opens_nothing_on_the_second_pass()
    {
        MakeFile(@"GENRES\Techno\a.mp3", blob: Blob());
        MakeFile(@"GENRES\Techno\b.mp3", blob: Blob());

        var first = _sync.Reconcile(_root);
        Assert.Equal(2, first.ImportedFromTags);
        Assert.Equal(2, _tags.Opened.Count);

        _tags.Opened.Clear();
        var second = _sync.Reconcile(_root);

        Assert.Empty(_tags.Opened);          // ← the whole point
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.Opened);
    }

    [Fact]
    public void A_changed_file_is_re_read()
    {
        var path = MakeFile(@"a.mp3", blob: Blob());
        _sync.Reconcile(_root);
        _tags.Opened.Clear();

        File.WriteAllText(path, "audio, but longer now");   // size changes

        var report = _sync.Reconcile(_root);

        Assert.Equal([path], _tags.Opened);
        Assert.Equal(1, report.ImportedFromTags);
        Assert.Equal(0, report.Unchanged);
    }

    // ── Import instead of DSP ─────────────────────────────────────────────────

    [Fact]
    public void A_stored_blob_becomes_a_full_entry_without_any_analysis()
    {
        var path = MakeFile(@"a.mp3", blob: Blob());

        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.ImportedFromTags);
        Assert.Empty(report.QueuedForAnalysis);

        var entry = _db.TryGet(path);
        Assert.NotNull(entry);
        Assert.True(entry.Success);
        Assert.Equal(145.0, entry.Bpm);
        Assert.Equal("8A", entry.KeyCamelot);
        Assert.Equal(8, entry.Energy);
        Assert.Equal(0.81, entry.GridConfidence);
        // The blob's own version becomes the entry's detection version — that field finally means
        // something (the CLI's legacy constant is 1 regardless of what actually ran).
        Assert.Equal(TrackAnalysisState.CurrentVersion, entry.DetectionVersion);
    }

    [Fact]
    public void A_file_without_a_blob_is_queued_but_still_shows_up_in_the_library()
    {
        var path = MakeFile(@"fresh-download.mp3");   // no blob

        var report = _sync.Reconcile(_root);

        Assert.Equal([path], report.QueuedForAnalysis);
        Assert.Equal(0, report.ImportedFromTags);

        // Never leave a song behind: it is IN the index, marked as not analysed.
        var entry = _db.TryGet(path);
        Assert.NotNull(entry);
        Assert.True(entry.NeedsAnalysis);
        Assert.Equal("fresh-download", entry.Title);            // tags were still captured
        Assert.Empty(_db.Query(new LibraryQuery()));            // hidden from a normal query
        Assert.Single(_db.Query(new LibraryQuery { SuccessOnly = false }));
    }

    [Fact]
    public void A_blob_older_than_the_floor_is_queued_for_re_analysis()
    {
        var path = MakeFile(@"old.mp3", blob: Blob(version: 8));

        var report = _sync.Reconcile(_root, new SyncOptions { MinBlobVersion = 9 });

        Assert.Equal([path], report.QueuedForAnalysis);
        Assert.Equal(0, report.ImportedFromTags);
    }

    [Fact]
    public void By_default_any_blob_is_trusted_as_is()
    {
        MakeFile(@"old.mp3", blob: Blob(version: 6));

        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.ImportedFromTags);
        Assert.Empty(report.QueuedForAnalysis);
    }

    [Fact]
    public void An_unreadable_file_is_counted_and_queued_but_never_aborts_the_sweep()
    {
        var ok     = MakeFile(@"ok.mp3", blob: Blob());
        var locked = MakeFile(@"locked.mp3", blob: Blob());
        _tags.Unreadable.Add(locked);

        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.TagReadFailed);
        Assert.Equal([locked], report.QueuedForAnalysis);
        Assert.Equal(1, report.ImportedFromTags);
        Assert.NotNull(_db.TryGet(ok));
    }

    // ── Never leave a song behind ─────────────────────────────────────────────

    [Fact]
    public void A_vanished_file_is_flagged_not_deleted()
    {
        var stays = MakeFile(@"stays.mp3", blob: Blob());
        var goes  = MakeFile(@"goes.mp3", blob: Blob());
        _sync.Reconcile(_root);

        File.Delete(goes);
        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.MarkedMissing);
        Assert.Equal(2, _db.Count);                                   // still known
        Assert.NotNull(_db.TryGet(goes));
        Assert.Single(_db.Query(new LibraryQuery()));                 // hidden
        Assert.Equal(2, _db.Query(new LibraryQuery { IncludeMissing = true }).Count);
        Assert.NotNull(_db.TryGet(stays));
    }

    [Fact]
    public void A_file_that_comes_back_unchanged_is_recovered_without_being_re_read()
    {
        var path = MakeFile(@"flaky.mp3", blob: Blob());
        _sync.Reconcile(_root);

        var size     = new FileInfo(path).Length;
        var modified = File.GetLastWriteTimeUtc(path);
        var bytes    = File.ReadAllBytes(path);

        File.Delete(path);
        Assert.Equal(1, _sync.Reconcile(_root).MarkedMissing);

        // Same bytes, same timestamp — e.g. the share was simply offline for a while.
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Equal(size, new FileInfo(path).Length);

        _tags.Opened.Clear();
        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.Recovered);
        Assert.Empty(_tags.Opened);                        // recovery is one UPDATE, not a re-read
        Assert.Single(_db.Query(new LibraryQuery()));
    }

    // ── What is not library content ───────────────────────────────────────────

    [Fact]
    public void Recycle_bins_and_dot_folders_are_not_the_library()
    {
        MakeFile(@"GENRES\real.mp3", blob: Blob());
        MakeFile(@"#recycle\deleted.mp3", blob: Blob());
        MakeFile(@"@eaDir\thumb.mp3", blob: Blob());
        MakeFile(@".Trashes\gone.mp3", blob: Blob());

        var report = _sync.Reconcile(_root);

        Assert.Equal(1, report.Scanned);
        Assert.Equal(1, report.ImportedFromTags);
    }

    // ── Staleness is a separate question from the filesystem ──────────────────

    [Fact]
    public void FindStale_surfaces_what_a_rule_rejects_without_touching_the_disk()
    {
        MakeFile(@"good.mp3", blob: Blob());
        MakeFile(@"no-analysis.mp3");
        _sync.Reconcile(_root);
        _tags.Opened.Clear();

        var stale = _sync.FindStale(new StalenessPolicy().With(StaleRule.NeverAnalysed()));

        Assert.Single(stale);
        Assert.EndsWith("no-analysis.mp3", stale[0]);
        Assert.Empty(_tags.Opened);
    }

    [Fact]
    public void The_report_reads_like_a_log_line()
    {
        MakeFile(@"a.mp3", blob: Blob());
        var text = _sync.Reconcile(_root).ToString();

        Assert.Contains("1 on disk", text);
        Assert.Contains("1 imported from tags", text);
    }
}
