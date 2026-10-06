using System;
using System.Threading;

namespace JustPlay.Core.Streaming;

/// <summary>
/// The bookkeeping behind a broadcast's auto-reconnect: whether a retry loop is at work, and WHICH
/// one. No socket, no encoder, no timer - so every way a loop can start and end is a plain test.
///
/// <para><b>(!!) Every way a loop ends has to retire it.</b> A loop that returned without saying so
/// stays registered as "at work", and the NEXT drop in the same session is then swallowed: nothing
/// reconnects and no state changes, so the UI keeps reading ON AIR while nothing goes out. That was
/// a real failure after the first successful auto-reconnect. Success retires the loop through
/// <see cref="Finish"/>, every other exit (cancelled, threw) through <see cref="End"/>.</para>
///
/// <para><b>A loop only ever retires ITSELF.</b> The user's CONNECT / DISCONNECT supersedes a loop
/// (<see cref="Cancel"/>), and a drop after that may already have started the next one. The old
/// loop finishing late must not clear its successor, so every call names its <see cref="Ticket"/>
/// and is compared by instance.</para>
///
/// <para><b>A drop of the connection a loop has just made is not lost.</b> Between an attempt
/// succeeding and the loop retiring, the fresh connection can already die - and that drop finds a
/// loop still at work. It is recorded by connection id, and <see cref="Finish"/> then answers "keep
/// going" instead of retiring. A late notice from an OLDER connection carries another id and does
/// not tear down the new one.</para>
///
/// <para>Thread-safe: drops arrive on the audio library's notify thread, the loop runs on the pool,
/// and CONNECT / DISCONNECT come from the UI. The callbacks passed in run under the gate's lock, so
/// a state change they make is ordered against <see cref="Cancel"/> - keep them short and never
/// block in them.</para>
/// </summary>
public sealed class ReconnectGate
{
    private readonly object _sync = new();

    private Ticket? _current;

    // The connection id of the last drop that arrived while a loop was at work. 0 = none.
    private int _droppedWhileBusy;

    /// <summary>One retry loop's registration. The loop owns it and ends it with <see cref="End"/>.</summary>
    public sealed class Ticket
    {
        private readonly CancellationTokenSource _cts = new();

        internal Ticket() => Token = _cts.Token;

        /// <summary>Cancelled when the user's CONNECT / DISCONNECT supersedes this loop.</summary>
        public CancellationToken Token { get; }

        internal void Cancel() => _cts.Cancel();

        internal void Dispose() => _cts.Dispose();
    }

    /// <summary>True while a retry loop is registered.</summary>
    public bool IsActive
    {
        get { lock (_sync) return _current is not null; }
    }

    /// <summary>
    /// A connection dropped. Returns the ticket for a new retry loop, or null when one is already at
    /// work - in which case the drop is remembered, so that loop cannot retire on a success the drop
    /// has already overtaken.
    /// </summary>
    /// <param name="droppedConnection">Opaque id of the connection that dropped (the encoder handle);
    /// 0 when unknown.</param>
    /// <param name="onStart">Runs under the lock when a new loop starts - the place to enter the
    /// Reconnecting state, ordered against <see cref="Cancel"/>.</param>
    public Ticket? TryBegin(int droppedConnection, Action? onStart = null)
    {
        lock (_sync)
        {
            if (_current is not null)
            {
                _droppedWhileBusy = droppedConnection;
                return null;
            }

            var ticket = new Ticket();
            _current = ticket;
            _droppedWhileBusy = 0;
            onStart?.Invoke();
            return ticket;
        }
    }

    /// <summary>
    /// An attempt connected. True when the loop is done - retired here, or already superseded by the
    /// user. False when the connection it just made has dropped in the meantime: the loop stays
    /// registered and has to keep going.
    /// </summary>
    /// <param name="ticket">The loop's own ticket.</param>
    /// <param name="newConnection">Id of the connection the attempt made (the new encoder handle).</param>
    public bool Finish(Ticket ticket, int newConnection)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        lock (_sync)
        {
            if (!ReferenceEquals(_current, ticket)) return true;

            if (newConnection != 0 && _droppedWhileBusy == newConnection)
            {
                _droppedWhileBusy = 0;
                return false;
            }

            Retire();
            return true;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the lock, but only while <paramref name="ticket"/> is still
    /// the registered, uncancelled loop. Returns whether it ran. This is how a loop re-enters the
    /// Reconnecting state between attempts without overwriting the user's DISCONNECT that landed a
    /// moment earlier.
    /// </summary>
    public bool IfCurrent(Ticket ticket, Action action)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(action);

        lock (_sync)
        {
            if (!ReferenceEquals(_current, ticket) || ticket.Token.IsCancellationRequested) return false;
            action();
            return true;
        }
    }

    /// <summary>
    /// The loop is leaving, for whatever reason. Retires it if it is still the registered one, and
    /// releases the ticket. Safe to call after <see cref="Finish"/> and after <see cref="Cancel"/> -
    /// meant for the loop's <c>finally</c>, so no exit can forget it.
    /// </summary>
    public void End(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        lock (_sync)
        {
            if (ReferenceEquals(_current, ticket)) Retire();
        }

        // The loop is the ticket's only user and it is on its way out, so this is the one place a
        // dispose cannot race a wait on the token.
        ticket.Dispose();
    }

    /// <summary>The user's CONNECT or DISCONNECT: stop whatever loop is at work. The loop itself
    /// still calls <see cref="End"/> on its way out.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _current?.Cancel();
            Retire();
        }
    }

    private void Retire()
    {
        _current = null;
        _droppedWhileBusy = 0;
    }
}
