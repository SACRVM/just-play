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
using JustPlay.UI.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// One row in the folder pane - "..", a sub-folder, or a PLAYLIST as a virtual folder.
///
/// <para>A folder with nothing to browse into (no sub-folders, no playlists of its own) does NOT
/// descend when activated: it shows its tracks in the file pane and leaves the folder pane where it
/// is, exactly like a playlist (Chloe 2026-08-06: "ordner ohne subfolder so behandeln wie
/// playlisten"). Descending would leave you staring at a pane containing nothing but "..". That is
/// decided when the row is ACTIVATED, not stored here - see MainWindow.ActivateFolder.</para>
/// </summary>
public sealed record FolderRow(string Name, string Path, bool IsUp, bool IsPlaylist = false)
{
    /// <summary>The Finder's row icon, verbatim - same shared control, same size, same kind mapping;
    /// see FinderEntryViewModel.Icon. (!!) Never draw a local variant here: the PRE CUE FINDER is the
    /// reference for this pane, and an icon only JUST TAG has is a divergence, not an improvement.</summary>
    public IconKind Icon => IsPlaylist ? IconKind.Playlist : IconKind.Folder;
}

/// <summary>
/// One row in the file pane - a <see cref="TrackViewModel"/> plus the two things a TAGGER needs that a
/// player does not: where it sits in the listing, and whether its tags have been read yet.
///
/// <para>The row itself is the SHARED one (<c>JustPlay.UI.Controls.TrackRow</c>): the same cells, widths,
/// key pill and sort behaviour as the JUST PLAY queue and the PRE CUE FINDER, with JUST TAG's own columns
/// switched on. What differs between the three apps is which columns are enabled - not what a row is.</para>
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

    /// <summary>The shared row view model - every visible cell reads off this.</summary>
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
    /// One-shot claim, so each file is read exactly ONCE no matter who reaches it first - the viewport
    /// (a row scrolling into view) or the background pass working through the folder. Without it the
    /// two race and a row on screen can be read twice over SMB while a row further down waits.
    /// </summary>
    public bool TryClaimHydration() => System.Threading.Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>The ID3v2 major version at the head of the file ("2.3"), or null for a file that
    /// carries none. Read from four bytes during the tag pass - no parsing, no TagLib.</summary>
    public string? Id3
    {
        get => Track.Id3Version;
        set => Track.Id3Version = value;
    }

    /// <summary>
    /// One searchable field as TEXT. Everything reduces to a string on purpose: it keeps ONE set of
    /// comparisons - and "is empty" then means the same thing whether the field is a genre, a BPM,
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
/// What the expert search can aim at - every editable tag, plus the file facts you cannot see by
/// looking at a name: whether there is a cover at all, and which ID3 version the file carries.
/// </summary>
public enum TagField
{
    /// <summary>The default: name, title, artist and genre at once - the "just type something" case.</summary>
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
/// <c>TagEditorPanel</c> - what the file tells other tools (editable) versus what we measured
/// (read-only) - and FILTER is the search. One row of tabs, three entries.
/// </summary>
public enum TagPane { Editor, Analysis, Filter }

/// <summary>
/// JUST TAG's browser: a folder on the left, its audio files in the middle, and the SHARED
/// <see cref="TagEditorViewModel"/> docked on the right.
///
/// <para><b>It browses the DISK, not the library index.</b> This is the tool you reach for when a
/// download just landed somewhere that no index has ever seen - mp3tag's model, and the reason the
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
    /// belongs to the HOST, so all three live on one line - EDITOR | ANALYSIS | FILTER (Chloe
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
        if (pane == TagPane.Filter && !CanFilter) return;   // still reading - the tab says so
        if (pane == TagPane.Analysis && !CanShowAnalysisTab) return;
        Pane = pane;
    }

    /// <summary>ANALYSIS shows ONE file's measurements. Across a selection there is no such thing, so
    /// the tab is not offered rather than offered and empty.</summary>
    public bool CanShowAnalysisTab => Editor.CanShowAnalysis;

    /// <summary>
    /// Call after the editor's target changes. If ANALYSIS just stopped being available and you were
    /// standing on it, the pane falls back to EDITOR - a tab that vanishes under you must leave you
    /// somewhere, not on a blank half.
    /// </summary>
    public void SyncAnalysisTab()
    {
        Raise(nameof(CanShowAnalysisTab));
        if (!CanShowAnalysisTab && _pane == TagPane.Analysis) Pane = TagPane.Editor;
    }

    public TaggerViewModel(TagEditorViewModel editor, PreviewViewModel preview,
                           IMetadataReader reader, TagSettingsService settings,
                           StartupTarget startup)
    {
        Editor = editor;
        Preview = preview;
        _reader = reader;
        _settings = settings;

        // The SHARED column state (JustPlay.UI) - the same object the JUST PLAY queue and the Finder
        // use, with JUST TAG's own set switched on. Null means "never chosen", which is what makes a
        // first run land on the tagging default; an empty array is a choice and is kept.
        Columns = new TrackColumns(settings.Current.Columns ?? DefaultColumns)
        {
            SortColumn = settings.Current.SortColumn,
            SortDescending = settings.Current.SortDescending,
        };
        Columns.SortRequested += () =>
        {
            // A header CLICK is the one thing allowed to re-order a listing that is already on
            // screen - because you asked for it, so it is not a surprise. It also unlocks sorting for
            // a listing that opened unsorted (see ApplyListingSort).
            _listingSorted = Columns.SortColumn is not null;
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

            // A column appearing or disappearing changes who is asking for the row's width, but not
            // what is IN the rows - so re-share, don't re-measure.
            Columns.Widths.Refit(Columns);
        };

        // Where to land. Anything that has since vanished (NAS not mounted, stick pulled) must fall
        // through to the next candidate and finally to the empty state - never to an error at startup.
        //   1. what we were launched ON (a folder or a file's folder) - this is what the planned
        //      Explorer right-click will hand us, and it beats any memory: you asked for THIS folder.
        //   2. where you were last.
        //   3. the machine's library root. If nothing has ever told JUST TAG where to go, the music
        //      is the obvious place, and the library layer already knows where that is - the same
        //      registry that decides whether a folder is indexed (Chloe 2026-08-05).
        StartFile = startup.SelectFile;
        var start = FirstExisting(startup.Folder, settings.Current.LastFolder, FirstLibraryRoot());
        // GoTo, not Open: the remembered folder has to be entered by the SAME rule a click
        // uses, or a leaf that was opened once stays opened for good.
        if (start is not null) GoTo(start);
    }

    /// <summary>A file we were launched on - the window selects it once the listing is up.</summary>
    public string? StartFile { get; }

    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));

    private static string? FirstLibraryRoot()
    {
        try { return LibraryIndexRegistry.Roots().FirstOrDefault(Directory.Exists); }
        catch (Exception) { return null; }   // no registry, unreadable, share gone - just no default
    }

    /// <summary>
    /// What a TAGGER wants to see on a first run. Lean on purpose - the pane is the middle third of
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

    /// <summary>The shared editor - the sidebar is <c>TagEditorPanel</c> bound to exactly this.</summary>
    public TagEditorViewModel Editor { get; }

    /// <summary>Listen to what you are tagging. Releases the file by itself when a save needs it.</summary>
    public PreviewViewModel Preview { get; }

    public ObservableCollection<FolderRow> Folders { get; } = [];

    /// <summary>What the file pane SHOWS - the search applied to <see cref="_all"/>. Bulk, because a
    /// library root holds ~15,000 rows and refilling those one Add at a time freezes the window.</summary>
    public BulkObservableCollection<FileRow> Files { get; } = [];

    /// <summary>Everything the current folder or set holds, before the search narrows it.</summary>
    private List<FileRow> _all = [];

    private string _countText = "";

    private bool _includeSubfolders;
    /// <summary>Search below this folder as well. Re-reads the listing, because the set of files
    /// itself changes - not just what is shown.</summary>
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value) return;
            Set(ref _includeSubfolders, value);
            if (ShowingFolder && Folder is { } f) Open(f);
        }
    }

    // -- Search ----------------------------------------------------------------------------------
    //
    // ONE search, not two. The field picker STARTS at "All fields", so the simple case is the
    // default and needs no decision; picking a single field is how "genre contains hard" or, the
    // one that actually finds the damage, "genre is empty" gets asked. A second condition can be
    // switched on and joined with AND / OR when that is not enough.
    //
    // Chloe 2026-08-05: "think simple and allow complex things ... doppelt suchen verwirrt nur".

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

    // Condition 1 - always there, and it starts as the plain "type something" search.
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

    /// <summary>What the search box suggests - it follows the FIELD, because "title, artist, genre or
    /// file name..." under a box that is about to compare ID3 versions is not a hint, it is a wrong
    /// statement. Chloe 2026-08-05.</summary>
    public string Hint1 => HintFor(_field);

    public string Hint2 => HintFor(_field2);

    /// <summary>
    /// A concrete EXAMPLE per field, not a restatement of the label - the label already says "Genre",
    /// so a placeholder reading "genre..." adds nothing. Showing <c>hard techno</c> / <c>8A</c> /
    /// <c>2.3</c> / <c>FLAC</c> teaches the shape of the value in the one place you are about to type it.
    /// </summary>
    private static string HintFor(FieldChoice? field) => (field?.Value ?? TagField.All) switch
    {
        TagField.All         => "title, artist, genre or file name...",
        TagField.FileName    => "part of the file name...",
        TagField.Title       => "part of the title...",
        TagField.Artist      => "artist name...",
        TagField.Album       => "album name...",
        TagField.AlbumArtist => "album artist...",
        TagField.Genre       => "hard techno",
        TagField.Comment     => "text in the comment...",
        TagField.Year        => "2024",
        TagField.Track       => "7",
        TagField.Bpm         => "150",
        TagField.Key         => "8A",
        TagField.Energy      => "7",
        // A cover is there or it is not, so the useful modes here are "is empty" / "is not empty" -
        // and the placeholder says so instead of pretending there is text to match.
        TagField.Cover       => "",
        TagField.Id3Version  => "2.3",
        TagField.FileType    => "FLAC",
        _                    => "search...",
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

    /// <summary>"is empty" / "is not empty" take no text - and neither does ART, which is present or
    /// absent. The box goes away rather than sit there looking ignorable.</summary>
    public bool NeedsValue1 => TagSearch.NeedsValue(_field?.Value ?? TagField.All, _mode?.Value);

    // Condition 2 - off until asked for.
    private bool _second;
    public bool HasSecond { get => _second; private set { Set(ref _second, value); ApplyFilter(); } }

    public void ToggleSecond() => HasSecond = !HasSecond;

    private bool _joinAnd = true;
    /// <summary>How the two conditions join. AND narrows, OR widens.</summary>
    // The OR radio binds {Binding !JoinAnd}. VERIFIED against Avalonia release/12.0.3 -
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

    /// <summary>A search is narrowing the list right now - what the FILTER tab reads in the accent,
    /// so the state is visible from the other two tabs and not only from the second number in the
    /// FILES count (Chloe 2026-08-06).</summary>
    public bool IsFiltering => Filtering;

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

    // The deciding is TagSearch's - pure, and pinned by tests. This class only supplies the picker
    // state (a null picker means its default: "All fields" / "contains").
    private bool Matches(FileRow row) =>
        TagSearch.Matches(row,
                          _field?.Value ?? TagField.All, _mode?.Value, _value1,
                          HasSecond, JoinAnd,
                          _field2?.Value ?? TagField.All, _mode2?.Value, _value2);

    private void ApplyFilter()
    {
        var filtering = Filtering;
        // Every path that can change a search condition ends here, so this is the one place the
        // FILTER tab's accent has to be announced from - no call site can forget it.
        Raise(nameof(IsFiltering));
        var shown = filtering ? _all.Where(Matches).ToList() : [.. _all];
        Sort(shown);

        // Only touch the collection when the result actually changed. The tag pass finishes with one
        // more ApplyFilter, and a refill there would drop the ListBox selection - i.e. blank the
        // editor out from under whatever you had already started typing into.
        if (!shown.SequenceEqual(Files)) Files.Reset(shown);

        // Size the text columns to what is ACTUALLY on screen - this list, not the library (Chloe
        // 2026-08-06 "pro Ansicht"; measured 2026-08-07 that a column's P90 moves 50-130 % from one
        // folder to the next). Here rather than in Open(), because a filter changes the visible set
        // just as much as a folder change does, and it is one pass over strings already in memory.
        Columns.Widths.Measure(shown.Select(r => r.Track).ToList());

        FileCount = filtering ? $"{shown.Count} of {_all.Count}" : _countText;
    }

    /// <summary>
    /// Is THIS listing sorted by a column, or is it in its own order? Decided ONCE when the listing
    /// opens and never revisited - see <see cref="ApplyListingSort"/>.
    /// </summary>
    private bool _listingSorted;

    /// <summary>
    /// Decide a freshly opened listing's order, once, BEFORE its rows are shown.
    ///
    /// <para>(!!) NOTHING may re-order a listing after it is on screen. It used to: sorting was locked
    /// until every row had been read, so a folder appeared in disk order and then jumped into your
    /// saved sort a second later, every single time. "Es ergibt null Sinn, dass du wo reingehst und
    /// innerhalb 1 Sekunde aendert sich die Sortierung - sei dir vorher im Klaren darueber oder lass
    /// es" (2026-08-07). So we are clear beforehand: if the data a sort needs is not there at the
    /// moment the listing opens, the listing simply is not sorted, and it stays that way until you
    /// ask for a sort yourself.</para>
    ///
    /// <para>A PLAYLIST is never auto-sorted at all - the sequence IS the work, and last week's "by
    /// BPM" would destroy exactly the thing you built. (OpenPlaylist's own doc has promised this
    /// since it was written; the code did not keep the promise.)</para>
    ///
    /// <para>When we cannot sort, the sort column is CLEARED rather than left pointing at a column
    /// that is being ignored - so the header arrow always describes the list you are looking at. That
    /// clearing costs nothing: only a header CLICK raises SortRequested, so the saved preference in
    /// settings is untouched and comes back with the next folder that can honour it.</para>
    /// </summary>
    private void ApplyListingSort(bool isPlaylist)
    {
        var wanted = isPlaylist ? null : _settings.Current.SortColumn;

        // Ready = the listing is complete (Listing done AND every row hydrated). Sorting on BPM or KEY
        // over rows that have not been read yet would silently be a sort over the half we happened to
        // have - which is why this is a decision point and not a "do it later".
        _listingSorted = wanted is not null && Ready;

        Columns.SortColumn = _listingSorted ? wanted : null;
        Columns.SortDescending = _listingSorted && _settings.Current.SortDescending;
    }

    /// <summary>Apply this listing's sort, or leave it in its own order. Which of the two was settled
    /// when the listing opened (<see cref="ApplyListingSort"/>) and does not change underneath you.</summary>
    private void Sort(List<FileRow> list)
    {
        if (list.Count < 2) return;

        if (!_listingSorted || Columns.SortColumn is null)
        {
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return;
        }

        var d = Columns.SortDescending;
        var col = Columns.SortColumn;
        // The SHARED comparator - "sort by genre" means the same thing here, in the queue and in the
        // Finder, because it is literally the same code.
        list.Sort((a, b) => { var c = TrackSort.Compare(a.Track, b.Track, col); return d ? -c : c; });
    }

    // -- Reading the tags ------------------------------------------------------------------------
    //
    // Eagerly, in the background, as soon as a listing lands - NOT on demand when a search asks.
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

        // A newer listing owns the pane - anything the previous one still had in flight is dropped
        // rather than drained into a list it no longer belongs to.
        DropPending();

        // Rows carried over from the previous listing are already READ - count them as done up front.
        // Without this the pass could never reach its total (Hydrate skips a claimed row silently),
        // so Finish would never run and the header would say "reading tags..." forever.
        _hydrated = rows.Count(r => r.Tagged);
        _hydrateTotal = rows.Count;
        AllHydrated = _hydrated >= _hydrateTotal;
        Searching = !AllHydrated;
        RaiseProgress();

        // Nothing left to read - the whole listing survived the re-read (the "Include subfolders"
        // case in a folder that has none). No pass, no flicker, and the index note stands as it was.
        if (_hydrated >= _hydrateTotal)
        {
            if (rows.Count == 0) { Indexed = false; IndexNote = null; }
            return;
        }

        // How many rows this pass did NOT have to read. Only a pass that reads the WHOLE listing can
        // honestly say where the listing came from.
        var carried = _hydrated;

        StartDrain();

        RunJob(() =>
        {
            try
            {
                // -- The library index, when this folder is inside one ----------------------------
                // Measured on her library: an index hit is ~0 ms per row against ~57 ms for a tag
                // read over SMB. A folder OUTSIDE every indexed root simply gets none of this and is
                // read from disk - that is the normal case for this app, not a fallback.
                var known = LookUpInIndex(rows, ct);
                var toRead = rows.Count - carried;
                var fromDisk = toRead - known;

                Dispatcher.UIThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    // A PARTIAL re-read (some rows survived from the previous listing) knows nothing
                    // about where those rows came from, so it says nothing rather than quoting a
                    // count that covers only the new arrivals.
                    if (carried > 0) return;
                    Indexed = known > 0;
                    IndexNote = known == 0 ? null
                        : fromDisk == 0
                            ? $"{known} from the library index"
                            : $"{known} from the library index - {fromDisk} read from disk";
                });

                if (fromDisk == 0) return;

                // Tag reads are I/O-bound (NAS latency), and TagLibMetadataReader is stateless, so a
                // handful of workers is both safe and the whole win on a network share. Rows the index
                // already filled are CLAIMED and skipped here - and a row that scrolls into view is
                // read by HydrateVisible first, so what you are looking at fills before the tail of a
                // 1,200-file folder does (Chloe 2026-08-05: "und nicht erst das sichtbare?").
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = ct },
                    row => Hydrate(row, ct));
            }
            catch (OperationCanceledException)
            {
                // Navigated away - a newer listing owns the pane.
            }
        });
    }

    /// <summary>
    /// A row just scrolled INTO view - fill it now, ahead of the background pass. The one-shot claim
    /// means whoever gets there first wins and the file is never read twice.
    ///
    /// <para>(!) A row that is already <c>Tagged</c> is NOT necessarily complete. A row filled from the
    /// library INDEX has its tags but knows nothing about artwork (the index stores tags, not
    /// pictures) and nothing about its ID3 version - both cost a file open and are fetched on demand.
    /// Returning early on <c>Tagged</c> alone is what left the COV column empty on an indexed folder
    /// while the editor happily showed the cover for the same file (Chloe 2026-08-06). So the
    /// visible row also tops up whatever the visible COLUMNS are actually asking for. That closes the
    /// hole for good: what you can SEE is filled by the thing that put it on screen, not by a bulk
    /// pass someone has to remember to trigger.</para>
    /// </summary>
    public void HydrateVisible(FileRow row)
    {
        var ct = _tagCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested) return;

        if (!row.Tagged)
        {
            RunJob(() => Hydrate(row, ct));
            return;
        }

        TopUpFileFacts(row, ct);
    }

    /// <summary>
    /// Fetch the file FACTS a single row is still missing - the ones that cost a file open and are
    /// therefore never read speculatively. Silent no-op when nothing is asking for them.
    /// </summary>
    private void TopUpFileFacts(FileRow row, CancellationToken ct)
    {
        var wantCover = NeedsCover && row.Track.Artwork is null;
        var wantId3 = NeedsId3 && row.Id3 is null;
        if (!wantCover && !wantId3) return;

        RunJob(() =>
        {
            bool? cover = wantCover ? CoverProbe.Has(row.Path) : null;
            var id3 = wantId3 ? (Id3VersionProbe.Read(row.Path) is { } major ? $"2.{major}" : null) : null;
            if (ct.IsCancellationRequested) return;

            if (cover is { } c) row.Track.Artwork = c;
            if (wantId3) row.Id3 = id3;
            Touched(row);
        });
    }

    /// <summary>
    /// Fill what the machine's index already knows, and report how many rows that was. Rows it does not
    /// cover stay untouched (<c>Tagged == false</c>) and go through the file read afterwards - an index
    /// that is one scan behind must never make a track invisible or blank.
    /// </summary>
    private int LookUpInIndex(List<FileRow> rows, CancellationToken ct)
    {
        // A folder resolves to its own root. A SET's tracks can in principle live under different
        // roots; we resolve by the first track and let the rest fall through to the file read, which
        // is correct - only slower - rather than juggling several databases for one list.
        var folder = ShowingFolder ? Folder : System.IO.Path.GetDirectoryName(rows[0].Path);

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

                // The COV tick and the ID3 version, straight off the row - no file is opened for
                // either any more (Chloe 2026-08-07: "null live zugriffe, ergo kommt alles in den
                // index rein"). They used to be a SECOND and THIRD open per visible row, which on a
                // share threw away the whole point of the index: the tags came back in ~0 ms and then
                // 1,200 SMB opens queued up behind them.
                //
                // (!) Only from a row the CURRENT extraction shape wrote. An older row has NULL in
                // those columns, and NULL here means "nobody has looked yet", not "no cover" - so it
                // is left alone and TopUpFileFacts fills it live, exactly as before, until the next
                // sync re-reads the file once. Degrading to the old behaviour beats showing a blank
                // tick that claims the file has no artwork.
                if (entry.TagsAreCurrent)
                {
                    row.Track.Artwork = entry.HasCover;
                    row.Id3 = entry.Id3Version;
                }

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

        // Rows the index filled go through the SAME drip as the ones read from disk (see Done): even
        // this fast path is 15,000 rows on a library root, and refreshing them all in one dispatcher
        // callback is a single long block on the UI thread - which is the thing navigation must never
        // wait behind.
        foreach (var row in rows)
            if (row.Tagged) Done(row);

        return found;
    }

    private bool _indexed;
    /// <summary>This listing came (at least partly) from the machine's library index.</summary>
    public bool Indexed { get => _indexed; private set { Set(ref _indexed, value); Raise(nameof(HasIndexNote)); } }

    private string? _indexNote;
    /// <summary>Where the rows came from, in words - so "why was this folder instant and that one
    /// slow" is answered on screen rather than guessed at.</summary>
    public string? IndexNote { get => _indexNote; private set { Set(ref _indexNote, value); Raise(nameof(HasIndexNote)); } }

    /// <summary>Shown only once the read is DONE - while it runs, the progress line occupies that spot
    /// and two texts in one cell would overlap.</summary>
    public bool HasIndexNote => !string.IsNullOrEmpty(_indexNote) && Ready;

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
                // Trust the blob as-is, any version - re-analysing stays an explicit action, and it is
                // not this app's action at all. Same rule as the Finder.
                row.Track.Model.Analysis = stored.Detected;
                row.Track.Model.AnalysedAtUtc = stored.AnalysedAtUtc;
                row.Track.Model.AnalysisStatus = AnalysisStatus.Done;
            }

            // The ID3 version rides along on the metadata read now (TagLibMetadataReader fills it via
            // the shared Id3VersionProbe), so this row costs ONE open instead of two. Null for
            // anything that carries no ID3v2 tag - a FLAC, an MP4, a bare MP3.
            row.Id3 = row.Meta?.Id3Version;

            // The file itself is the authority on artwork, and we just read it - so record the answer.
            // A null Artwork therefore always means "nobody has looked yet", never "no cover".
            row.Track.Artwork = row.Meta?.CoverArt is { Length: > 0 };
        }
        catch (Exception)
        {
            // An unreadable file still shows, and still matches on its NAME - it just cannot match
            // on tags. Dropping the row would be losing a song silently.
        }
        row.Tagged = true;
        Done(row);
    }

    // -- Getting results onto the screen without owning the UI thread -----------------------------
    //
    // (!)(!) NAVIGATION ALWAYS WINS. Everything in this section exists to keep that true (Chloe
    // 2026-08-06: "navigation muss immer gewinnen - der rest als hintergrund prozess ... und das
    // muss auch unterbrechbar sein wenn ich wilde ordner wechsel vornehme").
    //
    // What used to break it: one Dispatcher.Post PER ROW. On a folder of 30 that is invisible; on a
    // library root it is 15,000 callbacks queued ahead of your click, and the window stops answering.
    // A finished row is now just dropped in a bag; a timer drains the bag on the UI thread in bounded
    // chunks at Background priority, so input is always served first and a folder switch cuts in
    // immediately.

    private readonly object _doneLock = new();

    /// <summary>Rows that just gained their TAGS - they count toward the pass.</summary>
    private readonly List<FileRow> _doneRows = [];

    /// <summary>Rows that only need repainting (a cover or ID3 version arrived later). They must NOT
    /// count toward the pass, or "reading tags... 900 of 700" would be the honest report.</summary>
    private readonly List<FileRow> _touchedRows = [];

    private DispatcherTimer? _drain;

    /// <summary>Background jobs still able to produce rows. The drain keeps ticking while any is
    /// running, so a slow SMB probe that lands after a quiet second is not left in the bag.</summary>
    private int _jobs;

    /// <summary>Rows refreshed per tick. Bounded so ONE tick can never become the long block this
    /// whole mechanism exists to avoid.</summary>
    private const int DrainChunk = 400;

    private static readonly TimeSpan DrainTick = TimeSpan.FromMilliseconds(100);

    /// <summary>A row now has its TAGS. Called from worker threads - cheap, and never touches the UI.</summary>
    private void Done(FileRow row)
    {
        lock (_doneLock) _doneRows.Add(row);
    }

    /// <summary>A row's cells changed but the pass is not affected. Worker threads only.</summary>
    private void Touched(FileRow row)
    {
        lock (_doneLock) _touchedRows.Add(row);
    }

    /// <summary>Run <paramref name="work"/> in the background and keep the drain alive for as long as
    /// it can still produce rows. UI thread only (it starts the timer).</summary>
    private void RunJob(Action work)
    {
        Interlocked.Increment(ref _jobs);
        StartDrain();
        _ = Task.Run(() =>
        {
            try { work(); }
            finally { Interlocked.Decrement(ref _jobs); }
        }, CancellationToken.None);
    }

    private void StartDrain()
    {
        _drain ??= new DispatcherTimer(DrainTick, DispatcherPriority.Background, (_, _) => Drain());
        _drain.Start();
    }

    private void Drain()
    {
        List<FileRow>? done = null, touched = null;
        lock (_doneLock)
        {
            if (_doneRows.Count > 0)
            {
                var take = Math.Min(DrainChunk, _doneRows.Count);
                done = _doneRows.GetRange(0, take);
                _doneRows.RemoveRange(0, take);
            }
            var left = DrainChunk - (done?.Count ?? 0);
            if (left > 0 && _touchedRows.Count > 0)
            {
                var take = Math.Min(left, _touchedRows.Count);
                touched = _touchedRows.GetRange(0, take);
                _touchedRows.RemoveRange(0, take);
            }
        }

        if (done is not null) foreach (var row in done) row.Track.Refresh();
        if (touched is not null) foreach (var row in touched) row.Track.Refresh();

        if (done is { Count: > 0 })
        {
            _hydrated += done.Count;   // UI thread only
            RaiseProgress();
            if (_hydrated >= _hydrateTotal && !AllHydrated) Finish();
        }

        // Idle AND nothing can still arrive -> stop. Anything that starts later starts the drain again.
        if (done is null && touched is null && Volatile.Read(ref _jobs) == 0) _drain?.Stop();
    }

    /// <summary>Throw away whatever the previous folder was still holding - a newer listing owns the
    /// pane, and refreshing rows that are no longer in it is work nobody asked for.</summary>
    private void DropPending()
    {
        lock (_doneLock)
        {
            _doneRows.Clear();
            _touchedRows.Clear();
        }
    }

    private void Finish()
    {
        AllHydrated = true;
        Searching = false;

        // Re-runs the FILTER only: a search typed while loading now sees every row. It deliberately
        // does NOT re-sort - the order was settled when the listing opened and may not move under
        // you a second later (see ApplyListingSort). Sorting is merely UNLOCKED now, for a click.
        ApplyFilter();

        // The load is over, so whatever the on-demand columns still do not know is worth going after
        // now. Belt to HydrateVisible's braces: the visible rows are already filled by then, this is
        // for the rest of the folder, so a sort or a search on COV / ID3 has real values under it.
        if (NeedsId3) EnsureId3();
        if (NeedsCover) EnsureCover();
    }

    // -- The ID3 version: fetched only when it is actually wanted ---------------------------------

    /// <summary>Is anything asking for the ID3 version right now - the column, or a search condition?</summary>
    private bool NeedsId3 => Wanted(TagField.Id3Version, Columns.ShowId3);

    /// <summary>Is anything asking about the cover right now - the COV column, or a search condition?</summary>
    private bool NeedsCover => Wanted(TagField.Cover, Columns.ShowCover);

    // -- Workers ---------------------------------------------------------------------------------

    /// <summary>
    /// How many files to read at once - JUST PLAY's "Analysis threads" control, ported (Chloe
    /// 2026-08-07). Reading tags waits on the NETWORK rather than on a core, so this is what decides
    /// whether a 1,200-file folder fills in seconds or in a minute.
    ///
    /// <para>Clamped to 1..<see cref="JustPlay.Library.AnalysisBatchOptions.MaxSupportedConcurrency"/>
    /// - the suite's one ceiling, so a hand-edited settings file cannot ask for 500 threads.</para>
    /// </summary>
    public int Threads
    {
        get => Math.Clamp(_settings.Current.Threads, 1, AnalysisBatchOptions.MaxSupportedConcurrency);
        set
        {
            var clamped = Math.Clamp(value, 1, AnalysisBatchOptions.MaxSupportedConcurrency);
            if (clamped == Threads) return;
            _settings.Current.Threads = clamped;
            _settings.Save();
            Raise(nameof(Threads));
            Raise(nameof(ThreadsText));
        }
    }

    /// <summary>String form, so the settings panel's radio buttons can drive their active state
    /// through the same StringEquals converter JUST PLAY's do.</summary>
    public string ThreadsText => Threads.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Set the worker count from the settings panel (the button's CommandParameter).</summary>
    public void SetThreads(string? value)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            Threads = n;
    }

    /// <summary>Is this field on screen or being searched on? Both fields it gates now come out of the
    /// index for free; this only decides whether the LEGACY live fallback below is worth running for
    /// the rows the index could not answer.</summary>
    private bool Wanted(TagField field, bool columnVisible) =>
        columnVisible
        || _field?.Value == field
        || (HasSecond && _field2?.Value == field);

    private CancellationTokenSource? _id3Cts;
    private CancellationTokenSource? _coverCts;

    /// <summary>
    /// The FALLBACK for "has a cover" - and after 2026-08-07 it should almost never run: the index
    /// stores the answer now (schema v3), so a row from a current index row already has it and a row
    /// read from disk got it from the tag read. What is left for this: folders OUTSIDE every indexed
    /// root, and rows still carrying pre-v3 data until the next sync re-reads them once.
    ///
    /// <para>It is kept rather than deleted because those two cases are real, and a blank tick that
    /// claims "no artwork" would be a lie. It still costs one file open per row, so it stays gated on
    /// the column actually being visible or searched.</para>
    /// </summary>
    private void EnsureCover()
    {
        var rows = _all.Where(r => r.Tagged && r.Track.Artwork is null).ToList();
        if (rows.Count == 0) return;

        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var ct = cts.Token;

        RunJob(() =>
        {
            try
            {
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = ct },
                    row =>
                    {
                        var has = CoverProbe.Has(row.Path);
                        if (ct.IsCancellationRequested) return;
                        row.Track.Artwork = has;
                        Touched(row);
                    });
            }
            catch (OperationCanceledException) { /* newer listing, or the column went away again */ }
        });
    }

    /// <summary>
    /// The same FALLBACK for the ID3 version - see <see cref="EnsureCover"/> for why it survives.
    /// The index carries the version now, and the disk path gets it from the tag read, so the 1,200
    /// SMB opens this used to cause (the stall Chloe hit 2026-08-05) no longer happen on any indexed
    /// folder at all.
    /// </summary>
    private void EnsureId3()
    {
        var rows = _all.Where(r => r.Tagged && r.Id3 is null).ToList();
        if (rows.Count == 0) return;

        _id3Cts?.Cancel();
        var cts = _id3Cts = new CancellationTokenSource();
        var ct = cts.Token;

        RunJob(() =>
        {
            try
            {
                Parallel.ForEach(rows,
                    new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = ct },
                    row =>
                    {
                        var v = Id3VersionProbe.Read(row.Path) is { } major ? $"2.{major}" : null;
                        if (v is null || ct.IsCancellationRequested) return;
                        row.Id3 = v;
                        Touched(row);
                    });
            }
            catch (OperationCanceledException) { /* newer listing, or the column went away again */ }
        });
    }

    // -- Progress --------------------------------------------------------------------------------

    /// <summary>How far the read has got, in words. Null once everything is in - a progress line that
    /// stays put after it finishes is furniture.</summary>
    public string? LoadingText =>
        Listing ? "listing folder..."
        : AllHydrated || _hydrateTotal == 0 ? null
        : $"reading tags... {_hydrated} of {_hydrateTotal}";

    /// <summary>Sorting and searching both need EVERY row, so both wait - and say so rather than
    /// quietly answering from the half that happens to be loaded (Chloe 2026-08-05).</summary>
    public bool CanFilter => Ready;

    /// <summary>Called once per DRAIN tick (~10x/s), not once per row - the drain already batches, so
    /// there is nothing left to throttle here and a modulo would just make the counter skip.</summary>
    private void RaiseProgress() => Raise(nameof(LoadingText));

    private bool _allHydrated = true;
    /// <summary>Every row in the listing has been read. Sorting waits for it; the header says so.</summary>
    public bool AllHydrated
    {
        get => _allHydrated;
        private set
        {
            Set(ref _allHydrated, value);
            foreach (var n in (string[])[nameof(LockedOpacity), nameof(LockedTip),
                                         nameof(LoadingText), nameof(CanFilter), nameof(Ready),
                                         nameof(HasIndexNote)])
                Raise(n);

            // (!) There used to be a "a load started -> leave the FILTER tab" rule here, and it was wrong
            // in the one case that matters: "Include subfolders" LIVES on the filter tab and re-reads
            // the listing by design, so ticking it threw you off the tab you were standing on
            // (Chloe 2026-08-06: "ich klick auf include sub folders und er macht ne art refresh und
            // ich lande im editor - wtf"). Leaving a tab is a NAVIGATION decision, so it belongs to
            // navigation - see Open(), which only does it when the FOLDER actually changes. Staying
            // put is also safe: the search reruns when the read finishes (Finish -> ApplyFilter), and
            // "Reading tags..." says so while it is happening.
        }
    }

    /// <summary>The header dims while sorting is locked, so "nothing happens on click" is visible
    /// before the click rather than after it.</summary>
    public double LockedOpacity => Ready ? 1.0 : 0.45;

    /// <summary>Why sorting and searching are not available yet - shown on the header strip AND on the
    /// FILTER tab, because both are locked for the same reason: they need every row.</summary>
    public string? LockedTip => Ready
        ? null
        : "Still reading the folder - sorting and searching need every file.";

    private bool _searching;
    /// <summary>A tag pass is running - the pane says so instead of looking stuck.</summary>
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

    /// <summary>The three working surfaces, so "where am I" is one value and not a set of flags that
    /// can disagree with each other.</summary>
    /// <remarks>Named FocusPane, not Pane: <see cref="Pane"/> is already taken by the right side's
    /// TAB (EDITOR | ANALYSIS | FILTER). Two different questions - "which surface has the keyboard"
    /// and "which tab is showing" - so two names.</remarks>
    public enum FocusPane { Folders, Files, Panel }

    private FocusPane _active = FocusPane.Files;

    /// <summary>
    /// Which pane has the cursor. The Finder's dual-pane cue, kept identical here: the hot pane lights
    /// its header and draws a far brighter selected row, so "where am I" is answered by colour rather
    /// than by remembering what was clicked last.
    ///
    /// <para>The EDITOR / ANALYSIS / FILTER pane counts as a third one (Chloe 2026-08-06: "mach ich was
    /// rechts ... dann sollte logisch der header fokus von den dateien zu dem rechten side panel
    /// springen"). It was left out originally, which made the cue lie - the FILES header stayed lit
    /// while the keyboard was in a text box on the other side of the window.</para>
    /// </summary>
    public bool FoldersActive => _active == FocusPane.Folders;
    public bool FilesActive => _active == FocusPane.Files;
    public bool PanelActive => _active == FocusPane.Panel;

    /// <summary>
    /// One level up, if there is one. Backspace in the folder pane, and the ".." row's own action -
    /// one rule, so both go to the same place. At a drive root there is no parent and it is a no-op
    /// rather than an error.
    /// </summary>
    public void GoUp()
    {
        if (Folder is not { Length: > 0 } here) return;
        if (Directory.GetParent(here)?.FullName is { Length: > 0 } parent) Open(parent);
    }

    /// <summary>
    /// Does <paramref name="folder"/> have anywhere to go - a sub-folder or a playlist of its own?
    /// A folder that does NOT is a LEAF: clicking it opens its tracks instead of descending.
    ///
    /// <para>(!)(!) BOTH CHECKS MUST STAY SERVER-FILTERED. This first shipped as one
    /// <c>EnumerateFileSystemEntries</c> walk with <c>Directory.Exists(entry)</c> per item, on the
    /// theory that one enumeration beats two. On a network share that is catastrophic: Exists is a
    /// STAT ROUND TRIP PER ENTRY, so a sub-folder holding 700 tracks cost 700 round trips to conclude
    /// "no directories" - times every sub-folder in the listing. Opening \\nas\music\GENRES took the
    /// app out completely (Chloe 2026-08-06: "ui haengt voellig bei genres"). EnumerateDirectories and
    /// a pattern-filtered EnumerateFiles are each ONE call the server answers itself, and the first
    /// one usually settles it. Two cheap calls beat one expensive walk.</para>
    ///
    /// <para>Unreadable counts as NOT a leaf, deliberately: treat it as an ordinary folder and let
    /// <see cref="Open"/> report why it cannot be read, rather than silently pouring the contents of
    /// a folder we could not enumerate into the file pane.</para>
    /// </summary>
    public static bool HasNavigableChildren(string folder)
    {
        try
        {
            if (Directory.EnumerateDirectories(folder).Any()) return true;

            foreach (var pattern in M3uPlaylist.Extensions)
                if (Directory.EnumerateFiles(folder, "*" + pattern).Any()) return true;

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Hand the cursor to one pane - never two.</summary>
    public void Activate(FocusPane pane)
    {
        if (_active == pane) return;
        _active = pane;
        foreach (var n in (string[])[nameof(FoldersActive), nameof(FilesActive), nameof(PanelActive)])
            Raise(n);
    }

    // -- Settings --------------------------------------------------------------------------------

    private bool _settingsOpen;

    /// <summary>Whether the settings panel is showing. It is a slide-in PANEL, not a window (Chloe
    /// 2026-08-06) - same surface and same gesture as the JUST PLAY tweaks sidebar.</summary>
    public bool IsSettingsOpen { get => _settingsOpen; private set => Set(ref _settingsOpen, value); }

    public void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    public void CloseSettings() => IsSettingsOpen = false;

    // -- The selection ---------------------------------------------------------------------------

    private List<FileRow> _selected = [];

    /// <summary>
    /// Every selected row, not just the cursor. A tagger has to be able to grab a run of files -
    /// to drag them somewhere, and (next) to write one field across all of them.
    ///
    /// <para>(!) What is NOT here yet: multi-EDIT. The editor still targets the CURSOR row, because
    /// writing across a selection is a real feature and not a side effect of selecting - it needs
    /// mp3tag's rules (a differing value shows as &lt;keep&gt;, only fields you TOUCHED get written,
    /// and you see how many files you are about to change before you do). Until that exists the
    /// count in the FILES header is what keeps this honest: it says how many rows are held, so
    /// nobody can think an edit landed on all of them.</para>
    /// </summary>
    public IReadOnlyList<FileRow> Selected => _selected;

    /// <summary>How many rows are held, when it is more than the cursor. Null the rest of the time -
    /// "1 selected" next to a highlighted row is noise.</summary>
    public string? SelectionText => _selected.Count > 1 ? $"{_selected.Count} selected" : null;

    public bool HasMultiSelection => _selected.Count > 1;

    public bool HasSelection => _selected.Count > 0;

    /// <summary>Exactly one row. The entries that act on A FILE - play it, show it on disk - have no
    /// meaning for a set, and offering them greyed would be a worse answer than not offering them.</summary>
    public bool HasOneSelected => _selected.Count == 1;

    // -- Row menu headers ------------------------------------------------------------------------
    // They carry the COUNT and the platform's own wording, because a menu that says what it will do
    // to how many files is the cheapest kind of protection there is.

    /// <summary>Play or pause - the same toggle Space is, named for what it will actually do.</summary>
    public string PreviewMenuHeader =>
        Preview.IsPlaying && _selected.Count == 1 &&
        string.Equals(Preview.Path, _selected[0].Path, StringComparison.OrdinalIgnoreCase)
            ? "Pause" : "Play";

    /// <summary>"Show in Explorer" / "Reveal in Finder" - muscle memory is platform-shaped.</summary>
    public static string RevealMenuHeader => SystemFileBrowser.RevealVerb;

    public string CopyPathMenuHeader =>
        _selected.Count > 1 ? $"Copy paths ({_selected.Count})" : "Copy path";

    public void SetSelection(IEnumerable<FileRow>? rows)
    {
        var next = rows?.ToList() ?? [];
        if (next.Count == _selected.Count && next.SequenceEqual(_selected)) return;
        _selected = next;
        Raise(nameof(Selected));
        Raise(nameof(SelectionText));
        Raise(nameof(HasMultiSelection));
        Raise(nameof(HasSelection));
        Raise(nameof(HasOneSelected));
        Raise(nameof(PreviewMenuHeader));
        Raise(nameof(CopyPathMenuHeader));
    }

    private string? _problem;
    /// <summary>Why a folder could not be listed. Shown in place of the file list rather than thrown:
    /// a permission-denied folder is a normal thing to click on, not a crash.</summary>
    public string? Problem { get => _problem; private set { Set(ref _problem, value); Raise(nameof(HasProblem)); } }

    public bool HasProblem => _problem is not null;

    /// <summary>
    /// Show a folder: its sub-folders on the left, its audio files in the middle. Not recursive -
    /// one folder is one screen, and descending is a click. (Batch across a tree is the workbench
    /// step, and it is a different feature with a different confirmation.)
    /// </summary>
    /// <summary>
    /// Land on a folder the way ACTIVATING its row does - the ONE entry point for "take me to this
    /// folder", used by startup, by a drop, by the folder picker.
    ///
    /// <para>(!) <b>Why this exists.</b> The leaf rule - a folder with nothing to browse into shows
    /// its TRACKS instead of being descended into - lived in the view, on the click handler alone.
    /// Every other way of reaching a folder called <see cref="Open"/> raw and simply went in, and
    /// <see cref="Open"/> is what records LastFolder. So dropping a leaf folder on the window put you
    /// inside it AND remembered it, and the next start reopened it: a state you cannot reach by
    /// clicking, but that the app could put you in and keep you in (Chloe 2026-08-08). The rule is
    /// here now, so a rule stated once is applied everywhere.</para>
    ///
    /// <para>The check is one directory probe and runs off the UI thread - on a share it is a round
    /// trip, and it is the same call the click path already pays for.</para>
    /// </summary>
    /// <param name="alreadyInParent">The folder pane is ALREADY listing this folder's parent - which
    /// is true of a click on a row and of nothing else. A leaf then only has to fill the file pane;
    /// re-listing the parent would rebuild the folder list under the pointer for no gain.</param>
    /// <param name="whenShownAsList">Runs if it turned out to be a leaf and its tracks were listed.
    /// The keyboard's business only: Enter hands the cursor to the file pane, a mouse click leaves it
    /// where you put it.</param>
    public void GoTo(string path, bool alreadyInParent = false, Action? whenShownAsList = null)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { Problem = "That path is not valid."; return; }

        Task.Run(() => HasNavigableChildren(full)).ContinueWith(t =>
        {
            // A probe that threw counts as browsable, matching HasNavigableChildren's own rule:
            // let Open report why a folder cannot be read rather than pouring an unreadable folder
            // into the file pane.
            var canBrowse = t.Status != TaskStatus.RanToCompletion || t.Result;

            Dispatcher.UIThread.Post(() =>
            {
                if (canBrowse) { Open(full); return; }

                // A LEAF. Stand in its PARENT with the leaf's tracks in the file pane - exactly the
                // state clicking the row produces. A leaf with no parent (a drive root holding only
                // songs) has nowhere to stand, so it is opened as an ordinary folder.
                if (alreadyInParent) OpenFolderTracks(full);
                else if (Directory.GetParent(full)?.FullName is { Length: > 0 } parent)
                    Open(parent, thenShowTracksOf: full);
                else { Open(full); return; }

                whenShownAsList?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    /// <param name="thenShowTracksOf">A LEAF folder whose tracks fill the file pane once
    /// <paramref name="path"/> is listed. See <see cref="GoTo"/>.</param>
    public void Open(string path, string? thenShowTracksOf = null)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { Problem = "That path is not valid."; return; }

        // Going somewhere ELSE steps back to the editor: a search you typed for the last folder
        // is not a search you meant for this one. Re-reading the SAME folder (what "Include
        // subfolders" does) is not going anywhere, so it leaves the tab alone.
        var movedFolder = !string.Equals(Folder, full, StringComparison.OrdinalIgnoreCase);
        if (movedFolder && _pane == TagPane.Filter) Pane = TagPane.Editor;

        Folder = full;
        Problem = null;
        ListSource = null;
        _sourcePath = null;
        Crumbs = BuildCrumbs(full);
        _settings.Current.LastFolder = full;
        _settings.Save();

        // (!) THE LISTING RUNS OFF THE UI THREAD. It used to run right here, and on a local folder
        // nobody could tell. On \\nas\music\GENRES with "include subfolders" on it is a recursive
        // SMB walk over ~15,000 files, and the window was simply gone for the duration (Chloe
        // 2026-08-06: "waehle links den ordner GENRES und baemmm... app ist tot und non responding").
        // Directory.Exists is a single stat and stays here so a vanished folder answers instantly.
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var ct = cts.Token;

        // A newer folder owns the pane - stop the previous one's tag pass and the on-demand probes
        // before its rows are dropped, and throw away anything they had already queued for painting.
        _tagCts?.Cancel();
        _coverCts?.Cancel();
        _id3Cts?.Cancel();
        DropPending();

        var recursive = IncludeSubfolders;

        // Snapshot the rows we may re-use, on the UI thread, before handing off. A row is KEPT when
        // the same file is still in the listing, instead of being rebuilt: re-reading a folder used
        // to mean new FileRow objects for every path, which threw away every tag already read AND
        // made the collection differ from itself - so the list cleared and refilled, the sort dropped
        // to folder order while the re-read ran, and it visibly built itself twice (Chloe 2026-08-06:
        // "warum zuckt die files liste ... es gibt keine subfolder, trotzdem ... baut sie sich 2x neu
        // auf"). Matching on the absolute path is always safe: same path, same file, same facts.
        var kept = _all.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        Listing = true;

        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(full))
                {
                    Post(ct, () => { Listing = false; Problem = "That folder is not there any more."; });
                    return;
                }

                var folders = new List<FolderRow>();
                var parent = Directory.GetParent(full)?.FullName;
                if (parent is not null) folders.Add(new FolderRow("..", parent, IsUp: true));
                // Leaf-ness is NOT decided here - it is decided when a row is activated, exactly as
                // the Finder does it. Measured on \nas\music\GENRES, the check is linear in a
                // folder's file count (~370 ms for 1,092) because SMB has no cheap "has a
                // sub-directory" answer; doing it for every sub-folder up front is seconds of listing
                // latency for information the row never displays.
                foreach (var dir in Directory.EnumerateDirectories(full)
                                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    if (ct.IsCancellationRequested) return;
                    folders.Add(new FolderRow(Path.GetFileName(dir) ?? dir, dir, IsUp: false));
                }

                // Playlists are VIRTUAL FOLDERS, exactly as in the Finder - a set is a place you go to
                // tag its tracks, and her sets live in one folder away from the music. Dropping them
                // would mean "tag a set" needs a detour through the file system.
                foreach (var pl in Directory.EnumerateFiles(full)
                                            .Where(M3uPlaylist.IsPlaylist)
                                            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    folders.Add(new FolderRow(Path.GetFileNameWithoutExtension(pl) ?? pl, pl,
                                              IsUp: false, IsPlaylist: true));

                if (ct.IsCancellationRequested) return;

                // EnumerateWithKeys is the form that takes `recursive` - Enumerate() always walks the
                // whole tree, which is not what a folder listing means.
                //
                // (!) The walk is CANCELLED MID-STREAM, not only checked when it finishes. Under a
                // library root it is a 15,000-file SMB enumeration; clicking three folders in a row
                // has to abandon the first two immediately, not queue three full walks behind each
                // other (Chloe 2026-08-06: "das muss auch unterbrechbar sein wenn ich wilde ordner
                // wechsel vornehme").
                var paths = new List<string>();
                foreach (var s in AudioFiles.EnumerateWithKeys(full, recursive: recursive))
                {
                    if (ct.IsCancellationRequested) return;
                    paths.Add(s.Path);
                }

                paths.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                                    StringComparison.OrdinalIgnoreCase));
                if (ct.IsCancellationRequested) return;

                var rows = new List<FileRow>(paths.Count);
                for (var i = 0; i < paths.Count; i++)
                {
                    var f = paths[i];
                    var row = kept.TryGetValue(f, out var old) ? old : new FileRow(Path.GetFileName(f) ?? f, f);
                    row.Order = i;
                    rows.Add(row);
                }

                Post(ct, () =>
                {
                    Folders.Clear();
                    foreach (var f in folders) Folders.Add(f);

                    _all = rows;
                    // (!) "no audio files" alone is TRUE and useless. \\nas\music\GENRES holds no loose
                    // tracks at all - everything is one level down - so a bare "no audio files" reads
                    // as "this app cannot see your music" (Chloe 2026-08-06: "no audio files haha -
                    // luege"). When there is nothing here but there IS something below, say that
                    // instead; the answer is "look one level down", not "there is nothing".
                    var below = folders.Count(f => !f.IsUp);
                    _countText = rows.Count switch
                    {
                        0 when below == 1 => "no audio files here - 1 folder below",
                        0 when below > 1 => $"no audio files here - {below} folders below",
                        0 => "no audio files",
                        1 => "1 file",
                        var n => $"{n} files",
                    };
                    Listing = false;
                    StartTagPass();
                    ApplyListingSort(isPlaylist: false);
                    ApplyFilter();

                    // The folder pane is now standing in the parent; swap the file pane over to the
                    // leaf. It has to happen HERE and not right after Open returns, because both
                    // listings share _listCts - starting the second one early would cancel the first
                    // and leave the folder pane empty.
                    if (thenShowTracksOf is { Length: > 0 } leaf) OpenFolderTracks(leaf);
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Listing a folder we may not read is an ordinary click, not a failure of the app.
                Post(ct, () =>
                {
                    Problem = $"Can't read this folder - {ex.Message}";
                    Folders.Clear();
                    _all = [];
                    _countText = "";
                    Listing = false;
                    StartTagPass();
                    ApplyFilter();
                });
            }
        }, CancellationToken.None);
    }

    private CancellationTokenSource? _listCts;

    private static void Post(CancellationToken ct, Action action) =>
        Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) action(); });

    private bool _listing;

    /// <summary>The folder is being walked. Separate from <see cref="AllHydrated"/> because it is a
    /// different wait with a different message: this one is "how many files are there at all", the
    /// other is "what do they say". Both lock sorting and the FILTER tab.</summary>
    public bool Listing
    {
        get => _listing;
        private set
        {
            Set(ref _listing, value);
            foreach (var n in (string[])[nameof(LockedOpacity), nameof(LockedTip),
                                         nameof(LoadingText), nameof(CanFilter), nameof(Ready),
                                         nameof(HasIndexNote)])
                Raise(n);
        }
    }

    /// <summary>Nothing is in flight: the folder is listed AND every row has been read.</summary>
    public bool Ready => !Listing && AllHydrated;

    /// <summary>
    /// Show a PLAYLIST's tracks in the file pane. The folder pane stays where it is - a set is a
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
            // what turns that into a visible number - showing 40 of 60 tracks and saying "40" is
            // exactly the silent loss we do not do (memory never-leave-songs-behind).
            var declared = File.ReadLines(playlistPath)
                               .Count(l => l.Length > 0 && !l.TrimStart().StartsWith('#'));
            var missing = Math.Max(0, declared - tracks.Count);

            ListSource = Path.GetFileNameWithoutExtension(playlistPath);
            _sourcePath = playlistPath;
            Problem = null;

            _all = [.. tracks.Select((t, i) => new FileRow(Path.GetFileName(t) ?? t, t) { Order = i })];
            _countText = missing == 0
                ? $"{tracks.Count} in this set"
                : $"{tracks.Count} in this set - {missing} missing";
            // After StartTagPass, because that is what settles AllHydrated - and whether the listing
            // is complete right now is exactly what decides if it may be sorted at all.
            StartTagPass();
            ApplyListingSort(isPlaylist: true);
            ApplyFilter();
            Crumbs = [.. BuildCrumbs(Path.GetDirectoryName(playlistPath) ?? playlistPath)
                            .Select(c => c with { IsLast = false }),
                      new Crumb(ListSource ?? "set", playlistPath, IsLast: true)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Problem = $"Can't read this playlist - {ex.Message}";
            _all = [];
            _countText = "";
            StartTagPass();
            ApplyFilter();
        }
    }

    /// <summary>
    /// Show a LEAF folder's tracks in the file pane without going into it. Same shape as
    /// <see cref="OpenPlaylist"/>, and for the same reason: a folder with nothing to browse into is
    /// not a place you navigate to, it is a list of songs (Chloe 2026-08-06: "ordner ohne subfolder
    /// so behandeln wie playlisten"). Descending would replace the sibling folders with a pane
    /// containing nothing but "..".
    ///
    /// <para>Not recursive - a leaf has no sub-folders by definition, so there is nothing below it.</para>
    /// </summary>
    public void OpenFolderTracks(string folderPath)
    {
        ListSource = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar,
                                                         Path.AltDirectorySeparatorChar));
        _sourcePath = folderPath;
        Problem = null;

        var kept = _all.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var ct = cts.Token;
        _tagCts?.Cancel();
        _coverCts?.Cancel();
        _id3Cts?.Cancel();
        DropPending();

        Listing = true;

        Task.Run(() =>
        {
            try
            {
                var paths = new List<string>();
                foreach (var s in AudioFiles.EnumerateWithKeys(folderPath, recursive: false))
                {
                    if (ct.IsCancellationRequested) return;
                    paths.Add(s.Path);
                }
                paths.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                                    StringComparison.OrdinalIgnoreCase));

                var rows = new List<FileRow>(paths.Count);
                for (var i = 0; i < paths.Count; i++)
                {
                    var f = paths[i];
                    var row = kept.TryGetValue(f, out var old) ? old : new FileRow(Path.GetFileName(f) ?? f, f);
                    row.Order = i;
                    rows.Add(row);
                }

                Post(ct, () =>
                {
                    _all = rows;
                    _countText = rows.Count switch
                    {
                        0 => "no audio files",
                        1 => "1 file",
                        var n => $"{n} files",
                    };
                    Listing = false;
                    StartTagPass();
                    ApplyListingSort(isPlaylist: false);
                    ApplyFilter();
                    Crumbs = [.. BuildCrumbs(Path.GetDirectoryName(folderPath) ?? folderPath)
                                    .Select(c => c with { IsLast = false }),
                              new Crumb(ListSource ?? "folder", folderPath, IsLast: true)];
                });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Post(ct, () =>
                {
                    Problem = $"Can't read this folder - {ex.Message}";
                    _all = [];
                    _countText = "";
                    Listing = false;
                    StartTagPass();
                    ApplyFilter();
                });
            }
        }, CancellationToken.None);
    }

    private string? _sourceName;
    private string? _sourcePath;

    /// <summary>
    /// What the FILE pane is showing when it is NOT showing <see cref="Folder"/>'s own files - a
    /// playlist, or a LEAF folder opened as a list. Null means "this folder", the ordinary case.
    ///
    /// <para>It was called <c>Playlist</c> until leaf folders started using the same mechanism
    /// (Chloe 2026-08-06). Two states with one meaning get one name; two names for one meaning is how
    /// the second one quietly stops being handled.</para>
    /// </summary>
    public string? ListSource
    {
        get => _sourceName;
        private set { Set(ref _sourceName, value); Raise(nameof(ShowingFolder)); Raise(nameof(CanIncludeSubfolders)); }
    }

    /// <summary>The file pane is showing the current folder's own files.</summary>
    public bool ShowingFolder => _sourceName is null;

    /// <summary>A set names its tracks wherever they live, and a leaf folder has no sub-folders by
    /// definition - so "below this folder" has nothing to mean in either case. The checkbox greys out
    /// rather than lying.</summary>
    public bool CanIncludeSubfolders => ShowingFolder;

    /// <summary>Read the current folder again - after a rename, or when something changed on disk
    /// behind our back.</summary>
    public void Refresh()
    {
        if (_sourcePath is { Length: > 0 } src)
        {
            if (M3uPlaylist.IsPlaylist(src)) OpenPlaylist(src); else OpenFolderTracks(src);
        }
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

    // -- INPC ------------------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }
}
