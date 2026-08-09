namespace JustPlay.Library;

/// <summary>
/// A folder boiled down to two numbers: how many tracks it holds, and the newest modification time
/// among them - the check (2026-07-30): sort the folder's files by modification time and take the
/// newest one.
///
/// <para>Why those two and not the FOLDER's own timestamp: a directory's mtime moves when an entry
/// is created, deleted or renamed, but NOT when a file's contents change - so it would miss every
/// retag. Measured proof on 2026-07-30: <c>GENRES\Bass_House</c> held a file modified 07-29 22:05
/// while the directory still said 07-17 11:35. The newest FILE mtime moves for all three cases.</para>
///
/// <para>This never replaces the per-file check - it is the early-out that decides whether a folder
/// needs one at all. Computing it costs the same directory enumeration either way (0.12-0.25 ms per
/// file, measured), which is why that work belongs in the background, off the path that paints the
/// list.</para>
/// </summary>
/// <param name="FileCount">Audio files directly in the folder.</param>
/// <param name="MaxModifiedUtc">Newest last-write time among them, or null when the folder is empty.</param>
public readonly record struct FolderFingerprint(int FileCount, DateTime? MaxModifiedUtc)
{
    /// <summary>An empty / never-recorded folder.</summary>
    public static readonly FolderFingerprint None = new(0, null);

    /// <summary>
    /// True if two fingerprints describe the same folder state. Times compare with the same 2 s
    /// tolerance the per-file cheap key uses (FAT/exFAT round to 2 s, SMB clients drift a hair).
    /// </summary>
    public bool Matches(FolderFingerprint other)
    {
        if (FileCount != other.FileCount) return false;
        if (MaxModifiedUtc is null || other.MaxModifiedUtc is null)
            return MaxModifiedUtc is null && other.MaxModifiedUtc is null;

        var delta = MaxModifiedUtc.Value.ToUniversalTime() - other.MaxModifiedUtc.Value.ToUniversalTime();
        return delta.Duration() <= TimeSpan.FromSeconds(2);
    }

    /// <summary>Computes the fingerprint of what is on disk right now (one enumeration).</summary>
    public static FolderFingerprint FromDisk(string folder)
    {
        var count = 0;
        DateTime? newest = null;

        foreach (var file in AudioFiles.EnumerateWithKeys(folder, recursive: false))
        {
            count++;
            if (newest is null || file.ModifiedUtc > newest) newest = file.ModifiedUtc;
        }

        return new FolderFingerprint(count, newest);
    }

    /// <summary>Computes the fingerprint of an already-enumerated listing (no second walk).</summary>
    public static FolderFingerprint FromFiles(IReadOnlyList<AudioFiles.ScannedFile> files)
    {
        DateTime? newest = null;
        foreach (var f in files)
            if (newest is null || f.ModifiedUtc > newest) newest = f.ModifiedUtc;

        return new FolderFingerprint(files.Count, newest);
    }
}
