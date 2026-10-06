using System;
using System.Threading.Tasks;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// What closing the window has to settle before the process may exit, in order. Kept apart from the
/// window so the order is pinned by a test - the window supplies the one thing it owns, the dialog.
///
/// <list type="number">
/// <item><description>A running ANALYZE batch is cancelled and awaited. Cancelling stops new files
/// from starting and aborts a decode; a file whose measurement already finished is still written, so
/// the wait is for the files in progress, not for the batch.</description></item>
/// <item><description>A running multi-file save is stopped at the write gate: the file it is writing
/// finishes, the next one is refused.</description></item>
/// <item><description>Only when no save was running is the unsaved-changes prompt shown. Its Save
/// STARTS a save, and a second save on top of a running one is exactly what this exists to
/// prevent.</description></item>
/// <item><description>Last, the gate shuts and drains: nothing is being written when the process
/// exits, and nothing can start.</description></item>
/// </list>
///
/// <para>While it waits, <see cref="Finishing"/> is on - the window shows the shared busy overlay
/// saying it is finishing the current file.</para>
/// </summary>
public sealed class CloseSequence
{
    public required TagWriteGate Writes { get; init; }

    /// <summary>An ANALYZE batch is running.</summary>
    public required Func<bool> Analysing { get; init; }

    /// <summary>Ask the running batch to stop after the files in progress.</summary>
    public required Action CancelAnalysis { get; init; }

    /// <summary>The running batch - or a completed task when there is none.</summary>
    public required Func<Task> AnalysisRun { get; init; }

    /// <summary>The shared editor is writing a multi-file save.</summary>
    public required Func<bool> EditorSaving { get; init; }

    /// <summary>The shared editor holds edits nobody has saved.</summary>
    public required Func<bool> EditorDirty { get; init; }

    /// <summary>On while closing waits for a write to finish, off when it stops waiting.</summary>
    public Action<bool>? Finishing { get; init; }

    /// <summary>
    /// The close that needs nothing settled - no batch, no save, no unsaved edits, no write in
    /// flight. Shuts the gate and answers true when the window may close right away; false means
    /// <see cref="RunAsync"/> has to do it.
    /// </summary>
    public bool TryNow()
    {
        if (Analysing() || EditorSaving() || EditorDirty()) return false;
        return Writes.ShutAsync().IsCompleted;
    }

    /// <summary>
    /// Settle everything, asking about unsaved edits through <paramref name="confirmLeave"/> when
    /// that is safe. True: the window may close now - nothing is being written and nothing can start.
    /// False: the user chose to stay, and nothing has been shut.
    /// </summary>
    public async Task<bool> RunAsync(Func<Task<bool>> confirmLeave)
    {
        ArgumentNullException.ThrowIfNull(confirmLeave);

        // Asked ONCE, up front: after the gate is shut a stopped save is no longer "running", and the
        // prompt below must still stay away from it.
        var saving = EditorSaving();

        if (Analysing() || saving)
        {
            Finishing?.Invoke(true);
            try
            {
                CancelAnalysis();

                // Shut BEFORE waiting for the batch: the save is what has to stop, between two files.
                // The batch's own finished files are written by then or refused - a refused one is
                // filed as not done, never as failed.
                if (saving) await Writes.ShutAsync().ConfigureAwait(true);

                try
                {
                    await AnalysisRun().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // A batch that ended in an exception has stopped all the same, which is all
                    // closing needs to know. Its own handler already reported it.
                }
            }
            finally
            {
                Finishing?.Invoke(false);
            }
        }

        if (!saving && EditorDirty() && !await confirmLeave().ConfigureAwait(true)) return false;

        var drained = Writes.ShutAsync();
        if (!drained.IsCompleted)
        {
            Finishing?.Invoke(true);
            await drained.ConfigureAwait(true);
        }

        return true;
    }
}
