namespace JustPlay.Library;

/// <summary>
/// Keeps one machine's library index fresh while the app runs (milestone 0.6, P4 - "the observer").
///
/// <para><b>The periodic sweep is the truth; the watcher is only an accelerator.</b> Measured
/// 2026-07-30 on Chloe's real library (see <c>.claude/milestone-0.6-scope.md</c>, "Measured: what a
/// sync pass costs over SMB"): enumerating all 14,186 files with size+mtime costs 3.2 s total, while
/// opening a file to read its tags costs 57.5 ms. A sweep is therefore cheap enough to run on a
/// timer regardless of what the watcher saw; the <see cref="System.IO.FileSystemWatcher"/> only
/// exists to make a new or changed file show up sooner than the next scheduled sweep. Over SMB the
/// watcher is lossy - buffer overflows, a NAS going to sleep, a dropped and silently-reconnected
/// share - so losing a watcher event must never lose a track. Every place this class cannot
/// represent a change precisely, it falls back to <see cref="WatchQueue.MarkDirty"/> instead of
/// guessing.</para>
///
/// <para><b>Off by default, opt-in per root.</b> Nothing here runs until <see cref="Start"/> is
/// called - constructing an instance does not touch the filesystem, does not open the database
/// (that already happened; the caller hands in an already-open <see cref="LibrarySync"/>), and does
/// not create anything. This mirrors <c>LibraryIndexService</c>'s "indexing is opt-in" contract one
/// layer down.</para>
///
/// <para><b>It yields to playback and to being on air.</b> This project must never know what
/// playback or broadcasting IS (CLAUDE.md's layering rule - no Avalonia, no ManagedBass, and
/// nothing here references either), so the caller injects a <c>mayWorkNow</c> predicate. When it
/// returns false, a tick is a no-op: nothing already queued is discarded, it is simply tried again
/// on the next tick. Work is POSTPONED, never dropped.</para>
/// </summary>
public interface ILibraryWatcher : IDisposable
{
    /// <summary>The library root this watcher covers.</summary>
    string Root { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the <see cref="System.IO.FileSystemWatcher"/> and the periodic tick. Idempotent -
    /// calling it twice is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops watching and cancels any in-flight sweep/verify pass. Idempotent. Whatever was still
    /// settling in the queue is discarded - the caller is choosing to stop caring about this root,
    /// which is not "losing a track": the next <see cref="Start"/>, or a manual scan, reconciles it
    /// from scratch against what is actually on disk.
    /// </summary>
    void Stop();

    /// <summary>
    /// Raised after a background pass (a full sweep or a settled-paths verify) actually ran and had
    /// something to report. Exactly one of <see cref="LibraryWatcherSyncedEventArgs.Sweep"/> /
    /// <see cref="LibraryWatcherSyncedEventArgs.Verify"/> is set, matching
    /// <see cref="LibraryWatcherSyncedEventArgs.WasFullSweep"/>.
    /// </summary>
    event EventHandler<LibraryWatcherSyncedEventArgs>? Synced;
}

/// <summary>Tunables for <see cref="LibraryWatcher"/>. All optional - see each property for the default.</summary>
public sealed record LibraryWatcherOptions
{
    /// <summary>How long a changed file must stay quiet before it is verified. Default 10 s.</summary>
    public TimeSpan SettleWindow { get; init; } = WatchQueue.DefaultSettleWindow;

    /// <summary>How often a full sweep runs regardless of watcher activity. Default 10 minutes.</summary>
    public TimeSpan SweepInterval { get; init; } = WatchQueue.DefaultSweepInterval;

    /// <summary>
    /// How often the internal timer wakes up to check the settle buffer / sweep-due flag. Cheap
    /// (a dictionary scan and a clock read) unless there is actually something to do, so this can
    /// stay well below <see cref="SettleWindow"/> without costing anything. Default 2 s.
    /// </summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static readonly LibraryWatcherOptions Default = new();
}

/// <summary>What a completed background pass found. See <see cref="ILibraryWatcher.Synced"/>.</summary>
public sealed class LibraryWatcherSyncedEventArgs(
    string root, bool wasFullSweep, SyncReport? sweep, TrackVerifyResult? verify) : EventArgs
{
    public string Root { get; } = root;

    /// <summary>True = <see cref="Sweep"/> is set (a full <c>LibrarySync.Reconcile</c> ran).
    /// False = <see cref="Verify"/> is set (a batch of settled paths was checked).</summary>
    public bool WasFullSweep { get; } = wasFullSweep;

    public SyncReport? Sweep { get; } = sweep;
    public TrackVerifyResult? Verify { get; } = verify;
}

/// <inheritdoc cref="ILibraryWatcher"/>
public sealed class LibraryWatcher : ILibraryWatcher
{
    private readonly string _root;
    private readonly LibrarySync _sync;
    private readonly Func<bool> _mayWorkNow;
    private readonly LibraryWatcherOptions _options;
    private readonly WatchQueue _queue;
    private readonly Action<string>? _log;

    private readonly Lock _lifecycleGate = new();
    private FileSystemWatcher? _fsw;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _running;

    // Re-entrancy guard: System.Threading.Timer fires on its own schedule even if the previous
    // callback has not returned. The FIRST sweep of a brand-new index can take on the order of
    // minutes (measured ~13.5 min for 14k files over SMB - see the milestone doc); a short
    // TickInterval must not stack a second concurrent Reconcile on top of that one. 0 = idle, 1 = a
    // tick is currently running.
    private int _ticking;

    /// <param name="root">The library root this watcher covers.</param>
    /// <param name="sync">An already-open <see cref="LibrarySync"/> for that root's index. This
    /// class never opens, creates, or closes a <see cref="LibraryDb"/> - the caller owns that
    /// lifetime, exactly like <c>LibrarySync</c> itself is handed an already-open <c>LibraryDb</c>.</param>
    /// <param name="mayWorkNow">Polled once at the start of every tick. False postpones the tick's
    /// work entirely (nothing queued is discarded) - the gig-safe / on-air yield point. This project
    /// stays platform-agnostic by not knowing WHAT the predicate checks.</param>
    /// <param name="options">Tunables; <see cref="LibraryWatcherOptions.Default"/> if omitted.</param>
    /// <param name="clock">Injectable "now" for the internal <see cref="WatchQueue"/>; defaults to
    /// real time. Tests pass a fake clock and drive <see cref="Start"/>-independent internal seams
    /// instead of waiting on real timers.</param>
    /// <param name="log">Optional diagnostic sink (mirrors <c>LibraryIndexService</c>'s
    /// <c>Console.WriteLine</c> pattern) - a flaky share must never crash the observer, so failures
    /// are swallowed and logged here instead of thrown.</param>
    public LibraryWatcher(
        string root,
        LibrarySync sync,
        Func<bool> mayWorkNow,
        LibraryWatcherOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _root       = root;
        _sync       = sync;
        _mayWorkNow = mayWorkNow;
        _options    = options ?? LibraryWatcherOptions.Default;
        _queue      = new WatchQueue(_options.SettleWindow, _options.SweepInterval, clock);
        _log        = log;
    }

    public string Root => _root;

    public bool IsRunning { get { lock (_lifecycleGate) return _running; } }

    public event EventHandler<LibraryWatcherSyncedEventArgs>? Synced;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_running) return;

            _cts = new CancellationTokenSource();

            _fsw = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                             | NotifyFilters.DirectoryName | NotifyFilters.Size,
                // Default is 8 KB (~a few thousand short-path events). A large drag-drop copy can
                // exceed that; a bigger buffer only reduces how often the Error/overflow fallback
                // below has to fire, it does not replace it - the fallback is what makes overflow
                // safe, not this number.
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Created += (_, e) => HandleRawEvent(WatcherChangeTypes.Created, e.FullPath);
            _fsw.Changed += (_, e) => HandleRawEvent(WatcherChangeTypes.Changed, e.FullPath);
            _fsw.Deleted += (_, e) => HandleRawEvent(WatcherChangeTypes.Deleted, e.FullPath);
            _fsw.Renamed += (_, e) => HandleRawEvent(WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath);
            _fsw.Error   += (_, e) => HandleError(e.GetException());
            _fsw.EnableRaisingEvents = true;

            _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (!_running) return;
            _running = false;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _fsw?.Dispose();
            _fsw = null;

            // Dispose(WaitHandle) would block until any in-flight callback returns; a plain Dispose
            // is enough here because the cancellation above is what actually stops the work - this
            // just stops scheduling new ticks.
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();

    // -- Testable seams -------------------------------------------------------
    //
    // HandleRawEvent / HandleError / Tick contain every bit of actual DECISION logic and take no
    // dependency on FileSystemWatcher or Timer having fired for real - Start() only wires those two
    // up to call the same methods. Tests call them directly with a fake clock (via the WatchQueue
    // constructor above) and real, small, local temp-directory files for LibrarySync to read, so
    // nothing sleeps and nothing depends on the OS actually delivering a watcher event. That is also
    // why these are `internal` rather than `private`: JustPlay.Library.Tests has InternalsVisibleTo.

    /// <summary>
    /// Translates one raw filesystem notification into either a <see cref="WatchQueue.Touch"/> or a
    /// <see cref="WatchQueue.MarkDirty"/>. Never throws - a raw watcher callback runs on BASS/OS
    /// infrastructure threads, so an exception here would surface somewhere with no good handler.
    /// </summary>
    internal void HandleRawEvent(WatcherChangeTypes kind, string fullPath, string? oldFullPath = null)
    {
        try
        {
            if (kind == WatcherChangeTypes.Renamed && oldFullPath is not null)
            {
                // FileSystemWatcher fires exactly ONE Renamed event for a folder rename - never one
                // per file inside it - so there is no way to remap however many tracks it contained.
                // The sweep will see every "moved" file as new and every old path as gone, correctly,
                // on its own; trying to be clever here would mean silently missing files instead.
                if (Directory.Exists(fullPath))
                {
                    _queue.MarkDirty();
                    return;
                }

                if (AudioFiles.IsAudioFileName(Path.GetFileName(oldFullPath.AsSpan())))
                    _queue.Touch(oldFullPath);
                if (AudioFiles.IsAudioFileName(Path.GetFileName(fullPath.AsSpan())))
                    _queue.Touch(fullPath);
                return;
            }

            var fileName = Path.GetFileName(fullPath.AsSpan());

            if (!AudioFiles.IsAudioFileName(fileName))
            {
                // A folder being created/changed is harmless to ignore - its own contents raise
                // their own events. A folder being DELETED is the same one-event problem as a
                // rename: nothing will ever tell us individually which tracks it held.
                if (kind == WatcherChangeTypes.Deleted) _queue.MarkDirty();
                return;
            }

            _queue.Touch(fullPath);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the always-correct fallback is a full sweep, not a lost change.
            _queue.MarkDirty();
            _log?.Invoke($"[LibraryWatcher] event handling failed for '{fullPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// The watcher's own Error event - internal buffer overflow (too many changes between two
    /// deliveries) or the share dropping out from under it. Either way this is exactly what the
    /// dirty flag exists for.
    /// </summary>
    internal void HandleError(Exception ex)
    {
        _queue.MarkDirty();
        _log?.Invoke($"[LibraryWatcher] watcher error for '{_root}': {ex.Message}");
    }

    /// <summary>
    /// One pass: yield to the gate, then either run a full sweep (dirty or due) or verify whatever
    /// settled since the last tick. Guarded against overlapping with itself (see <see cref="_ticking"/>).
    /// </summary>
    internal void Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
            return;   // a previous tick - very possibly a long first sweep - is still running

        try
        {
            if (!_mayWorkNow())
                return;   // postponed, not dropped: nothing above this line touched the queue

            var ct = _cts?.Token ?? CancellationToken.None;

            if (_queue.IsSweepDue)
            {
                var report = _sync.Reconcile(_root, options: null, progress: null, ct);
                _queue.NoteSweepCompleted();
                RaiseSynced(wasFullSweep: true, report, verify: null);
                return;
            }

            // NOTE: DrainReady() removes these paths from the queue before VerifyTracks runs. If
            // VerifyTracks throws below, those specific paths are not re-queued individually - but
            // the catch block marks the whole root dirty, so the NEXT tick's full sweep (a superset
            // check over everything on disk, not just what was popped here) still catches them.
            // "Never silently lose a change" holds through the dirty flag, not through retrying the
            // exact popped list.
            var ready = _queue.DrainReady();
            if (ready.Count == 0) return;

            var result = _sync.VerifyTracks(ready, options: null, ct);
            RaiseSynced(wasFullSweep: false, sweep: null, result);
        }
        catch (OperationCanceledException)
        {
            // Stop() cancelled an in-flight pass - not an error, nothing to log or mark dirty for.
        }
        catch (Exception ex)
        {
            _queue.MarkDirty();
            _log?.Invoke($"[LibraryWatcher] tick failed for '{_root}': {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void RaiseSynced(bool wasFullSweep, SyncReport? sweep, TrackVerifyResult? verify) =>
        Synced?.Invoke(this, new LibraryWatcherSyncedEventArgs(_root, wasFullSweep, sweep, verify));

    // -- Test introspection ---------------------------------------------------

    internal int PendingCount => _queue.PendingCount;
    internal bool IsDirty => _queue.IsDirty;
}
