using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// What closing JUST TAG settles before the process exits.
///
/// <para>What was wrong: closing looked at unsaved edits alone. An ANALYZE batch or a multi-file save
/// kept writing on worker threads, so the process could exit in the middle of TagLib's save - which
/// rewrites the file in place when the tag grows, and truncates the audio when it is cut off. And
/// the unsaved-changes prompt could start a second save over a running one.</para>
/// </summary>
public class CloseSequenceTests
{
    private readonly List<string> _steps = [];
    private readonly TagWriteGate _gate = new(releaseFile: _ => { }, log: _ => { });

    private bool _analysing;
    private bool _saving;
    private bool _dirty;
    private Task _run = Task.CompletedTask;

    private CloseSequence Sequence() => new()
    {
        Writes = _gate,
        Analysing = () => _analysing,
        CancelAnalysis = () => _steps.Add("cancel"),
        AnalysisRun = () => _run,
        EditorSaving = () => _saving,
        EditorDirty = () => _dirty,
        Finishing = on => _steps.Add(on ? "finishing" : "finished"),
    };

    private Func<Task<bool>> Prompt(bool answer) => () =>
    {
        _steps.Add("prompt");
        return Task.FromResult(answer);
    };

    private bool GateIsOpen() => _gate.Execute("probe.mp3", _ => { }) == TagWriteOutcome.Written;

    // -- The close that needs nothing ----------------------------------------------------------------

    [Fact]
    public void Nothing_running_and_nothing_unsaved_closes_at_once_and_shuts_the_gate()
    {
        Assert.True(Sequence().TryNow());
        Assert.False(GateIsOpen());
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Anything_to_settle_defers_the_close_and_leaves_the_gate_open(
        bool analysing, bool saving, bool dirty)
    {
        (_analysing, _saving, _dirty) = (analysing, saving, dirty);

        Assert.False(Sequence().TryNow());
        Assert.True(GateIsOpen());
    }

    // -- A running batch -----------------------------------------------------------------------------

    [Fact]
    public async Task A_running_batch_is_cancelled_and_awaited_before_the_window_closes()
    {
        _analysing = true;
        var run = new TaskCompletionSource();
        _run = run.Task;

        var closing = Sequence().RunAsync(Prompt(true));

        Assert.Equal(["finishing", "cancel"], _steps);
        Assert.False(closing.IsCompleted);   // the files in progress are still being written

        run.SetResult();

        Assert.True(await closing);
        Assert.Equal(["finishing", "cancel", "finished"], _steps);
        Assert.False(GateIsOpen());
    }

    /// <summary>Edits typed while the batch ran are still asked about - once nothing is writing, so
    /// the prompt's Save is the only save.</summary>
    [Fact]
    public async Task Unsaved_edits_are_asked_about_after_the_batch_has_stopped()
    {
        (_analysing, _dirty) = (true, true);

        Assert.True(await Sequence().RunAsync(Prompt(true)));

        Assert.Equal(["finishing", "cancel", "finished", "prompt"], _steps);
    }

    [Fact]
    public async Task A_batch_that_ended_in_an_exception_does_not_hold_the_window_open()
    {
        _analysing = true;
        _run = Task.FromException(new InvalidOperationException("boom"));

        Assert.True(await Sequence().RunAsync(Prompt(true)));
    }

    // -- A running save ------------------------------------------------------------------------------

    /// <summary>The save is stopped between two files and the prompt is NOT shown, although the
    /// editor is dirty: its Save would start a second save on top of the one being stopped.</summary>
    [Fact]
    public async Task A_running_save_is_stopped_at_the_gate_and_no_second_save_is_offered()
    {
        (_saving, _dirty) = (true, true);

        Assert.True(await Sequence().RunAsync(Prompt(true)));

        Assert.DoesNotContain("prompt", _steps);
        Assert.False(GateIsOpen());
    }

    [Fact]
    public async Task A_running_save_finishes_the_file_it_is_writing_first()
    {
        _saving = true;
        using var writing = new ManualResetEventSlim();
        using var mayFinish = new ManualResetEventSlim();
        var finished = false;

        var save = Task.Run(() => _gate.Execute("a.mp3", _ =>
        {
            writing.Set();
            mayFinish.Wait();
            finished = true;
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));

        var closing = Sequence().RunAsync(Prompt(true));
        Assert.False(closing.IsCompleted);

        mayFinish.Set();

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(finished);
        Assert.Equal(TagWriteOutcome.Written, await save);
    }

    // -- Staying -------------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_to_stay_keeps_the_window_and_every_write_working()
    {
        _dirty = true;

        Assert.False(await Sequence().RunAsync(Prompt(false)));

        Assert.Equal(["prompt"], _steps);
        Assert.True(GateIsOpen());
    }

    // -- The last write ------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_still_in_flight_at_the_end_is_waited_for_and_said_so()
    {
        using var writing = new ManualResetEventSlim();
        using var mayFinish = new ManualResetEventSlim();

        var write = Task.Run(() => _gate.Execute("a.mp3", _ =>
        {
            writing.Set();
            mayFinish.Wait();
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));

        var closing = Sequence().RunAsync(Prompt(true));
        Assert.False(closing.IsCompleted);
        Assert.Equal(["finishing"], _steps);

        mayFinish.Set();

        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(10)));
        await write;
    }
}
