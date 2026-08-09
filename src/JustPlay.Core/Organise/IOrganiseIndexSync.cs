using System.Collections.Generic;

namespace JustPlay.Core.Organise;

/// <summary>
/// Told which files appeared and which disappeared, so a library index can be brought back in line
/// with the disk.
///
/// <para><b>Why it is a seam and not a call.</b> Never leaving a song behind is a standing rule:
/// moving a track out of an indexed folder must not make it vanish from the library. But the index
/// lives one layer up from here (it is SQLite, and it knows about roots and tag blobs), and the
/// preview window that runs the operation cannot see that layer at all. So the operation states what
/// it changed and whoever owns an index implements this and reconciles it.</para>
///
/// <para>An app with no index simply passes null, and nothing about the move changes.</para>
/// </summary>
public interface IOrganiseIndexSync
{
    /// <summary>
    /// Bring the index in line after files moved. Called once per run, off the UI thread, with the
    /// paths from an <see cref="OrganiseReport"/>.
    ///
    /// <para>Must never throw: a library index that cannot be reached is a slower browser, not a
    /// failed move, and the files have already been moved by the time this is called.</para>
    /// </summary>
    /// <param name="added">Files that now exist and did not before.</param>
    /// <param name="removed">Files that are no longer where they were.</param>
    void PathsChanged(IReadOnlyList<string> added, IReadOnlyList<string> removed);
}
