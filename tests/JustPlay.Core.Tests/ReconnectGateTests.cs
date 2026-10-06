using JustPlay.Core.Streaming;

namespace JustPlay.Core.Tests;

/// <summary>
/// The rules behind a broadcast's auto-reconnect, without BASS: which loop is at work, and that
/// every way a loop ends lets the next drop start a new one. The encoder ids below stand in for the
/// BASS encoder handles the broadcast service passes.
/// </summary>
public sealed class ReconnectGateTests
{
    private const int FirstEncoder = 101;
    private const int SecondEncoder = 102;
    private const int ThirdEncoder = 103;

    [Fact]
    public void A_drop_starts_one_loop_and_a_second_drop_meanwhile_starts_none()
    {
        var gate = new ReconnectGate();
        var starts = 0;

        var loop = gate.TryBegin(FirstEncoder, () => starts++);
        var again = gate.TryBegin(FirstEncoder, () => starts++);

        Assert.NotNull(loop);
        Assert.Null(again);
        Assert.Equal(1, starts);
        Assert.True(gate.IsActive);
    }

    /// <summary>
    /// The live-set failure this gate exists for: the first drop reconnected fine, and the SECOND
    /// drop in the same session found the finished loop still registered, so nothing reconnected and
    /// the UI kept showing ON AIR.
    /// </summary>
    [Fact]
    public void After_a_successful_reconnect_the_next_drop_starts_a_new_loop()
    {
        var gate = new ReconnectGate();

        var first = gate.TryBegin(FirstEncoder)!;
        Assert.True(gate.Finish(first, SecondEncoder));
        gate.End(first);

        Assert.False(gate.IsActive);

        var starts = 0;
        var second = gate.TryBegin(SecondEncoder, () => starts++);

        Assert.NotNull(second);
        Assert.Equal(1, starts);   // the Reconnecting state is entered again
    }

    [Fact]
    public void Success_alone_retires_the_loop_even_before_it_reaches_its_finally()
    {
        var gate = new ReconnectGate();

        var first = gate.TryBegin(FirstEncoder)!;
        Assert.True(gate.Finish(first, SecondEncoder));

        Assert.False(gate.IsActive);
        Assert.NotNull(gate.TryBegin(SecondEncoder));
    }

    [Fact]
    public void A_loop_that_ends_any_other_way_retires_too()
    {
        // An exception out of the loop, or a return on a cancelled wait: both leave through the
        // loop's finally, and neither may leave it registered.
        var gate = new ReconnectGate();

        var first = gate.TryBegin(FirstEncoder)!;
        gate.End(first);

        Assert.False(gate.IsActive);
        Assert.NotNull(gate.TryBegin(FirstEncoder));
    }

    [Fact]
    public void Connect_or_disconnect_cancels_the_loop_and_frees_the_gate()
    {
        var gate = new ReconnectGate();

        var loop = gate.TryBegin(FirstEncoder)!;
        gate.Cancel();

        Assert.True(loop.Token.IsCancellationRequested);
        Assert.False(gate.IsActive);
        Assert.NotNull(gate.TryBegin(SecondEncoder));
    }

    [Fact]
    public void A_superseded_loop_finishing_late_does_not_retire_its_successor()
    {
        // The user switches station mid-retry (cancel), the new connection drops (a new loop), and
        // only THEN does the old loop's last attempt come back. It must not clear the new loop.
        var gate = new ReconnectGate();

        var old = gate.TryBegin(FirstEncoder)!;
        gate.Cancel();
        var successor = gate.TryBegin(SecondEncoder)!;

        Assert.True(gate.Finish(old, ThirdEncoder));   // the old loop stops...
        gate.End(old);

        Assert.True(gate.IsActive);                    // ...and the new one is still registered
        Assert.Null(gate.TryBegin(SecondEncoder));
        Assert.False(successor.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_superseded_loop_may_not_touch_the_state()
    {
        var gate = new ReconnectGate();
        var loop = gate.TryBegin(FirstEncoder)!;

        var ranWhileCurrent = gate.IfCurrent(loop, () => { });
        gate.Cancel();
        var ranAfterCancel = gate.IfCurrent(loop, () => Assert.Fail("ran for a cancelled loop"));

        Assert.True(ranWhileCurrent);
        Assert.False(ranAfterCancel);
    }

    [Fact]
    public void A_drop_of_the_fresh_connection_before_the_loop_retires_keeps_it_going()
    {
        // The attempt connected encoder 102, and 102 died before the loop got to retire. That drop
        // found a loop "at work" and started nothing - so the loop itself has to carry on.
        var gate = new ReconnectGate();

        var loop = gate.TryBegin(FirstEncoder)!;
        Assert.Null(gate.TryBegin(SecondEncoder));       // the fresh connection drops

        Assert.False(gate.Finish(loop, SecondEncoder));  // keep going
        Assert.True(gate.IsActive);

        Assert.True(gate.Finish(loop, ThirdEncoder));    // the next attempt holds
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void A_late_notice_from_an_older_connection_does_not_undo_a_good_reconnect()
    {
        // Encoder 101 reports twice (cast died, then encoder died) while the loop waits. The loop
        // then connects 102 - the old notice is not a reason to tear 102 down again.
        var gate = new ReconnectGate();

        var loop = gate.TryBegin(FirstEncoder)!;
        Assert.Null(gate.TryBegin(FirstEncoder));

        Assert.True(gate.Finish(loop, SecondEncoder));
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void End_after_finish_and_end_after_cancel_are_harmless()
    {
        var gate = new ReconnectGate();

        var a = gate.TryBegin(FirstEncoder)!;
        gate.Finish(a, SecondEncoder);
        gate.End(a);

        var b = gate.TryBegin(SecondEncoder)!;
        gate.Cancel();
        gate.End(b);

        Assert.False(gate.IsActive);
    }
}
