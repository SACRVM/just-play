using System;
using System.Collections.Generic;
using System.Linq;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Single entry point for reading ANY playlist format JustPlay understands — M3U/M3U8, PLS, XSPF — so
/// callers (the finder's playlist listing/loading, the main-window open/drag-drop routing, a future
/// format, …) never need to know which parser backs which extension. Dispatches by extension to
/// <see cref="M3uPlaylist"/>, <see cref="PlsPlaylist"/>, or <see cref="XspfPlaylist"/>; each keeps its own
/// format-specific parsing, but all three share the same contract: absolute local paths, order preserved,
/// de-duplicated, filtered to files that exist, never throws on a malformed entry.
///
/// (Write/export stays M3U-only for now — <see cref="M3uPlaylist.Write"/> — PLS/XSPF are import-only here;
/// every DJ tool in this repo's target list reads M3U/M3U8 for interchange, see M3uPlaylist's own remarks.)
/// </summary>
public static class Playlist
{
    /// <summary>Every extension recognized by any of the backing parsers.</summary>
    public static readonly string[] Extensions =
        M3uPlaylist.Extensions.Concat(PlsPlaylist.Extensions).Concat(XspfPlaylist.Extensions).ToArray();

    /// <summary>True if the path looks like a playlist of ANY recognized format (by extension).</summary>
    public static bool IsPlaylist(string? path) =>
        path is not null && Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read a playlist of any recognized format and return the local file paths it references —
    /// dispatches by extension to the matching parser. An unrecognized extension returns an empty list
    /// (never throws).</summary>
    public static List<string> ReadPaths(string playlistPath)
    {
        if (PlsPlaylist.IsPlaylist(playlistPath)) return PlsPlaylist.ReadPaths(playlistPath);
        if (XspfPlaylist.IsPlaylist(playlistPath)) return XspfPlaylist.ReadPaths(playlistPath);
        if (M3uPlaylist.IsPlaylist(playlistPath)) return M3uPlaylist.ReadPaths(playlistPath);
        return new List<string>();
    }
}
