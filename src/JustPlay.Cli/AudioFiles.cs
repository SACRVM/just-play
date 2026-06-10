namespace JustPlay.Cli;

/// <summary>
/// Shared constants and utilities for finding audio files in the library.
/// </summary>
internal static class AudioFiles
{
    /// <summary>
    /// Lower-case extensions (including the dot) that JustPlay recognises as audio.
    /// Matches the set in <c>TrackEngine</c> plus AAC/WMA which can appear in DJ libraries.
    /// </summary>
    public static readonly string[] Extensions =
        [".mp3", ".wav", ".flac", ".aif", ".aiff", ".m4a", ".aac", ".ogg", ".wma"];

    /// <summary>
    /// Returns an enumeration of all audio files under <paramref name="root"/> (recursive),
    /// sorted by full path for deterministic ordering.
    /// </summary>
    public static IEnumerable<string> Enumerate(string root)
        => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(
                Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes the SHA-256 hex digest of a file's bytes WITHOUT loading it entirely into
    /// memory — reads in 128 KB chunks. Used for exact-dedup and index freshness checks.
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
