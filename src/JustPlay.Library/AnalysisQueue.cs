namespace JustPlay.Library;

/// <summary>What <see cref="AnalysisQueue.TryLease"/> decided, and - when it declined - why.</summary>
public enum AnalysisLease
{
    /// <summary>A path was handed out and is now in flight. The caller MUST finish the lease with
    /// <see cref="AnalysisQueue.NoteSucceeded"/> / <see cref="AnalysisQueue.NoteFailed"/> /
    /// <see cref="AnalysisQueue.NoteSkipped"/> / <see cref="AnalysisQueue.Release"/>.</summary>
    Leased,

    /// <summary>Nothing is waiting. Checked FIRST, before pause and before the gate: a drained batch
    /// is finished even if it happens to be paused, so a worker never idles on an empty queue.</summary>
    Empty,

    /// <summary>Work is waiting but the user paused the batch. Nothing was removed from the queue.</summary>
    Paused,

    /// <summary>Work is waiting but the caller's gate is closed - a track is playing or JUST STREAM
    /// is on air. Nothing was removed from the queue; it is POSTPONED, never dropped.</summary>
    Yielding,
}

/// <summary>Coarse state for the shared floating <c>BusyOverlay</c>'s phase line.</summary>
public enum AnalysisPhase
{
    /// <summary>Nothing queued, nothing done yet.</summary>
    Idle,

    /// <summary>Running (or about to lease the next file).</summary>
    Analysing,

    /// <summary>Explicitly paused by the user. Files already in flight are still finishing -
    /// see <see cref="AnalysisBatchProgress.InFlight"/>.</summary>
    Paused,

    /// <summary>Yielding to playback / being on air. Same in-flight caveat as <see cref="Paused"/>.</summary>
    Yielding,

    /// <summary>The queue is drained: nothing pending, nothing in flight.</summary>
    Done,
}

/// <summary>One track the batch could not analyse. Every failure is named - a track is never
/// silently dropped (standing invariant, memory <c>never-leave-songs-behind</c>).</summary>
/// <param name="Path">The file that failed.</param>
/// <param name="Message">The exception message, already flattened to a string so the report can
/// outlive the exception and cross a thread without carrying a stack.</param>
public sealed record AnalysisFailure(string Path, string Message);

/// <summary>
/// A snapshot of a running batch, shaped for the shared floating <c>BusyOverlay</c>: a phase plus
/// counts, exactly like <see cref="SyncProgress"/> is for the scan. (!!) Never render this as inline
/// progress that shifts the layout - the overlay floats and is anti-flicker on purpose
/// (memory <c>progress-overlay-shared</c>).
/// </summary>
/// <param name="Phase">Coarse state for the phase line.</param>
/// <param name="Done">Finished one way or another = <paramref name="Succeeded"/> +
/// <paramref name="Failed"/> + <paramref name="Skipped"/>.</param>
/// <param name="Total">Everything this batch knows about: done + in flight + still pending. Grows if
/// more paths are enqueued mid-run.</param>
/// <param name="Succeeded">Files the DSP delegate completed without throwing.</param>
/// <param name="Failed">Files the DSP delegate threw on. The batch kept going.</param>
/// <param name="Skipped">Files that no longer needed analysis by the time their turn came (someone
/// else - the observer, the CLI, the other machine's tag write - got there first).</param>
/// <param name="InFlight">Files being analysed right now. Non-zero while <see cref="AnalysisPhase.Paused"/>
/// or <see cref="AnalysisPhase.Yielding"/> means "the tail is still finishing".</param>
/// <param name="CurrentPath">The most recently STARTED file. With several workers there are several
/// in flight; this is the newest one, which is what a single-line overlay can honestly show.</param>
/// <param name="Elapsed">WORKING time only - the wall clock while at least one file was in flight.
/// Pauses and gate-closed stretches do not count, so a batch paused for a two-hour set does not come
/// back claiming it took two hours.</param>
/// <param name="EstimatedRemaining">Null until enough files have actually run to mean anything. Based
/// on <paramref name="Succeeded"/> + <paramref name="Failed"/> only - a skipped file costs no DSP, so
/// counting it would make the estimate wildly optimistic.</param>
public readonly record struct AnalysisBatchProgress(
    AnalysisPhase Phase,
    int Done,
    int Total,
    int Succeeded,
    int Failed,
    int Skipped,
    int InFlight,
    string? CurrentPath,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    /// <summary>Still to go: in flight + pending.</summary>
    public int Remaining => Total - Done;

    /// <summary>0..1 for a progress bar, or null when there is nothing to divide by.</summary>
    public double? Fraction => Total > 0 ? (double)Done / Total : null;
}

/// <summary>
/// The pure decision core behind <see cref="AnalysisBatchRunner"/> - no threads, no timers, no
/// filesystem, no database, no audio (milestone 0.6, P2 - "batch scan / analyse inside JUST PLAY").
/// Everything here is deterministic given an injected clock, which is what makes the awkward cases -
/// pause mid-file, the gig-safe gate flipping, cancellation, resume - testable without a single
/// sleep. Same idiom as <see cref="WatchQueue"/> (P4), <see cref="JustPlay.Core.Playback.CueArbiter"/>
/// and <see cref="JustPlay.Core.Playback.PendingTagWriteQueue"/>.
///
/// <para><b>The gap it closes.</b> <see cref="LibrarySync.Reconcile"/> already returns
/// <see cref="SyncReport.QueuedForAnalysis"/> - every indexed file with no analysis. Measured on
/// Chloe's real index 2026-07-31: 18,009 rows, 14,994 analysed, <b>3,015 needing analysis</b>, 0
/// failed. Today the app throws that list away. This is the queue that consumes it.</para>
///
/// <para><b>Everything is a decision, nothing is an action.</b> <see cref="TryLease"/> is the single
/// point where "may another file start right now?" is answered - pause, the gig-safe gate and
/// emptiness are all resolved there, in one place, in one order, so there is exactly one thing to
/// test and exactly one thing that can be wrong.</para>
///
/// <para><b>Work is postponed, never dropped.</b> A closed gate and a pause both leave the queue
/// untouched. A cancelled lease goes back to the FRONT via <see cref="Release"/> so the same file is
/// the next one tried. Nothing in this class can lose a track.</para>
/// </summary>
public sealed class AnalysisQueue
{
    /// <summary>
    /// How many files must have actually run before <see cref="AnalysisBatchProgress.EstimatedRemaining"/>
    /// is offered at all. One or two samples of a several-second decode produce an estimate that is
    /// worse than none - a wrong ETA is not a small error, it is a number she would plan around.
    /// </summary>
    private const int MinSamplesForEstimate = 3;

    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();

    // Order matters (the caller queued them in sync-report order), and Release has to push back to
    // the FRONT - hence a linked list rather than a Queue<T>. The set is membership only.
    private readonly LinkedList<string> _pending = new();
    private readonly HashSet<string> _queued   = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // Kept in full, never capped: the failure list is HOW a failed track gets surfaced, and a capped
    // list would silently stop naming tracks. 3,015 entries of (path, message) is well under a MB.
    private readonly List<AnalysisFailure> _failures = [];

    private int _succeeded;
    private int _failed;
    private int _skipped;
    private bool _paused;
    private string? _lastLeased;

    // "Working time": accumulated only while at least one file is in flight, so pauses and
    // gate-closed stretches are excluded for free - and, because it is wall clock across N workers,
    // it already accounts for concurrency without this class knowing what the concurrency IS.
    private TimeSpan _activeElapsed;
    private DateTimeOffset? _activeSince;

    /// <param name="clock">Injectable "now" for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>
    /// (mirrors <see cref="WatchQueue"/> and <c>PendingTagWriteQueue</c>).</param>
    public AnalysisQueue(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Files waiting for a worker.</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Files being analysed right now.</summary>
    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }

    /// <summary>Succeeded + failed + skipped.</summary>
    public int DoneCount { get { lock (_gate) return _succeeded + _failed + _skipped; } }

    /// <summary>True while the user has paused the batch. Independent of the gig-safe gate.</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>Nothing pending and nothing in flight.</summary>
    public bool IsDrained { get { lock (_gate) return _pending.Count == 0 && _inFlight.Count == 0; } }

    /// <summary>Every track this batch could not analyse, in the order they failed.</summary>
    public IReadOnlyList<AnalysisFailure> Failures { get { lock (_gate) return _failures.ToArray(); } }

    // -- Filling the queue ----------------------------------------------------

    /// <summary>
    /// Adds one path unless it is already pending or in flight. Deliberately does NOT remember paths
    /// that already completed: re-adding a finished path is an explicit caller decision (a retry),
    /// not an accident to defend against, and remembering every completed path would grow a set for
    /// the whole run to prevent something nobody does by mistake.
    /// </summary>
    /// <returns>True if it was added.</returns>
    public bool Enqueue(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (_gate)
        {
            if (_inFlight.Contains(path) || !_queued.Add(path)) return false;
            _pending.AddLast(path);
            return true;
        }
    }

    /// <summary>Adds many paths (typically <see cref="SyncReport.QueuedForAnalysis"/>).</summary>
    /// <returns>How many were actually added.</returns>
    public int EnqueueMany(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
            if (Enqueue(path)) added++;
        return added;
    }

    // -- Pause / resume -------------------------------------------------------

    /// <summary>
    /// Stops new files from being leased. (!) Nothing queued is discarded and nothing in flight is
    /// interrupted - "pause must not lose the queue". The tail of at most (worker count) files
    /// finishes; see <see cref="AnalysisBatchRunner"/>'s class doc for why finishing beats aborting.
    /// </summary>
    public void Pause() { lock (_gate) _paused = true; }

    /// <summary>Lets leasing continue where it left off. Idempotent.</summary>
    public void Resume() { lock (_gate) _paused = false; }

    // -- The one decision -----------------------------------------------------

    /// <summary>
    /// The single "may another file start right now?" decision. Order is deliberate:
    /// <b>empty -> paused -> gate -> lease</b>.
    ///
    /// <para>Emptiness is checked FIRST so a drained batch reports <see cref="AnalysisLease.Empty"/>
    /// even while paused or gated - otherwise a worker would sit in a poll loop forever on a queue
    /// with nothing in it, and the run would never complete.</para>
    ///
    /// <para>The gate is passed IN rather than held as a <c>Func&lt;bool&gt;</c> so this class stays
    /// pure and the whole matrix (paused x gated x empty) is one table-driven test. The library layer
    /// must never know what playback IS - same rule as <see cref="LibraryWatcher"/>'s
    /// <c>mayWorkNow</c>.</para>
    /// </summary>
    /// <param name="mayWorkNow">False = a track is playing or the stream is on air. Postpones, never drops.</param>
    /// <param name="path">The leased file, when the result is <see cref="AnalysisLease.Leased"/>.</param>
    public AnalysisLease TryLease(bool mayWorkNow, out string? path)
    {
        path = null;

        lock (_gate)
        {
            if (_pending.Count == 0) return AnalysisLease.Empty;
            if (_paused)             return AnalysisLease.Paused;
            if (!mayWorkNow)         return AnalysisLease.Yielding;

            var node = _pending.First!;
            _pending.RemoveFirst();
            _queued.Remove(node.Value);

            EnterFlight(node.Value);
            path = _lastLeased = node.Value;
            return AnalysisLease.Leased;
        }
    }

    // -- Closing a lease ------------------------------------------------------

    /// <summary>The DSP delegate returned without throwing.</summary>
    public void NoteSucceeded(string path)
    {
        lock (_gate)
        {
            if (!LeaveFlight(path)) return;
            _succeeded++;
        }
    }

    /// <summary>
    /// The DSP delegate threw. Recorded by name and counted; the batch KEEPS GOING - one unreadable
    /// file must not cost the other 3,014. The run is not retried automatically (a decode that fails
    /// fails again, and retrying burns the same NAS read twice); the caller can feed
    /// <see cref="Failures"/> back into a fresh run.
    /// </summary>
    public void NoteFailed(string path, string message)
    {
        lock (_gate)
        {
            if (!LeaveFlight(path)) return;
            _failed++;
            _failures.Add(new AnalysisFailure(path, message));
        }
    }

    /// <summary>
    /// The file no longer needed analysis when its turn came - somebody else got there first. Counted
    /// separately from success on purpose: a skip cost no DSP, so it must not be allowed to flatter
    /// the throughput estimate, and it must not read as "we analysed 3,015 tracks" when we did not.
    /// </summary>
    public void NoteSkipped(string path)
    {
        lock (_gate)
        {
            if (!LeaveFlight(path)) return;
            _skipped++;
        }
    }

    /// <summary>
    /// Hands an in-flight lease back to the FRONT of the queue without counting it - the run was
    /// cancelled mid-file. The same file is therefore the next one tried when the batch resumes, and
    /// the counters stay honest (it neither succeeded nor failed; it never finished).
    /// </summary>
    public void Release(string path)
    {
        lock (_gate)
        {
            if (!LeaveFlight(path)) return;
            if (_queued.Add(path)) _pending.AddFirst(path);
        }
    }

    // -- Progress -------------------------------------------------------------

    /// <summary>
    /// The current state, shaped for the shared floating <c>BusyOverlay</c>. Takes the gate for the
    /// same reason <see cref="TryLease"/> does: the phase line has to be able to say
    /// <see cref="AnalysisPhase.Yielding"/> ("a track is playing") rather than a bare stall - a
    /// progress bar that stops with no reason on it is one she stops trusting.
    /// </summary>
    public AnalysisBatchProgress Snapshot(bool mayWorkNow)
    {
        lock (_gate)
        {
            var done      = _succeeded + _failed + _skipped;
            var remaining = _pending.Count + _inFlight.Count;
            var elapsed   = ElapsedLocked();

            var phase =
                remaining == 0 ? (done > 0 ? AnalysisPhase.Done : AnalysisPhase.Idle)
                : _paused      ? AnalysisPhase.Paused
                : !mayWorkNow  ? AnalysisPhase.Yielding
                :                AnalysisPhase.Analysing;

            // Only files that actually ran the DSP carry timing information.
            var worked = _succeeded + _failed;
            TimeSpan? eta = null;
            if (worked >= MinSamplesForEstimate && remaining > 0 && elapsed > TimeSpan.Zero)
                eta = TimeSpan.FromTicks((long)(elapsed.Ticks / (double)worked * remaining));

            return new AnalysisBatchProgress(
                phase,
                done,
                done + remaining,
                _succeeded,
                _failed,
                _skipped,
                _inFlight.Count,
                _inFlight.Count > 0 ? _lastLeased : null,
                elapsed,
                eta);
        }
    }

    /// <summary>Working time so far - see <see cref="AnalysisBatchProgress.Elapsed"/>.</summary>
    public TimeSpan Elapsed { get { lock (_gate) return ElapsedLocked(); } }

    // -- Internals (all called under _gate) -----------------------------------

    private void EnterFlight(string path)
    {
        if (_inFlight.Count == 0) _activeSince = _now();
        _inFlight.Add(path);
    }

    /// <returns>False when the path was not actually in flight - a double-complete, which must never
    /// move a counter twice.</returns>
    private bool LeaveFlight(string path)
    {
        if (!_inFlight.Remove(path)) return false;

        if (_inFlight.Count == 0 && _activeSince is { } since)
        {
            _activeElapsed += _now() - since;
            _activeSince = null;
        }

        return true;
    }

    private TimeSpan ElapsedLocked() =>
        _activeSince is { } since ? _activeElapsed + (_now() - since) : _activeElapsed;
}
