using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JustPlay.Core.Organise;

/// <summary>
/// The pure decision core behind moving, copying and deleting tracks - no writes, no deletes, no
/// threads, no timers. Given a request and an <see cref="IFileProbe"/> it answers, BEFORE anything
/// happens: where each file would land, which names are already taken, whether a move is a rename or
/// a copy across volumes, how many bytes are involved, and what makes the whole thing impossible.
///
/// <para>Same shape as <c>WatchQueue</c> behind <c>LibraryWatcher</c> and <c>AnalysisQueue</c> behind
/// <c>AnalysisBatchRunner</c>: everything is a decision here, nothing is an action, so the awkward
/// cases - a name already taken twice over, a source that vanished while the menu was open, a
/// destination that is the folder you are already in - are one table-driven test each.</para>
///
/// <para><b>Protection goes in FRONT of the action.</b> This class exists so the preview can state
/// what will happen and the user can say no. There is no undo behind it, and there is no overwrite
/// in it: a taken name is a collision, and the two answers are "leave both alone" and "keep both".
/// </para>
/// </summary>
public static class OrganisePlanner
{
    /// <summary>
    /// How many suffixed names "keep both" will try before giving up on one file. A folder holding
    /// 999 files of the same name is not a case worth a cleverer algorithm; the file stays a
    /// collision and is left alone, which is the answer that cannot lose anything.
    /// </summary>
    public const int MaxKeepBothAttempts = 999;

    /// <summary>Work out the whole plan. Cheap enough to re-run on every keystroke or toggle -
    /// it is a handful of stats per file and no file is opened.</summary>
    public static OrganisePlan Plan(OrganiseRequest request, IFileProbe probe)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(probe);

        var sources = Distinct(request.Sources);
        var blockers = new List<OrganiseBlocker>();

        if (sources.Count == 0) blockers.Add(OrganiseBlocker.NoFiles);

        var destination = Normalise(request.Destination);

        if (request.Action == OrganiseAction.Delete)
        {
            // A missing bin is not a blocker. It changes WHAT the delete is, the preview says which
            // one in the words the button carries, and the person in front of it decides. Refusing
            // would leave them with no way to do a thing this window otherwise offers.
            var deleteItems = sources.Select(s => DeleteItem(s, probe)).ToList();

            return new OrganisePlan
            {
                Action        = request.Action,
                Destination   = null,
                Collisions    = request.Collisions,
                Deletion      = request.RecycleBinAvailable ? DeleteKind.Recycle : DeleteKind.Permanent,
                Items         = deleteItems,
                Blockers      = blockers,
                SourceFolders = FoldersOf(sources),
            };
        }

        if (destination is null)
        {
            blockers.Add(OrganiseBlocker.NoDestination);
        }
        else if (!probe.DirectoryExists(destination))
        {
            blockers.Add(OrganiseBlocker.DestinationMissing);
        }
        else
        {
            if (!probe.CanWriteTo(destination)) blockers.Add(OrganiseBlocker.DestinationNotWritable);
            if (sources.Any(s => probe.DirectoryExists(s) && IsInside(s, destination)))
                blockers.Add(OrganiseBlocker.DestinationInsideSelection);
        }

        // Names this plan has already claimed. A collision is not only against the disk: two
        // selected files can carry the same name out of two different folders, and the second one
        // must not be allowed to quietly land on the first one's new copy.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<OrganiseItem>(sources.Count);

        foreach (var source in sources)
            items.Add(TransferItem(source, destination, request.Collisions, probe, claimed));

        return new OrganisePlan
        {
            Action        = request.Action,
            Destination   = destination,
            Collisions    = request.Collisions,
            Items         = items,
            Blockers      = blockers,
            SourceFolders = FoldersOf(sources),
        };
    }

    // -- One file --------------------------------------------------------------------------------

    private static OrganiseItem DeleteItem(string source, IFileProbe probe)
    {
        if (probe.DirectoryExists(source))
            return new OrganiseItem { SourcePath = source, Status = OrganiseItemStatus.NotAFile };

        if (!probe.FileExists(source))
            return new OrganiseItem { SourcePath = source, Status = OrganiseItemStatus.SourceMissing };

        return new OrganiseItem
        {
            SourcePath = source,
            Status     = OrganiseItemStatus.Ready,
            Bytes      = probe.SizeOf(source),
        };
    }

    private static OrganiseItem TransferItem(
        string source, string? destination, CollisionPolicy policy,
        IFileProbe probe, HashSet<string> claimed)
    {
        if (probe.DirectoryExists(source))
            return new OrganiseItem { SourcePath = source, Status = OrganiseItemStatus.NotAFile };

        if (!probe.FileExists(source))
            return new OrganiseItem { SourcePath = source, Status = OrganiseItemStatus.SourceMissing };

        var bytes = probe.SizeOf(source);

        // With no destination there is still a useful answer - the size and the fact that the file
        // is there - so the preview can already show the list while she picks a folder.
        if (destination is null)
            return new OrganiseItem
            {
                SourcePath = source,
                Status     = OrganiseItemStatus.Ready,
                Bytes      = bytes,
            };

        var sourceFolder = FolderOf(source);
        if (sourceFolder is not null && PathsEqual(sourceFolder, destination))
            return new OrganiseItem
            {
                SourcePath      = source,
                DestinationPath = source,
                Status          = OrganiseItemStatus.SameFolder,
                Bytes           = bytes,
            };

        var crossVolume = CrossesVolume(source, destination, probe);
        var wanted = Path.Combine(destination, Path.GetFileName(source));

        if (!IsTaken(wanted, probe, claimed))
        {
            claimed.Add(wanted);
            return new OrganiseItem
            {
                SourcePath      = source,
                DestinationPath = wanted,
                Status          = OrganiseItemStatus.Ready,
                Bytes           = bytes,
                CrossVolume     = crossVolume,
            };
        }

        if (policy == CollisionPolicy.KeepBoth && FreeName(wanted, probe, claimed) is { } free)
        {
            claimed.Add(free);
            return new OrganiseItem
            {
                SourcePath      = source,
                DestinationPath = free,
                Status          = OrganiseItemStatus.KeepBoth,
                Bytes           = bytes,
                CrossVolume     = crossVolume,
            };
        }

        return new OrganiseItem
        {
            SourcePath      = source,
            DestinationPath = wanted,
            Status          = OrganiseItemStatus.Collides,
            Bytes           = bytes,
            CrossVolume     = crossVolume,
        };
    }

    // -- Naming ----------------------------------------------------------------------------------

    private static bool IsTaken(string path, IFileProbe probe, HashSet<string> claimed) =>
        claimed.Contains(path) || probe.FileExists(path) || probe.DirectoryExists(path);

    /// <summary>
    /// The first free "name (n).ext" beside a taken name, counting from 2 - the convention every
    /// file manager on Windows and macOS already uses, so it needs no explaining. Null when nothing
    /// is free within <see cref="MaxKeepBothAttempts"/>.
    /// </summary>
    internal static string? FreeName(string wanted, IFileProbe probe, HashSet<string> claimed)
    {
        var folder = Path.GetDirectoryName(wanted) ?? "";
        var stem   = Path.GetFileNameWithoutExtension(wanted);
        var ext    = Path.GetExtension(wanted);

        for (var n = 2; n <= MaxKeepBothAttempts; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!IsTaken(candidate, probe, claimed)) return candidate;
        }

        return null;
    }

    // -- Paths -----------------------------------------------------------------------------------

    private static bool CrossesVolume(string source, string destination, IFileProbe probe)
    {
        var from = probe.VolumeOf(source);
        var to   = probe.VolumeOf(destination);

        // Two nulls mean "cannot tell", and the safe reading of "cannot tell" is that this is NOT a
        // rename: copy - verify - remove is correct on one volume too, only slower.
        if (from is null || to is null) return true;
        return !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Distinct(IReadOnlyList<string>? sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (var raw in sources ?? [])
        {
            if (Normalise(raw) is not { } path) continue;
            if (seen.Add(path)) kept.Add(path);
        }

        return kept;
    }

    private static IReadOnlyList<string> FoldersOf(IReadOnlyList<string> sources) =>
        sources.Select(FolderOf).OfType<string>()
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
               .ToList();

    private static string? FolderOf(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Absolute, with any trailing separator taken off, or null when it makes no sense.
    ///
    /// <para>(!!) A DRIVE ROOT keeps its separator, and that is not cosmetic. "D:\" trimmed to "D:"
    /// is not the drive - it is a DRIVE-RELATIVE path, and <c>Path.Combine("D:", "track.mp3")</c>
    /// yields "D:track.mp3", which Windows resolves against the process's current directory on that
    /// drive. Measured: with the app started from D:\repos\just-play, "D:track.mp3" resolves to
    /// D:\repos\just-play\track.mp3. Picking a drive root - a USB stick, say - as the destination of
    /// a MOVE would therefore take the file out of the library and put it somewhere nobody chose.
    /// A UNC share root is safe (it has no relative form), so this is a drive-letter case only.</para>
    /// </summary>
    internal static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length == 0) return full;

            // "D:" - a drive letter and nothing else. Give it back its separator.
            return trimmed.Length == 2 && trimmed[1] == Path.VolumeSeparatorChar
                ? trimmed + Path.DirectorySeparatorChar
                : trimmed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="inner"/> the folder itself or below it? Compares on a separator
    /// boundary, so "...\music2" is not treated as being inside "...\music" - the same rule
    /// <c>LibraryIndexRegistry</c> uses for roots.</summary>
    private static bool IsInside(string outer, string inner) =>
        PathsEqual(outer, inner) ||
        inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        inner.StartsWith(outer + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
