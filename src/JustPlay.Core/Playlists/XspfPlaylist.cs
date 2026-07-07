using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Minimal reader for XSPF ("XML Shareable Playlist Format", exported by foobar2000, VLC, and others) —
/// a `&lt;playlist&gt;/&lt;trackList&gt;/&lt;track&gt;/&lt;location&gt;` XML document. Elements are matched
/// by LOCAL NAME only, ignoring the namespace: real-world XSPF declares xmlns="http://xspf.org/ns/0/", but
/// some exporters omit it or vary it, and a namespace mismatch must never silently drop a whole set.
///
/// Platform-agnostic: BCL <see cref="XDocument"/> only (no serializer, no reflection) — trim/AOT-safe.
/// </summary>
public static class XspfPlaylist
{
    public static readonly string[] Extensions = { ".xspf" };

    /// <summary>True if the path looks like an XSPF playlist (by extension).</summary>
    public static bool IsPlaylist(string? path) =>
        path is not null && Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read an XSPF playlist and return the local file paths its tracks reference, in document
    /// order — one path per &lt;track&gt;, taken from its FIRST &lt;location&gt;. Relative locations
    /// resolve against the playlist's own folder; "file://" URIs (including UNC "file://host/share/…")
    /// become local paths; "http(s)://" and other remote schemes are skipped (this is a LOCAL library
    /// importer). Result is resolved to absolute, de-duplicated, and filtered to files that actually
    /// exist — same contract as <see cref="M3uPlaylist.ReadPaths"/>. Never throws — malformed XML or a
    /// malformed entry is just skipped.</summary>
    public static List<string> ReadPaths(string playlistPath)
    {
        var result = new List<string>();
        if (!File.Exists(playlistPath)) return result;

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? string.Empty;

        XDocument doc;
        try
        {
            doc = XDocument.Load(playlistPath);
        }
        catch
        {
            return result; // not well-formed XML — never throw, just report nothing
        }

        if (doc.Root is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in doc.Root.Descendants().Where(e => e.Name.LocalName == "track"))
        {
            var location = track.Elements().FirstOrDefault(e => e.Name.LocalName == "location");
            if (location is null) continue; // track with no <location> — nothing to resolve

            var raw = (location.Value ?? string.Empty).Trim();
            if (raw.Length == 0) continue;

            var full = PlaylistUriResolver.ResolveLocalPath(raw, baseDir);
            if (full is null) continue; // remote (http/https/…) or malformed — skip

            if (File.Exists(full) && seen.Add(full)) result.Add(full);
        }
        return result;
    }
}
