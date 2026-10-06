using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JustPlay.Core.Playback;

/// <summary>What became of one write request handed to <see cref="PendingTagWriteQueue"/>.</summary>
public enum QueuedWriteOutcome
{
    /// <summary>The write reached the file, now.</summary>
    Written,

    /// <summary>The file is busy; the write is queued and retried until it lands or the queue gives up.</summary>
    Deferred,

    /// <summary>The write failed in a way waiting will not fix, and was dropped (onGiveUp was told).</summary>
    Failed,
}

/// <summary>
/// N27 fix: a per-file RETRY QUEUE, with backoff, for tag writes that fail because the file's
/// handle is held open by something other than the MAIN engine's currently-playing track.
///
/// <para>
/// <see cref="PlaybackController.WithFileReleased"/> (<c>_deferred</c>) already defers a write for
/// the MAIN engine's <c>CurrentTrack</c> and flushes it on the next track change - but it only knows
/// about that ONE lock. Two other lock sources are real and verified (2026-07-07, repro "Let It Be
/// (remix)"): (a) the PRE-CUE finder's own engine holding a <i>different</i> file, and (b) an
/// EXTERNAL app - the actual trigger was Windows Media Player having the file open. Neither of
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
/// <b>One entry per file; a later request is APPENDED to it</b> and the writes land in the order they
/// were asked for. A later write must not replace an earlier one: the tag editor saves only what
/// changed since its last save, so a second save of a still-locked file carries the new title but not
/// the key corrected in the first - replacing would have dropped the key. Run in order, the newest
/// value of a field is written last and wins, and nothing asked for is lost.
/// </para>
///
/// <para>
/// <b>The give-up budget restarts with every request</b>, because it belongs to the request, not to
/// the file. It used to belong to the file: a second save of a still-locked file was given up on five
/// minutes after the FIRST save, possibly seconds after it had been accepted.
/// </para>
///
/// <para>
/// <b>Every request gets its real outcome</b> (<see cref="QueuedWriteOutcome"/>) as the return value
/// of <see cref="EnqueueAndTryNow"/>. It used to be inferred from callbacks that only fired on the
/// first failure of a file and stayed silent while a retry was running - so a second save of a
/// locked file came back looking written, and the editor said "Saved" for tags still waiting in here.
/// </para>
/// </summary>
public sealed class PendingTagWriteQueue
{
    /// <summary>Default per-request attempt cap (matches the N27 spec: "~20").</summary>
    public const int DefaultMaxAttempts = 20;

    /// <summary>Default per-request time budget (matches the N27 spec: "give up after ~5 min").</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    private readonly int _maxAttempts;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One file's pending writes. Mutated only under <see cref="_gate"/>.</summary>
    private sealed class Entry
    {
        public readonly List<Action> Writes = [];   // oldest first; each one leaves the moment it lands
        public DateTime QueuedUtc;                   // the latest request - the budget runs from here
        public int Attempts;                         // failed attempts since the latest request
        public bool InFlight;                        // an attempt is running right now, on some thread
    }

    /// <param name="maxAttempts">Give up after this many failed attempts since the latest request.</param>
    /// <param name="maxAge">Give up after this much wall-clock time since the latest request, even if
    /// <paramref name="maxAttempts"/> hasn't been reached (protects against irregular tick timing, e.g.
    /// the app being suspended).</param>
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
    /// Queue the write for <paramref name="path"/>, behind anything already waiting for it. Does NOT
    /// attempt it - call <see cref="EnqueueAndTryNow"/> when the first attempt should run immediately
    /// (the common case: most files aren't locked, so this keeps the zero-delay write behaviour
    /// JustPlay had before this queue existed), or let the next <see cref="RetryAll"/> tick pick it up.
    /// </summary>
    public void Enqueue(string path, Action write)
    {
        lock (_gate) Append(path, write);
    }

    /// <summary>
    /// Queue <paramref name="write"/> for <paramref name="path"/> and attempt it right away (behind
    /// anything already waiting for the same file), and say what happened to it:
    /// <list type="bullet">
    /// <item><description><see cref="QueuedWriteOutcome.Written"/> - it landed.</description></item>
    /// <item><description><see cref="QueuedWriteOutcome.Deferred"/> - the file is locked (a retryable
    /// <see cref="IOException"/>), or an attempt for this file is running on another thread right now;
    /// either way the write is queued, and <paramref name="onDeferred"/> fires.</description></item>
    /// <item><description><see cref="QueuedWriteOutcome.Failed"/> - it failed in a way waiting will
    /// not fix; <paramref name="onGiveUp"/> fired and nothing is queued for the file any more.</description></item>
    /// </list>
    /// </summary>
    public QueuedWriteOutcome EnqueueAndTryNow(
        string path, Action write, Action<string, Exception>? onGiveUp = null, Action<string>? onDeferred = null)
    {
        Entry entry;
        bool claimed;
        lock (_gate)
        {
            entry = Append(path, write);
            claimed = !entry.InFlight;
            if (claimed) entry.InFlight = true;
        }

        // Not claimed: another thread is mid-attempt on this file. It works through the whole list
        // before it lets go (see Run), so this write runs straight after - queued, not yet written.
        var outcome = claimed ? Run(path, entry, onGiveUp) : QueuedWriteOutcome.Deferred;
        if (outcome == QueuedWriteOutcome.Deferred) onDeferred?.Invoke(path);
        return outcome;
    }

    /// <summary>
    /// Drive one retry pass over every currently-queued file (call this from a periodic timer, e.g.
    /// every ~15s). Safe to call from a background thread - each queued <c>Write</c> delegate is
    /// responsible for its own thread-marshalling (the same contract <c>MainWindowViewModel.DoWrite</c>
    /// already has for its file-IO actions). A file whose attempt is already running elsewhere is
    /// skipped this pass.
    /// </summary>
    public void RetryAll(Action<string, Exception>? onGiveUp = null)
    {
        string[] keys;
        lock (_gate) keys = _pending.Keys.ToArray();

        foreach (var path in keys)
        {
            Entry? entry;
            lock (_gate)
            {
                if (!_pending.TryGetValue(path, out entry) || entry.InFlight) continue;
                entry.InFlight = true;
            }
            Run(path, entry, onGiveUp);
        }
    }

    /// <summary>Add <paramref name="write"/> to <paramref name="path"/>'s entry, creating it if needed,
    /// and restart the budget - this is a new request. Caller holds <see cref="_gate"/>.</summary>
    private Entry Append(string path, Action write)
    {
        if (!_pending.TryGetValue(path, out var entry))
        {
            entry = new Entry();
            _pending[path] = entry;
        }

        entry.Writes.Add(write);
        entry.QueuedUtc = _now();
        entry.Attempts = 0;
        return entry;
    }

    /// <summary>
    /// The single decision point shared by the immediate first attempt and every periodic retry. The
    /// caller has already claimed <paramref name="entry"/> (InFlight). Runs the queued writes oldest
    /// first, each leaving the list the moment it lands, until the list is empty (Written) or one of
    /// them fails. A retryable <see cref="IOException"/> keeps the rest queued (unless the attempt/age
    /// budget is exhausted); any OTHER exception is treated as non-transient (it won't self-resolve by
    /// waiting - e.g. a corrupt tag block) and gives up immediately rather than spinning on it.
    /// <paramref name="onGiveUp"/> fires exactly once each time a file is dropped for good, so the
    /// caller can surface a visible, non-modal failure (see <c>MainWindowViewModel.EventLog</c>).
    /// <para>A write appended while this runs is picked up by the same loop - so a request that found
    /// the file in flight is not left waiting for the next timer tick.</para>
    /// </summary>
    private QueuedWriteOutcome Run(string path, Entry entry, Action<string, Exception>? onGiveUp)
    {
        while (true)
        {
            Action write;
            lock (_gate)
            {
                if (entry.Writes.Count == 0)
                {
                    entry.InFlight = false;
                    Remove(path, entry);
                    return QueuedWriteOutcome.Written;
                }
                // Only the owner of InFlight removes from the list, and others only append - so this
                // stays the oldest write until it is removed below.
                write = entry.Writes[0];
            }

            Exception? error = null;
            try { write(); }
            catch (Exception ex) { error = ex; }

            Exception? gaveUp = null;
            QueuedWriteOutcome outcome;
            lock (_gate)
            {
                if (error is null)
                {
                    entry.Writes.RemoveAt(0);
                    continue;   // landed - on to the next one, if there is one
                }

                entry.InFlight = false;
                if (error is not IOException
                    || ++entry.Attempts >= _maxAttempts
                    || _now() - entry.QueuedUtc >= _maxAge)
                {
                    Remove(path, entry);
                    gaveUp = error;
                    outcome = QueuedWriteOutcome.Failed;
                }
                else
                {
                    outcome = QueuedWriteOutcome.Deferred;
                }
            }

            if (gaveUp is not null) onGiveUp?.Invoke(path, gaveUp);
            return outcome;
        }
    }

    /// <summary>Drop <paramref name="entry"/> if it is still the one queued for <paramref name="path"/>.
    /// Caller holds <see cref="_gate"/>.</summary>
    private void Remove(string path, Entry entry)
    {
        if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
            _pending.Remove(path);
    }
}
