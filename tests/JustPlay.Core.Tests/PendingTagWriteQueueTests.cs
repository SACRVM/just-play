using System;
using System.Collections.Generic;
using System.IO;
using JustPlay.Core.Playback;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PendingTagWriteQueue"/> - the N27 fix (2026-07-13): a tag write that
/// hits a LOCKED file (the pre-cue engine holding a different track, or an external app - the
/// verified real-world trigger was Windows Media Player) used to be logged once and dropped forever,
/// so the JUSTPLAY blob never landed and the track re-analyzed on every load despite "write auto
/// tags" being on. This queue retries on a timer instead, with a capped attempt/time budget, and
/// gives up visibly instead of silently.
///
/// A fake clock (<see cref="Func{DateTime}"/>) is injected everywhere so the ~5-minute default give-up
/// budget is exercised without an actual 5-minute wait.
/// </summary>
public class PendingTagWriteQueueTests
{
    // =========================================================================
    // 1. The common case - file isn't locked - lands immediately, no retry needed.
    // =========================================================================

    [Fact]
    public void EnqueueAndTryNow_Succeeds_LandsImmediately_NotPending()
    {
        var queue = new PendingTagWriteQueue();
        var writes = 0;

        queue.EnqueueAndTryNow("C:\\track.mp3", () => writes++);

        Assert.Equal(1, writes);
        Assert.False(queue.IsPending("C:\\track.mp3"));
        Assert.Equal(0, queue.Count);
    }

    // =========================================================================
    // 2. A locked file (IOException) stays queued instead of being dropped, and
    //    onDeferred fires exactly once so the caller can surface "will retry".
    // =========================================================================

    [Fact]
    public void EnqueueAndTryNow_IOException_StaysQueued_AndFiresOnDeferredOnce()
    {
        var queue = new PendingTagWriteQueue();
        var deferredCount = 0;
        (string Path, Exception Ex)? gaveUp = null;

        queue.EnqueueAndTryNow(
            "C:\\locked.mp3",
            () => throw new IOException("file in use"),
            onGiveUp: (p, ex) => gaveUp = (p, ex),
            onDeferred: _ => deferredCount++);

        Assert.True(queue.IsPending("C:\\locked.mp3"));
        Assert.Equal(1, queue.Count);
        Assert.Null(gaveUp);
        Assert.Equal(1, deferredCount);
    }

    // =========================================================================
    // 3. Repeated RetryAll ticks keep trying a locked file, and it lands the
    //    moment the write stops throwing - mirrors the file being released.
    // =========================================================================

    [Fact]
    public void RetryAll_LandsOnceTheWriteStopsFailing()
    {
        var queue = new PendingTagWriteQueue();
        var attempts = 0;
        var shouldFail = true;

        queue.EnqueueAndTryNow("C:\\locked.mp3", () =>
        {
            attempts++;
            if (shouldFail) throw new IOException("file in use");
        });

        Assert.True(queue.IsPending("C:\\locked.mp3"));
        Assert.Equal(1, attempts);

        // A couple of ticks while still locked: stays queued, no progress beyond retrying.
        queue.RetryAll();
        queue.RetryAll();
        Assert.True(queue.IsPending("C:\\locked.mp3"));
        Assert.Equal(3, attempts);

        // The lock releases (e.g. Windows Media Player closed the file) - next tick lands it.
        shouldFail = false;
        queue.RetryAll();

        Assert.False(queue.IsPending("C:\\locked.mp3"));
        Assert.Equal(4, attempts);
        Assert.Equal(0, queue.Count);
    }

    // =========================================================================
    // 4. Give up after the attempt cap - a persistently locked file must not
    //    retry forever; onGiveUp fires exactly once and the entry is dropped.
    // =========================================================================

    [Fact]
    public void RetryAll_GivesUpAfterMaxAttempts()
    {
        var queue = new PendingTagWriteQueue(maxAttempts: 3);
        var attempts = 0;
        var giveUps = 0;

        queue.EnqueueAndTryNow("C:\\stuck.mp3",
            () => { attempts++; throw new IOException("still locked"); },
            onGiveUp: (_, _) => giveUps++);

        queue.RetryAll(onGiveUp: (_, _) => giveUps++);   // attempt 2
        queue.RetryAll(onGiveUp: (_, _) => giveUps++);   // attempt 3 -> hits the cap, gives up

        Assert.Equal(3, attempts);
        Assert.Equal(1, giveUps);
        Assert.False(queue.IsPending("C:\\stuck.mp3"));

        // A further tick must be a no-op (nothing left to retry) - proves it doesn't busy-loop.
        queue.RetryAll(onGiveUp: (_, _) => giveUps++);
        Assert.Equal(3, attempts);
        Assert.Equal(1, giveUps);
    }

    // =========================================================================
    // 5. Give up after the time budget even if the attempt cap hasn't been hit -
    //    protects against irregular tick timing (e.g. app suspended).
    // =========================================================================

    [Fact]
    public void RetryAll_GivesUpAfterMaxAge_EvenBelowAttemptCap()
    {
        var now = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
        var queue = new PendingTagWriteQueue(maxAttempts: 1000, maxAge: TimeSpan.FromMinutes(5), clock: () => now);
        Exception? gaveUpEx = null;

        queue.EnqueueAndTryNow("C:\\stuck.mp3",
            () => throw new IOException("still locked"),
            onGiveUp: (_, ex) => gaveUpEx = ex);

        Assert.True(queue.IsPending("C:\\stuck.mp3"));

        now = now.AddMinutes(5).AddSeconds(1);   // past the budget, attempts nowhere near the cap
        queue.RetryAll(onGiveUp: (_, ex) => gaveUpEx = ex);

        Assert.False(queue.IsPending("C:\\stuck.mp3"));
        Assert.IsType<IOException>(gaveUpEx);
    }

    // =========================================================================
    // 6. A NON-IOException failure is treated as non-transient: it gives up
    //    immediately (no retry loop for an error that won't self-resolve).
    // =========================================================================

    [Fact]
    public void NonIOException_GivesUpImmediately_NoRetry()
    {
        var queue = new PendingTagWriteQueue();
        var attempts = 0;
        (string Path, Exception Ex)? gaveUp = null;

        queue.EnqueueAndTryNow("C:\\corrupt.mp3",
            () => { attempts++; throw new InvalidDataException("corrupt tag block"); },
            onGiveUp: (p, ex) => gaveUp = (p, ex));

        Assert.Equal(1, attempts);
        Assert.False(queue.IsPending("C:\\corrupt.mp3"));
        Assert.NotNull(gaveUp);
        Assert.IsType<InvalidDataException>(gaveUp!.Value.Ex);
    }

    // =========================================================================
    // 7. Re-queuing the same path APPENDS: one entry, and once the file frees
    //    up both writes land, oldest first. A later save carries only what
    //    changed since the earlier one, so replacing would lose the earlier edit.
    // =========================================================================

    [Fact]
    public void Enqueue_SamePath_Appends_BothLandInOrder()
    {
        var queue = new PendingTagWriteQueue();
        var locked = true;
        var landed = new List<string>();

        Action Write(string name) => () =>
        {
            if (locked) throw new IOException("locked");
            landed.Add(name);
        };

        queue.EnqueueAndTryNow("C:\\track.mp3", Write("key"));
        queue.Enqueue("C:\\track.mp3", Write("title"));
        Assert.Equal(1, queue.Count);

        locked = false;
        queue.RetryAll();

        Assert.Equal(["key", "title"], landed);
        Assert.False(queue.IsPending("C:\\track.mp3"));
    }

    // =========================================================================
    // 8. Every request gets its REAL outcome back - not one inferred from
    //    callbacks that only fire on a file's first failure.
    // =========================================================================

    [Fact]
    public void EnqueueAndTryNow_ReportsWrittenDeferredAndFailed()
    {
        var queue = new PendingTagWriteQueue();

        Assert.Equal(QueuedWriteOutcome.Written,
            queue.EnqueueAndTryNow("C:\\free.mp3", () => { }));
        Assert.Equal(QueuedWriteOutcome.Deferred,
            queue.EnqueueAndTryNow("C:\\locked.mp3", () => throw new IOException("locked")));
        Assert.Equal(QueuedWriteOutcome.Failed,
            queue.EnqueueAndTryNow("C:\\corrupt.mp3", () => throw new InvalidDataException("corrupt")));
    }

    [Fact]
    public void SecondSave_OfAStillLockedFile_IsDeferred_NotWritten()
    {
        // The regression: the second save came back as Written (onDeferred only fired on the file's
        // FIRST failure), so the editor said "Saved", cleared its dirty state and renamed a locked file.
        var queue = new PendingTagWriteQueue();
        var deferredCalls = 0;

        var first = queue.EnqueueAndTryNow("C:\\cued.mp3", () => throw new IOException("locked"),
                                           onDeferred: _ => deferredCalls++);
        var second = queue.EnqueueAndTryNow("C:\\cued.mp3", () => throw new IOException("locked"),
                                            onDeferred: _ => deferredCalls++);

        Assert.Equal(QueuedWriteOutcome.Deferred, first);
        Assert.Equal(QueuedWriteOutcome.Deferred, second);
        Assert.Equal(2, deferredCalls);
        Assert.True(queue.IsPending("C:\\cued.mp3"));
    }

    [Fact]
    public void SecondSave_RestartsTheGiveUpBudget()
    {
        // The budget belongs to the request: a save accepted at 4:50 must not be dropped at 5:00
        // because an EARLIER save of the same file started the clock.
        var now = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
        var queue = new PendingTagWriteQueue(maxAttempts: 1000, maxAge: TimeSpan.FromMinutes(5), clock: () => now);
        var gaveUp = 0;

        queue.EnqueueAndTryNow("C:\\cued.mp3", () => throw new IOException("locked"));
        now = now.AddMinutes(4).AddSeconds(50);
        queue.EnqueueAndTryNow("C:\\cued.mp3", () => throw new IOException("locked"));

        now = now.AddSeconds(20);   // 5:10 after the first save, 0:20 after the second
        queue.RetryAll(onGiveUp: (_, _) => gaveUp++);
        Assert.True(queue.IsPending("C:\\cued.mp3"));
        Assert.Equal(0, gaveUp);

        now = now.AddMinutes(5);    // past the SECOND save's budget
        queue.RetryAll(onGiveUp: (_, _) => gaveUp++);
        Assert.False(queue.IsPending("C:\\cued.mp3"));
        Assert.Equal(1, gaveUp);
    }

    [Fact]
    public void Save_WhileAnAttemptIsRunning_IsDeferred_AndLandsInTheSamePass()
    {
        // A second request arriving while another thread is mid-attempt on the same file. Simulated
        // without threads: the running write itself makes the second request.
        var queue = new PendingTagWriteQueue();
        var landed = new List<string>();
        QueuedWriteOutcome? inner = null;

        var outer = queue.EnqueueAndTryNow("C:\\track.mp3", () =>
        {
            inner = queue.EnqueueAndTryNow("C:\\track.mp3", () => landed.Add("second"));
            landed.Add("first");
        });

        Assert.Equal(QueuedWriteOutcome.Deferred, inner);   // not written when it returned...
        Assert.Equal(QueuedWriteOutcome.Written, outer);
        Assert.Equal(["first", "second"], landed);          // ...but run straight after, not at the next tick
        Assert.False(queue.IsPending("C:\\track.mp3"));
    }

    // =========================================================================
    // 9. THE required simulated repro: hold a real OS file lock via FileStream,
    //    enqueue a write that itself needs an exclusive handle (mirrors TagLib#
    //    opening the file), release the lock, and assert the write lands on retry.
    // =========================================================================

    [Fact]
    public void SimulatedFileLock_ReleasedLock_WriteLandsOnRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jp-tagwrite-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "seed");
        try
        {
            using var externalLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var queue = new PendingTagWriteQueue();
            var landed = false;
            Exception? gaveUp = null;

            void SimulatedTagWrite()
            {
                // Mirrors TagLib#'s TagLib.File.Create(filePath): needs an exclusive-enough handle,
                // so it throws IOException while externalLock (standing in for Windows Media Player /
                // the pre-cue engine) still holds the file.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                landed = true;
            }

            queue.EnqueueAndTryNow(path, SimulatedTagWrite, onGiveUp: (_, ex) => gaveUp = ex);

            Assert.False(landed);
            Assert.True(queue.IsPending(path));
            Assert.Null(gaveUp);

            externalLock.Dispose();   // the external app / pre-cue engine releases the file

            queue.RetryAll(onGiveUp: (_, ex) => gaveUp = ex);

            Assert.True(landed);
            Assert.False(queue.IsPending(path));
            Assert.Null(gaveUp);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
