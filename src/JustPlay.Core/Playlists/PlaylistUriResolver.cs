using System;
using System.IO;

namespace JustPlay.Core.Playlists;

/// <summary>
/// Shared URI/path resolution for the PLS and XSPF readers - M3U doesn't need this, its entries are
/// always bare paths, never URIs (see <see cref="M3uPlaylist.ReadPaths"/>). PLS ("FileN=...") and XSPF
/// ("&lt;location&gt;...&lt;/location&gt;") both carry the SAME three possible shapes for a track
/// reference: a "file://" URI (including UNC "file://host/share/..."), a bare local path (absolute, or
/// relative to the playlist's own folder), or a remote URI ("http://", "https://", ...) - out of scope for
/// a LOCAL library importer, reported as unresolved so the caller skips it. One resolver, so a fix here
/// benefits both formats instead of drifting apart in two copies.
///
/// Platform-agnostic: BCL only (System.Uri + System.IO.Path), no external dependencies.
/// </summary>
internal static class PlaylistUriResolver
{
    /// <summary>Resolve one raw entry to an absolute local path, or null if it's remote or unparsable.
    /// Never throws.</summary>
    public static string? ResolveLocalPath(string raw, string baseDir)
    {
        // Uri.TryCreate(..., UriKind.Absolute, ...) recognizes not just "scheme://..." URIs but also bare
        // Windows drive paths ("C:\...") and UNC paths ("\\host\share\...") as implicit file:// URIs - a
        // long-standing System.Uri compatibility quirk. That conveniently means ONE branch handles
        // "file:///C:/Music/x.mp3", "file://host/share/x.mp3" (Uri.LocalPath turns this into the UNC form
        // "\\host\share\x.mp3" - this is the documented example in the LocalPath API docs), AND an
        // already-absolute plain local path, all via uri.LocalPath.
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return uri.IsFile ? uri.LocalPath : null; // non-file scheme (http/https/...) - remote, skip
        }

        // Not a recognized absolute URI - treat as a plain path, relative to the playlist's own folder
        // unless it's already rooted. Mirrors M3uPlaylist.ReadPaths exactly (no GetFullPath normalization
        // on an already-rooted entry - kept identical on purpose so behaviour matches across formats).
        try
        {
            return Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(baseDir, raw));
        }
        catch
        {
            return null; // malformed path - skip, never break the import
        }
    }
}
