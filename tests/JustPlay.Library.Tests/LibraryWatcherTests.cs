using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Library.Tests;

/// <summary>
/// The thin adapter (0.6, P4). These tests never construct a real
/// <see cref="System.IO.FileSystemWatcher"/> or wait on a real <see cref="System.Threading.Timer"/>
/// — <see cref="LibraryWatcher.Start"/>/<see cref="LibraryWatcher.Stop"/> (the only methods that
/// touch either) are exercised by running the app, not by this suite. Instead, every test drives the
/// same internal seams <c>Start()</c> wires the real OS callbacks to
/// (<see cref="LibraryWatcher.HandleRawEvent"/> / <see cref="LibraryWatcher.HandleError"/> /
/// <see cref="LibraryWatcher.Tick"/>) directly, with a fake clock and real (but small, local,
/// temp-directory) files for <see cref="LibrarySync"/> to actually read — deterministic, no sleeps,
/// no dependence on the OS ever delivering a notification.
/// </summary>
public sealed class LibraryWatcherTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-watcher-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly LibraryDb _db;
    private readonly FakeReader _tags = new();
    private readonly LibrarySync _sync;

    public LibraryWatcherTests()
    {
        Directory.CreateDirectory(_root);
        // ".db" is dot-prefixed, so AudioFiles' own enumeration already ignores it — same trick
        // LibrarySyncTests uses to keep the index file out of the library it indexes.
        _db   = LibraryDb.Open(Path.Combine(_root, ".db", "index.db"));
        _sync = new LibrarySync(_db, _tags);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Test doubles / helpers (mirrors LibrarySyncTests' own private fixtures) ─

    private sealed class FakeReader : IMetadataReader
    {
        public readonly Dictionary<string, TrackMetadata> ByPath = new(StringComparer.OrdinalIgnoreCase);

        public TrackMetadata Read(string filePath) =>
            ByPath.TryGetValue(filePath, out var m)
                ? m
                : new TrackMetadata { FallbackName = Path.GetFileNameWithoutExtension(filePath) };

        public EditableTags ReadEditable(string filePath) => new();
    }

    private static TrackAnalysisState Blob() => new()
    {
        Version = TrackAnalysisState.CurrentVersion,
        Detected = new AnalysisResult
        {
            Bpm = 145.0,
            Key = new MusicalKey(9, KeyMode.Minor),
            Energy = 8,
            GridConfidence = 0.81,
            Harshness = 0.2,
        },
    };

    private string MakeFile(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio");

        _tags.ByPath[path] = new TrackMetadata
        {
            FallbackName = Path.GetFileNameWithoutExtension(path),
            Title = Path.GetFileNameWithoutExtension(path),
            Duration = TimeSpan.FromMinutes(6),
            StoredAnalysis = Blob(),
        };

        return path;
    }

    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private LibraryWatcher MakeWatcher(
        FakeClock clock, Func<bool>? mayWorkNow = null, LibraryWatcherOptions? options = null) =>
        new(_root, _sync, mayWorkNow ?? (() => true), options, clock.Read);

    private static readonly LibraryWatcherOptions FastSettle = new()
    {
        SettleWindow  = TimeSpan.FromSeconds(10),
        SweepInterval = TimeSpan.FromMinutes(10),
        TickInterval  = TimeSpan.FromSeconds(2),
    };

    // =========================================================================
    // 1. Translating raw events into Touch / MarkDirty.
    // =========================================================================

    [Fact]
    public void An_audio_file_event_queues_a_touch()
    {
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleRawEvent(WatcherChangeTypes.Changed, Path.Combine(_root, "a.mp3"));

        Assert.Equal(1, watcher.PendingCount);
        Assert.False(watcher.IsDirty);
    }

    [Fact]
    public void A_non_audio_created_or_changed_event_is_ignored_not_marked_dirty()
    {
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        // No extension at all — the shape of a folder being created.
        watcher.HandleRawEvent(WatcherChangeTypes.Created, Path.Combine(_root, "NewGenre"));
        watcher.HandleRawEvent(WatcherChangeTypes.Changed, Path.Combine(_root, "cover.jpg"));

        Assert.Equal(0, watcher.PendingCount);
        Assert.False(watcher.IsDirty);
    }

    [Fact]
    public void A_deleted_audio_file_queues_a_touch()
    {
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleRawEvent(WatcherChangeTypes.Deleted, Path.Combine(_root, "gone.mp3"));

        Assert.Equal(1, watcher.PendingCount);
        Assert.False(watcher.IsDirty);
    }

    [Fact]
    public void A_deleted_event_with_no_audio_extension_marks_the_root_dirty()
    {
        // FileSystemWatcher fires exactly ONE Deleted event for a whole folder removal — never one
        // per file inside it — so a non-audio-shaped Deleted path is treated as "maybe a folder":
        // there is no way to know which tracks it held, so fall back to a full sweep.
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleRawEvent(WatcherChangeTypes.Deleted, Path.Combine(_root, "OldGenreFolder"));

        Assert.Equal(0, watcher.PendingCount);
        Assert.True(watcher.IsDirty);
    }

    [Fact]
    public void A_plain_file_rename_touches_both_the_old_and_new_path()
    {
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleRawEvent(
            WatcherChangeTypes.Renamed,
            fullPath: Path.Combine(_root, "new-name.mp3"),
            oldFullPath: Path.Combine(_root, "old-name.mp3"));

        Assert.Equal(2, watcher.PendingCount);
        Assert.False(watcher.IsDirty);
    }

    [Fact]
    public void A_folder_rename_marks_the_root_dirty_instead_of_guessing()
    {
        var renamedFolder = Path.Combine(_root, "RenamedGenre");
        Directory.CreateDirectory(renamedFolder);   // the new path really is a directory

        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleRawEvent(
            WatcherChangeTypes.Renamed,
            fullPath: renamedFolder,
            oldFullPath: Path.Combine(_root, "OldGenreName"));

        Assert.Equal(0, watcher.PendingCount);
        Assert.True(watcher.IsDirty);
    }

    [Fact]
    public void HandleError_marks_the_root_dirty()
    {
        var watcher = MakeWatcher(new FakeClock(), options: FastSettle);

        watcher.HandleError(new InternalBufferOverflowException("too many changes"));

        Assert.True(watcher.IsDirty);
    }

    // =========================================================================
    // 2. Tick() — yielding to the gate.
    // =========================================================================

    [Fact]
    public void Tick_does_nothing_while_the_gate_is_closed()
    {
        var clock = new FakeClock();
        var synced = 0;
        var watcher = MakeWatcher(clock, mayWorkNow: () => false, options: FastSettle);
        watcher.Synced += (_, _) => synced++;

        watcher.HandleRawEvent(WatcherChangeTypes.Changed, MakeFile("a.mp3"));
        clock.Now += TimeSpan.FromSeconds(30);   // long past the settle window
        watcher.Tick();

        Assert.Equal(0, synced);
        Assert.Equal(1, watcher.PendingCount);   // postponed, NOT dropped — still queued
        Assert.Null(_db.TryGet(Path.Combine(_root, "a.mp3")));
    }

    [Fact]
    public void Tick_resumes_once_the_gate_reopens()
    {
        var clock = new FakeClock();
        var gateOpen = false;
        var watcher = MakeWatcher(clock, mayWorkNow: () => gateOpen, options: FastSettle);

        var path = MakeFile("a.mp3");
        watcher.HandleRawEvent(WatcherChangeTypes.Changed, path);
        clock.Now += TimeSpan.FromSeconds(30);

        watcher.Tick();                          // gate closed: nothing happens
        Assert.Equal(1, watcher.PendingCount);

        gateOpen = true;
        watcher.Tick();                          // gate open: the same queued touch now lands

        Assert.Equal(0, watcher.PendingCount);
        Assert.NotNull(_db.TryGet(path));
    }

    // =========================================================================
    // 3. Tick() — settled paths become a VerifyTracks batch.
    // =========================================================================

    [Fact]
    public void A_settled_new_file_is_indexed_via_VerifyTracks()
    {
        var clock = new FakeClock();
        LibraryWatcherSyncedEventArgs? seen = null;
        var watcher = MakeWatcher(clock, options: FastSettle);
        watcher.Synced += (_, e) => seen = e;

        var path = MakeFile("fresh.mp3");
        watcher.HandleRawEvent(WatcherChangeTypes.Created, path);

        clock.Now += TimeSpan.FromSeconds(9);
        watcher.Tick();
        Assert.Null(_db.TryGet(path));           // not settled yet

        clock.Now += TimeSpan.FromSeconds(2);
        watcher.Tick();

        Assert.NotNull(_db.TryGet(path));
        Assert.NotNull(seen);
        Assert.False(seen!.WasFullSweep);
        Assert.NotNull(seen.Verify);
        Assert.Equal(1, seen.Verify!.Added);
    }

    [Fact]
    public void A_tick_with_nothing_settled_and_no_sweep_due_raises_no_event()
    {
        var clock = new FakeClock();
        var synced = 0;
        var watcher = MakeWatcher(clock, options: FastSettle);
        watcher.Synced += (_, _) => synced++;

        watcher.Tick();

        Assert.Equal(0, synced);
    }

    // =========================================================================
    // 4. Tick() — the dirty flag and the periodic interval both force a full sweep.
    // =========================================================================

    [Fact]
    public void A_dirty_root_gets_a_full_sweep_and_the_flag_clears_afterward()
    {
        var clock = new FakeClock();
        LibraryWatcherSyncedEventArgs? seen = null;
        var watcher = MakeWatcher(clock, options: FastSettle);
        watcher.Synced += (_, e) => seen = e;

        var path = MakeFile("indexed-by-sweep.mp3");
        watcher.HandleError(new IOException("share dropped"));   // marks dirty directly, no per-file event at all

        watcher.Tick();

        Assert.NotNull(seen);
        Assert.True(seen!.WasFullSweep);
        Assert.NotNull(seen.Sweep);
        Assert.NotNull(_db.TryGet(path));         // the sweep found it even though nothing ever touched it
        Assert.False(watcher.IsDirty);
    }

    [Fact]
    public void The_periodic_interval_forces_a_sweep_with_no_watcher_activity_at_all()
    {
        var clock = new FakeClock();
        LibraryWatcherSyncedEventArgs? seen = null;
        var options = FastSettle with { SweepInterval = TimeSpan.FromMinutes(5) };
        var watcher = MakeWatcher(clock, options: options);
        watcher.Synced += (_, e) => seen = e;

        var path = MakeFile("found-by-periodic-sweep.mp3");

        clock.Now += TimeSpan.FromMinutes(4);
        watcher.Tick();
        Assert.Null(seen);                         // not due yet, nothing queued either

        clock.Now += TimeSpan.FromMinutes(2);
        watcher.Tick();

        Assert.NotNull(seen);
        Assert.True(seen!.WasFullSweep);
        Assert.NotNull(_db.TryGet(path));
    }

    [Fact]
    public void A_full_sweep_takes_priority_over_a_settled_batch_in_the_same_tick()
    {
        var clock = new FakeClock();
        LibraryWatcherSyncedEventArgs? seen = null;
        var watcher = MakeWatcher(clock, options: FastSettle);
        watcher.Synced += (_, e) => seen = e;

        var touched = MakeFile("touched.mp3");
        watcher.HandleRawEvent(WatcherChangeTypes.Changed, touched);
        clock.Now += TimeSpan.FromSeconds(30);      // long settled
        watcher.HandleError(new IOException("forced dirty for test"));   // and also dirty

        watcher.Tick();

        Assert.True(seen!.WasFullSweep);
        // The settled touch is still whatever it was — a full Reconcile does not drain the settle
        // buffer, it just makes THIS tick's answer a superset check instead.
    }

    // =========================================================================
    // 5. Re-entrancy: the timer must not stack a second Reconcile on top of one
    //    still running (the first sweep of a fresh index can take minutes).
    // =========================================================================

    [Fact]
    public async Task Overlapping_ticks_are_ignored_while_one_is_in_flight()
    {
        var enteredGate = new SemaphoreSlim(0);
        var releaseGate  = new ManualResetEventSlim(false);
        var gateCalls = 0;

        bool Gate()
        {
            Interlocked.Increment(ref gateCalls);
            enteredGate.Release();
            releaseGate.Wait(TimeSpan.FromSeconds(5));
            return true;
        }

        var watcher = MakeWatcher(new FakeClock(), mayWorkNow: Gate, options: FastSettle);

        var first = Task.Run(watcher.Tick);
        Assert.True(await enteredGate.WaitAsync(TimeSpan.FromSeconds(5)), "first tick never entered the gate");

        // A second call while the first is still blocked inside the gate must return immediately —
        // that is the guard, proven by the gate itself never being entered a second time.
        var second = Task.Run(watcher.Tick);
        var secondFinished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))) == second;
        Assert.True(secondFinished, "a concurrent tick was not a no-op");

        releaseGate.Set();
        await first;

        Assert.Equal(1, gateCalls);
    }
}
