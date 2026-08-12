using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;

namespace JustPlay.Library;

/// <summary>
/// Brings the library index back in line after files were moved, copied or deleted - the
/// <see cref="IOrganiseIndexSync"/> half that actually knows what an index is.
///
/// <para><b>Never leave a song behind.</b> A track moved out of an indexed folder is not gone, it is
/// somewhere else, and the library has to say so. Without this, moving a file inside JUST TAG would
/// leave JUST PLAY's finder pointing at a path that no longer exists and never showing the one that
/// does.</para>
///
/// <para><b>It calls <see cref="LibrarySync.VerifyTracks"/> and nothing else.</b> Reconciliation is
/// already written, tested and used by the observer and the CLI; a second copy of it here would be
/// the exact divergence the suite rule forbids. The departed paths come back as "missing" and the
/// arrivals are indexed straight out of their own tag blob - so a moved track keeps its analysis and
/// nothing is re-run.</para>
///
/// <para>(!) Only folders that are ALREADY indexed are touched, via
/// <see cref="LibraryIndexRegistry.OpenFor"/>: <see cref="LibraryDb.Open"/> is
/// <c>ReadWriteCreate</c>, so reaching for a database that is not there would leave an empty one
/// behind that reads as "indexed" forever. A move between two unindexed download folders therefore
/// does nothing here, which is correct.</para>
///
/// <para><b>Nothing here throws.</b> By the time it runs, the files have already moved. An index on
/// a share that just went away is a browser that is a little out of date until the next sweep - it
/// is not a failed move, and it must not be reported as one.</para>
/// </summary>
public sealed class LibraryOrganiseSync(IMetadataReader metadata, Action<string>? log = null)
    : IOrganiseIndexSync
{
    public void PathsChanged(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var touched = new List<string>();
        if (added is not null) touched.AddRange(added);
        if (removed is not null) touched.AddRange(removed);
        if (touched.Count == 0) return;

        // (!) The machine's registry is read ONCE for the whole batch. LibraryIndexRegistry.RootFor
        // reads and parses roots.json on every call, so asking it per path made a 500-file move open
        // and re-parse the same small file 500 times. The pure overload takes the roots it was given.
        var roots = LibraryIndexRegistry.Roots();
        if (roots.Count == 0) return;

        // One database per registered root. Two paths under the same root share a connection; a path
        // under no root has no index to correct and is dropped here.
        var byRoot = touched
            .Select(p => (Path: p, Root: RootOf(roots, p)))
            .Where(x => x.Root is not null)
            .GroupBy(x => x.Root!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byRoot)
        {
            try
            {
                using var db = LibraryIndexRegistry.OpenFor(group.Key);
                if (db is null) continue;   // registered but never actually indexed - leave it alone

                var paths = group.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // The folder fingerprint is a (count, newest mtime) early-out that a later
                // VerifyFolder trusts. A move changes both folders, so forgetting their fingerprints
                // is what stops the next folder check from short-circuiting past the change.
                foreach (var folder in paths.Select(Path.GetDirectoryName)
                                            .OfType<string>()
                                            .Distinct(StringComparer.OrdinalIgnoreCase))
                    db.ForgetFolderState(folder);

                var result = new LibrarySync(db, metadata).VerifyTracks(paths);

                log?.Invoke(
                    $"[organise] index {group.Key}: +{result.Added} ~{result.Updated} " +
                    $"-{result.MarkedMissing}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[organise] index {group.Key} not updated: {ex.Message}");
            }
        }
    }

    private static string? RootOf(IEnumerable<string> roots, string path)
    {
        try
        {
            return Path.GetDirectoryName(path) is { Length: > 0 } folder
                ? LibraryIndexRegistry.RootFor(roots, folder)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
