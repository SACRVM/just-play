using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Minimal reader/writer for the classic M3U / M3U8 playlist format — the universal interchange
/// format every player and DJ tool (Traktor, rekordbox, Serato, VirtualDJ, Engine…) can import.
/// We write UTF-8 (.m3u8) and carry the running order; the track analysis (BPM/key/energy) rides
/// along in the files' OWN tags, which those tools read on import — so we hand over a set as a
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

    /// <summary>Write an extended M3U8 (UTF-8, no BOM) listing the entries in order. Paths are written
    /// portably: a track that lives UNDER the destination folder is written relative (so saving the
    /// .m3u8 into your music folder makes the whole set copy-anywhere on a USB stick), while tracks
    /// elsewhere — other folders or drives — stay absolute so they always resolve. ReadPaths resolves
    /// relatives against the playlist's own folder, so this round-trips.</summary>
    public static void Write(string destPath, IEnumerable<Entry> entries)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(destPath)) ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        foreach (var e in entries)
        {
            var secs = e.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : -1;
            var title = (e.Title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            sb.Append("#EXTINF:").Append(secs).Append(',').Append(title).Append('\n');
            sb.Append(ToPortablePath(e.Path, baseDir)).Append('\n');
        }
        File.WriteAllText(destPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Relative path if <paramref name="fullPath"/> sits under <paramref name="baseDir"/>
    /// (same drive, no climbing out with "..") — otherwise the absolute path. Never throws.</summary>
    private static string ToPortablePath(string fullPath, string baseDir)
    {
        if (baseDir.Length == 0) return fullPath;
        try
        {
            var rel = Path.GetRelativePath(baseDir, fullPath);
            return !Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal)
                ? rel
                : fullPath;
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>Read a playlist and return the file paths it references — resolved to absolute
    /// (relative entries against the playlist's own folder), de-duplicated, order preserved, and
    /// filtered to files that actually exist. Directive / comment lines (#…) are skipped. Never
    /// throws on a malformed entry — it's just skipped.</summary>
    public static List<string> ReadPaths(string playlistPath)
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
                continue; // malformed path — skip, never break the import
            }

            if (File.Exists(full) && seen.Add(full)) result.Add(full);
        }
        return result;
    }
}
