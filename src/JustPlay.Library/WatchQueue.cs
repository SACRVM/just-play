namespace JustPlay.Library;

/// <summary>
/// The pure decision core behind <see cref="ILibraryWatcher"/> - no IO, no
/// <see cref="System.IO.FileSystemWatcher"/>, no <see cref="System.Threading.Timer"/>. Everything
/// here is deterministic given an injected clock, which is what makes it testable without a single
/// sleep - the same idiom as <see cref="JustPlay.Core.Playback.PendingTagWriteQueue"/>'s injectable
/// <c>Func&lt;DateTime&gt;</c> clock and <see cref="JustPlay.Core.Playback.CueArbiter"/>'s pure
/// "tell me what to do" shape (CLAUDE.md's "the two precedents in this repo").
///
/// <para><b>What it decides (milestone 0.6, P4 - the observer):</b></para>
/// <list type="bullet">
/// <item><description><b>Settling.</b> <see cref="Touch"/> records that a path changed right now;
/// <see cref="DrainReady"/> returns (and forgets) every path that has been quiet for at least the
/// settle window (~10 s by default - a file copied over SMB shows up in a directory listing before
/// it is complete). Touching the SAME path again before it settles overwrites its timestamp - "last
/// touch wins" restarts the wait, which is exactly what you want for an editor re-saving a file five
/// times or a copy still landing bytes.</description></item>
/// <item><description><b>Coalescing.</b> A burst of touches for the same path collapses to one
/// dictionary entry. A burst of touches for many DIFFERENT paths (a 300-file copy, a folder rename
/// handled file-by-file) tends to settle at roughly the same moment, and <see cref="DrainReady"/>
/// hands back everything that is ready in a SINGLE call - never a per-file callback - so the adapter
/// turns that into one <c>LibrarySync.VerifyTracks</c> batch, not a storm of individual passes.</description></item>
/// <item><description><b>The dirty flag.</b> <see cref="MarkDirty"/> is the escape hatch for
/// anything the settle buffer cannot represent precisely: a watcher buffer overflow, a whole-folder
/// rename or delete (<c>FileSystemWatcher</c> fires exactly ONE event for the folder itself, never
/// one per file inside it, so there is no way to remap hundreds of paths from that event alone), or
/// the NAS dropping the connection. It forces the next opportunity to run a full
/// <c>LibrarySync.Reconcile</c> instead of trusting anything this class inferred - "never silently
/// lose a change" beats a precise but fragile reconstruction.</description></item>
/// <item><description><b>Sweep scheduling.</b> <see cref="IsSweepDue"/> / <see cref="NoteSweepCompleted"/>
/// track the periodic full-sweep interval - the mechanism the whole design leans on: measured
/// 2026-07-30: enumerating all 14,186 files costs 3.2 s total, so a full
/// <c>Reconcile</c> is cheap enough to run on a timer as the SOURCE OF TRUTH, with the watcher only
/// ever an accelerator that makes a new file show up sooner. "Is a sweep due" is exactly the same
/// kind of clock-driven decision as "is this path ready to verify", so it lives in the same
/// clock-injectable class rather than a second one, and the whole schedule is testable without ever
/// creating a real timer.</description></item>
/// </list>
///
/// <para><b>What it deliberately does NOT do:</b> open a file, touch the database, or own a
/// <see cref="System.IO.FileSystemWatcher"/>/timer. That is <see cref="LibraryWatcher"/>'s job -
/// this class only ever gets called BY it.</para>
/// </summary>
public sealed class WatchQueue
{
    /// <summary>
    /// A file copied over SMB shows up in the directory listing before its bytes have fully landed.
    /// 10 s is the milestone's chosen settle window (see <c>.claude/milestone-0.6-scope.md</c>, P4).
    /// </summary>
    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the periodic full sweep re-runs even if the watcher reported nothing. Conservative
    /// on purpose: measured 2026-07-30, the full enumeration alone costs 3.2 s for 14,186 files, so
    /// this interval is about tolerating a lossy watcher (buffer overflow, NAS sleep, a missed
    /// reconnect), not about the sweep being expensive.
    /// </summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _settleWindow;
    private readonly TimeSpan _sweepInterval;
    private readonly Func<DateTimeOffset> _now;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _dirty;
    private DateTimeOffset _lastSweepUtc;

    /// <param name="settleWindow">How long a path must stay quiet before it is ready. Defaults to
    /// <see cref="DefaultSettleWindow"/>.</param>
    /// <param name="sweepInterval">How often a full sweep is due regardless of watcher activity.
    /// Defaults to <see cref="DefaultSweepInterval"/>.</param>
    /// <param name="clock">Injectable "now" for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>
    /// (mirrors <c>PendingTagWriteQueue</c>'s <c>Func&lt;DateTime&gt;</c> constructor parameter).</param>
    public WatchQueue(TimeSpan? settleWindow = null, TimeSpan? sweepInterval = null, Func<DateTimeOffset>? clock = null)
    {
        _settleWindow  = settleWindow  ?? DefaultSettleWindow;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        _now = clock ?? (() => DateTimeOffset.UtcNow);

        // A freshly-constructed watcher just inherited whatever state the caller's own index is in
        // (typically right after an explicit "Scan library") - it should not treat that moment as
        // "a sweep is overdue" and immediately repeat the work.
        _lastSweepUtc = _now();
    }

    /// <summary>Paths currently waiting out their settle window (diagnostics + tests).</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>
    /// True from <see cref="MarkDirty"/> until the next <see cref="NoteSweepCompleted"/> - the
    /// watcher believes it may have missed something and a full sweep is owed.
    /// </summary>
    public bool IsDirty { get { lock (_gate) return _dirty; } }

    /// <summary>
    /// Records that <paramref name="path"/> changed right now. Calling this again for the same path
    /// before it settles simply overwrites the timestamp - the wait restarts, so a file still being
    /// written (or an editor saving repeatedly) never gets verified mid-write.
    /// </summary>
    public void Touch(string path)
    {
        lock (_gate) _pending[path] = _now();
    }

    /// <summary>
    /// Removes and returns every path that has been quiet for at least the settle window, as of
    /// right now. Empty means "nothing to do this tick" - not an error, and not a reason to poll
    /// more aggressively.
    /// </summary>
    public IReadOnlyList<string> DrainReady()
    {
        var now = _now();

        lock (_gate)
        {
            if (_pending.Count == 0) return [];

            List<string>? ready = null;
            foreach (var (path, touchedAt) in _pending)
            {
                if (now - touchedAt >= _settleWindow)
                {
                    ready ??= [];
                    ready.Add(path);
                }
            }

            if (ready is null) return [];
            foreach (var path in ready) _pending.Remove(path);
            return ready;
        }
    }

    /// <summary>
    /// Forces the next opportunity to run a full sweep instead of trusting the settle buffer. See
    /// the class doc's "dirty flag" bullet for the cases this covers.
    /// </summary>
    public void MarkDirty() { lock (_gate) _dirty = true; }

    /// <summary>
    /// Call once a full sweep has actually run (whether or not it found anything). Clears
    /// <see cref="IsDirty"/> and restarts the sweep-interval clock - a failed sweep should NOT call
    /// this, so the same interval keeps retrying rather than the caller spinning on every tick.
    /// </summary>
    public void NoteSweepCompleted()
    {
        lock (_gate)
        {
            _dirty = false;
            _lastSweepUtc = _now();
        }
    }

    /// <summary>
    /// True once the sweep interval has elapsed since the last completed sweep, OR the dirty flag is
    /// set. Either is a reason to run a full sweep instead of draining the settle buffer this tick.
    /// </summary>
    public bool IsSweepDue
    {
        get
        {
            lock (_gate)
                return _dirty || _now() - _lastSweepUtc >= _sweepInterval;
        }
    }
}
