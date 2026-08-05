using System.Diagnostics;

namespace JustPlay.Library.Tests;

/// <summary>
/// The thin adapter (0.6, P2). ⛔ No audio is decoded anywhere in this file — the DSP is an injected
/// delegate, so a "long analysis" is a <see cref="TaskCompletionSource"/> that has not been completed
/// yet, not a sleep. The only real waiting is on explicit handshakes (with a timeout), plus a couple
/// of short bounded windows where the assertion is that something did NOT happen.
/// </summary>
public sealed class AnalysisBatchRunnerTests
{
    /// <summary>Poll fast: every wait in this file is a handshake, not a duration under test.</summary>
    private static readonly AnalysisBatchOptions Fast =
        new() { MaxConcurrency = 1, PollInterval = TimeSpan.FromMilliseconds(5) };

    /// <summary>How long a "nothing new started" assertion watches for. ~12 poll intervals.</summary>
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(60);

    /// <summary>A fake DSP: records what was started/finished and how many ran at once.</summary>
    private sealed class Dsp
    {
        private readonly Lock _gate = new();
        private readonly List<string> _started = [];
        private readonly List<string> _finished = [];
        private int _concurrent;

        /// <summary>Swap in per-test behaviour. Null = complete immediately.</summary>
        public Func<string, CancellationToken, Task>? Behaviour;

        public int MaxConcurrent { get; private set; }

        public int StartedCount { get { lock (_gate) return _started.Count; } }
        public int FinishedCount { get { lock (_gate) return _finished.Count; } }
        public string[] Started { get { lock (_gate) return [.. _started]; } }
        public void ClearStarted() { lock (_gate) _started.Clear(); }

        public async Task Run(string path, CancellationToken ct)
        {
            lock (_gate)
            {
                _started.Add(path);
                _concurrent++;
                if (_concurrent > MaxConcurrent) MaxConcurrent = _concurrent;
            }

            try
            {
                if (Behaviour is not null) await Behaviour(path, ct);
                else await Task.Yield();

                lock (_gate) _finished.Add(path);
            }
            finally
            {
                lock (_gate) _concurrent--;
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    // =========================================================================
    // 1. The happy path.
    // =========================================================================

    [Fact]
    public async Task It_analyses_everything_it_was_given()
    {
        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var report = await runner.RunAsync(["a.mp3", "b.mp3", "c.mp3"]);

        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(0, report.Remaining);
        Assert.False(report.Cancelled);
        Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], dsp.Started);
    }

    // =========================================================================
    // 2. Bounded concurrency (UserSettings.AnalysisThreads, passed IN).
    // =========================================================================

    [Fact]
    public async Task It_never_runs_more_files_at_once_than_it_was_allowed()
    {
        var release = new TaskCompletionSource();
        var dsp = new Dsp { Behaviour = (_, _) => release.Task };
        var runner = new AnalysisBatchRunner(
            dsp.Run, () => true,
            new AnalysisBatchOptions { MaxConcurrency = 3, PollInterval = TimeSpan.FromMilliseconds(5) });

        var run = runner.RunAsync(["1", "2", "3", "4", "5", "6", "7", "8"]);

        await WaitUntil(() => dsp.StartedCount == 3, "three files to be in flight");
        await Task.Delay(NegativeWindow);
        Assert.Equal(3, dsp.StartedCount);      // a fourth worker does not exist

        release.SetResult();
        var report = await run;

        Assert.Equal(8, report.Succeeded);
        Assert.Equal(3, dsp.MaxConcurrent);
    }

    [Fact]
    public async Task A_nonsense_thread_count_is_clamped_to_one()
    {
        var dsp = new Dsp { Behaviour = async (_, ct) => await Task.Delay(5, ct) };
        var runner = new AnalysisBatchRunner(
            dsp.Run, () => true,
            new AnalysisBatchOptions { MaxConcurrency = 0, PollInterval = TimeSpan.FromMilliseconds(5) });

        var report = await runner.RunAsync(["a", "b", "c"]);

        Assert.Equal(3, report.Succeeded);
        Assert.Equal(1, dsp.MaxConcurrent);
    }

    // =========================================================================
    // 3. The gig-safe gate.
    // =========================================================================

    [Fact]
    public async Task A_closed_gate_starts_nothing_and_loses_nothing()
    {
        var open = false;
        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(dsp.Run, () => Volatile.Read(ref open), Fast);

        var run = runner.RunAsync(["a.mp3", "b.mp3"]);

        await Task.Delay(NegativeWindow);
        Assert.Equal(0, dsp.StartedCount);
        Assert.Equal(2, runner.Queue.PendingCount);   // postponed, not dropped

        Volatile.Write(ref open, true);
        var report = await run;

        Assert.Equal(2, report.Succeeded);
    }

    [Fact]
    public async Task The_file_in_flight_when_the_gate_closes_finishes_and_no_new_one_starts()
    {
        // ⭐ The documented decision. Aborting a half-decoded track would throw away everything
        // already pulled over SMB and guarantee the same file is re-read from byte zero later — an
        // abort costs MORE NAS traffic than finishing, and produces no row to resume from.
        var open = true;
        var first = new TaskCompletionSource();
        var dsp = new Dsp { Behaviour = (path, _) => path == "a.mp3" ? first.Task : Task.CompletedTask };
        var runner = new AnalysisBatchRunner(dsp.Run, () => Volatile.Read(ref open), Fast);

        var run = runner.RunAsync(["a.mp3", "b.mp3"]);
        await WaitUntil(() => dsp.StartedCount == 1, "the first file to start");

        Volatile.Write(ref open, false);   // she pressed play
        first.SetResult();                 // the in-flight decode reaches its end on its own

        await WaitUntil(() => runner.Queue.DoneCount == 1, "the in-flight file to finish");
        Assert.Equal(1, dsp.FinishedCount);

        await Task.Delay(NegativeWindow);
        Assert.Equal(1, dsp.StartedCount);              // and nothing new was leased
        Assert.Equal(1, runner.Queue.PendingCount);

        Volatile.Write(ref open, true);
        var report = await run;

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public async Task A_gate_that_throws_is_read_as_closed()
    {
        var broken = true;
        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(
            dsp.Run,
            () => Volatile.Read(ref broken) ? throw new InvalidOperationException("half-disposed") : true,
            Fast);

        var run = runner.RunAsync(["a.mp3"]);

        await Task.Delay(NegativeWindow);
        Assert.Equal(0, dsp.StartedCount);

        Volatile.Write(ref broken, false);
        var report = await run;

        Assert.Equal(1, report.Succeeded);
    }

    // =========================================================================
    // 4. Pause / resume.
    // =========================================================================

    [Fact]
    public async Task Pause_lets_the_tail_finish_holds_the_queue_and_resumes_where_it_stopped()
    {
        var first = new TaskCompletionSource();
        var dsp = new Dsp { Behaviour = (path, _) => path == "a.mp3" ? first.Task : Task.CompletedTask };
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var run = runner.RunAsync(["a.mp3", "b.mp3", "c.mp3"]);
        await WaitUntil(() => dsp.StartedCount == 1, "the first file to start");

        runner.Pause();
        Assert.True(runner.IsPaused);
        first.SetResult();

        await WaitUntil(() => runner.Queue.DoneCount == 1, "the in-flight file to finish");
        await Task.Delay(NegativeWindow);

        Assert.Equal(1, dsp.StartedCount);
        Assert.Equal(2, runner.Queue.PendingCount);   // ⚠ pause must not lose the queue

        runner.Resume();
        var report = await run;

        Assert.Equal(3, report.Succeeded);
        Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], dsp.Started);
    }

    // =========================================================================
    // 5. Failures never stop the batch.
    // =========================================================================

    [Fact]
    public async Task One_bad_file_is_recorded_by_name_and_the_rest_still_run()
    {
        var dsp = new Dsp
        {
            Behaviour = (path, _) => path == "b.mp3"
                ? Task.FromException(new IOException("file is locked"))
                : Task.CompletedTask,
        };
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var report = await runner.RunAsync(["a.mp3", "b.mp3", "c.mp3"]);

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Remaining);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("b.mp3", failure.Path);
        Assert.Equal("file is locked", failure.Message);
    }

    [Fact]
    public async Task Every_file_failing_still_completes_the_run()
    {
        var dsp = new Dsp { Behaviour = (path, _) => Task.FromException(new Exception($"nope: {path}")) };
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var report = await runner.RunAsync(["a.mp3", "b.mp3"]);

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(2, report.Failed);
        Assert.Equal(2, report.Failures.Count);
        Assert.False(report.Cancelled);
    }

    // =========================================================================
    // 6. Resume — the index is the memory, and it is re-checked at lease time.
    // =========================================================================

    [Fact]
    public async Task A_file_someone_else_analysed_in_the_meantime_is_skipped_not_redone()
    {
        var dsp = new Dsp();
        var alreadyDone = new HashSet<string> { "b.mp3" };
        var runner = new AnalysisBatchRunner(
            dsp.Run, () => true, Fast, stillNeedsAnalysis: p => !alreadyDone.Contains(p));

        var report = await runner.RunAsync(["a.mp3", "b.mp3", "c.mp3"]);

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.Equal(["a.mp3", "c.mp3"], dsp.Started);   // the DSP never saw b.mp3
    }

    [Fact]
    public async Task A_freshness_check_that_throws_analyses_the_file_anyway()
    {
        // "Never leave songs behind": when we cannot tell whether it still needs analysis, doing the
        // work is the only answer that cannot lose a track.
        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(
            dsp.Run, () => true, Fast,
            stillNeedsAnalysis: _ => throw new InvalidOperationException("db is busy"));

        var report = await runner.RunAsync(["a.mp3"]);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Skipped);
    }

    // =========================================================================
    // 7. Cancellation — and picking the same batch back up afterwards.
    // =========================================================================

    [Fact]
    public async Task Cancelling_mid_file_reports_instead_of_throwing_and_keeps_the_remainder()
    {
        using var cts = new CancellationTokenSource();
        var blocked = new TaskCompletionSource();
        var dsp = new Dsp
        {
            Behaviour = async (path, ct) =>
            {
                if (path == "a.mp3") await blocked.Task.WaitAsync(ct);
            },
        };
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var run = runner.RunAsync(["a.mp3", "b.mp3"], ct: cts.Token);
        await WaitUntil(() => dsp.StartedCount == 1, "the first file to start");

        await cts.CancelAsync();
        var report = await run;   // ⚠ a report, not an OperationCanceledException

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(0, report.Failed);          // it never finished — that is not a failure
        Assert.Empty(report.Failures);
        Assert.Equal(2, report.Remaining);       // including the one that was in flight

        // Resume: same runner, no paths — the queue kept them, front-first.
        dsp.ClearStarted();
        dsp.Behaviour = null;
        var second = await runner.RunAsync();

        Assert.Equal(2, second.Succeeded);
        Assert.False(second.Cancelled);
        Assert.Equal(["a.mp3", "b.mp3"], dsp.Started);
    }

    [Fact]
    public async Task A_run_that_is_already_in_progress_refuses_a_second_one()
    {
        var release = new TaskCompletionSource();
        var dsp = new Dsp { Behaviour = (_, _) => release.Task };
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        var run = runner.RunAsync(["a.mp3", "b.mp3"]);
        await WaitUntil(() => dsp.StartedCount == 1, "the batch to get going");
        Assert.True(runner.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(["c.mp3"]));

        release.SetResult();
        var report = await run;

        Assert.False(runner.IsRunning);
        Assert.Equal(2, report.Succeeded);   // the refused call did not sneak c.mp3 into the live batch
    }

    // =========================================================================
    // 8. Progress — shaped for the shared floating BusyOverlay.
    // =========================================================================

    [Fact]
    public async Task Progress_reports_every_completed_file_and_ends_on_done()
    {
        var seen = new List<AnalysisBatchProgress>();
        var gate = new Lock();
        var progress = new SynchronousProgress(p => { lock (gate) seen.Add(p); });

        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(dsp.Run, () => true, Fast);

        await runner.RunAsync(["a.mp3", "b.mp3", "c.mp3"], progress);

        AnalysisBatchProgress[] reports;
        lock (gate) reports = [.. seen];

        Assert.Equal(AnalysisPhase.Done, reports[^1].Phase);
        Assert.Equal(3, reports[^1].Done);
        Assert.Equal(3, reports[^1].Total);
        Assert.Equal(1.0, reports[^1].Fraction);

        // Every completed file produced a report, and the counter only ever moves forward.
        foreach (var n in new[] { 0, 1, 2, 3 }) Assert.Contains(reports, r => r.Done == n);
        for (var i = 1; i < reports.Length; i++) Assert.True(reports[i].Done >= reports[i - 1].Done);
    }

    [Fact]
    public async Task Progress_says_yielding_rather_than_just_stalling()
    {
        var open = false;
        var seen = new List<AnalysisPhase>();
        var gate = new Lock();
        var progress = new SynchronousProgress(p => { lock (gate) seen.Add(p.Phase); });

        var dsp = new Dsp();
        var runner = new AnalysisBatchRunner(dsp.Run, () => Volatile.Read(ref open), Fast);

        var run = runner.RunAsync(["a.mp3"], progress);
        await WaitUntil(
            () => { lock (gate) return seen.Contains(AnalysisPhase.Yielding); },
            "a yielding report");

        Volatile.Write(ref open, true);
        await run;

        // A progress bar that stops with no reason on it is one she stops trusting.
        lock (gate)
        {
            Assert.Contains(AnalysisPhase.Yielding, seen);
            Assert.Equal(AnalysisPhase.Done, seen[^1]);
        }
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to the captured SynchronizationContext, which in a test host
    /// means the callback can land after the assertion. This one calls straight through, so the
    /// reports are whatever the runner actually emitted, in order.
    /// </summary>
    private sealed class SynchronousProgress(Action<AnalysisBatchProgress> onReport)
        : IProgress<AnalysisBatchProgress>
    {
        public void Report(AnalysisBatchProgress value) => onReport(value);
    }
}
