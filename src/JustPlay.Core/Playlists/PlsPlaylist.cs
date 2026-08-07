using System;
using System.Collections.Generic;
using System.IO;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Minimal reader for the INI-style PLS playlist format (Winamp/SHOUTcast heritage, still emitted by some
/// station directories and older DJ tools). Entries are INDEXED lines - "File1=", "Title1=", "Length1=" ...
/// up to "NumberOfEntries=" - so, unlike M3U, the order the lines appear ON DISK doesn't matter; the
/// numeric suffix does. Sibling metadata (TitleN/LengthN/NumberOfEntries/Version/the "[playlist]" header)
/// is ignored - we only care about the FileN paths, mirroring what <see cref="M3uPlaylist"/> does for M3U.
///
/// Platform-agnostic: pure text + path logic, no external dependencies (BCL only, no Regex).
/// </summary>
public static class PlsPlaylist
{
    public static readonly string[] Extensions = { ".pls" };

    /// <summary>True if the path looks like a PLS playlist (by extension).</summary>
    public static bool IsPlaylist(string? path) =>
        path is not null && Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read a PLS playlist and return the local file paths it references, in FileN order (1, 2,
    /// 3... - NOT necessarily the order the lines appear in the file). Relative entries resolve against the
    /// playlist's own folder; "file://" URIs (including UNC "file://host/share/...") become local paths;
    /// "http(s)://" and other remote schemes are skipped (this is a LOCAL library importer). Result is
    /// resolved to absolute, de-duplicated, and filtered to files that actually exist - same contract as
    /// <see cref="M3uPlaylist.ReadPaths"/>. Never throws on a malformed entry - it's just skipped.</summary>
    public static List<string> ReadPaths(string playlistPath)
    {
        var result = new List<string>();
        if (!File.Exists(playlistPath)) return result;

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? string.Empty;
        var byIndex = new SortedDictionary<int, string>();

        foreach (var raw in File.ReadAllLines(playlistPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '[' || line[0] == ';') continue; // blank, section header, ini comment

            var eq = line.IndexOf('=');
            if (eq <= 0) continue; // no "key=value" shape - skip

            var key = line[..eq].Trim();
            if (!TryParseFileIndex(key, out var n)) continue; // TitleN/LengthN/NumberOfEntries/Version/... - ignored

            byIndex[n] = line[(eq + 1)..].Trim(); // last one wins if FileN repeats - matches map-assignment intuition
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in byIndex)
        {
            var value = pair.Value;
            if (value.Length == 0) continue;

            var full = PlaylistUriResolver.ResolveLocalPath(value, baseDir);
            if (full is null) continue; // remote (http/https/...) or malformed - skip

            if (File.Exists(full) && seen.Add(full)) result.Add(full);
        }
        return result;
    }

    /// <summary>Matches an ini key of the shape "File&lt;N&gt;" (case-insensitive) - e.g. "File1",
    /// "FILE12". Everything else ("Title1", "Length1", "NumberOfEntries", "Version") is left alone.</summary>
    private static bool TryParseFileIndex(string key, out int n)
    {
        n = 0;
        const string prefix = "File";
        if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(key.AsSpan(prefix.Length), out n) && n > 0;
    }
}
