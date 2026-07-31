namespace JustPlay.Library.Tests;

/// <summary>
/// The pure state machine behind the observer (0.6, P4): a settle buffer with coalescing, a dirty
/// flag, and periodic-sweep scheduling — all driven by an injected clock, exactly like
/// <c>PendingTagWriteQueueTests</c> drives <c>PendingTagWriteQueue</c>. No sleeping anywhere here:
/// every "time passes" step is a fake clock advance, never a real wait.
/// </summary>
public sealed class WatchQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock the test controls by mutating <see cref="Now"/> between calls.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now = T0;
        public DateTimeOffset Read() => Now;
    }

    private static WatchQueue Make(
        FakeClock clock, TimeSpan? settle = null, TimeSpan? sweep = null) =>
        new(settle ?? TimeSpan.FromSeconds(10), sweep ?? TimeSpan.FromMinutes(10), clock.Read);

    // =========================================================================
    // 1. The settle window itself.
    // =========================================================================

    [Fact]
    public void A_fresh_touch_is_not_ready_before_the_settle_window_elapses()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");
        clock.Now += TimeSpan.FromSeconds(9);

        Assert.Empty(q.DrainReady());
        Assert.Equal(1, q.PendingCount);
    }

    [Fact]
    public void A_touch_becomes_ready_once_it_has_been_quiet_for_the_settle_window()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");
        clock.Now += TimeSpan.FromSeconds(10);

        Assert.Equal([@"C:\a.mp3"], q.DrainReady());
        Assert.Equal(0, q.PendingCount);
    }

    [Fact]
    public void DrainReady_forgets_what_it_returned_a_second_call_is_empty()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");
        clock.Now += TimeSpan.FromSeconds(10);
        q.DrainReady();

        Assert.Empty(q.DrainReady());
    }

    [Fact]
    public void An_empty_queue_drains_to_nothing()
    {
        var q = Make(new FakeClock());
        Assert.Empty(q.DrainReady());
    }

    // =========================================================================
    // 2. Re-touching restarts the wait — an editor saving five times, or a file
    //    still being copied, must never settle mid-write.
    // =========================================================================

    [Fact]
    public void Re_touching_inside_the_window_restarts_the_wait()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");
        clock.Now += TimeSpan.FromSeconds(8);
        q.Touch(@"C:\a.mp3");                       // still being written — restarts the 10s wait

        clock.Now += TimeSpan.FromSeconds(8);        // 16s since the FIRST touch, only 8s since the second
        Assert.Empty(q.DrainReady());

        clock.Now += TimeSpan.FromSeconds(2);         // now 10s since the second touch
        Assert.Equal([@"C:\a.mp3"], q.DrainReady());
    }

    [Fact]
    public void Repeated_touches_for_the_same_path_never_grow_the_pending_count()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        for (var i = 0; i < 20; i++)
        {
            q.Touch(@"C:\same.mp3");
            clock.Now += TimeSpan.FromMilliseconds(50);
        }

        Assert.Equal(1, q.PendingCount);
    }

    // =========================================================================
    // 3. Coalescing across many DIFFERENT paths — the "300-file copy must be
    //    ONE batch, not a storm" requirement.
    // =========================================================================

    [Fact]
    public void Independent_paths_settle_on_their_own_schedule()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");                         // touched at T0
        clock.Now += TimeSpan.FromSeconds(3);
        q.Touch(@"C:\b.mp3");                         // touched at T0+3s

        clock.Now = T0 + TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(500);
        Assert.Equal([@"C:\a.mp3"], q.DrainReady());  // a is ready (10.5s old), b is not (7.5s old)

        clock.Now = T0 + TimeSpan.FromSeconds(13) + TimeSpan.FromMilliseconds(500);
        Assert.Equal([@"C:\b.mp3"], q.DrainReady());
    }

    [Fact]
    public void A_burst_of_many_paths_touched_together_drains_as_ONE_batch()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        var paths = Enumerable.Range(0, 300).Select(i => $@"C:\lib\track-{i:000}.mp3").ToList();
        foreach (var p in paths)
        {
            q.Touch(p);
            clock.Now += TimeSpan.FromMicroseconds(1);   // a fast local copy — microseconds apart
        }

        Assert.Equal(300, q.PendingCount);

        clock.Now += TimeSpan.FromSeconds(10);
        var ready = q.DrainReady();

        Assert.Equal(300, ready.Count);                  // one call, everything at once
        Assert.Equal(0, q.PendingCount);
        Assert.Equal(paths.ToHashSet(), ready.ToHashSet());
    }

    [Fact]
    public void Path_comparison_is_case_insensitive_like_Windows()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\Music\Track.mp3");
        q.Touch(@"C:\MUSIC\TRACK.MP3");                 // same file, different casing

        Assert.Equal(1, q.PendingCount);
    }

    // =========================================================================
    // 4. The dirty flag — the fallback for anything the buffer can't represent.
    // =========================================================================

    [Fact]
    public void MarkDirty_sets_the_flag_and_NoteSweepCompleted_clears_it()
    {
        var q = Make(new FakeClock());

        Assert.False(q.IsDirty);
        q.MarkDirty();
        Assert.True(q.IsDirty);

        q.NoteSweepCompleted();
        Assert.False(q.IsDirty);
    }

    [Fact]
    public void MarkDirty_does_not_disturb_the_settle_buffer()
    {
        var clock = new FakeClock();
        var q = Make(clock);

        q.Touch(@"C:\a.mp3");
        q.MarkDirty();

        Assert.Equal(1, q.PendingCount);
        Assert.True(q.IsDirty);
    }

    // =========================================================================
    // 5. Sweep scheduling — "is a full sweep due" is the other half of "what is
    //    ready to process".
    // =========================================================================

    [Fact]
    public void A_freshly_constructed_queue_does_not_consider_a_sweep_due_immediately()
    {
        var q = Make(new FakeClock(), sweep: TimeSpan.FromMinutes(10));
        Assert.False(q.IsSweepDue);
    }

    [Fact]
    public void A_sweep_becomes_due_once_the_interval_elapses()
    {
        var clock = new FakeClock();
        var q = Make(clock, sweep: TimeSpan.FromMinutes(10));

        clock.Now += TimeSpan.FromMinutes(9);
        Assert.False(q.IsSweepDue);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(q.IsSweepDue);
    }

    [Fact]
    public void NoteSweepCompleted_restarts_the_interval_clock()
    {
        var clock = new FakeClock();
        var q = Make(clock, sweep: TimeSpan.FromMinutes(10));

        clock.Now += TimeSpan.FromMinutes(10);
        Assert.True(q.IsSweepDue);

        q.NoteSweepCompleted();
        Assert.False(q.IsSweepDue);

        clock.Now += TimeSpan.FromMinutes(10);
        Assert.True(q.IsSweepDue);
    }

    [Fact]
    public void MarkDirty_makes_a_sweep_due_immediately_even_mid_interval()
    {
        var clock = new FakeClock();
        var q = Make(clock, sweep: TimeSpan.FromMinutes(10));

        clock.Now += TimeSpan.FromMinutes(1);   // nowhere near the interval
        Assert.False(q.IsSweepDue);

        q.MarkDirty();
        Assert.True(q.IsSweepDue);
    }

    [Fact]
    public void Defaults_match_the_milestone_doc()
    {
        // .claude/milestone-0.6-scope.md, P4: "~10 s" settle window. The sweep interval has no
        // single fixed number in the doc ("can run every few minutes") — 10 minutes is this
        // implementation's conservative default, pinned here so a future change is a deliberate edit.
        Assert.Equal(TimeSpan.FromSeconds(10), WatchQueue.DefaultSettleWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), WatchQueue.DefaultSweepInterval);
    }
}
