namespace JustPlay.Library;

/// <summary>
/// A folder boiled down to three numbers: how many tracks it holds, the newest modification time
/// among them, and a hash of the whole listing - every file's name, size and mtime.
///
/// <para>Why not the FOLDER's own timestamp: a directory's mtime moves when an entry is created,
/// deleted or renamed, but NOT when a file's contents change - so it would miss every retag. Measured
/// proof on 2026-07-30: <c>GENRES\Bass_House</c> held a file modified 07-29 22:05 while the directory
/// still read 07-17 11:35.</para>
///
/// <para>Why the count and newest mtime are not enough on their own: a rename keeps the file's
/// mtime, and an Explorer copy keeps the SOURCE file's mtime, which is usually older than the folder's
/// newest. Both leave the count and the newest time exactly where they were, so a renamed track or one
/// replaced by another stayed invisible behind the early-out. The listing hash moves for both.</para>
///
/// <para>This never replaces the per-file check - it is the early-out that decides whether a folder
/// needs one at all. Computing it costs the same directory enumeration either way (0.12-0.25 ms per
/// file, measured), which is why that work belongs in the background, off the path that paints the
/// list.</para>
/// </summary>
/// <param name="FileCount">Audio files directly in the folder.</param>
/// <param name="MaxModifiedUtc">Newest last-write time among them, or null when the folder is empty.</param>
/// <param name="ListingHash">
/// Order-independent hash of every file's name, size and mtime (to the second). Null for a fingerprint
/// stored by a build that did not compute one - see <see cref="Matches"/>.
/// </param>
public readonly record struct FolderFingerprint(
    int FileCount, DateTime? MaxModifiedUtc, long? ListingHash = null)
{
    /// <summary>An empty / never-recorded folder.</summary>
    public static readonly FolderFingerprint None = new(0, null);

    /// <summary>
    /// True if two fingerprints describe the same folder state. Times compare with the same 2 s
    /// tolerance the per-file cheap key uses (FAT/exFAT round to 2 s, SMB clients drift a hair).
    ///
    /// <para>A fingerprint without a listing hash never matches one with a hash. That is the upgrade
    /// path for fingerprints stored by an older build: one extra per-file pass, which then stores the
    /// new form.</para>
    /// </summary>
    public bool Matches(FolderFingerprint other)
    {
        if (FileCount != other.FileCount) return false;
        if (ListingHash != other.ListingHash) return false;
        if (MaxModifiedUtc is null || other.MaxModifiedUtc is null)
            return MaxModifiedUtc is null && other.MaxModifiedUtc is null;

        var delta = MaxModifiedUtc.Value.ToUniversalTime() - other.MaxModifiedUtc.Value.ToUniversalTime();
        return delta.Duration() <= TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Computes the fingerprint of what is on disk right now (one enumeration). Throws when the folder
    /// cannot be listed rather than fingerprinting it as empty.
    /// </summary>
    public static FolderFingerprint FromDisk(string folder) =>
        FromFiles(AudioFiles.ListFolderStrict(folder));

    /// <summary>Computes the fingerprint of an already-enumerated listing (no second walk).</summary>
    public static FolderFingerprint FromFiles(IReadOnlyList<AudioFiles.ScannedFile> files)
    {
        DateTime? newest = null;
        var hash = 0L;

        foreach (var f in files)
        {
            if (newest is null || f.ModifiedUtc > newest) newest = f.ModifiedUtc;
            // A SUM, so the order the share happens to list in does not matter and nothing needs sorting.
            hash = unchecked(hash + EntryHash(f));
        }

        return new FolderFingerprint(files.Count, newest, hash);
    }

    /// <summary>
    /// One file's share of <see cref="ListingHash"/>. The name is case-folded, matching the index's
    /// case-insensitive paths; the mtime is cut to whole seconds, so a share that reports sub-second
    /// precision differently after a reconnect costs at most one extra per-file pass. FNV-1a, then
    /// MurmurHash3's 64-bit finaliser so every bit is mixed before the entries are summed.
    /// </summary>
    private static long EntryHash(AudioFiles.ScannedFile file)
    {
        unchecked
        {
            const ulong prime = 1099511628211UL;
            var h = 14695981039346656037UL;

            foreach (var c in Path.GetFileName(file.Path.AsSpan()))
                h = (h ^ char.ToUpperInvariant(c)) * prime;

            h = (h ^ (ulong)file.SizeBytes) * prime;
            h = (h ^ (ulong)(file.ModifiedUtc.Ticks / TimeSpan.TicksPerSecond)) * prime;

            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            h *= 0xc4ceb3f53fe1a85bUL;
            h ^= h >> 33;
            return (long)h;
        }
    }
}
