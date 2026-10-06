using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The gate every JUST TAG write goes through.
///
/// <para>What was wrong: the executor JUST TAG handed the editor never answered
/// <see cref="TagWriteOutcome.Failed"/> - it let the writer's exception escape. A read-only, locked
/// or vanished file therefore produced no visible error, and a save started from the
/// unsaved-changes prompt threw out of the selection handler halfway through.</para>
/// </summary>
public class TagWriteGateTests
{
    private readonly List<string> _log = [];
    private readonly List<string> _released = [];

    private TagWriteGate Gate() => new(releaseFile: _released.Add, log: _log.Add);

    [Fact]
    public void A_write_that_lands_is_Written_and_logs_nothing()
    {
        var written = new List<string>();

        var outcome = Gate().Execute(@"C:\music\a.mp3", written.Add);

        Assert.Equal(TagWriteOutcome.Written, outcome);
        Assert.Equal([@"C:\music\a.mp3"], written);
        Assert.Empty(_log);
    }

    [Fact]
    public void A_write_that_throws_is_Failed_and_named_in_the_log()
    {
        var outcome = Gate().Execute(@"C:\music\locked.mp3",
            _ => throw new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Equal(TagWriteOutcome.Failed, outcome);
        var line = Assert.Single(_log);
        Assert.Contains("\"locked.mp3\"", line);
        Assert.Contains("Access to the path is denied.", line);
    }

    /// <summary>The editor's single-file save rethrows after noting the message - that is how its
    /// "Save failed - reason" line gets the reason. The gate must absorb that rethrow too.</summary>
    [Fact]
    public void A_rethrown_failure_still_comes_back_as_an_outcome()
    {
        Exception? noted = null;

        var outcome = Gate().Execute(@"C:\music\gone.mp3", _ =>
        {
            try { throw new FileNotFoundException("Could not find file."); }
            catch (Exception ex) { noted = ex; throw; }
        });

        Assert.Equal(TagWriteOutcome.Failed, outcome);
        Assert.IsType<FileNotFoundException>(noted);
    }

    [Fact]
    public void The_preview_lets_go_of_the_file_before_it_is_written()
    {
        var order = new List<string>();
        var gate = new TagWriteGate(releaseFile: p => order.Add("release " + p), log: _log.Add);

        gate.Execute("x.mp3", p => order.Add("write " + p));

        Assert.Equal(["release x.mp3", "write x.mp3"], order);
    }

    /// <summary>The analyze run reports failures through its batch runner, which counts an
    /// exception - so its entry point must keep throwing. An outcome code there would be counted as
    /// a file analysed.</summary>
    [Fact]
    public void Write_lets_the_failure_through_for_a_caller_that_reports_it_itself()
    {
        var gate = Gate();

        var ex = Assert.Throws<IOException>(() =>
            gate.Write("x.mp3", _ => throw new IOException("The process cannot access the file.")));

        Assert.Equal("The process cannot access the file.", ex.Message);
        Assert.Empty(_log);
    }

    // -- Closing ----------------------------------------------------------------------------------
    //
    // When a tag grows, TagLib rewrites the file in place; a process that exits mid-write truncates
    // the audio. The gate is what lets the window wait for the write in progress and refuse the rest.

    [Fact]
    public void Shutting_an_idle_gate_completes_at_once()
    {
        Assert.True(Gate().ShutAsync().IsCompleted);
    }

    [Fact]
    public async Task Shutting_waits_for_the_write_in_progress_and_never_cuts_it_off()
    {
        var gate = Gate();
        using var writing = new ManualResetEventSlim();
        using var mayFinish = new ManualResetEventSlim();
        var finished = false;

        var writer = Task.Run(() => gate.Execute("a.mp3", _ =>
        {
            writing.Set();
            mayFinish.Wait();
            finished = true;
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));

        var drained = gate.ShutAsync();
        Assert.False(drained.IsCompleted);
        Assert.Equal(1, gate.InFlight);

        mayFinish.Set();
        await drained.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(finished);
        Assert.Equal(TagWriteOutcome.Written, await writer);
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public void After_shutting_a_write_is_refused_without_touching_the_file()
    {
        var gate = Gate();
        var touched = false;
        gate.ShutAsync();

        var outcome = gate.Execute(@"C:\music\next.mp3", _ => touched = true);

        Assert.Equal(TagWriteOutcome.Failed, outcome);
        Assert.False(touched);
        Assert.Empty(_released);
        Assert.Contains("\"next.mp3\"", Assert.Single(_log));
    }

    /// <summary>A refusal on the analyze run's entry point is a CANCELLATION, so its batch runner
    /// files the file as not done rather than failed - it was never opened.</summary>
    [Fact]
    public void After_shutting_Write_throws_a_cancellation()
    {
        var gate = Gate();
        gate.ShutAsync();

        Assert.ThrowsAny<OperationCanceledException>(() => gate.Write("a.mp3", _ => { }));
    }

    [Fact]
    public async Task A_write_that_fails_while_closing_still_lets_the_close_through()
    {
        var gate = Gate();
        using var writing = new ManualResetEventSlim();
        using var mayFail = new ManualResetEventSlim();

        var writer = Task.Run(() => gate.Execute("a.mp3", _ =>
        {
            writing.Set();
            mayFail.Wait();
            throw new IOException("The network path was not found.");
        }));
        Assert.True(writing.Wait(TimeSpan.FromSeconds(10)));

        var drained = gate.ShutAsync();
        mayFail.Set();
        await drained.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TagWriteOutcome.Failed, await writer);
    }
}
