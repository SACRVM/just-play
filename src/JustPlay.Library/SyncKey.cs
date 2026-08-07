namespace JustPlay.Library;

/// <summary>
/// What the index remembers about a file's identity, and nothing else. A sync pass compares this
/// against the directory entry it just enumerated - no file is opened unless they disagree.
///
/// <para>Measured 2026-07-30 on <c>\\nas\music\GENRES</c>: a directory entry costs 0.23 ms,
/// opening the file to read its tags costs 57.5 ms. This struct is the ~250x saving.</para>
/// </summary>
/// <param name="FileSizeBytes">Size as recorded at index time.</param>
/// <param name="ModifiedUtc">ISO-8601 UTC last-write time as recorded, or null for a pre-0.6 entry.</param>
/// <param name="Missing">True if the last sync could not find the file.</param>
/// <param name="Analysed">
/// True if the entry holds a successful analysis. A sweep that only refreshes the index does not
/// care, but <c>analyze</c> does: an entry parked as "known but never analysed" must not be
/// mistaken for finished work.
/// </param>
/// <param name="TagRev">
/// Which tag-extraction shape wrote the row - see <see cref="TrackIndexEntry.TagRev"/>. It rides in
/// the cheap key because <c>Reconcile</c>, the pass that walks the WHOLE library, compares nothing
/// else: without it a new indexed column would never reach a single existing row, since every
/// unchanged file is skipped here before anything is opened.
/// </param>
public readonly record struct SyncKey(
    long FileSizeBytes, string? ModifiedUtc, bool Missing, bool Analysed, int TagRev = 0)
{
    /// <summary>True if the file on disk still matches this key (2 s mtime tolerance) AND the row was
    /// written by the current extraction shape - otherwise it needs re-reading even though nothing
    /// about the FILE changed.</summary>
    public bool Matches(long sizeBytes, DateTime modifiedUtc) =>
        TagRev >= TrackIndexEntry.CurrentTagRev &&
        TrackIndexEntry.LooksUnchanged(FileSizeBytes, ModifiedUtc, sizeBytes, modifiedUtc);
}
