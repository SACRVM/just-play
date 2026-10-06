namespace JustPlay.Library;

/// <summary>
/// What a STRICT look at one folder found - the evidence a sync needs before it may call a file gone.
///
/// <para>Both everyday ways of asking "is it there?" answer "no" when the honest answer is "could not
/// look": <c>FileInfo.Exists</c> returns false for a share that is asleep or unreachable exactly as
/// for a deleted file, and the library walk lists an access-denied folder as empty. Taken at face
/// value, one playlist refresh against a sleeping NAS flagged every listed track missing, which hides
/// it from the library views. A strict listing throws instead, so the two answers stay apart.</para>
/// </summary>
internal sealed class FolderListing
{
    public enum Outcome
    {
        /// <summary>The folder was read in full; what it does not hold is not there.</summary>
        Listed,

        /// <summary>The folder itself does not exist - deleted, renamed, or an unmounted share.</summary>
        NotFound,

        /// <summary>The folder could not be read (access denied, share unreachable). Says nothing.</summary>
        Unreachable,
    }

    private static readonly Dictionary<string, AudioFiles.ScannedFile> NoFiles = [];

    private readonly Dictionary<string, AudioFiles.ScannedFile> _byName;

    private FolderListing(Outcome state, Dictionary<string, AudioFiles.ScannedFile> byName)
    {
        State   = state;
        _byName = byName;
    }

    public Outcome State { get; }

    /// <summary>Lists <paramref name="folder"/> strictly (non-recursive, audio files only).</summary>
    public static FolderListing Of(string folder)
    {
        try
        {
            // Keyed by file name with the index's own case rule (paths are COLLATE NOCASE there).
            var byName = new Dictionary<string, AudioFiles.ScannedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in AudioFiles.ListFolderStrict(folder))
                byName[Path.GetFileName(file.Path)] = file;

            return new FolderListing(Outcome.Listed, byName);
        }
        catch (DirectoryNotFoundException)
        {
            return new FolderListing(Outcome.NotFound, NoFiles);
        }
        catch (Exception)
        {
            return new FolderListing(Outcome.Unreachable, NoFiles);
        }
    }

    /// <summary>
    /// True if <paramref name="folder"/> can be listed right now. Opens it and reads the first batch
    /// only, so it costs the same for a folder of ten files as for one of ten thousand.
    /// </summary>
    public static bool CanList(string folder)
    {
        try
        {
            using var entries = Directory
                .EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = false })
                .GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The listed entry for <paramref name="path"/>, matched by file name.</summary>
    public bool TryGet(string path, out AudioFiles.ScannedFile file) =>
        _byName.TryGetValue(Path.GetFileName(path), out file);
}
