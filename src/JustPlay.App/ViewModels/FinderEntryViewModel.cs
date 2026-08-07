using System.IO;
using JustPlay.UI.Controls;

namespace JustPlay.App.ViewModels;

/// <summary>What a left-pane row IS: a hop up, a real sub-folder, or a playlist treated as a
/// virtual folder (N26 P2 - Norton-Commander flat navigation, Chloe 2026-07-05).</summary>
public enum FinderEntryKind { Up, Folder, Playlist }

/// <summary>
/// One row in the PRE CUE FINDER's LEFT pane - a flat, Norton-Commander-style listing of the
/// current folder's navigable children (no expand chevrons, no nesting): a ".." hop, each
/// sub-folder, and each playlist in the folder. Playlists are "virtual folders" - entering one
/// loads its tracks into the file pane exactly as entering a folder loads that folder's files.
/// Immutable + tiny; the list is rebuilt wholesale on every navigation.
/// </summary>
public sealed class FinderEntryViewModel
{
    private FinderEntryViewModel(FinderEntryKind kind, string name, string fullPath)
    {
        Kind = kind;
        Name = name;
        FullPath = fullPath;
    }

    /// <summary>The ".." row - <paramref name="parentPath"/> is the folder it hops to.</summary>
    public static FinderEntryViewModel Up(string parentPath) =>
        new(FinderEntryKind.Up, "..", parentPath);

    public static FinderEntryViewModel Folder(string fullPath) =>
        new(FinderEntryKind.Folder, DisplayName(fullPath), fullPath);

    /// <summary>A playlist file (.m3u/.m3u8 today), shown as a virtual folder - the extension is
    /// dropped so it reads like a folder; the list glyph is what marks it as a playlist.</summary>
    public static FinderEntryViewModel Playlist(string fullPath) =>
        new(FinderEntryKind.Playlist, Path.GetFileNameWithoutExtension(fullPath), fullPath);

    public FinderEntryKind Kind { get; }
    public string Name { get; }

    /// <summary>Folder path (Up/Folder) or playlist file path (Playlist).</summary>
    public string FullPath { get; }

    public bool IsUp => Kind == FinderEntryKind.Up;
    public bool IsPlaylist => Kind == FinderEntryKind.Playlist;

    /// <summary>Norton-Commander row icon: ".." and real folders carry the folder icon; a playlist
    /// gets the list icon (Chloe 2026-07-05: "ordner symbol + ein listen symbol fuer playlist").
    /// (!!) A SHIPPED VECTOR (controls:JustIcon), never a character - see CLAUDE.md rule 5. These two
    /// used to be the emoji "folder" / "list", and the identical source line rendered yellow in JUST TAG and
    /// white here, because the OS picks the font, not us.</summary>
    public IconKind Icon => Kind == FinderEntryKind.Playlist ? IconKind.Playlist : IconKind.Folder;

    private static string DisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
        return name.Length == 0 ? fullPath : name; // drive/share root - show it verbatim
    }
}

/// <summary>One clickable segment of the breadcrumb path in the chrome bar. Clicking a folder
/// segment jumps straight to that level (Chloe 2026-07-05: "jedes segment anklickbar um direkt
/// mehrere ebenen hoch"). The playlist tail segment isn't navigable (FolderPath = null).</summary>
public sealed record BreadcrumbSegment(string Name, string? FolderPath, bool IsPlaylist, bool IsLast);
