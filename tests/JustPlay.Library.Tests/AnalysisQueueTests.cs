namespace JustPlay.Library.Tests;

/// <summary>
/// The pure decision core (0.6, P2). Nothing here sleeps, opens a file, touches the database or
/// decodes a sample - the whole point of splitting the state machine out of
/// <see cref="AnalysisBatchRunner"/> is that pause-mid-file, a gate flipping, cancellation and
/// resume become table-driven assertions instead of timing experiments.
/// </summary>
public sealed class AnalysisQueueTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now = new(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static AnalysisQueue Queue(FakeClock? clock = null, params string[] paths)
    {
        var q = new AnalysisQueue(clock is null ? null : clock.Read);
        q.EnqueueMany(paths);
        return q;
    }

    // =========================================================================
    // 1. Filling it.
    // =========================================================================

    [Fact]
    public void Enqueue_keeps_order_and_ignores_duplicates()
    {
        var q = new AnalysisQueue();

        Assert.Equal(3, q.EnqueueMany(["a.mp3", "b.mp3", "c.mp3"]));
        Assert.False(q.Enqueue("b.mp3"));
        Assert.Equal(3, q.PendingCount);

        Assert.Equal(AnalysisLease.Leased, q.TryLease(true, out var first));
        Assert.Equal("a.mp3", first);
        Assert.Equal(AnalysisLease.Leased, q.TryLease(true, out var second));
        Assert.Equal("b.mp3", second);
    }

    [Fact]
    public void A_path_already_in_flight_is_not_queued_again()
    {
        var q = Queue(null, "a.mp3");

        Assert.Equal(AnalysisLease.Leased, q.TryLease(true, out _));
        Assert.False(q.Enqueue("a.mp3"));
        Assert.Equal(0, q.PendingCount);
        Assert.Equal(1, q.InFlightCount);
    }

    [Fact]
    public void Blank_paths_are_refused()
    {
        var q = new AnalysisQueue();

        Assert.False(q.Enqueue(""));
        Assert.False(q.Enqueue("   "));
        Assert.Equal(0, q.PendingCount);
    }

    // =========================================================================
    // 2. The one decision: empty -> paused -> gate -> lease.
    // =========================================================================

    [Fact]
    public void An_empty_queue_reports_empty_even_when_paused_or_gated()
    {
        var q = new AnalysisQueue();
        q.Pause();

        // Emptiness is checked FIRST on purpose: otherwise a worker would poll forever on a drained
        // batch that happens to be paused, and the run would never complete.
        Assert.Equal(AnalysisLease.Empty, q.TryLease(mayWorkNow: false, out _));
        q.Resume();
        Assert.Equal(AnalysisLease.Empty, q.TryLease(mayWorkNow: false, out _));
    }

    [Fact]
    public void Pause_beats_the_gate_and_keeps_the_queue()
    {
        var q = Queue(null, "a.mp3", "b.mp3");
        q.Pause();

        Assert.Equal(AnalysisLease.Paused, q.TryLease(mayWorkNow: true, out var path));
        Assert.Null(path);
        Assert.Equal(2, q.PendingCount);   // (!) pause must not lose the queue
        Assert.True(q.IsPaused);

        q.Resume();
        Assert.Equal(AnalysisLease.Leased, q.TryLease(true, out var leased));
        Assert.Equal("a.mp3", leased);
    }

    [Fact]
    public void A_closed_gate_postpones_but_never_drops()
    {
        var q = Queue(null, "a.mp3", "b.mp3");

        Assert.Equal(AnalysisLease.Yielding, q.TryLease(mayWorkNow: false, out var path));
        Assert.Null(path);
        Assert.Equal(2, q.PendingCount);

        Assert.Equal(AnalysisLease.Leased, q.TryLease(mayWorkNow: true, out var leased));
        Assert.Equal("a.mp3", leased);
        Assert.Equal(1, q.PendingCount);
    }

    // =========================================================================
    // 3. Closing a lease.
    // =========================================================================

    [Fact]
    public void Success_failure_and_skip_move_their_own_counters()
    {
        var q = Queue(null, "a.mp3", "b.mp3", "c.mp3");

        q.TryLease(true, out var a); q.NoteSucceeded(a!);
        q.TryLease(true, out var b); q.NoteFailed(b!, "decode blew up");
        q.TryLease(true, out var c); q.NoteSkipped(c!);

        var s = q.Snapshot(true);
        Assert.Equal(1, s.Succeeded);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(3, s.Done);
        Assert.Equal(3, s.Total);
        Assert.Equal(0, s.Remaining);
        Assert.True(q.IsDrained);
    }

    [Fact]
    public void Every_failure_is_named_never_only_counted()
    {
        var q = Queue(null, "a.mp3", "b.mp3");

        q.TryLease(true, out var a); q.NoteFailed(a!, "locked");
        q.TryLease(true, out var b); q.NoteFailed(b!, "corrupt header");

        Assert.Equal(
            [new AnalysisFailure("a.mp3", "locked"), new AnalysisFailure("b.mp3", "corrupt header")],
            q.Failures);
    }

    [Fact]
    public void Completing_the_same_lease_twice_does_not_double_count()
    {
        var q = Queue(null, "a.mp3");

        q.TryLease(true, out var a);
        q.NoteSucceeded(a!);
        q.NoteSucceeded(a!);
        q.NoteFailed(a!, "late arrival");

        var s = q.Snapshot(true);
        Assert.Equal(1, s.Succeeded);
        Assert.Equal(0, s.Failed);
        Assert.Empty(q.Failures);
    }

    [Fact]
    public void Completing_a_path_that_was_never_leased_is_ignored()
    {
        var q = new AnalysisQueue();

        q.NoteSucceeded("ghost.mp3");
        q.NoteFailed("ghost.mp3", "nope");
        q.Release("ghost.mp3");

        Assert.Equal(0, q.DoneCount);
        Assert.Equal(0, q.PendingCount);
    }

    [Fact]
    public void Release_puts_the_file_back_at_the_front_and_counts_nothing()
    {
        var q = Queue(null, "a.mp3", "b.mp3");

        q.TryLease(true, out var a);
        Assert.Equal("a.mp3", a);
        q.Release(a!);

        Assert.Equal(0, q.DoneCount);
        Assert.Equal(2, q.PendingCount);

        // Front, not back: resuming a cancelled run retries the exact file it was on.
        Assert.Equal(AnalysisLease.Leased, q.TryLease(true, out var again));
        Assert.Equal("a.mp3", again);
    }

    [Fact]
    public void Release_of_a_path_that_is_somehow_still_queued_does_not_duplicate_it()
    {
        var q = Queue(null, "a.mp3");

        q.TryLease(true, out var a);
        q.Enqueue("a.mp3");     // refused - still in flight
        q.Release(a!);

        Assert.Equal(1, q.PendingCount);
    }

    // =========================================================================
    // 4. Phases - what the overlay's phase line gets to say.
    // =========================================================================

    [Fact]
    public void Phase_is_idle_before_anything_and_done_after_everything()
    {
        var q = new AnalysisQueue();
        Assert.Equal(AnalysisPhase.Idle, q.Snapshot(true).Phase);

        q.Enqueue("a.mp3");
        Assert.Equal(AnalysisPhase.Analysing, q.Snapshot(true).Phase);

        q.TryLease(true, out var a);
        q.NoteSucceeded(a!);
        Assert.Equal(AnalysisPhase.Done, q.Snapshot(true).Phase);
    }

    [Fact]
    public void Paused_and_yielding_are_distinct_phases()
    {
        var q = Queue(null, "a.mp3");

        Assert.Equal(AnalysisPhase.Yielding, q.Snapshot(mayWorkNow: false).Phase);

        q.Pause();
        // Pause wins the label: it is the reason the USER can act on.
        Assert.Equal(AnalysisPhase.Paused, q.Snapshot(mayWorkNow: false).Phase);
        Assert.Equal(AnalysisPhase.Paused, q.Snapshot(mayWorkNow: true).Phase);
    }

    [Fact]
    public void A_paused_batch_still_reports_its_in_flight_tail()
    {
        var q = Queue(null, "a.mp3", "b.mp3");

        q.TryLease(true, out var a);
        q.Pause();

        var s = q.Snapshot(true);
        Assert.Equal(AnalysisPhase.Paused, s.Phase);
        Assert.Equal(1, s.InFlight);            // "pausing - 1 track finishing"
        Assert.Equal("a.mp3", s.CurrentPath);
    }

    [Fact]
    public void Current_path_is_null_when_nothing_is_in_flight()
    {
        var q = Queue(null, "a.mp3");

        q.TryLease(true, out var a);
        q.NoteSucceeded(a!);

        Assert.Null(q.Snapshot(true).CurrentPath);
    }

    [Fact]
    public void Total_grows_when_more_files_are_queued_mid_run()
    {
        var q = Queue(null, "a.mp3");

        q.TryLease(true, out var a); q.NoteSucceeded(a!);
        Assert.Equal(1, q.Snapshot(true).Total);

        q.EnqueueMany(["b.mp3", "c.mp3"]);
        var s = q.Snapshot(true);
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Done);
        Assert.Equal(2, s.Remaining);
    }

    // =========================================================================
    // 5. Elapsed + ETA - the clock-injectable part.
    // =========================================================================

    [Fact]
    public void Elapsed_counts_only_time_with_work_in_flight()
    {
        var clock = new FakeClock();
        var q = Queue(clock, "a.mp3", "b.mp3");

        // Nothing leased yet: no time has been spent working, however long we wait.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.Zero, q.Elapsed);

        q.TryLease(true, out var a);
        clock.Advance(TimeSpan.FromSeconds(5));
        q.NoteSucceeded(a!);
        Assert.Equal(TimeSpan.FromSeconds(5), q.Elapsed);

        // A two-hour set with the gate closed must not show up as two hours of analysis.
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromSeconds(5), q.Elapsed);

        q.TryLease(true, out var b);
        clock.Advance(TimeSpan.FromSeconds(3));
        q.NoteSucceeded(b!);
        Assert.Equal(TimeSpan.FromSeconds(8), q.Elapsed);
    }

    [Fact]
    public void Elapsed_with_overlapping_workers_is_wall_clock_not_the_sum_of_files()
    {
        var clock = new FakeClock();
        var q = Queue(clock, "a.mp3", "b.mp3");

        q.TryLease(true, out var a);
        q.TryLease(true, out var b);
        clock.Advance(TimeSpan.FromSeconds(10));
        q.NoteSucceeded(a!);
        q.NoteSucceeded(b!);

        // Two files, ten seconds of wall clock - not twenty. That is what makes the estimate
        // account for concurrency without this class knowing what the concurrency is.
        Assert.Equal(TimeSpan.FromSeconds(10), q.Elapsed);
    }

    [Fact]
    public void No_estimate_before_there_is_enough_to_estimate_from()
    {
        var clock = new FakeClock();
        var q = Queue(clock, "a.mp3", "b.mp3", "c.mp3", "d.mp3", "e.mp3");

        for (var i = 0; i < 2; i++)
        {
            q.TryLease(true, out var p);
            clock.Advance(TimeSpan.FromSeconds(4));
            q.NoteSucceeded(p!);
        }

        Assert.Null(q.Snapshot(true).EstimatedRemaining);

        q.TryLease(true, out var third);
        clock.Advance(TimeSpan.FromSeconds(4));
        q.NoteSucceeded(third!);

        // 3 files in 12 s = 4 s/file, 2 left.
        Assert.Equal(TimeSpan.FromSeconds(8), q.Snapshot(true).EstimatedRemaining);
    }

    [Fact]
    public void Skipped_files_do_not_flatter_the_estimate()
    {
        var clock = new FakeClock();
        var q = Queue(clock, "a.mp3", "b.mp3", "c.mp3", "d.mp3", "e.mp3", "f.mp3");

        // Three real analyses at 4 s each ...
        for (var i = 0; i < 3; i++)
        {
            q.TryLease(true, out var p);
            clock.Advance(TimeSpan.FromSeconds(4));
            q.NoteSucceeded(p!);
        }

        // ... then a free skip (someone else had already analysed it).
        q.TryLease(true, out var skipped);
        q.NoteSkipped(skipped!);

        // 12 s / 3 worked files = 4 s each, 2 left. Counting the skip would say 3 s and lie.
        var s = q.Snapshot(true);
        Assert.Equal(2, s.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(8), s.EstimatedRemaining);
    }

    [Fact]
    public void A_finished_batch_offers_no_estimate()
    {
        var clock = new FakeClock();
        var q = Queue(clock, "a.mp3", "b.mp3", "c.mp3");

        for (var i = 0; i < 3; i++)
        {
            q.TryLease(true, out var p);
            clock.Advance(TimeSpan.FromSeconds(4));
            q.NoteSucceeded(p!);
        }

        var s = q.Snapshot(true);
        Assert.Equal(AnalysisPhase.Done, s.Phase);
        Assert.Null(s.EstimatedRemaining);
        Assert.Equal(1.0, s.Fraction);
    }

    [Fact]
    public void Fraction_is_null_with_nothing_to_divide_by()
    {
        Assert.Null(new AnalysisQueue().Snapshot(true).Fraction);
    }
}
