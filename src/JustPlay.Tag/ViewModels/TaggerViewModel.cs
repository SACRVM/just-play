using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playlists;
using JustPlay.Library;
using JustPlay.Metadata;
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
/// One row in the file pane — a <see cref="TrackViewModel"/> plus the two things a TAGGER needs that a
/// player does not: where it sits in the listing, and whether its tags have been read yet.
///
/// <para>The row itself is the SHARED one (<c>JustPlay.UI.Controls.TrackRow</c>): the same cells, widths,
/// key pill and sort behaviour as the JUST PLAY queue and the PRE CUE FINDER, with JUST TAG's own columns
/// switched on. What differs between the three apps is which columns are enabled — not what a row is.</para>
/// </summary>
public sealed class FileRow
{
    public FileRow(string name, string path)
    {
        Name = name;
        Path = path;
        Track = new TrackViewModel(new Track(path));
    }

    public string Name { get; }
    public string Path { get; }

    /// <summary>The shared row view model — every visible cell reads off this.</summary>
    public TrackViewModel Track { get; }

    /// <summary>Position in the folder's own order. Restores it when sorting is switched back off.</summary>
    public int Order { get; set; }

    /// <summary>What the file says, once it has been read. Null until then.</summary>
    public TrackMetadata? Meta
    {
        get => Track.Model.Metadata;
        set => Track.Model.Metadata = value;
    }

    /// <summary>Whether the tag read has happened for this row.</summary>
    public bool Tagged { get; set; }

    private int _claimed;

    /// <summary>
    /// One-shot claim, so each file is read exactly ONCE no matter who reaches it first — the viewport
    /// (a row scrolling into view) or the background pass working through the folder. Without it the
    /// two race and a row on screen can be read twice over SMB while a row further down waits.
    /// </summary>
    public bool TryClaimHydration() => System.Threading.Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>The ID3v2 major version at the head of the file ("2.3"), or null for a file that
    /// carries none. Read from four bytes during the tag pass — no parsing, no TagLib.</summary>
    public string? Id3
    {
        get => Track.Id3Version;
        set => Track.Id3Version = value;
    }

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
        // Via the ROW, not the metadata: an index-filled row has metadata with no CoverArt (the index
        // stores tags, not pictures), so asking the metadata would report "no cover" for nearly every
        // file. TrackViewModel.HasCover prefers the probed answer when there is one.
        TagField.Cover       => Track.HasCover ? "yes" : null,
        TagField.Id3Version  => Id3,
        TagField.FileType    => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } x ? x : null,
        _                    => null,
    };

    /// <summary>The general search: name, title, artist and genre at once.</summary>
    public bool MatchesAnywhere(string needle) =>
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
/// What the right-hand pane is showing. EDITOR and ANALYSIS are the two halves of the shared
/// <c>TagEditorPanel</c> — what the file tells other tools (editable) versus what we measured
/// (read-only) — and FILTER is the search. One row of tabs, three entries.
/// </summary>
public enum TagPane { Editor, Analysis, Filter }

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

    private TagPane _pane = TagPane.Editor;

    /// <summary>
    /// Which tab the right pane shows. Three, not two: the shared editor panel used to carry its own
    /// TAGS | ANALYSIS switch, which put a second row of tabs directly under this header. The switch
    /// belongs to the HOST, so all three live on one line — EDITOR | ANALYSIS | FILTER (Chloe
    /// 2026-08-05). Same shape as the Finder's INFO | FILTER, one entry wider.
    /// </summary>
    public TagPane Pane
    {
        get => _pane;
        private set
        {
            Set(ref _pane, value);
            foreach (var n in (string[])[nameof(ShowEditor), nameof(ShowAnalysis), nameof(ShowFilter),
                                         nameof(ShowPanel)])
                Raise(n);
        }
    }

    public bool ShowEditor   => _pane == TagPane.Editor;
    public bool ShowAnalysis => _pane == TagPane.Analysis;
    public bool ShowFilter   => _pane == TagPane.Filter;

    /// <summary>EDITOR and ANALYSIS are two halves of the SAME panel, so the panel is visible for
    /// both and only FILTER replaces it.</summary>
    public bool ShowPanel => _pane != TagPane.Filter;

    public void ShowTab(TagPane pane)
    {
        if (pane == TagPane.Filter && !CanFilter) return;   // still reading — the tab says so
        Pane = pane;
    }

    public TaggerViewModel(TagEditorViewModel editor, PreviewViewModel preview,
                           IMetadataReader reader, TagSettingsService settings,
                           StartupTarget startup)
    {
        Editor = editor;
        Preview = preview;
        _reader = reader;
        _settings = settings;

        // The SHARED column state (JustPlay.UI) — the same object the JUST PLAY queue and the Finder
        // use, with JUST TAG's own set switched on. Null means "never chosen", which is what makes a
        // first run land on the tagging default; an empty array is a choice and is kept.
        Columns = new TrackColumns(settings.Current.Columns ?? DefaultColumns)
        {
            SortColumn = settings.Current.SortColumn,
            SortDescending = settings.Current.SortDescending,
        };
        Columns.SortRequested += () =>
        {
            _settings.Current.SortColumn = Columns.SortColumn;
            _settings.Current.SortDescending = Columns.SortDescending;
            _settings.Save();
            ApplyFilter();
        };
        Columns.VisibilityChanged += () =>
        {
            _settings.Current.Columns = [.. Columns.Enabled];
            _settings.Save();
            // Switching one of the file-fact columns ON is the moment it becomes worth a file open.
            if (NeedsId3) EnsureId3();
            if (NeedsCover) EnsureCover();
        };

        // Where to land. Anything that has since vanished (NAS not mounted, stick pulled) must fall
        // through to the next candidate and finally to the empty state — never to an error at startup.
        //   1. what we were launched ON (a folder or a file's folder) — this is what the planned
        //      Explorer right-click will hand us, and it beats any memory: you asked for THIS folder.
        //   2. where you were last.
        //   3. the machine's library root. If nothing has ever told JUST TAG where to go, the music
        //      is the obvious place, and the library layer already knows where that is — the same
        //      registry that decides whether a folder is indexed (Chloe 2026-08-05).
        StartFile = startup.SelectFile;
        var start = FirstExisting(startup.Folder, settings.Current.LastFolder, FirstLibraryRoot());
        if (start is not null) Open(start);
    }

    /// <summary>A file we were launched on — the window selects it once the listing is up.</summary>
    public string? StartFile { get; }

    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));

    private static string? FirstLibraryRoot()
    {
        try { return LibraryIndexRegistry.Roots().FirstOrDefault(Directory.Exists); }
        catch (Exception) { return null; }   // no registry, unreadable, share gone — just no default
    }

    /// <summary>
    /// What a TAGGER wants to see on a first run. Lean on purpose — the pane is the middle third of
    /// the window and every column costs the file name room. AN (the analysis traffic light) because
    /// checking and re-triggering our own analysis is what this app is FOR; ART because a wrong cover
    /// is invisible in text; GENRE because it is the field that is most often wrong; BPM/KEY/NRG
    /// because they are what the suite measured. Everything else is one right-click away, remembered.
    /// </summary>
    private static readonly string[] DefaultColumns =
    [
        TrackColumns.Analysis, TrackColumns.Cover, TrackColumns.Genre,
        TrackColumns.Bpm, TrackColumns.Key, TrackColumns.Nrg,
    ];

    /// <summary>Column visibility + sort state, shared with the row and the header strip.</summary>
    public TrackColumns Columns { get; }

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
    public FieldChoice? Field1
    {
        get => _field;
        set
        {
            Set(ref _field, value);
            // ART has nothing to type against, so picking it lands on a mode that decides something
            // instead of on a text mode with an invisible box and no effect.
            if (value?.Value == TagField.Cover && TagSearch.NeedsValue(_mode?.Value)) _mode = HasArt;
            foreach (var n in (string[])[nameof(Hint1), nameof(NeedsValue1), nameof(Mode1)]) Raise(n);
            if (NeedsId3) EnsureId3();   // aiming at these is the other reason to pay for a file open
            if (NeedsCover) EnsureCover();
            ApplyFilter();
        }
    }

    /// <summary>The "is not empty" entry, reused when a field is switched to one that takes no text.</summary>
    private ModeChoice HasArt => Modes.First(m => m.Value == MatchMode.IsNotEmpty);

    /// <summary>What the search box suggests — it follows the FIELD, because "title, artist, genre or
    /// file name…" under a box that is about to compare ID3 versions is not a hint, it is a wrong
    /// statement. Chloe 2026-08-05.</summary>
    public string Hint1 => HintFor(_field);

    public string Hint2 => HintFor(_field2);

    /// <summary>
    /// A concrete EXAMPLE per field, not a restatement of the label — the label already says "Genre",
    /// so a placeholder reading "genre…" adds nothing. Showing <c>hard techno</c> / <c>8A</c> /
    /// <c>2.3</c> / <c>FLAC</c> teaches the shape of the value in the one place you are about to type it.
    /// </summary>
    private static string HintFor(FieldChoice? field) => (field?.Value ?? TagField.All) switch
    {
        TagField.All         => "title, artist, genre or file name…",
        TagField.FileName    => "part of the file name…",
        TagField.Title       => "part of the title…",
        TagField.Artist      => "artist name…",
        TagField.Album       => "album name…",
        TagField.AlbumArtist => "album artist…",
        TagField.Genre       => "hard techno",
        TagField.Comment     => "text in the comment…",
        TagField.Year        => "2024",
        TagField.Track       => "7",
        TagField.Bpm         => "150",
        TagField.Key         => "8A",
        TagField.Energy      => "7",
        // A cover is there or it is not, so the useful modes here are "is empty" / "is not empty" —
        // and the placeholder says so instead of pretending there is text to match.
        TagField.Cover       => "",
        TagField.Id3Version  => "2.3",
        TagField.FileType    => "FLAC",
        _                    => "search…",
    };

    private ModeChoice? _mode;
    public ModeChoice? Mode1
    {
        get => _mode;
        set { Set(ref _mode, value); Raise(nameof(NeedsValue1)); ApplyFilter(); }
    }

    private string? _value1;
    public string? Value1 { get => _value1; set { Set(ref _value1, value); Raise(nameof(HasValue1)); ApplyFilter(); } }

    public bool HasValue1 => !string.IsNullOrEmpty(_value1);

    /// <summary>"is empty" / "is not empty" take no text — and neither does ART, which is present or
    /// absent. The box goes away rather than sit there looking ignorable.</summary>
    public bool NeedsValue1 => TagSearch.NeedsValue(_field?.Value ?? TagField.All, _mode?.Value);

    // Condition 2 — off until asked for.
    private bool _second;
    public bool HasSecond { get => _second; private set { Set(ref _second, value); ApplyFilter(); } }

    public void ToggleSecond() => HasSecond = !HasSecond;

    private bool _joinAnd = true;
    /// <summary>How the two conditions join. AND narrows, OR widens.</summary>
    // The OR radio binds {Binding !JoinAnd}. VERIFIED against Avalonia release/12.0.3 —
    // src/Avalonia.Base/Data/Core/ExpressionNodes/LogicalNotNode.cs implements WriteValueToSource and
    // negates on the way back, so the negated binding is genuinely two-way. (Checked 2026-08-05 while
    // hunting an "inverted filter": this was a suspect and it is innocent.)
    public bool JoinAnd { get => _joinAnd; set { Set(ref _joinAnd, value); ApplyFilter(); } }

    private FieldChoice? _field2;
    public FieldChoice? Field2
    {
        get => _field2;
        set
        {
            Set(ref _field2, value);
            if (value?.Value == TagField.Cover && TagSearch.NeedsValue(_mode2?.Value)) _mode2 = HasArt;
            foreach (var n in (string[])[nameof(Hint2), nameof(NeedsValue2), nameof(Mode2)]) Raise(n);
            if (NeedsId3) EnsureId3();
            if (NeedsCover) EnsureCover();
            ApplyFilter();
        }
    }

    private ModeChoice? _mode2;
    public ModeChoice? Mode2
    {
        get => _mode2;
        set { Set(ref _mode2, value); Raise(nameof(NeedsValue2)); ApplyFilter(); }
    }

    private string? _value2;
    public string? Value2 { get => _value2; set { Set(ref _value2, value); ApplyFilter(); } }

    public bool NeedsValue2 => TagSearch.NeedsValue(_field2?.Value ?? TagField.All, _mode2?.Value);

    private bool Filtering =>
        TagSearch.IsActive(_field?.Value ?? TagField.All, _mode?.Value, _value1)
        || (HasSecond && TagSearch.IsActive(_field2?.Value ?? TagField.All, _mode2?.Value, _value2));

    /// <summary>Everything back to "show the folder".</summary>
    public void ClearSearch()
    {
        _value1 = _value2 = null;
        _mode = _mode2 = null;
        _field2 = null;
        _second = false;
        foreach (var n in (string[])[nameof(Value1), nameof(Value2), nameof(Mode1), nameof(Mode2),
                                     nameof(Field2), nameof(HasSecond), nameof(NeedsValue1),
                                     nameof(NeedsValue2), nameof(HasValue1), nameof(Hint2)])
            Raise(n);
        ApplyFilter();
    }

    // The deciding is TagSearch's — pure, and pinned by tests. This class only supplies the picker
    // state (a null picker means its default: "All fields" / "contains").
    private bool Matches(FileRow row) =>
        TagSearch.Matches(row,
                          _field?.Value ?? TagField.All, _mode?.Value, _value1,
                          HasSecond, JoinAnd,
                          _field2?.Value ?? TagField.All, _mode2?.Value, _value2);

    private void ApplyFilter()
    {
        var filtering = Filtering;
        var shown = filtering ? _all.Where(Matches).ToList() : [.. _all];
        Sort(shown);

        // Only touch the collection when the result actually changed. The tag pass finishes with one
        // more ApplyFilter, and a Clear()+refill there would drop the ListBox selection — i.e. blank
        // the editor out from under whatever you had already started typing into.
        if (!shown.SequenceEqual(Files))
        {
            Files.Clear();
            foreach (var f in shown) Files.Add(f);
        }

        FileCount = filtering ? $"{shown.Count} of {_all.Count}" : _countText;
    }

    /// <summary>Apply the active column sort, or put the folder's own order back. Sorting is LOCKED
    /// until every row has been read (<see cref="AllHydrated"/>) — a sort over half-empty cells would
    /// silently be a sort over the half we happened to have.</summary>
    private void Sort(List<FileRow> list)
    {
        if (list.Count < 2) return;

        if (!AllHydrated || Columns.SortColumn is null)
        {
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return;
        }

        var d = Columns.SortDescending;
        var col = Columns.SortColumn;
        // The SHARED comparator — "sort by genre" means the same thing here, in the queue and in the
        // Finder, because it is literally the same code.
        list.Sort((a, b) => { var c = TrackSort.Compare(a.Track, b.Track, col); return d ? -c : c; });
    }

    // ── Reading the tags ────────────────────────────────────────────────────────────────────────
    //
    // Eagerly, in the background, as soon as a listing lands — NOT on demand when a search asks.
    // The moment the table can show BPM / KEY / ART / ID3, "read it later" means "show blank cells
    // and hope nobody looks". Same mechanic as the Finder: a bounded parallel pass, each row pushed
    // to the UI as it lands, sorting unlocked when the last one is in.

    private CancellationTokenSource? _tagCts;
    private int _hydrated;      // UI thread only
    private int _hydrateTotal;

    private void StartTagPass()
    {
        _tagCts?.Cancel();
        var cts = _tagCts = new CancellationTokenSource();
        var ct = cts.Token;
        var rows = _all;

        _hydrated = 0;
        _hydrateTotal = rows.Count;
        AllHydrated = rows.Count == 0;
        Searching = rows.Count > 0;
        RaiseProgress();
        if (rows.Count == 0) { Indexed = false; IndexNote = null; return; }

        Task.Run(() =>
        {
            try
            {
                // ── The library index, when this folder is inside one ────────────────────────────
                // Measured on her library: an index hit is ~0 ms per row against ~57 ms for a tag
                // read over SMB. A folder OUTSIDE every indexed root simply gets none of this and is
                // read from disk — that is the normal case for this app, not a fallback.
                var known = LookUpInIndex(rows, ct);
                var fromDisk = rows.Count - known;

                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    Indexed = known > 0;
                    IndexNote = known == 0 ? null
                        : fromDisk == 0
                            ? $"{known} from the library index"
                            : $"{known} from the library index · {fromDisk} read from disk";
                });

                if (fromDisk == 0) return;

                // Tag reads are I/O-bound (NAS latency), and TagLibMetadataReader is stateless, so a
                // handful of workers is both safe and the whole win on a network share. Rows the index
                // already filled are CLAIMED and skipped here — and a row that scrolls into view is
                // read by HydrateVisible first, so what you are looking at fills before the tail of a
                // 1,200-file folder does (Chloe 2026-08-05: "und nicht erst das sichtbare?").
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    row => Hydrate(row, ct));
            }
            catch (OperationCanceledException)
            {
                // Navigated away — a newer listing owns the pane.
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// A row just scrolled INTO view — read it now, ahead of the background pass. The one-shot claim
    /// means whoever gets there first wins and the file is never read twice.
    /// </summary>
    public void HydrateVisible(FileRow row)
    {
        if (row.Tagged) return;
        var ct = _tagCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested) return;
        _ = Task.Run(() => Hydrate(row, ct), CancellationToken.None);
    }

    /// <summary>
    /// Fill what the machine's index already knows, and report how many rows that was. Rows it does not
    /// cover stay untouched (<c>Tagged == false</c>) and go through the file read afterwards — an index
    /// that is one scan behind must never make a track invisible or blank.
    /// </summary>
    private int LookUpInIndex(List<FileRow> rows, CancellationToken ct)
    {
        // A folder resolves to its own root. A SET's tracks can in principle live under different
        // roots; we resolve by the first track and let the rest fall through to the file read, which
        // is correct — only slower — rather than juggling several databases for one list.
        var folder = Playlist is null ? Folder : System.IO.Path.GetDirectoryName(rows[0].Path);

        using var db = LibraryIndexRegistry.OpenFor(folder);
        if (db is null) return 0;

        var found = 0;
        try
        {
            var hits = db.LookupMany(rows.Select(r => r.Path).ToList());
            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) return found;
                if (!hits.TryGetValue(row.Path, out var entry)) continue;
                if (!row.TryClaimHydration()) continue;   // the viewport already took it

                row.Meta = TrackIndexMapping.ToMetadata(entry);
                if (entry.Success)
                {
                    row.Track.Model.Analysis = TrackIndexMapping.ToAnalysisResult(entry);
                    row.Track.Model.AnalysisStatus = AnalysisStatus.Done;
                    // The traffic light without opening a single file: the index stores the detector
                    // version it was analysed with, and that is the same number as the blob's.
                    row.Track.IndexedAnalysisVersion = entry.DetectionVersion;
                }

                // ⛔ The ID3 version is deliberately NOT read here. It is four bytes off the file head
                // — but reading it means OPENING EVERY FILE, which on a network share costs the entire
                // point of the index (the tags came back in ~0 ms and then we would queue 1,200 SMB
                // opens behind them). It is fetched only when it is actually wanted: see EnsureId3.

                row.Tagged = true;
                found++;
            }
        }
        catch (Exception)
        {
            // A locked or half-written index costs speed, never correctness: everything it did not
            // fill is still marked un-read and goes through the file pass below.
            return found;
        }

        // Rows the index filled still have to reach the UI — in ONE post, not one per row: this is the
        // fast path and a thousand separate dispatcher callbacks would be its own stall.
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            foreach (var row in rows)
                if (row.Tagged) row.Track.Refresh();

            _hydrated += found;
            RaiseProgress();
            if (_hydrated >= _hydrateTotal) Finish();
            if (NeedsId3) EnsureId3();
            if (NeedsCover) EnsureCover();
        });

        return found;
    }

    private bool _indexed;
    /// <summary>This listing came (at least partly) from the machine's library index.</summary>
    public bool Indexed { get => _indexed; private set { Set(ref _indexed, value); Raise(nameof(HasIndexNote)); } }

    private string? _indexNote;
    /// <summary>Where the rows came from, in words — so "why was this folder instant and that one
    /// slow" is answered on screen rather than guessed at.</summary>
    public string? IndexNote { get => _indexNote; private set { Set(ref _indexNote, value); Raise(nameof(HasIndexNote)); } }

    /// <summary>Shown only once the read is DONE — while it runs, the progress line occupies that spot
    /// and two texts in one cell would overlap.</summary>
    public bool HasIndexNote => !string.IsNullOrEmpty(_indexNote) && AllHydrated;

    /// <summary>Read ONE row off the UI thread and push it to its cells. Claimed once, so the viewport
    /// and the background pass never read the same file twice.</summary>
    private void Hydrate(FileRow row, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (!row.TryClaimHydration()) return;

        try
        {
            row.Meta = _reader.Read(row.Path);
            if (row.Meta?.StoredAnalysis is { } stored)
            {
                // Trust the blob as-is, any version — re-analysing stays an explicit action, and it is
                // not this app's action at all. Same rule as the Finder.
                row.Track.Model.Analysis = stored.Detected;
                row.Track.Model.AnalysedAtUtc = stored.AnalysedAtUtc;
                row.Track.Model.AnalysisStatus = AnalysisStatus.Done;
            }

            // Four bytes off the head of the file — that is the whole ID3 version check, and it is
            // what makes "find everything still on 2.2" a search rather than a guess. Null for
            // anything that carries no ID3v2 tag (FLAC, MP4, a bare MP3). Free here, because this row
            // is being opened anyway; on the INDEX path it is not free and is deferred — see EnsureId3.
            if (NeedsId3) row.Id3 = Id3VersionProbe.Read(row.Path) is { } major ? $"2.{major}" : null;

            // The file itself is the authority on artwork, and we just read it — so record the answer.
            // A null Artwork therefore always means "nobody has looked yet", never "no cover".
            row.Track.Artwork = row.Meta?.CoverArt is { Length: > 0 };
        }
        catch (Exception)
        {
            // An unreadable file still shows, and still matches on its NAME — it just cannot match
            // on tags. Dropping the row would be losing a song silently.
        }
        row.Tagged = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            row.Track.Refresh();

            // All these posts serialize on the UI thread, so a plain ++ is safe here.
            _hydrated++;
            RaiseProgress();
            if (_hydrated >= _hydrateTotal) Finish();
        });
    }

    private void Finish()
    {
        AllHydrated = true;
        Searching = false;
        ApplyFilter();   // a search typed while loading now sees every row, and sort unlocks
    }

    // ── The ID3 version: fetched only when it is actually wanted ─────────────────────────────────

    /// <summary>Is anything asking for the ID3 version right now — the column, or a search condition?</summary>
    private bool NeedsId3 => Wanted(TagField.Id3Version, Columns.ShowId3);

    /// <summary>Is anything asking about the cover right now — the COV column, or a search condition?</summary>
    private bool NeedsCover => Wanted(TagField.Cover, Columns.ShowCover);

    /// <summary>A field costs a FILE OPEN even when the row came from the index, so it is fetched only
    /// when the column shows it or a search aims at it.</summary>
    private bool Wanted(TagField field, bool columnVisible) =>
        columnVisible
        || _field?.Value == field
        || (HasSecond && _field2?.Value == field);

    private CancellationTokenSource? _id3Cts;
    private CancellationTokenSource? _coverCts;

    /// <summary>
    /// Fill "has a cover" for rows that came from the INDEX, which stores tags and not pictures. Same
    /// on-demand rule as the ID3 version: it costs a file open, so it happens when the COV column is
    /// switched on or a search aims at it — not for every folder on the off-chance.
    /// </summary>
    private void EnsureCover()
    {
        var rows = _all.Where(r => r.Tagged && r.Track.Artwork is null).ToList();
        if (rows.Count == 0) return;

        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var ct = cts.Token;

        Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    row =>
                    {
                        var has = CoverProbe.Has(row.Path);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            row.Track.Artwork = has;
                            row.Track.Refresh();
                        });
                    });
            }
            catch (OperationCanceledException) { /* newer listing, or the column went away again */ }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Fill the ID3 version for rows that do not have it yet. Separate from the tag pass because it is
    /// the one field that costs a FILE OPEN even when everything else came from the index — 1,200 SMB
    /// opens for a column nobody switched on is exactly the stall Chloe hit (2026-08-05). Runs when the
    /// column is turned on, or when a search starts aiming at it.
    /// </summary>
    private void EnsureId3()
    {
        var rows = _all.Where(r => r.Tagged && r.Id3 is null).ToList();
        if (rows.Count == 0) return;

        _id3Cts?.Cancel();
        var cts = _id3Cts = new CancellationTokenSource();
        var ct = cts.Token;

        Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    row =>
                    {
                        var v = Id3VersionProbe.Read(row.Path) is { } major ? $"2.{major}" : null;
                        if (v is null) return;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            row.Id3 = v;
                            row.Track.Refresh();
                        });
                    });
            }
            catch (OperationCanceledException) { /* newer listing, or the column went away again */ }
        }, CancellationToken.None);
    }

    // ── Progress ────────────────────────────────────────────────────────────────────────────────

    /// <summary>How far the read has got, in words. Null once everything is in — a progress line that
    /// stays put after it finishes is furniture.</summary>
    public string? LoadingText =>
        AllHydrated || _hydrateTotal == 0 ? null : $"reading tags… {_hydrated} of {_hydrateTotal}";

    /// <summary>Sorting and searching both need EVERY row, so both wait — and say so rather than
    /// quietly answering from the half that happens to be loaded (Chloe 2026-08-05).</summary>
    public bool CanFilter => AllHydrated;

    private void RaiseProgress()
    {
        // Not once per row: a 1,200-file folder would post 1,200 notifications at a number nobody can
        // read that fast. Every 25 rows, plus the last one.
        if (_hydrated % 25 != 0 && _hydrated < _hydrateTotal) return;
        Raise(nameof(LoadingText));
    }

    private bool _allHydrated = true;
    /// <summary>Every row in the listing has been read. Sorting waits for it; the header says so.</summary>
    public bool AllHydrated
    {
        get => _allHydrated;
        private set
        {
            Set(ref _allHydrated, value);
            foreach (var n in (string[])[nameof(LockedOpacity), nameof(LockedTip),
                                         nameof(LoadingText), nameof(CanFilter), nameof(HasIndexNote)])
                Raise(n);
            // Searching while half the folder is unread would answer from the half that happens to be
            // in. If the FILTER tab is open when a new folder starts loading, step back to the editor.
            if (!value && _pane == TagPane.Filter) Pane = TagPane.Editor;
        }
    }

    /// <summary>The header dims while sorting is locked, so "nothing happens on click" is visible
    /// before the click rather than after it.</summary>
    public double LockedOpacity => AllHydrated ? 1.0 : 0.45;

    /// <summary>Why sorting and searching are not available yet — shown on the header strip AND on the
    /// FILTER tab, because both are locked for the same reason: they need every row.</summary>
    public string? LockedTip => AllHydrated
        ? null
        : "Still reading the folder — sorting and searching need every file.";

    private bool _searching;
    /// <summary>A tag pass is running — the pane says so instead of looking stuck.</summary>
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
                                 .Select((f, i) => new FileRow(Path.GetFileName(f) ?? f, f) { Order = i })];

            _countText = _all.Count switch
            {
                0 => "no audio files",
                1 => "1 file",
                var n => $"{n} files",
            };
            StartTagPass();
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
            StartTagPass();
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

            _all = [.. tracks.Select((t, i) => new FileRow(Path.GetFileName(t) ?? t, t) { Order = i })];
            _countText = missing == 0
                ? $"{tracks.Count} in this set"
                : $"{tracks.Count} in this set · {missing} missing";
            StartTagPass();
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
            StartTagPass();
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
