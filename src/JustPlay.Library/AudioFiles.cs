namespace JustPlay.Library;

/// <summary>
/// Shared constants and utilities for finding audio files in the library.
///
/// <para>0.6 (THE LIBRARY): moved here from <c>JustPlay.Cli</c> — the app, the finder, the CLI
/// and JUST TAG must agree on WHAT counts as a track, or two front-ends disagree about the size
/// of the same library. This is the one enumeration.</para>
/// </summary>
public static class AudioFiles
{
    /// <summary>
    /// Lower-case extensions (including the dot) that JustPlay recognises as audio — the ONE list;
    /// the app's <c>AudioFileTypes</c> delegates here. Matches <c>TrackEngine</c> plus AAC/WMA,
    /// which turn up in DJ libraries.
    ///
    /// <para>⚠ <c>.opus</c> is in the list because the app has always listed it, and a format
    /// silently vanishing from a browser is worse than one that fails loudly — but only
    /// <c>bassenc_opus.dll</c> (the streaming ENCODER) is vendored, not the <c>bassopus</c> decoder
    /// plugin. An .opus file therefore shows up and then fails to play or analyse.</para>
    /// </summary>
    public static readonly string[] Extensions =
        [".mp3", ".wav", ".flac", ".aif", ".aiff", ".m4a", ".aac", ".ogg", ".opus", ".wma"];

    /// <summary>
    /// Folder names that never hold library content: NAS system dirs (Synology thumbnails and
    /// recycle bins, the Windows equivalents). Recycled files in particular must NOT enter the
    /// index — a deleted track is not a track. Name-based on purpose: an attributes check would
    /// cost an extra round-trip per entry on a network share.
    /// </summary>
    public static readonly IReadOnlySet<string> IgnoredFolderNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "@eaDir", "#recycle", "@Recycle", "$RECYCLE.BIN", "System Volume Information",
        };

    /// <summary>True if <paramref name="path"/> has an audio extension we handle.</summary>
    public static bool IsAudio(string path) =>
        IsAudioFileName(Path.GetFileName(path.AsSpan()));

    /// <summary>
    /// Allocation-free variant for the enumeration hot path — 14k files means 14k strings we
    /// would otherwise create just to throw away.
    /// </summary>
    public static bool IsAudioFileName(ReadOnlySpan<char> fileName)
    {
        // AppleDouble sidecars ("._foo.aiff") are resource-fork metadata macOS drops on
        // SMB/FAT volumes — they carry an audio extension but are not audio.
        if (fileName.StartsWith("._", StringComparison.Ordinal)) return false;

        var dot = fileName.LastIndexOf('.');
        if (dot < 0) return false;

        var ext = fileName[dot..];
        foreach (var candidate in Extensions)
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// One audio file as the directory listing already described it — no file was opened.
    /// </summary>
    public readonly record struct ScannedFile(string Path, long SizeBytes, DateTime ModifiedUtc);

    /// <summary>
    /// Enumerates all audio files under <paramref name="root"/> WITH their size and last-write
    /// time, taken straight out of the directory-enumeration buffer.
    ///
    /// <para>This matters: a follow-up <c>new FileInfo(path).Length</c> would be a second
    /// round-trip per file over SMB. Measured 2026-07-30, the whole of
    /// <c>\\nas\music\GENRES</c> (14,186 files) enumerates in ~3.2 s — that is the entire cost of
    /// a reconcile sweep, versus ~57 ms per file to open one.</para>
    ///
    /// <para>Unordered — the caller compares against a dictionary, so sorting would only cost a
    /// full materialisation of the tree.</para>
    /// </summary>
    /// <param name="root">Folder to enumerate.</param>
    /// <param name="recursive">
    /// True (default) walks the whole subtree. False lists only the folder's own tracks — what the
    /// finder needs when it is showing one directory, where the disk stays authoritative for WHICH
    /// files exist and the index only supplies what is already known about them.
    /// </param>
    public static IEnumerable<ScannedFile> EnumerateWithKeys(string root, bool recursive = true) =>
        new System.IO.Enumeration.FileSystemEnumerable<ScannedFile>(
            root,
            (ref System.IO.Enumeration.FileSystemEntry entry) => new ScannedFile(
                entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
            new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                // macOS/SMB: protected subfolders (e.g. TemporaryItems, .Trashes) throw
                // UnauthorizedAccess and would abort the whole walk without this.
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            })
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                !entry.IsDirectory && IsAudioFileName(entry.FileName),

            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                !entry.FileName.StartsWith(".", StringComparison.Ordinal)
                && !IgnoredFolderNames.Contains(entry.FileName.ToString()),
        };

    /// <summary>
    /// Paths only, sorted by full path for deterministic ordering. Delegates to
    /// <see cref="EnumerateWithKeys"/> so there is exactly ONE definition of what the library
    /// contains.
    /// </summary>
    public static IEnumerable<string> Enumerate(string root)
        => EnumerateWithKeys(root)
            .Select(f => f.Path)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the SHA-256 hex digest of a file's bytes WITHOUT loading it entirely into
    /// memory — reads in 128 KB chunks. Used for exact-dedup and index freshness checks.
    ///
    /// <para>⚠ 0.6 note: this reads EVERY byte, so over SMB it pulls the whole library through
    /// the network. It is the fallback, never the scan trigger — see
    /// <see cref="TrackIndexEntry.LooksUnchanged"/> for the cheap size+mtime key that decides
    /// whether hashing is needed at all.</para>
    /// </summary>
    public static string Sha256(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, useAsync: false);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Formats a byte count into a human-readable string (B / KB / MB / GB).
    /// </summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L                => $"{bytes} B",
        < 1024L * 1024L        => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024L * 1024L => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _                      => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };
}
