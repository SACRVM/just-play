using System;
using System.IO;
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
/// </summary>
public sealed class TagWriteGate
{
    private readonly Action<string> _release;
    private readonly Action<string> _log;

    /// <param name="releaseFile">Lets go of the file if the preview is holding it. Runs on the
    /// writing thread, before the write.</param>
    /// <param name="log">Where a write that did not happen is named.</param>
    public TagWriteGate(Action<string> releaseFile, Action<string> log)
    {
        _release = releaseFile ?? throw new ArgumentNullException(nameof(releaseFile));
        _log = log ?? throw new ArgumentNullException(nameof(log));
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
        catch (Exception ex)
        {
            _log($"Tag write failed - \"{Path.GetFileName(path)}\": {ex.Message}");
            return TagWriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Write now and let a failure THROW - for a caller that reports failures itself, as the analyze
    /// run does: its batch runner counts the exception and names the file in the run's report.
    /// </summary>
    public void Write(string path, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        _release(path);
        write(path);
    }
}
