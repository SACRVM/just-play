using System;
using System.IO;
using System.Threading.Tasks;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// The one door every tag write JUST TAG makes goes through - the editor's saves, TRANSFORM's, and
/// the ANALYZE run's. Program.cs hands <see cref="Execute"/> to the editor as its
/// <see cref="TagWriteExecutor"/>; the analyze run calls <see cref="Write"/>.
///
/// <para><b>It lets go of a previewed file first.</b> A file the preview is playing has an open
/// handle, and a tag write on an open handle fails.</para>
///
/// <para><b>A failed write is reported, never thrown at the editor.</b> <see cref="Execute"/> names
/// the file in the log and answers <see cref="TagWriteOutcome.Failed"/>, which the editor already
/// turns into its "Save failed" line. The exception used to escape instead: a read-only, locked or
/// vanished file produced no visible error at all, and a save started from the unsaved-changes
/// prompt threw out of the selection handler halfway, leaving the list on the new row while the
/// editor stayed on the old one.</para>
///
/// <para><b>It knows which writes are in flight, so closing can wait for them.</b> When a tag grows
/// - a first JUSTPLAY blob usually makes it grow - TagLib rewrites the file in place, and a process
/// that exits in the middle of that truncates the audio. The analyze run and the multi-file save
/// write from worker threads, so closing the window used to be able to end the process mid-write.
/// <see cref="ShutAsync"/> refuses every write that has not started and completes when the last one
/// that has is done; the window does not close before that.</para>
/// </summary>
public sealed class TagWriteGate
{
    private readonly Action<string> _release;
    private readonly Action<string> _log;

    private readonly object _sync = new();
    private int _inFlight;
    private bool _shut;
    private TaskCompletionSource? _drained;

    /// <param name="releaseFile">Lets go of the file if the preview is holding it. Runs on the
    /// writing thread, before the write.</param>
    /// <param name="log">Where a write that did not happen is named.</param>
    public TagWriteGate(Action<string> releaseFile, Action<string> log)
    {
        _release = releaseFile ?? throw new ArgumentNullException(nameof(releaseFile));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Writes that have started and not finished.</summary>
    public int InFlight
    {
        get { lock (_sync) return _inFlight; }
    }

    /// <summary>
    /// The editor's executor: write now and answer what happened. Never throws - a failure is named
    /// in the log and comes back as <see cref="TagWriteOutcome.Failed"/>.
    /// </summary>
    public TagWriteOutcome Execute(string path, Action<string> write)
    {
        try
        {
            Write(path, write);
            return TagWriteOutcome.Written;
        }
        catch (TagWritesClosedException)
        {
            _log($"Not written - \"{Path.GetFileName(path)}\": the window is closing");
            return TagWriteOutcome.Failed;
        }
        catch (Exception ex)
        {
            _log($"Tag write failed - \"{Path.GetFileName(path)}\": {ex.Message}");
            return TagWriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Write now and let a failure THROW - for a caller that reports failures itself, as the analyze
    /// run does: its batch runner counts the exception and names the file in the run's report.
    /// Once <see cref="ShutAsync"/> has been called this throws <see cref="TagWritesClosedException"/>
    /// without touching the file.
    /// </summary>
    public void Write(string path, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        // The check and the count are ONE step: a write that slipped in between them would be one
        // the shutdown neither refused nor waited for.
        lock (_sync)
        {
            if (_shut) throw new TagWritesClosedException();
            _inFlight++;
        }

        try
        {
            _release(path);
            write(path);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_sync)
            {
                if (--_inFlight == 0) (drained, _drained) = (_drained, null);
            }
            drained?.TrySetResult();
        }
    }

    /// <summary>
    /// Stop letting writes start, and complete when none is running any more. A file that is being
    /// written is always finished - never cut off - and every write asked for afterwards is refused
    /// without opening its file, so a multi-file save stops between two files.
    ///
    /// <para>One-way on purpose: it is called only once the window has decided to close, so there is
    /// no state in which a refused write would have to be retried.</para>
    /// </summary>
    public Task ShutAsync()
    {
        lock (_sync)
        {
            _shut = true;
            if (_inFlight == 0) return Task.CompletedTask;

            // Continuations run off the writing thread: the writer must not end up running the
            // window's close from inside its own finally.
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }
}

/// <summary>
/// A write refused because the window is closing. An <see cref="OperationCanceledException"/>, so
/// the analyze run's batch runner files the file under "not done" rather than "failed" - it was
/// never touched.
/// </summary>
public sealed class TagWritesClosedException()
    : OperationCanceledException("The window is closing - no further tag writes are started.");
