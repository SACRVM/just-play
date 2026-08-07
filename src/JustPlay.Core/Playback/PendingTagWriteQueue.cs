using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JustPlay.Core.Playback;

/// <summary>
/// N27 fix: a per-file RETRY QUEUE, with backoff, for tag writes that fail because the file's
/// handle is held open by something other than the MAIN engine's currently-playing track.
///
/// <para>
/// <see cref="PlaybackController.WithFileReleased"/> (<c>_deferred</c>) already defers a write for
/// the MAIN engine's <c>CurrentTrack</c> and flushes it on the next track change - but it only knows
/// about that ONE lock. Two other lock sources are real and verified (2026-07-07, repro "Let It Be
/// (remix)"): (a) the PRE-CUE finder's own engine holding a <i>different</i> file, and (b) an
/// EXTERNAL app - Chloe's actual trigger was Windows Media Player having the file open. Neither of
/// those is "the main current track", so re-adding a failed write to <c>_deferred</c> would busy-loop
/// forever (the track-change event that flushes it may never fire for an unrelated lock) - hence a
/// dedicated queue, polled on a timer instead of an event, so it self-heals once the external handle
/// releases without spinning the CPU.
/// </para>
///
/// <para>
/// Pure logic, no Avalonia/BASS - the caller (<c>MainWindowViewModel</c>) supplies the actual write
/// as an <see cref="Action"/> and drives <see cref="RetryAll"/> from a UI-thread timer (see
/// CLAUDE.md's Threading section: DispatcherTimer is the right tool for this kind of polling). A
/// clock can be injected for tests (<see cref="PendingTagWriteQueue(int, TimeSpan, Func{DateTime}?)"/>)
/// so the ~5-minute give-up budget doesn't require an actual 5-minute test.
/// </para>
///
/// <para>
/// Dedup mirrors <c>PreCueFinderViewModel._pendingLikes</c>'s "last press wins": queuing a second
/// write for a path that's already pending REPLACES the payload (the freshest analysis/tag state is
/// what should land) rather than stacking duplicate writes, while keeping the original queue time and
/// attempt count (the retry budget is per FILE, not per write request).
/// </para>
/// </summary>
public sealed class PendingTagWriteQueue
{
    /// <summary>Default per-file attempt cap (matches the N27 spec: "~20").</summary>
    public const int DefaultMaxAttempts = 20;

    /// <summary>Default per-file time budget (matches the N27 spec: "give up after ~5 min per file").</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    private readonly int _maxAttempts;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required Action Write { get; set; }
        public required DateTime FirstQueuedUtc { get; init; }
        public int Attempts;
        public bool InFlight;
    }

    /// <param name="maxAttempts">Give up after this many failed attempts for one file.</param>
    /// <param name="maxAge">Give up after this much wall-clock time since the file was first queued,
    /// even if <paramref name="maxAttempts"/> hasn't been reached (protects against irregular tick
    /// timing, e.g. the app being suspended).</param>
    /// <param name="clock">Injectable "now" for tests; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public PendingTagWriteQueue(int maxAttempts = DefaultMaxAttempts, TimeSpan? maxAge = null, Func<DateTime>? clock = null)
    {
        _maxAttempts = maxAttempts;
        _maxAge = maxAge ?? DefaultMaxAge;
        _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Number of files currently awaiting a retry (diagnostics + tests).</summary>
    public int Count { get { lock (_gate) return _pending.Count; } }

    /// <summary>True while <paramref name="path"/> has a write waiting for a free handle.</summary>
    public bool IsPending(string path) { lock (_gate) return _pending.ContainsKey(path); }

    /// <summary>
    /// Queue (or replace) the write for <paramref name="path"/>. Does NOT attempt it - call
    /// <see cref="EnqueueAndTryNow"/> when the first attempt should run immediately (the common case:
    /// most files aren't locked, so this keeps the zero-delay write behaviour JustPlay had before this
    /// queue existed), or let the next <see cref="RetryAll"/> tick pick it up.
    /// </summary>
    public void Enqueue(string path, Action write)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var existing))
                existing.Write = write;   // last write wins; keep FirstQueuedUtc/Attempts (same overall budget)
            else
                _pending[path] = new Entry { Write = write, FirstQueuedUtc = _now() };
        }
    }

    /// <summary>
    /// Queue <paramref name="write"/> for <paramref name="path"/> and attempt it right away. On a
    /// retryable <see cref="IOException"/> (locked file) it stays queued for <see cref="RetryAll"/>;
    /// on success or a non-retryable failure it's removed immediately. This is the entry point for a
    /// FRESH write request (mirrors the old behaviour for the common unlocked-file case: it lands
    /// with no delay). <paramref name="onDeferred"/>, if given, fires once - the moment this file
    /// FIRST needs a retry - so the caller can surface "will retry" instead of leaving the user
    /// wondering why nothing happened for up to <see cref="DefaultMaxAge"/>.
    /// </summary>
    public void EnqueueAndTryNow(
        string path, Action write, Action<string, Exception>? onGiveUp = null, Action<string>? onDeferred = null)
    {
        Enqueue(path, write);
        RetryOne(path, onGiveUp, onDeferred);
    }

    /// <summary>
    /// Drive one retry pass over every currently-queued file (call this from a periodic timer, e.g.
    /// every ~15s). Each file is attempted at most once per call. Safe to call from a background
    /// thread - each queued <c>Write</c> delegate is responsible for its own thread-marshalling (the
    /// same contract <c>MainWindowViewModel.DoWrite</c> already has for its file-IO actions).
    /// </summary>
    public void RetryAll(Action<string, Exception>? onGiveUp = null)
    {
        string[] keys;
        lock (_gate) keys = _pending.Keys.ToArray();
        foreach (var path in keys) RetryOne(path, onGiveUp, onDeferred: null);
    }

    /// <summary>
    /// The single decision point shared by the immediate first attempt and every periodic retry:
    /// try the write; a retryable <see cref="IOException"/> re-queues it (unless the attempt/age
    /// budget is exhausted); any OTHER exception is treated as non-transient (it won't self-resolve
    /// by waiting - e.g. a corrupt tag block) and gives up immediately rather than spinning on it.
    /// <paramref name="onGiveUp"/> fires exactly once per file, the moment it's removed from the
    /// queue for good, so the caller can surface a visible, non-modal failure (see
    /// <c>MainWindowViewModel.EventLog</c>).
    /// </summary>
    private void RetryOne(string path, Action<string, Exception>? onGiveUp, Action<string>? onDeferred)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_pending.TryGetValue(path, out entry)) return;
            if (entry.InFlight) return;   // a concurrent caller (immediate attempt + timer tick racing) already owns this one
            entry.InFlight = true;
        }

        try
        {
            Exception? transient = null;
            try
            {
                entry.Write();
            }
            catch (IOException ex)
            {
                transient = ex;
            }
            catch (Exception ex)
            {
                Remove(path, entry);
                onGiveUp?.Invoke(path, ex);
                return;
            }

            if (transient is null)
            {
                Remove(path, entry);
                return;   // landed
            }

            entry.Attempts++;
            if (entry.Attempts >= _maxAttempts || _now() - entry.FirstQueuedUtc >= _maxAge)
            {
                Remove(path, entry);
                onGiveUp?.Invoke(path, transient);
            }
            else if (entry.Attempts == 1)
            {
                onDeferred?.Invoke(path);   // first time this file needed a retry - tell the caller once
            }
        }
        finally
        {
            lock (_gate)
                if (_pending.TryGetValue(path, out var stillThere) && ReferenceEquals(stillThere, entry))
                    stillThere.InFlight = false;
        }
    }

    private void Remove(string path, Entry entry)
    {
        lock (_gate)
            if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
                _pending.Remove(path);
    }
}
