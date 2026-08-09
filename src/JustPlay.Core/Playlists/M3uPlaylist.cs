using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Minimal reader/writer for the classic M3U / M3U8 playlist format - the universal interchange
/// format every player and DJ tool (Traktor, rekordbox, Serato, VirtualDJ, Engine...) can import.
/// We write UTF-8 (.m3u8) and carry the running order; the track analysis (BPM/key/energy) rides
/// along in the files' OWN tags, which those tools read on import - so we hand over a set as a
/// standalone file and never touch the other tool's library.
///
/// Platform-agnostic: pure text + path logic, no external dependencies.
/// </summary>
public static class M3uPlaylist
{
    public static readonly string[] Extensions = { ".m3u8", ".m3u" };

    /// <summary>True if the path looks like an M3U/M3U8 playlist (by extension).</summary>
    public static bool IsPlaylist(string? path) =>
        path is not null && Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line to write: the absolute file path plus optional #EXTINF metadata.</summary>
    public readonly record struct Entry(string Path, TimeSpan? Duration, string? Title);

    /// <summary>Write an extended M3U8 (UTF-8, no BOM, CRLF) listing the entries in order. Paths are
    /// ALWAYS written RELATIVE with FORWARD slashes whenever the track shares a root with the playlist -
    /// INCLUDING climbing out with "../" (a set in SETS/ references its tracks in the sibling GENRES/ as
    /// ../GENRES/...). This is the format the Mac/Traktor setup needs (see the traktor-playlist-format note):
    /// relative + forward-slash so the set resolves on any machine/mount. Only a track on a DIFFERENT
    /// drive/root (no relative path possible) stays absolute. ReadPaths resolves relatives against the
    /// playlist's own folder, so this round-trips.</summary>
    public static void Write(string destPath, IEnumerable<Entry> entries)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(destPath)) ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\r\n");
        foreach (var e in entries)
        {
            var secs = e.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : -1;
            var title = (e.Title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            sb.Append("#EXTINF:").Append(secs).Append(',').Append(title).Append("\r\n");
            sb.Append(ToPortablePath(e.Path, baseDir)).Append("\r\n");
        }
        File.WriteAllText(destPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Relative, forward-slash path whenever <paramref name="fullPath"/> shares a root with
    /// <paramref name="baseDir"/> - INCLUDING "../" parents (sibling folders like SETS/ <-> GENRES/). Only
    /// a different drive/root (Path.GetRelativePath returns a rooted path) stays absolute. Forward slashes
    /// because the sets are played on a Mac/Traktor deck. Never throws.</summary>
    private static string ToPortablePath(string fullPath, string baseDir)
    {
        if (baseDir.Length == 0) return fullPath.Replace('\\', '/');
        try
        {
            var rel = Path.GetRelativePath(baseDir, fullPath);
            // A rooted result means there's no shared root (different drive/UNC host) -> can't be relative.
            // Otherwise use it relative, even when it climbs out with ".." - that's the normal SETS->GENRES case.
            return (Path.IsPathRooted(rel) ? fullPath : rel).Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    /// <summary>Read a playlist and return the file paths it references - resolved to absolute
    /// (relative entries against the playlist's own folder), de-duplicated, order preserved, and
    /// filtered to files that actually exist. Directive / comment lines (#...) are skipped. Never
    /// throws on a malformed entry - it's just skipped.</summary>
    public static List<string> ReadPaths(string playlistPath)
    {
        var result = new List<string>();
        foreach (var path in ResolvePaths(playlistPath))
            if (File.Exists(path)) result.Add(path);
        return result;
    }

    /// <summary>
    /// Every path the playlist REFERENCES - resolved to absolute (relative entries against the
    /// playlist's own folder), de-duplicated, order preserved - WITHOUT checking whether the file
    /// is still on disk. Directive / comment lines (#...) are skipped; a malformed path is skipped.
    ///
    /// <para>The existence filter is deliberately NOT applied here so the caller can choose its
    /// policy. <see cref="ReadPaths"/> drops what is gone (right for "open this set"); a batch job
    /// that must never leave a song behind - <c>analyze --playlist</c> - keeps the gone ones and
    /// REPORTS them by name. Both read the file through this one parser.</para>
    ///
    /// <para>An already-rooted line is kept VERBATIM, which is what preserves a UNC path
    /// (<c>\\host\share\...</c>) exactly as the playlist spelled it.</para>
    /// </summary>
    public static List<string> ResolvePaths(string playlistPath)
    {
        var result = new List<string>();
        if (!File.Exists(playlistPath)) return result;

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(playlistPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue; // blank line or #EXTM3U / #EXTINF directive

            string full;
            try
            {
                full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));
            }
            catch
            {
                continue; // malformed path - skip, never break the import
            }

            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }
}
