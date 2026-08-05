using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playlists;
using JustPlay.Library;
using JustPlay.Tag.Settings;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.ViewModels;

/// <summary>One row in the folder pane — "..", a sub-folder, or a PLAYLIST as a virtual folder.</summary>
public sealed record FolderRow(string Name, string Path, bool IsUp, bool IsPlaylist = false)
{
    /// <summary>The Finder's glyphs, verbatim: folders (and "..") carry the folder icon, a playlist
    /// the list icon (Chloe 2026-07-05: "ordner symbol + ein listen symbol für playlist").</summary>
    public string Glyph => IsPlaylist ? "☰" : "📁";
}

/// <summary>
/// One row in the file pane. The NAME is what the listing shows; <see cref="Artist"/>,
/// <see cref="Title"/> and <see cref="Genre"/> are filled only when a FILTER needs them, because
/// reading every file's tags up front would make opening a folder of 800 tracks a visible wait for
/// information nobody asked for yet.
/// </summary>
public sealed class FileRow(string name, string path)
{
    public string Name { get; } = name;
    public string Path { get; } = path;

    /// <summary>What the file says, once it has been read. Null until then.</summary>
    public TrackMetadata? Meta { get; set; }

    /// <summary>Whether the tag read has happened for this row.</summary>
    public bool Tagged { get; set; }

    /// <summary>The ID3v2 major version at the head of the file ("2.3"), or null for a file that
    /// carries none. Read from four bytes during the tag pass — no parsing, no TagLib.</summary>
    public string? Id3 { get; set; }

    /// <summary>
    /// One searchable field as TEXT. Everything reduces to a string on purpose: it keeps ONE set of
    /// comparisons — and "is empty" then means the same thing whether the field is a genre, a BPM,
    /// a cover or an ID3 version. An absent value is null, never "0" or "unknown", or "is empty"
    /// would quietly stop finding the very files it exists for.
    /// </summary>
    public string? Field(TagField f) => f switch
    {
        TagField.FileName    => Name,
        TagField.Title       => Meta?.Title,
        TagField.Artist      => Meta?.Artist,
        TagField.Album       => Meta?.Album,
        TagField.AlbumArtist => Meta?.AlbumArtist,
        TagField.Genre       => Meta?.Genre,
        TagField.Comment     => Meta?.Comment,
        TagField.Year        => Meta?.Year is > 0 ? Meta.Year.Value.ToString(CultureInfo.InvariantCulture) : null,
        TagField.Track       => Meta?.TrackNumber is > 0 ? Meta.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : null,
        TagField.Bpm         => Meta?.TaggedBpm is > 0 ? Meta.TaggedBpm.Value.ToString("0.##", CultureInfo.InvariantCulture) : null,
        TagField.Key         => Meta?.TaggedKey,
        TagField.Energy      => Meta?.TaggedEnergy is > 0 ? Meta.TaggedEnergy.Value.ToString(CultureInfo.InvariantCulture) : null,
        TagField.Cover       => Meta?.CoverArt is { Length: > 0 } ? "yes" : null,
        TagField.Id3Version  => Id3,
        TagField.FileType    => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } x ? x : null,
        _                    => null,
    };

    /// <summary>The general search: name, title, artist and genre at once.</summary>
    public bool Matches(string needle) =>
        Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Has(TagField.Title, needle) || Has(TagField.Artist, needle) || Has(TagField.Genre, needle);

    private bool Has(TagField f, string needle) =>
        Field(f)?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>
/// What the expert search can aim at — every editable tag, plus the file facts you cannot see by
/// looking at a name: whether there is a cover at all, and which ID3 version the file carries.
/// </summary>
public enum TagField
{
    /// <summary>The default: name, title, artist and genre at once — the "just type something" case.</summary>
    All,
    FileName, Title, Artist, Album, AlbumArtist, Genre, Comment, Year, Track,
    Bpm, Key, Energy, Cover, Id3Version, FileType,
}

/// <summary>
/// How the expert search compares. <see cref="IsEmpty"/> is the one that finds the damage, and
/// <see cref="IsNot"/> is what makes "everything that is NOT 2.4" expressible without a query
/// language.
/// </summary>
public enum MatchMode { Contains, NotContains, Is, IsNot, StartsWith, IsEmpty, IsNotEmpty }

// Labelled entries for the two pickers. Concrete records rather than one generic Choice&lt;T&gt;:
// XAML's x:DataType cannot name a generic, and an enum name in a dropdown is not copy.
public sealed record FieldChoice(string Label, TagField Value);
public sealed record ModeChoice(string Label, MatchMode Value);

/// <summary>One clickable segment of the path in the chrome bar.</summary>
public sealed record Crumb(string Name, string Path, bool IsLast);

/// <summary>
/// JUST TAG's browser: a folder on the left, its audio files in the middle, and the SHARED
/// <see cref="TagEditorViewModel"/> docked on the right.
///
/// <para><b>It browses the DISK, not the library index.</b> This is the tool you reach for when a
/// download just landed somewhere that no index has ever seen — mp3tag's model, and the reason the
/// app exists next to the PRE CUE FINDER rather than inside it.</para>
///
/// <para>What counts as an audio file comes from <see cref="AudioFiles"/>, the same enumeration JUST
/// PLAY, the Finder and the CLI use. If JUST TAG grew its own extension list, two front-ends would
/// disagree about the size of the same folder.</para>
/// </summary>
public sealed class TaggerViewModel : INotifyPropertyChanged
{
    private readonly TagSettingsService _settings;
    private readonly IMetadataReader _reader;

    private bool _showFilter;
    /// <summary>
    /// Which tab the right pane shows: the EDITOR, or the FILTER. Same shape as the Finder's
    /// INFO | FILTER — the third pane is where you look things up, and searching is looking up.
    /// </summary>
    public bool ShowFilter { get => _showFilter; private set { Set(ref _showFilter, value); Raise(nameof(ShowEditor)); } }

    public bool ShowEditor => !_showFilter;

    public void ShowTab(bool filter) => ShowFilter = filter;

    public TaggerViewModel(TagEditorViewModel editor, PreviewViewModel preview,
                           IMetadataReader reader, TagSettingsService settings)
    {
        Editor = editor;
        Preview = preview;
        _reader = reader;
        _settings = settings;

        // A remembered folder that has since vanished (NAS not mounted, stick pulled) must land on
        // the empty state, never on an error dialog at startup.
        var last = settings.Current.LastFolder;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) Open(last);
    }

    /// <summary>The shared editor — the sidebar is <c>TagEditorPanel</c> bound to exactly this.</summary>
    public TagEditorViewModel Editor { get; }

    /// <summary>Listen to what you are tagging. Releases the file by itself when a save needs it.</summary>
    public PreviewViewModel Preview { get; }

    public ObservableCollection<FolderRow> Folders { get; } = [];

    /// <summary>What the file pane SHOWS — the search applied to <see cref="_all"/>.</summary>
    public ObservableCollection<FileRow> Files { get; } = [];

    /// <summary>Everything the current folder or set holds, before the search narrows it.</summary>
    private List<FileRow> _all = [];

    private string _countText = "";

    private bool _includeSubfolders;
    /// <summary>Search below this folder as well. Re-reads the listing, because the set of files
    /// itself changes — not just what is shown.</summary>
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value) return;
            Set(ref _includeSubfolders, value);
            if (Playlist is null && Folder is { } f) Open(f);
        }
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────────
    //
    // ONE search, not two. The field picker STARTS at "All fields", so the simple case is the
    // default and needs no decision; picking a single field is how "genre contains hard" or, the
    // one that actually finds the damage, "genre is empty" gets asked. A second condition can be
    // switched on and joined with AND / OR when that is not enough.
    //
    // Chloe 2026-08-05: "think simple and allow complex things … doppelt suchen verwirrt nur".

    public IReadOnlyList<FieldChoice> Fields { get; } =
    [
        new("All fields", TagField.All),
        new("Genre", TagField.Genre),
        new("Artist", TagField.Artist),
        new("Title", TagField.Title),
        new("Album", TagField.Album),
        new("Album artist", TagField.AlbumArtist),
        new("Comment", TagField.Comment),
        new("Year", TagField.Year),
        new("Track #", TagField.Track),
        new("BPM", TagField.Bpm),
        new("Key", TagField.Key),
        new("Energy", TagField.Energy),
        new("Cover", TagField.Cover),
        new("ID3 version", TagField.Id3Version),
        new("File type", TagField.FileType),
        new("File name", TagField.FileName),
    ];

    public IReadOnlyList<ModeChoice> Modes { get; } =
    [
        new("contains", MatchMode.Contains),
        new("does not contain", MatchMode.NotContains),
        new("is", MatchMode.Is),
        new("is not", MatchMode.IsNot),
        new("starts with", MatchMode.StartsWith),
        new("is empty", MatchMode.IsEmpty),
        new("is not empty", MatchMode.IsNotEmpty),
    ];

    // Condition 1 — always there, and it starts as the plain "type something" search.
    private FieldChoice? _field;
    public FieldChoice? Field1 { get => _field; set { Set(ref _field, value); ApplyFilter(); } }

    private ModeChoice? _mode;
    public ModeChoice? Mode1
    {
        get => _mode;
        set { Set(ref _mode, value); Raise(nameof(NeedsValue1)); ApplyFilter(); }
    }

    private string? _value1;
    public string? Value1 { get => _value1; set { Set(ref _value1, value); Raise(nameof(HasValue1)); ApplyFilter(); } }

    public bool HasValue1 => !string.IsNullOrEmpty(_value1);

    /// <summary>"is empty" / "is not empty" take no text — the box goes away rather than sit there
    /// looking ignorable.</summary>
    public bool NeedsValue1 => NeedsValue(_mode);

    // Condition 2 — off until asked for.
    private bool _second;
    public bool HasSecond { get => _second; private set { Set(ref _second, value); ApplyFilter(); } }

    public void ToggleSecond() => HasSecond = !HasSecond;

    private bool _joinAnd = true;
    /// <summary>How the two conditions join. AND narrows, OR widens.</summary>
    public bool JoinAnd { get => _joinAnd; set { Set(ref _joinAnd, value); ApplyFilter(); } }

    private FieldChoice? _field2;
    public FieldChoice? Field2 { get => _field2; set { Set(ref _field2, value); ApplyFilter(); } }

    private ModeChoice? _mode2;
    public ModeChoice? Mode2
    {
        get => _mode2;
        set { Set(ref _mode2, value); Raise(nameof(NeedsValue2)); ApplyFilter(); }
    }

    private string? _value2;
    public string? Value2 { get => _value2; set { Set(ref _value2, value); ApplyFilter(); } }

    public bool NeedsValue2 => NeedsValue(_mode2);

    private static bool NeedsValue(ModeChoice? m) =>
        m is null || m.Value is not (MatchMode.IsEmpty or MatchMode.IsNotEmpty);

    /// <summary>A condition only counts once it can actually decide something.</summary>
    private static bool Active(FieldChoice? f, ModeChoice? m, string? v) =>
        (f is not null || !string.IsNullOrWhiteSpace(v))
        && (!NeedsValue(m) || !string.IsNullOrWhiteSpace(v));

    private bool Filtering => Active(_field, _mode, _value1)
                              || (HasSecond && Active(_field2, _mode2, _value2));

    /// <summary>Everything back to "show the folder".</summary>
    public void ClearSearch()
    {
        _value1 = _value2 = null;
        _mode = _mode2 = null;
        _field2 = null;
        _second = false;
        foreach (var n in (string[])[nameof(Value1), nameof(Value2), nameof(Mode1), nameof(Mode2),
                                     nameof(Field2), nameof(HasSecond), nameof(NeedsValue1),
                                     nameof(NeedsValue2), nameof(HasValue1)])
            Raise(n);
        ApplyFilter();
    }

    private bool Matches(FileRow row)
    {
        var first = Active(_field, _mode, _value1) ? Matches(row, _field, _mode, _value1) : true;
        if (!HasSecond || !Active(_field2, _mode2, _value2)) return first;

        var second = Matches(row, _field2, _mode2, _value2);
        return JoinAnd ? first && second : first || second;
    }

    private static bool Matches(FileRow row, FieldChoice? field, ModeChoice? mode, string? raw)
    {
        var value = raw?.Trim() ?? "";

        // "All fields" with a plain word is the simple case, and it stays simple: it looks in name,
        // title, artist and genre at once. Any other MODE needs one field to compare against, so
        // "all fields" falls back to the same broad contains.
        if (field is null || field.Value == TagField.All)
            return mode?.Value switch
            {
                null or MatchMode.Contains => row.Matches(value),
                MatchMode.NotContains      => !row.Matches(value),
                _                          => row.Matches(value),
            };

        var text = row.Field(field.Value);

        return (mode?.Value ?? MatchMode.Contains) switch
        {
            MatchMode.IsEmpty     => string.IsNullOrWhiteSpace(text),
            MatchMode.IsNotEmpty  => !string.IsNullOrWhiteSpace(text),
            MatchMode.Contains    => text?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false,
            // An EMPTY field counts as "does not contain" and as "is not" — otherwise
            // "ID3 version is not 2.4" would hide exactly the files that have no tag at all.
            MatchMode.NotContains => !(text?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false),
            MatchMode.Is          => string.Equals(text?.Trim(), value, StringComparison.OrdinalIgnoreCase),
            MatchMode.IsNot       => !string.Equals(text?.Trim(), value, StringComparison.OrdinalIgnoreCase),
            MatchMode.StartsWith  => text?.TrimStart().StartsWith(value, StringComparison.OrdinalIgnoreCase) ?? false,
            _                     => true,
        };
    }

    private void ApplyFilter()
    {
        var filtering = Filtering;

        // Anything beyond the file name needs the tags, and we only pay for that when a search is
        // actually running — once per listing, in the background.
        if (filtering && _all.Exists(f => !f.Tagged))
        {
            LoadTagsThenFilter();
            return;
        }

        Files.Clear();

        var shown = filtering ? _all.Where(Matches).ToList() : _all;
        foreach (var f in shown) Files.Add(f);

        FileCount = filtering ? $"{shown.Count} of {_all.Count}" : _countText;
    }

    /// <summary>
    /// Read the tags of the current listing off the UI thread, then filter. A stale run is dropped
    /// (the generation check): typing four letters must not queue four passes over the folder.
    /// </summary>
    private void LoadTagsThenFilter()
    {
        var rows = _all;
        var generation = ++_tagGeneration;
        Searching = true;

        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (generation != _tagGeneration) return;
                if (row.Tagged) continue;
                try
                {
                    row.Meta = _reader.Read(row.Path);
                    // Four bytes off the head of the file — that is the whole ID3 version check,
                    // and it is what makes "find everything still on 2.2" a search rather than a
                    // guess. Null for anything that carries no ID3v2 tag (FLAC, MP4, a bare MP3).
                    row.Id3 = Id3VersionProbe.Read(row.Path) is { } major ? $"2.{major}" : null;
                }
                catch (Exception)
                {
                    // An unreadable file still matches on its NAME — it just cannot match on tags.
                }
                row.Tagged = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _tagGeneration) return;
                Searching = false;
                ApplyFilter();
            });
        });
    }

    private int _tagGeneration;

    private bool _searching;
    /// <summary>A tag pass is running for the search — the pane says so instead of looking stuck.</summary>
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }

    private IReadOnlyList<Crumb> _crumbs = [];
    public IReadOnlyList<Crumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }

    private string? _folder;
    /// <summary>The folder being shown, or null before one has been picked.</summary>
    public string? Folder
    {
        get => _folder;
        private set { Set(ref _folder, value); Raise(nameof(HasFolder)); }
    }

    public bool HasFolder => _folder is not null;

    private string _fileCount = "";
    public string FileCount { get => _fileCount; private set => Set(ref _fileCount, value); }

    private bool _foldersActive;
    private bool _filesActive = true;

    /// <summary>
    /// Which pane the cursor belongs to. The Finder's dual-pane cue, kept identical here: the hot
    /// pane lights its header and draws a far brighter selected row, so "where am I" is answered by
    /// colour rather than by remembering what was clicked last.
    /// </summary>
    public bool FoldersActive { get => _foldersActive; private set => Set(ref _foldersActive, value); }

    public bool FilesActive { get => _filesActive; private set => Set(ref _filesActive, value); }

    /// <summary>Hand the cursor to one pane — they are never both hot.</summary>
    public void Activate(bool folders)
    {
        FoldersActive = folders;
        FilesActive = !folders;
    }

    private string? _problem;
    /// <summary>Why a folder could not be listed. Shown in place of the file list rather than thrown:
    /// a permission-denied folder is a normal thing to click on, not a crash.</summary>
    public string? Problem { get => _problem; private set { Set(ref _problem, value); Raise(nameof(HasProblem)); } }

    public bool HasProblem => _problem is not null;

    /// <summary>
    /// Show a folder: its sub-folders on the left, its audio files in the middle. Not recursive —
    /// one folder is one screen, and descending is a click. (Batch across a tree is the workbench
    /// step, and it is a different feature with a different confirmation.)
    /// </summary>
    public void Open(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
            {
                Problem = "That folder is not there any more.";
                return;
            }

            Folder = full;
            Problem = null;

            Folders.Clear();
            var parent = Directory.GetParent(full)?.FullName;
            if (parent is not null) Folders.Add(new FolderRow("..", parent, IsUp: true));
            foreach (var dir in Directory.EnumerateDirectories(full)
                                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Folders.Add(new FolderRow(Path.GetFileName(dir) ?? dir, dir, IsUp: false));

            // Playlists are VIRTUAL FOLDERS, exactly as in the Finder — a set is a place you go to
            // tag its tracks, and her sets live in one folder away from the music. Dropping them
            // would mean "tag a set" needs a detour through the file system.
            foreach (var pl in Directory.EnumerateFiles(full)
                                        .Where(M3uPlaylist.IsPlaylist)
                                        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Folders.Add(new FolderRow(Path.GetFileNameWithoutExtension(pl) ?? pl, pl,
                                          IsUp: false, IsPlaylist: true));

            Playlist = null;

            // EnumerateWithKeys is the form that takes `recursive` — Enumerate() always walks the
            // whole tree, which is not what a folder listing means.
            _all = [.. AudioFiles.EnumerateWithKeys(full, recursive: IncludeSubfolders)
                                 .Select(s => s.Path)
                                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                 .Select(f => new FileRow(Path.GetFileName(f) ?? f, f))];

            _countText = _all.Count switch
            {
                0 => "no audio files",
                1 => "1 file",
                var n => $"{n} files",
            };
            ApplyFilter();

            Crumbs = BuildCrumbs(full);

            _settings.Current.LastFolder = full;
            _settings.Save();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Listing a folder we may not read is an ordinary click, not a failure of the app.
            Problem = $"Can't read this folder — {ex.Message}";
            _all = [];
            _countText = "";
            ApplyFilter();
        }
    }

    /// <summary>
    /// Show a PLAYLIST's tracks in the file pane. The folder pane stays where it is — a set is a
    /// view onto files that live elsewhere, not a place you descend into.
    ///
    /// <para>The order is the playlist's and is NOT re-sorted: the sequence is the work. Entries
    /// that no longer resolve are dropped by <see cref="M3uPlaylist.ReadPaths"/>, and the count says
    /// so, because silently showing 40 of 60 tracks would be the worst of both.</para>
    /// </summary>
    public void OpenPlaylist(string playlistPath)
    {
        try
        {
            var tracks = M3uPlaylist.ReadPaths(playlistPath).Where(AudioFiles.IsAudio).ToList();

            // ReadPaths drops what no longer resolves. Counting the entries the file DECLARES is
            // what turns that into a visible number — showing 40 of 60 tracks and saying "40" is
            // exactly the silent loss we do not do (memory never-leave-songs-behind).
            var declared = File.ReadLines(playlistPath)
                               .Count(l => l.Length > 0 && !l.TrimStart().StartsWith('#'));
            var missing = Math.Max(0, declared - tracks.Count);

            Playlist = Path.GetFileNameWithoutExtension(playlistPath);
            Problem = null;

            _all = [.. tracks.Select(t => new FileRow(Path.GetFileName(t) ?? t, t))];
            _countText = missing == 0
                ? $"{tracks.Count} in this set"
                : $"{tracks.Count} in this set · {missing} missing";
            ApplyFilter();
            Crumbs = [.. BuildCrumbs(Path.GetDirectoryName(playlistPath) ?? playlistPath)
                            .Select(c => c with { IsLast = false }),
                      new Crumb(Playlist ?? "set", playlistPath, IsLast: true)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Problem = $"Can't read this playlist — {ex.Message}";
            _all = [];
            _countText = "";
            ApplyFilter();
        }
    }

    private string? _playlist;
    /// <summary>The set whose tracks the file pane is showing, or null when it is showing a folder.</summary>
    public string? Playlist
    {
        get => _playlist;
        private set { Set(ref _playlist, value); Raise(nameof(CanIncludeSubfolders)); }
    }

    /// <summary>A set already names its tracks wherever they live, so "below this folder" has
    /// nothing to mean there — the checkbox greys out rather than lying.</summary>
    public bool CanIncludeSubfolders => _playlist is null;

    /// <summary>Read the current folder again — after a rename, or when something changed on disk
    /// behind our back.</summary>
    public void Refresh()
    {
        if (Playlist is not null && Crumbs.Count > 0 && Crumbs[^1].Path is { Length: > 0 } pl)
            OpenPlaylist(pl);
        else if (Folder is { } f) Open(f);
    }

    /// <summary>Path segments, each one a jump target. The root keeps its separator ("D:\") so it
    /// reads as a drive rather than a letter.</summary>
    private static IReadOnlyList<Crumb> BuildCrumbs(string full)
    {
        var parts = new List<Crumb>();
        var dir = new DirectoryInfo(full);

        while (dir is not null)
        {
            var name = dir.Parent is null ? dir.FullName : dir.Name;
            parts.Add(new Crumb(name, dir.FullName, IsLast: false));
            dir = dir.Parent;
        }

        parts.Reverse();
        return parts.Count == 0
            ? parts
            : [.. parts.Take(parts.Count - 1), parts[^1] with { IsLast = true }];
    }

    // ── INPC ────────────────────────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }
}
