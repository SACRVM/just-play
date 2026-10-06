using System;
using System.Collections.Generic;

namespace JustPlay.Core.Layout;

/// <summary>
/// Puts a list's selection back after the list was rebuilt - matched by FILE PATH, never by object.
///
/// <para>Why by path: a rebuild is not always a re-sort of the same rows. The finder's background
/// check reloads a folder whose files moved on disk, and that builds NEW row objects for the same
/// files - so a selection kept by reference found nothing, fell back to the first row, and the tag
/// editor (which follows the selection) jumped to a track nobody had picked.</para>
///
/// <para>Paths compare case-insensitively, the way the finder compares them everywhere else.</para>
/// </summary>
public static class SelectionCarryOver
{
    /// <summary>The selection to show after a rebuild: the cursor row, and every selected row (the
    /// cursor included), in list order.</summary>
    public readonly record struct Result<T>(T? Cursor, IReadOnlyList<T> Selected) where T : class;

    /// <summary>
    /// Find the rows of <paramref name="rows"/> that hold the previously selected files.
    /// <list type="bullet">
    /// <item><description>The cursor stays on its file if that file is still listed; otherwise it
    /// moves to the first surviving selected row, and failing that to the first row.</description></item>
    /// <item><description>One row per file: a playlist may list the same file twice, and a selection
    /// naming one file twice would turn the editor into a "multi-file" edit of a single file.</description></item>
    /// <item><description>An empty result only for an empty list - with rows, something is always
    /// selected, as before.</description></item>
    /// </list>
    /// </summary>
    public static Result<T> Restore<T>(
        IReadOnlyList<T> rows, Func<T, string> pathOf, string? cursorPath, IEnumerable<string> selectedPaths)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pathOf);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        var wanted = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        if (cursorPath is not null) wanted.Add(cursorPath);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<T>();
        T? cursor = null;

        foreach (var row in rows)
        {
            var path = pathOf(row);
            if (!wanted.Contains(path) || !taken.Add(path)) continue;
            selected.Add(row);
            if (cursor is null && cursorPath is not null
                && string.Equals(path, cursorPath, StringComparison.OrdinalIgnoreCase))
                cursor = row;
        }

        cursor ??= selected.Count > 0 ? selected[0] : rows.Count > 0 ? rows[0] : null;
        if (selected.Count == 0 && cursor is not null) selected.Add(cursor);
        return new Result<T>(cursor, selected);
    }

    /// <summary>True when two selections name the same FILES - order, repeats and case aside. What a
    /// follower of the selection (the tag editor) needs to know before it retargets: a rebuild that put
    /// the same files back is not a new selection.</summary>
    public static bool SamePaths(IEnumerable<string> a, IEnumerable<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
    }
}
