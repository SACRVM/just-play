namespace JustPlay.Library;

/// <summary>Tunables for <see cref="AnalysisBatchRunner"/>. All optional — see each property.</summary>
public sealed record AnalysisBatchOptions
{
    /// <summary>
    /// Hard ceiling on <see cref="MaxConcurrency"/>, matching the app's own clamp
    /// (<c>MainWindowViewModel.SetAnalysisThreads</c> clamps the setting to 1..16). One rule, not two.
    /// </summary>
    public const int MaxSupportedConcurrency = 16;

    /// <summary>
    /// How many files may be analysed at once. Comes from <c>UserSettings.AnalysisThreads</c>
    /// (default 4) — ⚠ passed IN by the caller: the library layer never reads settings. Clamped to
    /// 1..<see cref="MaxSupportedConcurrency"/>.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// How long a worker waits before re-checking after a pause or a closed gate. Only ever costs a
    /// lock and a predicate call, so it can be short; 1 s makes "she pressed stop, the batch picks
    /// back up" feel immediate without spinning a core through a two-hour set. Anything negative or
    /// absurd falls back to 1 s rather than hanging the batch.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static readonly AnalysisBatchOptions Default = new();
}

/// <summary>What a <see cref="AnalysisBatchRunner.RunAsync"/> pass did.</summary>
public sealed record AnalysisBatchReport
{
    /// <summary>Everything the batch knew about: done + whatever is left.</summary>
    public int Total { get; init; }

    /// <summary>Files the DSP delegate completed without throwing.</summary>
    public int Succeeded { get; init; }

    /// <summary>Files the DSP delegate threw on. Named individually in <see cref="Failures"/>.</summary>
    public int Failed { get; init; }

    /// <summary>Files that no longer needed analysis when their turn came.</summary>
    public int Skipped { get; init; }

    /// <summary>Still queued — non-zero only for a cancelled run. The queue kept them.</summary>
    public int Remaining { get; init; }

    /// <summary>True when the run stopped because its <see cref="CancellationToken"/> fired.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Every track that failed, by name. ⛔ Never truncated — a dropped name is a dropped track.</summary>
    public IReadOnlyList<AnalysisFailure> Failures { get; init; } = [];

    /// <summary>Working time — pauses and gate-closed stretches excluded.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>One line for the log / library panel.</summary>
    public override string ToString() =>
        $"{Succeeded:N0} analysed · {Skipped:N0} already done · {Failed:N0} failed · " +
        $"{Remaining:N0} left{(Cancelled ? " (stopped)" : "")} · {Elapsed.TotalMinutes:F1} min working";
}

/// <summary>
/// Runs the analysis queue that <see cref="LibrarySync.Reconcile"/> hands back — the thin adapter
/// around <see cref="AnalysisQueue"/> (milestone 0.6, P2). It owns the workers and the waiting;
/// <see cref="AnalysisQueue"/> owns every decision.
///
/// <para><b>It does not know what analysis IS.</b> The DSP arrives as an injected
/// <c>Func&lt;string, CancellationToken, Task&gt;</c>, so this project keeps its layering: no
/// Avalonia, no ManagedBass, no reference to <c>JustPlay.Analysis</c> — and the tests never decode a
/// single sample. ⚠ One delegate, not an "analyse" plus a "persist" pair: the runner never inspects
/// a result, so splitting it would only fix an ordering the caller may legitimately want to own.</para>
///
/// <para><b>The tag-write seam (do NOT build a second write path).</b> Everything the caller wants to
/// happen per file goes INSIDE that one delegate — run the DSP, map the result with
/// <see cref="TrackIndexMapping.ToIndexEntry"/>, upsert it into <see cref="LibraryDb"/>, and, if
/// <c>UserSettings.AutoWriteOnAnalyze</c> is on, hand it to the EXISTING write path
/// (<c>MainWindowViewModel.WriteDetected</c> → <c>Persist</c> → <c>PlaybackController.WithFileReleased</c>
/// / <c>PendingTagWriteQueue</c>), which already defers a write for the currently playing track. The
/// runner deliberately writes nothing itself — not a tag, not a database row.</para>
///
/// <para><b>Gig-safe: it yields to playback and to being on air.</b> <c>mayWorkNow</c> is polled
/// before EVERY file. When it closes:</para>
/// <list type="bullet">
/// <item><description>no new file is leased — so the batch actually stops within one track's decode,
/// with a tail bounded by the worker count;</description></item>
/// <item><description><b>a file already in flight FINISHES.</b> Deliberate, and the opposite choice
/// would be worse: aborting a half-decoded track throws away everything already pulled over SMB and
/// guarantees the same file is read from byte zero again later, so an abort costs MORE NAS traffic
/// than finishing, not less. Finishing also produces a row, which is what makes resume exact. Same
/// shape as <see cref="LibraryWatcher"/>, which checks the gate at tick start and lets the in-flight
/// sweep finish — one rule for the observer and the batch instead of two (memory
/// <c>suite-unify-dont-patch-divergence</c>).</description></item>
/// <item><description>a caller that genuinely needs a HARD stop (app shutdown) has one: the
/// <see cref="CancellationToken"/> is passed straight into the DSP delegate. Yield and cancel are
/// separate controls on purpose — yield is "later", cancel is "stop".</description></item>
/// </list>
///
/// <para><b>Resume across restarts, with no second bookkeeping file.</b> Two mechanisms, both
/// leaning on the index as the memory (her file-as-memory principle, one layer up):</para>
/// <list type="number">
/// <item><description>The caller's delegate writes each finished analysis into the index (and, per
/// policy, into the file's own tags). <see cref="LibrarySync.Reconcile"/> builds
/// <see cref="SyncReport.QueuedForAnalysis"/> from exactly the rows that have no analysis, so after a
/// crash at 900 of 3,015 the next scan queues the remaining 2,115 by construction. Nothing needs to
/// be remembered anywhere else.</description></item>
/// <item><description><c>stillNeedsAnalysis</c> is re-checked at LEASE time, not at queue-build time.
/// A 3,015-file batch runs for hours; meanwhile the observer, the CLI, or the other machine's tag
/// write may have analysed some of them. An indexed lookup costs microseconds against seconds of DSP,
/// so re-asking is free — and files skipped that way are counted, never silently dropped.</description></item>
/// </list>
///
/// <para><b>A failure never stops the batch.</b> The file is recorded by name in
/// <see cref="AnalysisBatchReport.Failures"/>, counted, and the run continues.</para>
/// </summary>
public sealed class AnalysisBatchRunner
{
    private readonly Func<string, CancellationToken, Task> _analyse;
    private readonly Func<bool> _mayWorkNow;
    private readonly Func<string, bool>? _stillNeedsAnalysis;
    private readonly AnalysisBatchOptions _options;
    private readonly Action<string>? _log;
    private readonly AnalysisQueue _queue;

    private int _running;      // 0 = idle, 1 = a batch is in progress (re-entrancy guard)
    private int _lastPhase = -1;

    /// <param name="analyse">The DSP, injected. Called once per file, off the UI thread, with the
    /// run's cancellation token. Whatever else should happen per file (index upsert, tag write via
    /// the existing path) belongs in here — see the class doc's "tag-write seam".</param>
    /// <param name="mayWorkNow">The gig-safe gate: false while a track is playing or JUST STREAM is
    /// on air. Polled before every file. This project stays platform-agnostic by not knowing WHAT the
    /// predicate checks — exactly like <see cref="LibraryWatcher"/>'s parameter of the same name. A
    /// predicate that throws is read as CLOSED (yielding is the conservative answer).</param>
    /// <param name="options">Tunables; <see cref="AnalysisBatchOptions.Default"/> if omitted.</param>
    /// <param name="stillNeedsAnalysis">Optional re-check at lease time — "does this file still have
    /// no analysis?". Typically a <see cref="LibraryDb.TryGet"/> plus
    /// <see cref="TrackIndexEntry.NeedsAnalysis"/>. Omitted = always analyse. If it throws, the file
    /// IS analysed: when we cannot tell, doing the work is the answer that cannot lose a track.</param>
    /// <param name="clock">Injectable "now" for the internal <see cref="AnalysisQueue"/>; defaults to
    /// real time.</param>
    /// <param name="log">Optional diagnostic sink (mirrors <see cref="LibraryWatcher"/>'s). A single
    /// bad file must never crash a batch, so failures are recorded and logged, not thrown.</param>
    public AnalysisBatchRunner(
        Func<string, CancellationToken, Task> analyse,
        Func<bool> mayWorkNow,
        AnalysisBatchOptions? options = null,
        Func<string, bool>? stillNeedsAnalysis = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _analyse            = analyse;
        _mayWorkNow         = mayWorkNow;
        _options            = options ?? AnalysisBatchOptions.Default;
        _stillNeedsAnalysis = stillNeedsAnalysis;
        _log                = log;
        _queue              = new AnalysisQueue(clock);
    }

    /// <summary>The decision core. Exposed so the caller can enqueue more, inspect counts, or keep
    /// the remainder of a cancelled run — the runner never replaces it.</summary>
    public AnalysisQueue Queue => _queue;

    /// <summary>True between entering and leaving <see cref="RunAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>True while paused. Independent of the gig-safe gate — the two are different reasons
    /// with different copy on screen.</summary>
    public bool IsPaused => _queue.IsPaused;

    /// <summary>
    /// Stops new files from starting. ⚠ Does NOT lose the queue and does not interrupt the tail of
    /// files already in flight (see the class doc). Safe to call before, during or after a run.
    /// </summary>
    public void Pause() => _queue.Pause();

    /// <summary>Picks up exactly where the pause left off.</summary>
    public void Resume() => _queue.Resume();

    /// <summary>
    /// Runs the batch to completion (or until <paramref name="ct"/> fires).
    ///
    /// <para>Call it with the paths from a sync report; call it again with no paths to resume a
    /// cancelled run — the queue kept the remainder, including the file that was in flight when the
    /// cancellation landed. Only one run at a time per instance.</para>
    ///
    /// <para>⚠ A cancelled run RETURNS a report with <see cref="AnalysisBatchReport.Cancelled"/> set
    /// rather than throwing <see cref="OperationCanceledException"/>. "Stopped after 412 of 3,015, 4
    /// failed" is information she needs; an exception would throw the counts away with it.</para>
    ///
    /// <para>⚠ While the gate is closed or the batch is paused this task simply does not complete —
    /// the work is postponed, which is the whole point. Cancel to stop for real.</para>
    /// </summary>
    /// <param name="paths">Files to add before starting, typically
    /// <see cref="SyncReport.QueuedForAnalysis"/>. Null = run whatever is already queued.</param>
    /// <param name="progress">Reported on every completed file and on every phase change (paused,
    /// yielding, resumed, done). No timer, no throttle: DSP completions arrive at most a few per
    /// second, which is already below the flicker threshold the shared overlay guards against.</param>
    /// <param name="ct">Hard stop. Also passed into the DSP delegate.</param>
    public async Task<AnalysisBatchReport> RunAsync(
        IEnumerable<string>? paths = null,
        IProgress<AnalysisBatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        // ⚠ Guard BEFORE enqueuing: a rejected call must not have quietly changed the queue of the
        // run that is already going. To add work to a live batch, use Queue.Enqueue deliberately.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("This runner already has a batch in progress.");

        if (paths is not null) _queue.EnqueueMany(paths);

        try
        {
            Volatile.Write(ref _lastPhase, -1);
            Report(progress, SafeGate(), force: true);

            var workers = Math.Clamp(_options.MaxConcurrency, 1, AnalysisBatchOptions.MaxSupportedConcurrency);
            var tasks   = new Task[workers];
            for (var i = 0; i < workers; i++)
                tasks[i] = Task.Run(() => WorkerLoop(progress, ct), CancellationToken.None);

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }

        Report(progress, SafeGate(), force: true);

        var final = _queue.Snapshot(SafeGate());
        return new AnalysisBatchReport
        {
            Total     = final.Total,
            Succeeded = final.Succeeded,
            Failed    = final.Failed,
            Skipped   = final.Skipped,
            Remaining = final.Remaining,
            Cancelled = ct.IsCancellationRequested,
            Failures  = _queue.Failures,
            Elapsed   = final.Elapsed,
        };
    }

    // ── The worker ───────────────────────────────────────────────────────────
    //
    // Every real decision already happened in AnalysisQueue, which is why nothing below needs its own
    // seam: the tests drive RunAsync itself with a fake DSP delegate, where a "long analysis" is a
    // TaskCompletionSource nobody has completed yet rather than a sleep. The only thing here that
    // waits on real time is the poll delay, and tests set that to a few milliseconds.

    /// <summary>
    /// Never negative, never infinite: a bad option must not turn into a hung batch or an unhandled
    /// <see cref="ArgumentOutOfRangeException"/> on a worker thread.
    /// </summary>
    private TimeSpan PollDelay =>
        _options.PollInterval >= TimeSpan.Zero && _options.PollInterval < TimeSpan.FromMinutes(1)
            ? _options.PollInterval
            : TimeSpan.FromSeconds(1);

    private async Task WorkerLoop(IProgress<AnalysisBatchProgress>? progress, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var gate    = SafeGate();
            var outcome = _queue.TryLease(gate, out var path);

            Report(progress, gate);

            switch (outcome)
            {
                case AnalysisLease.Empty:
                    // Nothing left for THIS worker. Nothing re-queues on its own (a released lease
                    // only happens on cancellation, when everyone is stopping anyway), so an empty
                    // queue is genuinely the end of the run for this worker.
                    return;

                case AnalysisLease.Leased:
                    await RunOne(path!, progress, gate, ct).ConfigureAwait(false);
                    break;

                default:
                    // Paused or yielding: wait it out. The queue is untouched.
                    try
                    {
                        await Task.Delay(PollDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    break;
            }
        }
    }

    private async Task RunOne(
        string path, IProgress<AnalysisBatchProgress>? progress, bool gate, CancellationToken ct)
    {
        if (!StillNeeded(path))
        {
            _queue.NoteSkipped(path);
            Report(progress, gate, force: true);
            return;
        }

        try
        {
            await _analyse(path, ct).ConfigureAwait(false);
            _queue.NoteSucceeded(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Our own cancellation, mid-file. Not a failure — it never finished. Back to the front of
            // the queue so resuming tries this exact file first.
            _queue.Release(path);
            return;
        }
        catch (Exception ex)
        {
            // ⛔ One bad file must not cost the other 3,014. Recorded by name, counted, carry on.
            _queue.NoteFailed(path, ex.Message);
            _log?.Invoke($"[AnalysisBatch] analysis failed for '{path}': {ex.Message}");
        }

        Report(progress, gate, force: true);
    }

    /// <summary>
    /// The gate, defensively. A predicate that throws (a half-disposed view model during shutdown,
    /// say) is read as CLOSED: postponing is always recoverable, hammering the NAS through her set is
    /// not.
    /// </summary>
    private bool SafeGate()
    {
        try
        {
            return _mayWorkNow();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[AnalysisBatch] gate threw, treating as closed: {ex.Message}");
            return false;
        }
    }

    private bool StillNeeded(string path)
    {
        if (_stillNeedsAnalysis is null) return true;

        try
        {
            return _stillNeedsAnalysis(path);
        }
        catch (Exception ex)
        {
            // Never silently leave a song behind: if we cannot tell whether it still needs analysis,
            // analysing it again is the answer that cannot lose it.
            _log?.Invoke($"[AnalysisBatch] freshness check threw for '{path}', analysing anyway: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Pushes a snapshot to the overlay. Unforced calls only report when the PHASE changed, so the
    /// idle poll loop of every worker does not turn into a progress storm; a completed file always
    /// reports because its counts moved.
    /// </summary>
    private void Report(IProgress<AnalysisBatchProgress>? progress, bool mayWorkNow, bool force = false)
    {
        if (progress is null) return;

        var snapshot = _queue.Snapshot(mayWorkNow);
        var previous = Interlocked.Exchange(ref _lastPhase, (int)snapshot.Phase);

        if (!force && previous == (int)snapshot.Phase) return;

        progress.Report(snapshot);
    }
}
