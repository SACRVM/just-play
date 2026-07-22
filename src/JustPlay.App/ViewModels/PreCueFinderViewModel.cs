using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
using JustPlay.Core.Playlists;
using JustPlay.UI.Controls;

namespace JustPlay.App.ViewModels;

/// <summary>
/// PRE CUE FINDER — the keyboard-first library audition explorer (0.5.0 headline, queue N26).
///
/// <para><b>P2 browse model (Chloe 2026-07-05):</b> two flat panes, Norton-Commander style —
/// FOLDERS left, FILES right. The left pane lists the current folder's navigable children with no
/// nesting: a ".." hop, each sub-folder (📁), and each playlist (☰) treated as a VIRTUAL FOLDER
/// (entering one loads its tracks into the file pane). TAB switches which pane has focus (the
/// focused pane's header lights up); Enter descends / adds; Backspace goes up (only while the
/// folders pane is focused). The clickable breadcrumb in the chrome jumps to any level.</para>
///
/// <para>Play/pause is a browsing MODE (Space): in play mode the selected song starts after ~1 s
/// (so racing down a list never blasts the ears — see chloe-sound-sensitivity), paused browsing
/// stays silent. Enter (files) adds the selected song to the CURRENT queue and advances; L likes;
/// the detail panel shows everything the analyzer stored in the v9 blob — zero DSP on browse.</para>
///
/// <para>Shares the single <see cref="IPreListenEngine"/> (and thus the hard cue-isolation
/// invariant) and the cue device selection (<see cref="UserSettings.HeadphoneDeviceName"/>, edited
/// here via <see cref="Main"/>) with the pre-cue tab. Its own add-on settings (library root, seek
/// step) live in <see cref="IFinderSettingsService"/> — a separate file.</para>
/// </summary>
public sealed partial class PreCueFinderViewModel : ViewModelBase
{
    /// <summary>Which of the two panes currently owns focus (drives the header highlight).</summary>
    public enum FinderPane { Folders, Files }

    // Folder names never worth showing — dot-folders plus the usual NAS system dirs
    // (Synology thumbnail/recycle, Windows share clutter). Name-based on purpose: an
    // Attributes check would cost one extra round-trip per entry on a network share.
    private static readonly HashSet<string> IgnoredFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "#recycle", "@Recycle", "$RECYCLE.BIN", "System Volume Information"
    };

    /// <summary>Sub-folders of <paramref name="folder"/>, junk filtered, natural-sorted. May throw —
    /// callers guard (NAS).</summary>
    internal static List<string> ListSubfolders(string folder) =>
        Directory.EnumerateDirectories(folder)
            .Where(d => Path.GetFileName(d) is { Length: > 0 } n
                        && !n.StartsWith('.') && !IgnoredFolderNames.Contains(n))
            .OrderBy(Path.GetFileName, NaturalComparer.Instance)
            .ToList();

    /// <summary>Playlists in <paramref name="folder"/> (the same M3U source JUST PLAY imports, so
    /// the finder shows exactly what the app can open — .pls/.xspf ride in via the N25 façade).
    /// Natural-sorted. May throw — callers guard.</summary>
    internal static List<string> ListPlaylists(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(M3uPlaylist.IsPlaylist)
            .OrderBy(Path.GetFileName, NaturalComparer.Instance)
            .ToList();

    private readonly MainWindowViewModel _main;
    private readonly IPreListenEngine _preListen;
    private readonly IFinderSettingsService _settings;
    private readonly IMetadataReader _metadata;
    private readonly IMetadataWriter _writer;
    private readonly IWaveformService _waveform;

    /// <summary>Compute resolution of the cue waveform envelope. The scrubber control resamples this to
    /// its pixel width, so a fixed, generous value keeps the shape crisp at any window size.</summary>
    private const int WaveformBuckets = 1600;

    /// <summary>Debounce between selection and cue playback in play mode: ~1 s, so holding ↓
    /// browses silently and only the row she actually settles on starts sounding.</summary>
    private readonly DispatcherTimer _playDebounce = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>UI-rate position readout while the window is open (same cadence as the shell VM's tick).</summary>
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Normalised paths of everything on the current queue — drives the ✓ column.</summary>
    private readonly HashSet<string> _queuePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Like-writes deferred because an engine still holds the file (last press wins).
    /// Flushed on cue track change and window close.</summary>
    private readonly Dictionary<string, (FinderItemViewModel Item, bool Liked)> _pendingLikes =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _navCts;      // file-listing loads
    private CancellationTokenSource? _entriesCts;  // folder-entry (left pane) loads
    private FinderItemViewModel? _cuedItem;
    private string? _loadedCuePath;
    private int _pollCounter;

    public PreCueFinderViewModel(
        MainWindowViewModel main,
        IPreListenEngine preListen,
        IFinderSettingsService settings,
        IMetadataReader metadata,
        IMetadataWriter writer,
        IWaveformService waveform)
    {
        _main = main;
        _preListen = preListen;
        _settings = settings;
        _metadata = metadata;
        _writer = writer;
        _waveform = waveform;

        _playDebounce.Tick += (_, _) =>
        {
            _playDebounce.Stop();
            if (AutoPlayOnSelect && Selected is { } item) LoadCue(item);
        };
        _tick.Tick += (_, _) => OnTick();

        // Shared column visibility + sort state (see TrackColumns) — the SAME object the queue uses, so the
        // finder's TrackDataHeader + TrackRow bind straight to it and can't drift. Hydrated from
        // finder.settings.json; a toggle persists there, a header click re-sorts the current view.
        Columns = new TrackColumns(_settings.Current.VisibleColumns);
        Columns.VisibilityChanged += () => _settings.Save(_settings.Current with { Columns = Columns.Enabled.ToArray() });
        Columns.SortRequested += RebuildView;

        // Engine events fire on BASS's playback thread — marshal (CLAUDE.md threading rule).
        _preListen.StateChanged += (_, s) =>
            Dispatcher.UIThread.Post(() => IsCuePlaying = s == PlaybackState.Playing);
        _preListen.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(OnCueEnded);

        // ✓ stays live: the moment Enter (or anything else) changes the queue, re-flag rows.
        _main.Tracks.CollectionChanged += (_, _) => RebuildMembership();

        // FILTER ranges — one dual-handle slider per analyzed field, each re-filtering the view live.
        // Vibe scores gate on HasAnalysis (the finder loads the stored blob into Model.Analysis on browse),
        // so an un-analyzed row reads null and drops out of any active vibe band.
        Ranges =
        [
            new("bpm",      "BPM",      t => t.Bpm,                                     v => v.ToString("0"), RebuildView),
            new("nrg",      "Energy",   t => (double?)t.Energy,                          v => v.ToString("0"), RebuildView),
            // No gain-dB / LUFS / length ranges — nobody filters a set by those (Chloe 2026-07-07). They stay
            // as sortable columns; only the vibe scores + BPM + energy + key earn a range slider here.
            new("dark",     "Dark",     t => t.HasAnalysis ? t.DarkScore : null,         v => v.ToString("0.00", CultureInfo.InvariantCulture), RebuildView),
            new("hypnotic", "Hypnotic", t => t.HasAnalysis ? t.HypnoticScore : null,     v => v.ToString("0.00", CultureInfo.InvariantCulture), RebuildView),
            new("groove",   "Groove",   t => t.HasAnalysis ? t.GrooveScore : null,       v => v.ToString("0.00", CultureInfo.InvariantCulture), RebuildView),
            new("punch",    "Punch",    t => t.HasAnalysis ? t.PunchScore : null,        v => v.ToString("0.00", CultureInfo.InvariantCulture), RebuildView),
            new("harsh",    "Harsh",    t => t.HasAnalysis ? t.HarshScore : null,        v => v.ToString("0.00", CultureInfo.InvariantCulture), RebuildView),
        ];
    }

    /// <summary>The FILTER tab's numeric range sliders (BPM · energy · vibe scores) — the fields DJs actually
    /// narrow a set by; gain/LUFS/length stay sortable columns but earn no range slider.</summary>
    public IReadOnlyList<FinderFilterRange> Ranges { get; }

    /// <summary>The shell VM — the settings flyout binds the shared cue device + volume through
    /// this (HeadphoneDevices / SelectedHeadphoneDevice / PreCueVolume), so the finder and the
    /// pre-cue tab can never disagree about where cue audio goes.</summary>
    public MainWindowViewModel Main => _main;

    // ── Which folder / playlist we're browsing ───────────────────────────────

    /// <summary>The folder whose children fill the LEFT pane and whose files fill the RIGHT pane
    /// (unless a playlist is entered). Null before a library root is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    private string? _currentFolder;

    /// <summary>Non-null while a playlist ("virtual folder") is loaded into the file pane — the
    /// left pane still shows <see cref="CurrentFolder"/>'s children, with this playlist selected.</summary>
    [ObservableProperty]
    private string? _currentPlaylist;

    /// <summary>Non-null while a LEAF folder (no sub-folders/playlists) is opened into the file pane —
    /// same "virtual folder" treatment as <see cref="CurrentPlaylist"/>, but the source is the folder's
    /// own tracks rather than an M3U. Mutually exclusive with <see cref="CurrentPlaylist"/>.</summary>
    [ObservableProperty]
    private string? _currentLeafFolder;

    // ── Folder pane (left) ───────────────────────────────────────────────────

    /// <summary>Left-pane rows for the current folder: ".." + sub-folders + playlists (flat).</summary>
    public BulkObservableCollection<FinderEntryViewModel> FolderEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddSelectedEntry))]
    [NotifyPropertyChangedFor(nameof(SelectedEntryIsPlaylist))]
    private FinderEntryViewModel? _selectedEntry;

    /// <summary>↑/↓/PageUp/PageDown while the FOLDER pane has focus (relative, clamped).</summary>
    public void MoveFolderSelection(int delta)
    {
        if (FolderEntries.Count == 0) return;
        var i = SelectedEntry is null ? -1 : FolderEntries.IndexOf(SelectedEntry);
        SelectedEntry = FolderEntries[Math.Clamp(i + delta, 0, FolderEntries.Count - 1)];
    }

    /// <summary>Home/End in the FOLDER pane — jump to an absolute row (clamped).</summary>
    public void MoveFolderSelectionTo(int index)
    {
        if (FolderEntries.Count == 0) return;
        SelectedEntry = FolderEntries[Math.Clamp(index, 0, FolderEntries.Count - 1)];
    }

    /// <summary>Set by the window so the VM can hand keyboard focus to the file pane when it opens a
    /// playlist / leaf folder — a view concern the VM can't do itself.</summary>
    public Action? RequestFocusFiles { get; set; }

    /// <summary>Enter / single-click / double-click on a folder-pane row. ".." goes up; a playlist and a
    /// LEAF folder (no sub-folders or playlists of its own) open their tracks in the file pane and hand
    /// it focus — a leaf folder is treated exactly like a playlist, because descending into one would
    /// show only ".." (Chloe 2026-07-06: "in einen Ordner ohne Unterordner geht man nicht rein"). A
    /// folder that DOES have navigable children descends, keeping focus left so she can keep drilling.</summary>
    public void ActivateEntry()
    {
        if (SelectedEntry is not { } e) return;
        switch (e.Kind)
        {
            case FinderEntryKind.Up:
                GoUp();
                break;
            case FinderEntryKind.Playlist:
                EnterPlaylist(e.FullPath);
                RequestFocusFiles?.Invoke();
                break;
            case FinderEntryKind.Folder:
                ActivateFolder(e.FullPath);
                break;
        }
    }

    /// <summary>Decide off-thread whether <paramref name="folder"/> is a container (has sub-folders or
    /// playlists → descend) or a leaf (only tracks → open like a playlist). The enumeration is a NAS
    /// round-trip, so it never runs on the UI thread.</summary>
    private void ActivateFolder(string folder)
    {
        _ = Task.Run(() =>
        {
            bool hasChildren;
            try { hasChildren = ListSubfolders(folder).Count > 0 || ListPlaylists(folder).Count > 0; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Finder] leaf check failed: {folder}: {ex.GetType().Name}: {ex.Message}");
                hasChildren = false; // unreadable folder → treat as a leaf, at least show its tracks
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (hasChildren) NavigateToFolder(folder);
                else { OpenLeafFolder(folder); RequestFocusFiles?.Invoke(); }
            });
        });
    }

    /// <summary>Open a leaf folder's tracks in the file pane WITHOUT descending — the left pane keeps
    /// showing the parent (with this folder selected), exactly like entering a playlist.</summary>
    private void OpenLeafFolder(string folder)
    {
        CurrentPlaylist = null;
        CurrentLeafFolder = folder;
        RebuildBreadcrumb();
        LoadFolderFiles(folder);
    }

    /// <summary>Backspace (folders pane) / the ".." row: hop to the parent folder. Bounded at the
    /// library root — the finder never wanders above the configured library.</summary>
    [RelayCommand]
    private void GoUp()
    {
        if (CurrentFolder is not { } cur || !CanGoUpFrom(cur)) return;
        var parent = Path.GetDirectoryName(cur.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (!string.IsNullOrEmpty(parent)) NavigateToFolder(parent, selectChild: cur);
    }

    /// <summary>True when the current folder is a strict descendant of the library root.</summary>
    public bool CanGoUp => CurrentFolder is { } f && CanGoUpFrom(f);

    private bool CanGoUpFrom(string folder)
    {
        if (LibraryRoot is not { } root) return false;
        var nf = NormalizePath(folder);
        var nr = NormalizePath(root);
        return nf.Length > nr.Length
               && nf.StartsWith(nr, StringComparison.OrdinalIgnoreCase)
               && nf[nr.Length] == Path.DirectorySeparatorChar;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Descend into (or jump to) a folder: rebuild the left pane + breadcrumb and load the
    /// folder's own files into the right pane. <paramref name="selectChild"/> lands the left-pane
    /// cursor back on the folder we came from when going up.</summary>
    private void NavigateToFolder(string? folder, string? selectChild = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        CurrentPlaylist = null;
        CurrentLeafFolder = null;
        CurrentFolder = folder;
        BuildFolderEntries(folder, selectChild);
        LoadFolderFiles(folder);
    }

    /// <summary>Enter a playlist as a virtual folder: its tracks fill the right pane (in playlist
    /// order); the left pane keeps showing the containing folder, with the playlist selected.</summary>
    private void EnterPlaylist(string playlistPath)
    {
        CurrentLeafFolder = null;
        CurrentPlaylist = playlistPath;
        RebuildBreadcrumb();
        LoadPlaylistFiles(playlistPath);
    }

    /// <summary>Public entry point for a breadcrumb-segment click.</summary>
    [RelayCommand]
    private void NavigateToPath(string? folder)
    {
        if (!string.IsNullOrEmpty(folder)) NavigateToFolder(folder);
    }

    private void BuildFolderEntries(string folder, string? selectChild)
    {
        _entriesCts?.Cancel();
        var cts = _entriesCts = new CancellationTokenSource();

        _ = Task.Run(() =>
        {
            List<string> folders, playlists;
            try { folders = ListSubfolders(folder); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Finder] subfolder enumeration failed: {folder}: {ex.GetType().Name}: {ex.Message}");
                folders = [];
            }
            try { playlists = ListPlaylists(folder); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Finder] playlist enumeration failed: {folder}: {ex.GetType().Name}: {ex.Message}");
                playlists = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.Token.IsCancellationRequested) return;

                var entries = new List<FinderEntryViewModel>();
                if (CanGoUpFrom(folder))
                {
                    var parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, '/'));
                    if (!string.IsNullOrEmpty(parent)) entries.Add(FinderEntryViewModel.Up(parent));
                }
                entries.AddRange(folders.Select(FinderEntryViewModel.Folder));
                entries.AddRange(playlists.Select(FinderEntryViewModel.Playlist));

                FolderEntries.ReplaceAll(entries);
                RebuildBreadcrumb();
                OnPropertyChanged(nameof(CanGoUp));

                SelectedEntry = selectChild is not null
                    ? entries.FirstOrDefault(e => !e.IsUp && PathsEqual(e.FullPath, selectChild)) ?? entries.FirstOrDefault()
                    : entries.FirstOrDefault();
            });
        }, cts.Token);
    }

    // ── File pane (right) ────────────────────────────────────────────────────

    public BulkObservableCollection<FinderItemViewModel> Items { get; } = [];

    /// <summary>Everything loaded for the current folder/playlist, BEFORE the FILTER tab narrows it.
    /// <see cref="Items"/> is always sort(filter(this)). Filtering is purely in-memory over this list —
    /// the finder never touches a library index (Chloe 2026-07-07: "keine library, erstmal").</summary>
    private List<FinderItemViewModel> _masterItems = [];

    [ObservableProperty]
    private FinderItemViewModel? _selected;

    partial void OnSelectedChanged(FinderItemViewModel? value)
    {
        RaiseDetail();
        _playDebounce.Stop();
        // Auto-play mode (opt-in): settle on a row and it auditions after the ~1 s debounce, so racing
        // the list never blasts the ears (chloe-sound-sensitivity). Default OFF — Enter plays instead.
        if (value is not null && AutoPlayOnSelect) _playDebounce.Start();
    }

    /// <summary>Load failure for the current folder / playlist (NAS offline, permissions, root
    /// deleted…). Shown as an inline hint — the finder must never crash on a flaky share.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyFolderHint))]
    private string? _loadError;

    public bool HasNoRoot => string.IsNullOrWhiteSpace(LibraryRoot);

    public bool ShowEmptyFolderHint =>
        !HasNoRoot && LoadError is null && CurrentFolder is not null && _masterItems.Count == 0;

    /// <summary>The folder HAS tracks but the FILTER narrowed them all out — a distinct, non-alarming hint.</summary>
    public bool ShowNoMatches =>
        !HasNoRoot && LoadError is null && _masterItems.Count > 0 && Items.Count == 0;

    /// <summary>↑/↓/PageUp/PageDown while the FILE pane has focus — deterministic regardless of which
    /// control actually holds keyboard focus.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        var i = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[Math.Clamp(i + delta, 0, Items.Count - 1)];
    }

    /// <summary>Home/End in the FILE pane — jump to an absolute row (clamped).</summary>
    public void MoveSelectionTo(int index)
    {
        if (Items.Count == 0) return;
        Selected = Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    /// <summary>The "+" / A key: add to the current queue. With a multi-selection (Ctrl/Shift-click) it
    /// adds ALL selected rows at once — the whole point of finder multi-select ("viele zur PL hinzufügen",
    /// Chloe 2026-07-07). With a single cursor it adds that one row and advances, for fast one-handed queue
    /// building. Chloe 2026-07-06: adding is "+", NOT Enter — Enter PLAYS (see <see cref="PlaySelected"/>).</summary>
    public void ActivateSelected()
    {
        if (SelectedItems.Count > 1) { AddSelectionToQueue(); return; }
        if (Selected is not { } item) return;
        AddToQueue(item);
        AdvanceToNextTrackRow(item);
    }

    // ── Multi-select (Ctrl/Shift-click) → bulk right-click actions ───────────────────────────────
    // Kept in sync by the window's SelectionChanged (same pattern as the JUST PLAY queue's SelectedTracks).
    // The cursor (Selected) still drives the cue + INFO panel; this set drives the row context menu.

    /// <summary>Every file row currently selected in the file pane, in no particular order (the commands
    /// re-sort by list position). Mirrors <see cref="MainWindowViewModel.SelectedTracks"/>.</summary>
    public ObservableCollection<FinderItemViewModel> SelectedItems { get; } = [];

    /// <summary>Set by the window: clears the file ListBox's live (multi-)selection. Invoked right before
    /// <see cref="Items"/> is reset, because Avalonia's SelectionModel throws (ArgumentOutOfRangeException,
    /// enumerating SelectedItems against stale indices) when the bound source resets while a selection is
    /// live — and setting <see cref="Selected"/>=null alone can't reach a Ctrl-click MULTI-selection.
    /// Chloe 2026-07-08 (the KeyWheel-click crash).</summary>
    public Action? ClearFileSelection { get; set; }

    public bool HasFileSelection => SelectedItems.Count > 0;

    /// <summary>Row context-menu headers — carry "(N)" when more than one row is selected, exactly like the
    /// queue's bulk headers. "Analyze" until every selected row already has a v9 blob, then "Re-analyze".</summary>
    public string AddSelectionHeader => WithFileCount("Add to list");
    public string ReanalyzeSelectionHeader =>
        WithFileCount(SelectedItems.Count > 0 && SelectedItems.All(i => i.Track.HasAnalysis) ? "Re-analyze" : "Analyze");

    private string WithFileCount(string verb) =>
        SelectedItems.Count > 1 ? $"{verb} ({SelectedItems.Count})" : verb;

    /// <summary>Refresh the row-menu headers + visibility as the menu opens (called from the window's
    /// ContextRequested, after it has fixed up the selection) — mirrors the queue's RefreshMenuState.</summary>
    public void RefreshFileMenuState()
    {
        OnPropertyChanged(nameof(HasFileSelection));
        OnPropertyChanged(nameof(AddSelectionHeader));
        OnPropertyChanged(nameof(ReanalyzeSelectionHeader));
    }

    /// <summary>The selected rows in on-screen (list) order — a set added to a playlist should land in the
    /// order it reads, not the order it was clicked.</summary>
    private List<FinderItemViewModel> SelectionInListOrder() =>
        (SelectedItems.Count > 0 ? SelectedItems : (Selected is { } s ? [s] : []))
            .OrderBy(Items.IndexOf).ToList();

    /// <summary>Right-click → "Add to list": add every selected row to the current queue, in list order.</summary>
    [RelayCommand]
    private void AddSelectionToQueue()
    {
        var paths = SelectionInListOrder().Select(i => i.FullPath).ToList();
        if (paths.Count == 0) return;
        _ = AddPathsGuarded(paths);
    }

    /// <summary>Right-click → "(Re-)analyze": run the analyzer over every selected row through the shell's
    /// shared pipeline (threads cap + the auto-write-on-analyse consent). Each finder row wraps its own
    /// TrackViewModel, so the columns + INFO panel refresh in place as each track finishes.</summary>
    [RelayCommand]
    private void ReanalyzeSelection()
    {
        var targets = SelectionInListOrder().Select(i => i.Track).ToList();
        if (targets.Count == 0) return;
        _ = _main.AnalyzeExternalAsync(targets);
    }

    // ── Left-pane (folder / playlist) context actions ────────────────────────────────────────────
    // The finder already navigates INTO folders/playlists to audition; these two verbs act on a whole
    // ENTRY without entering it — the same two moves JUST PLAY already makes for external files
    // (a playlist REPLACES the queue; loose tracks are ADDED). Target is always the current queue —
    // no new playlist is created, no file on disk is touched (Chloe 2026-07-22: "keine neue magie").

    /// <summary>True when the (right-clicked) left-pane entry is a real folder/playlist, not the ".." hop
    /// — gates the "Add to list" context item.</summary>
    public bool CanAddSelectedEntry => SelectedEntry is { IsUp: false };

    /// <summary>True when the (right-clicked) left-pane entry is a playlist — gates "Open playlist".</summary>
    public bool SelectedEntryIsPlaylist => SelectedEntry is { IsPlaylist: true };

    /// <summary>Set by the window: show a modal yes/no confirm over the finder window
    /// (title, message, confirm-button label) → true when confirmed. Null-safe: a missing hook fails
    /// safe (treated as NOT confirmed), so a destructive action never runs unasked.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Context "Add to list" on a folder or playlist: add ALL its tracks to the queue — the bulk
    /// sibling of the file pane's per-selection add, run on a whole entry. Same guarded enqueue path.</summary>
    [RelayCommand]
    private async Task AddEntryToQueue()
    {
        if (SelectedEntry is not { IsUp: false } entry) return;

        List<string> paths;
        try { paths = await Task.Run(() => ReadEntryTracks(entry)); }   // NAS round-trip — off the UI thread
        catch (Exception ex) { ErrorReporter.Report(ex, $"Reading \"{entry.Name}\" to add to the queue"); return; }

        if (paths.Count == 0) { _main.EventLog.Append($"\"{entry.Name}\" has no tracks to add."); return; }
        await AddPathsGuarded(paths);
    }

    /// <summary>Context "Open playlist" on a playlist: open it like an Explorer double-click — REPLACE the
    /// queue with it (via the shell's LoadPlaylistAsync). Confirms first when the queue isn't empty, so a
    /// live set is never wiped by accident (Chloe: "open nachfragen").</summary>
    [RelayCommand]
    private async Task OpenPlaylist()
    {
        if (SelectedEntry is not { IsPlaylist: true } entry) return;

        if (_main.Tracks.Count > 0)
        {
            if (ConfirmAsync is not { } confirm) return;   // no way to ask → never wipe the queue silently
            var n = _main.Tracks.Count;
            var ok = await confirm("Open playlist?",
                $"This replaces your current queue ({n} track{(n == 1 ? "" : "s")}) with “{entry.Name}”.",
                "Open");
            if (!ok) return;
        }

        try { await _main.LoadPlaylistAsync(entry.FullPath); }
        catch (Exception ex) { ErrorReporter.Report(ex, $"Opening playlist \"{entry.Name}\""); }
    }

    /// <summary>Context "Refresh": re-read the current view from disk — a NAS browser changes under you
    /// (tracks dropped, playlists added/removed). Reloads whatever is showing: a loaded playlist, a leaf
    /// folder's tracks, or the current folder's entries + files. Always available, so no menu is ever
    /// empty (Chloe 2026-07-22: a refresh beats suppressing the empty ".." menu).</summary>
    [RelayCommand]
    private void RefreshFolder()
    {
        if (CurrentPlaylist is { } pl) EnterPlaylist(pl);
        else if (CurrentLeafFolder is { } leaf) OpenLeafFolder(leaf);
        else if (CurrentFolder is { } folder) NavigateToFolder(folder, SelectedEntry?.FullPath);
    }

    /// <summary>Read an entry's audio tracks: a playlist's referenced audio (in playlist order), or a
    /// folder's own audio files (natural-sorted). Same sources the file pane lists — may throw (NAS),
    /// so callers run it off the UI thread and guard.</summary>
    private static List<string> ReadEntryTracks(FinderEntryViewModel entry) =>
        entry.IsPlaylist
            ? M3uPlaylist.ReadPaths(entry.FullPath).Where(AudioFileTypes.IsAudio).ToList()
            : Directory.EnumerateFiles(entry.FullPath).Where(AudioFileTypes.IsAudio)
                .OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();

    private void LoadFolderFiles(string folder)
    {
        _navCts?.Cancel();
        var cts = _navCts = new CancellationTokenSource();
        LoadError = null;
        _ = Task.Run(() =>
        {
            List<string> paths;
            try
            {
                // Per-folder guard: a flaky NAS share can throw on any enumeration call.
                paths = Directory.EnumerateFiles(folder)
                    .Where(AudioFileTypes.IsAudio)
                    .OrderBy(Path.GetFileName, NaturalComparer.Instance)
                    .ToList();
            }
            catch (Exception ex)
            {
                PostLoadError(cts.Token, $"Can't open this folder — {ex.Message}");
                return;
            }
            PopulateItems(paths, cts.Token);
        }, cts.Token);
    }

    private void LoadPlaylistFiles(string playlistPath)
    {
        _navCts?.Cancel();
        var cts = _navCts = new CancellationTokenSource();
        LoadError = null;
        _ = Task.Run(() =>
        {
            List<string> paths;
            try
            {
                // M3uPlaylist.ReadPaths never throws on a bad entry (it skips) — but a dead share
                // on File.ReadAllLines can. Guard, and keep only audio it references (defensive).
                paths = M3uPlaylist.ReadPaths(playlistPath).Where(AudioFileTypes.IsAudio).ToList();
            }
            catch (Exception ex)
            {
                PostLoadError(cts.Token, $"Can't read this playlist — {ex.Message}");
                return;
            }
            PopulateItems(paths, cts.Token); // playlist ORDER is meaningful — do not re-sort
        }, cts.Token);
    }

    private void PostLoadError(CancellationToken ct, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            _masterItems = [];
            Items.ReplaceAll([]);
            LoadError = message;
            OnPropertyChanged(nameof(ShowEmptyFolderHint));
            OnPropertyChanged(nameof(ShowNoMatches));
        });

    // ── Hydration progress (fetch-what-you-see) ──────────────────────────────────────────────────
    // Sorting needs EVERY row's data, so it stays locked until the whole folder is hydrated.
    private int _hydratedCount;   // rows read so far this load (mutated on the UI thread only)
    private int _hydrateTotal;    // rows in this load

    /// <summary>False while a folder is still filling; true once every row is hydrated (or the folder is
    /// empty). Gates column sorting — the finder header disables + tooltips while this is false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedWhileLoadingTip))]
    [NotifyPropertyChangedFor(nameof(LockedOpacity))]
    private bool _allHydrated = true;

    /// <summary>Tooltip for the controls locked while a folder is still loading — the sort header AND the
    /// FILTER tab, both of which need every row's data. Null (no tooltip) once hydrated.</summary>
    public string? LockedWhileLoadingTip => AllHydrated ? null : "Unlocks once every track has loaded.";

    /// <summary>Dim factor for the locked controls (sort header + FILTER tab) so the lock is visible
    /// without hovering. 1.0 when ready, dimmed while a folder is still loading.</summary>
    public double LockedOpacity => AllHydrated ? 1.0 : 0.45;

    /// <summary>Turn a list of file paths into rows. Rows appear instantly; metadata is fetched
    /// what-you-see — the visible viewport (HydrateVisible) and a parallel background fill race to read
    /// each row once. Shared by the folder and playlist loads.</summary>
    private void PopulateItems(List<string> paths, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // Order = the load order (natural / playlist), so a "default" (unsorted) column state can restore it.
        var items = paths.Select((p, i) => new FinderItemViewModel(p) { Order = i }).ToList();
        // Mouse likes on the heart go through the same deferred path as the L key.
        foreach (var item in items)
            item.Track.ToggleFavoriteCallback = (_, liked) => { QueueLike(item, liked); return Task.CompletedTask; };

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            _masterItems = items;
            _hydratedCount = 0;
            _hydrateTotal = items.Count;
            AllHydrated = items.Count == 0;   // empty folder → nothing to gate; else lock sorting until filled
            ApplyMembership();
            RecomputeRangeDomains(); // scale the FILTER ranges to this folder (redone once tags are in)
            RebuildView();           // load order while filling — SortList stays in load order until AllHydrated
        });

        // Fetch-what-you-see: rows hydrate on demand as they scroll into view (HydrateVisible, from the
        // window's ContainerPrepared) AND a PARALLEL background pass fills the rest — so scrolling anywhere
        // in a huge folder populates THOSE rows at once, while sort/filter still get every row's data. A
        // one-shot claim per row (FinderItemViewModel.TryClaimHydration) means each file is read exactly
        // once, whoever reaches it first. Degree = the shared "Analysis threads" setting; tag reads are
        // I/O-bound (NAS latency), so this is a safe cap. TagLibMetadataReader is stateless → safe in parallel.
        var cores = Math.Max(1, _main.AnalysisThreads);
        try
        {
            Parallel.ForEach(items,
                new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = ct },
                item => HydrateItem(item, ct));
        }
        catch (OperationCanceledException) { /* navigated away — a newer load owns the pane */ }
    }

    /// <summary>Called from the window when a file row scrolls INTO view — hydrate it now (priority), off
    /// the UI thread. The one-shot claim means the background fill simply skips a row the viewport took.</summary>
    public void HydrateVisible(FinderItemViewModel item)
    {
        var ct = _navCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested) return;
        _ = Task.Run(() => HydrateItem(item, ct));
    }

    /// <summary>Read ONE row's metadata (the stored v9 blob IS the analysis) and push it to the row — off
    /// the UI thread. Guarded by the one-shot claim so each file is read once. Counts toward
    /// <see cref="AllHydrated"/> exactly once (even on a read failure); the last row unlocks sorting and
    /// finalises the filter ranges.</summary>
    private void HydrateItem(FinderItemViewModel item, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (!item.TryClaimHydration()) return;   // the viewport or another worker already owns this row

        var tvm = item.Track;
        try
        {
            var md = _metadata.Read(item.FullPath);
            tvm.Model.Metadata = md;
            if (md.StoredAnalysis is { } stored)
            {
                // Trust the blob as-is, any version — re-analyse stays an explicit action.
                tvm.Model.Analysis = stored.Detected;
                tvm.Model.AnalysisStatus = AnalysisStatus.Done;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Finder] tag read failed: {item.FullPath}: {ex.GetType().Name}: {ex.Message}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            tvm.Refresh();
            if (ReferenceEquals(item, Selected)) RaiseDetail();

            // Counter runs on the UI thread only (all these posts serialize here) — plain ++ is safe.
            if (++_hydratedCount >= _hydrateTotal)
            {
                AllHydrated = true;      // unlock sorting
                RecomputeRangeDomains(); // real values are all in → finalise the FILTER ranges + re-apply
                RebuildView();
            }
        });
    }

    // ── Pane focus (drives the header highlight; set from the window's GotFocus) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldersActive))]
    [NotifyPropertyChangedFor(nameof(FilesActive))]
    private FinderPane _activePane = FinderPane.Folders;

    public bool FoldersActive => ActivePane == FinderPane.Folders;
    public bool FilesActive => ActivePane == FinderPane.Files;

    public void SetActivePane(FinderPane pane) => ActivePane = pane;

    // ── Breadcrumb (clickable path in the chrome) ────────────────────────────

    public ObservableCollection<BreadcrumbSegment> BreadcrumbSegments { get; } = [];

    private void RebuildBreadcrumb()
    {
        BreadcrumbSegments.Clear();
        if (LibraryRoot is not { } root || CurrentFolder is not { } folder) return;

        var sep = Path.DirectorySeparatorChar;
        var nr = NormalizePath(root);
        var nf = NormalizePath(folder);

        var built = new List<BreadcrumbSegment>();
        var rootName = Path.GetFileName(root.TrimEnd(sep, '/'));
        if (rootName.Length == 0) rootName = root;
        built.Add(new BreadcrumbSegment(rootName, root, IsPlaylist: false, IsLast: false));

        if (nf.Length > nr.Length && nf.StartsWith(nr, StringComparison.OrdinalIgnoreCase))
        {
            var acc = nr;
            foreach (var part in nf[nr.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc + sep + part;
                built.Add(new BreadcrumbSegment(part, acc, IsPlaylist: false, IsLast: false));
            }
        }

        if (CurrentPlaylist is { } pl)
            built.Add(new BreadcrumbSegment(Path.GetFileNameWithoutExtension(pl), null, IsPlaylist: true, IsLast: false));
        else if (CurrentLeafFolder is { } leaf)
            built.Add(new BreadcrumbSegment(Path.GetFileName(leaf.TrimEnd(sep, '/')), null, IsPlaylist: false, IsLast: false));

        for (var i = 0; i < built.Count; i++)
            BreadcrumbSegments.Add(built[i] with { IsLast = i == built.Count - 1 });
    }

    // ── Queue membership (the ✓ column) ──────────────────────────────────────

    /// <summary>Normalise a path for membership matching: absolute, single separator style, no
    /// trailing slash. Compared case-insensitively (Windows/NAS semantics).</summary>
    internal static string NormalizePath(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch { /* malformed path — normalise what we can */ }
        return path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private void RebuildMembership()
    {
        _queuePaths.Clear();
        foreach (var t in _main.Tracks)
            _queuePaths.Add(NormalizePath(t.Model.FilePath));
        ApplyMembership();
    }

    private void ApplyMembership()
    {
        // Flag the whole loaded set, not just the filtered view, so the ✓ stays correct when the filter
        // is later relaxed and a hidden row reappears.
        foreach (var item in _masterItems)
            item.IsOnPlaylist = _queuePaths.Contains(item.NormalizedPath);
    }

    private void AddToQueue(FinderItemViewModel item) => _ = AddToQueueGuarded(item.FullPath);

    private async Task AddToQueueGuarded(string path)
    {
        try { await _main.AddPathsAsync([path], preserveOrder: true); }
        catch (Exception ex) { ErrorReporter.Report(ex, $"Adding to the queue from the finder: {path}"); }
    }

    private async Task AddPathsGuarded(IReadOnlyList<string> paths)
    {
        try { await _main.AddPathsAsync(paths, preserveOrder: true); }
        catch (Exception ex) { ErrorReporter.Report(ex, $"Adding {paths.Count} track(s) to the queue from the finder"); }
    }

    private void AdvanceToNextTrackRow(FinderItemViewModel from)
    {
        var i = Items.IndexOf(from);
        if (i < 0 || i + 1 >= Items.Count) return;
        Selected = Items[i + 1]; // autoplay follows via the selection debounce (in play mode)
    }

    // ── Window lifecycle ─────────────────────────────────────────────────────

    /// <summary>Called by the window when it opens: start ticking and land in the library root
    /// (or show the setup hint until one is picked in the finder settings). The VM is a singleton,
    /// so on reopen CurrentFolder is already set and we stay where she left off.</summary>
    internal void OnWindowOpened()
    {
        _tick.Start();
        RebuildMembership();
        if (CurrentFolder is null && !HasNoRoot) NavigateToFolder(LibraryRoot);
        if (HasNoRoot) SettingsOpen = true; // first run: point straight at the library-root picker
    }

    /// <summary>Called by the window on close: the audition stops, the file handle is released,
    /// and every deferred like lands on disk.</summary>
    internal void OnWindowClosed()
    {
        _tick.Stop();
        _playDebounce.Stop();
        try
        {
            _preListen.Stop();
            _preListen.Unload();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Stopping the finder cue on close");
        }
        _loadedCuePath = null;
        if (_cuedItem is not null) { _cuedItem.IsCued = false; _cuedItem = null; }
        _waveformCts?.Cancel();
        WaveformPeaks = null;
        WaveformLoading = false;
        HasCue = false;
        NowPlayingText = "";
        NowPlayingTitle = "";
        NowPlayingArtist = "";
        PositionFraction = 0;
        PositionText = "–:––";
        DurationText = "–:––";
        FlushPendingLikes(); // nothing is held any more — everything can land
    }

    // ── Cue playback: Enter PLAYS the selected song, Space pauses/resumes ─────

    /// <summary>True while a cue is loaded but paused (Space) — drives the "⏸ paused" transport hint.
    /// Distinct from <see cref="IsCuePlaying"/> (the engine is actually sounding).</summary>
    [ObservableProperty]
    private bool _isCuePaused;

    /// <summary>Mirror the paused state onto the cued ROW so its status glyph flips ▶ ↔ ⏸.</summary>
    partial void OnIsCuePausedChanged(bool value)
    {
        if (_cuedItem is not null) _cuedItem.Paused = value;
    }

    /// <summary>Enter / double-click / (auto-play): play the SELECTED song on the cue device NOW — the
    /// primary, explicit play action, no debounce (Chloe: "Return ist der default zum Abspielen").</summary>
    public void PlaySelected()
    {
        if (Selected is { } item) LoadCue(item);
    }

    /// <summary>Space: the universal play/pause. Pauses the sounding audition, or resumes the loaded
    /// song / starts the selected one. Chloe 2026-07-06 confirmed Space stays "unser Space Ding".</summary>
    public void TogglePlayPause()
    {
        if (IsCuePlaying)
        {
            _playDebounce.Stop();
            try { _preListen.Pause(); }
            catch (Exception ex) { ErrorReporter.Report(ex, "Pausing the finder cue"); }
            IsCuePaused = true;
            return;
        }

        // Not sounding: resume the loaded song if it's still the selected one, else (re)start the selection.
        if (_loadedCuePath is not null && Selected is { } sel
            && string.Equals(sel.NormalizedPath, _loadedCuePath, StringComparison.OrdinalIgnoreCase))
        {
            try { _preListen.Play(); }
            catch (Exception ex) { ErrorReporter.Report(ex, "Resuming the finder cue"); }
            IsCuePaused = false;
        }
        else if (Selected is { } item)
        {
            LoadCue(item);
        }
    }

    [ObservableProperty]
    private bool _isCuePlaying;

    [ObservableProperty]
    private string _nowPlayingText = "";

    /// <summary>Cued track title / artist, split for the two-line player-bar readout (title white, artist
    /// accent — the FUVI layout).</summary>
    [ObservableProperty]
    private string _nowPlayingTitle = "";

    [ObservableProperty]
    private string _nowPlayingArtist = "";

    /// <summary>True once a track is loaded in the cue slot — gates the seek buttons + waveform.</summary>
    [ObservableProperty]
    private bool _hasCue;

    /// <summary>Normalised peak envelope of the cued track for the waveform scrubber (null = nothing loaded
    /// yet / still decoding). Computed off-thread on cue, cleared on unload. See <see cref="IWaveformService"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<float>? _waveformPeaks;

    /// <summary>The decode for the cued track is in flight — the bar shows a faint "reading…" placeholder.</summary>
    [ObservableProperty]
    private bool _waveformLoading;

    [ObservableProperty]
    private string _positionText = "–:––";

    [ObservableProperty]
    private string _durationText = "–:––";

    /// <summary>0..1 — drives both the transport progress and the waveform playhead / played-fill.</summary>
    [ObservableProperty]
    private double _positionFraction;

    /// <summary>Cancels an in-flight waveform decode when the cue changes (racing down a list must never
    /// leave a stale waveform painting for the wrong track).</summary>
    private CancellationTokenSource? _waveformCts;

    private void LoadCue(FinderItemViewModel item)
    {
        try
        {
            _preListen.Load(item.FullPath);
            _preListen.Play();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, $"Cueing in the finder: {item.FullPath}");
            return;
        }
        if (_cuedItem is not null) _cuedItem.IsCued = false;
        _cuedItem = item;
        item.IsCued = true;
        IsCuePaused = false;
        HasCue = true;
        _loadedCuePath = item.NormalizedPath;
        var tvm = item.Track;
        NowPlayingText = tvm.Artist == "—" ? tvm.Title : $"{tvm.Artist} — {tvm.Title}";
        NowPlayingTitle = tvm.Title;
        NowPlayingArtist = tvm.Artist == "—" ? "" : tvm.Artist;
        StartWaveform(item.FullPath);
        UpdatePosition();
        FlushPendingLikes(); // the previously-held file is free now
    }

    /// <summary>Decode the cued track's waveform off-thread (cancelling any previous one), then paint it.
    /// The strip shows a faint placeholder until the peaks land; a cancelled/failed decode leaves it empty
    /// (the waveform is cosmetic — never worth an error dialog). ComputeAsync already offloads, and this is
    /// called on the UI thread, so the await resumes on the UI thread to set the bound state.</summary>
    private void StartWaveform(string fullPath)
    {
        _waveformCts?.Cancel();
        var cts = _waveformCts = new CancellationTokenSource();
        WaveformPeaks = null;
        WaveformLoading = true;
        _ = ComputeWaveformAsync(fullPath, cts);
    }

    private async Task ComputeWaveformAsync(string fullPath, CancellationTokenSource cts)
    {
        try
        {
            var peaks = await _waveform.ComputeAsync(fullPath, WaveformBuckets, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            WaveformPeaks = peaks;
            WaveformLoading = false;
        }
        catch (OperationCanceledException) { /* cue moved on — the newer decode owns the strip */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[Finder] waveform decode failed: {fullPath}: {ex.GetType().Name}: {ex.Message}");
            if (!cts.Token.IsCancellationRequested) WaveformLoading = false;
        }
    }

    /// <summary>Waveform click / drag → jump the cue playhead to that fraction (0..1) of the track.</summary>
    [RelayCommand]
    private void SeekToFraction(double fraction)
    {
        if (_loadedCuePath is null) return;
        var dur = _preListen.Duration;
        if (dur <= TimeSpan.Zero) return;
        try { _preListen.Position = TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * dur.TotalSeconds); }
        catch (Exception ex) { ErrorReporter.Report(ex, "Seeking the finder cue from the waveform"); }
        UpdatePosition();
    }

    private void OnCueEnded()
    {
        // Song played out: in auto-play mode, if she's still ON that row, flow to the next track; in
        // manual mode (Enter-to-play) a finished song just stops — don't yank the selection around.
        IsCuePaused = false;
        if (AutoPlayOnSelect && _cuedItem is { } ended && ReferenceEquals(Selected, ended))
            AdvanceToNextTrackRow(ended);
    }

    public void SeekForward() => Seek(+1);
    public void SeekBack() => Seek(-1);

    private void Seek(int sign)
    {
        if (_loadedCuePath is null) return;
        _preListen.Position = PreCueTransport.ClampedJump(
            _preListen.Position, TimeSpan.FromSeconds(sign * SeekStepSeconds), _preListen.Duration);
        UpdatePosition();
    }

    private void OnTick()
    {
        if (_loadedCuePath is not null) UpdatePosition();

        // The auto-reconnect CLOU works here too: while the finder is open, poll for the
        // saved-by-name headphone device reappearing (~every 2 s at the 200 ms tick).
        if (++_pollCounter >= 10)
        {
            _pollCounter = 0;
            _main.PollPreCueDeviceRebind();
        }
    }

    private void UpdatePosition()
    {
        var pos = _preListen.Position;
        var dur = _preListen.Duration;
        PositionText = pos.ToString(@"m\:ss");
        DurationText = dur > TimeSpan.Zero ? dur.ToString(@"m\:ss") : "–:––";
        PositionFraction = dur > TimeSpan.Zero ? Math.Clamp(pos / dur, 0, 1) : 0;
    }

    // ── Like (deferred POPM writes) ──────────────────────────────────────────

    /// <summary>L key: toggle the selected track's heart. Goes through the row command so the
    /// optimistic flip + callback are identical to a mouse click on the heart.</summary>
    public void ToggleLikeSelected()
    {
        if (Selected is { Track: { } tvm })
            tvm.ToggleFavoriteCommand.Execute(null);
    }

    private void QueueLike(FinderItemViewModel item, bool liked)
    {
        _pendingLikes[item.NormalizedPath] = (item, liked); // last press wins
        FlushPendingLikes();
    }

    private void FlushPendingLikes()
    {
        foreach (var (path, pending) in _pendingLikes.ToList())
        {
            // Our own cue engine holds this file's handle — keep it deferred until track change/close.
            if (string.Equals(path, _loadedCuePath, StringComparison.OrdinalIgnoreCase)) continue;
            _pendingLikes.Remove(path);

            var mainRow = _main.Tracks.FirstOrDefault(
                t => string.Equals(NormalizePath(t.Model.FilePath), path, StringComparison.OrdinalIgnoreCase));
            if (mainRow is not null)
            {
                // The file may be the MAIN engine's playing track — route through the shell's
                // toggle path, which defers via PlaybackController.WithFileReleased and keeps
                // the queue row's heart in sync (reverting it if the write fails).
                mainRow.IsFavorite = pending.Liked; // instant heart sync in the queue
                _ = _main.ToggleFavoriteForTrack(mainRow, pending.Liked);
            }
            else
            {
                var item = pending.Item;
                var liked = pending.Liked;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _writer.Write(item.FullPath, new TagWrite { Favorite = liked });
                        item.Track.Model.Metadata = _metadata.Read(item.FullPath);
                        Dispatcher.UIThread.Post(() =>
                            item.Track.IsFavorite = item.Track.Model.Metadata?.IsFavorite ?? false);
                    }
                    catch (Exception ex)
                    {
                        _main.EventLog.Append($"Finder like write failed — \"{System.IO.Path.GetFileName(item.FullPath)}\": {ex.Message}");
                        // Revert the optimistic flip, same contract as the queue's toggle path.
                        Dispatcher.UIThread.Post(() => item.Track.IsFavorite = !liked);
                    }
                });
            }
        }
    }

    // ── Detail panel visibility (values bind through Selected.Track.*) ──────

    public bool ShowDetail => Selected is not null;
    public bool ShowDetailAnalysis => Selected is { Track.HasAnalysis: true };
    public bool ShowDetailUnanalyzed => Selected is { Track.HasAnalysis: false };

    private void RaiseDetail()
    {
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowDetailAnalysis));
        OnPropertyChanged(nameof(ShowDetailUnanalyzed));
    }

    // ── Help + settings overlays ─────────────────────────────────────────────

    [ObservableProperty]
    private bool _helpOpen;

    [RelayCommand]
    private void ToggleHelp() => HelpOpen = !HelpOpen;

    [ObservableProperty]
    private bool _settingsOpen;

    [RelayCommand]
    private void ToggleSettings() => SettingsOpen = !SettingsOpen;

    // ── Add-on settings (finder.settings.json) ───────────────────────────────

    /// <summary>The user's Music folder — the default library root when none has been picked yet.
    /// Chloe 2026-07-05: don't ship a hardcoded NAS path; land in the home Music folder.</summary>
    public static string DefaultLibraryRoot { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

    public string? LibraryRoot
    {
        // Fall back to the user's Music folder when nothing is configured, so the finder always
        // opens somewhere real instead of a blank pane.
        get => string.IsNullOrWhiteSpace(_settings.Current.LibraryRoot)
            ? DefaultLibraryRoot
            : _settings.Current.LibraryRoot;
        set
        {
            if (string.Equals(value, LibraryRoot, StringComparison.Ordinal)) return;
            _settings.Save(_settings.Current with { LibraryRoot = value });
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNoRoot));
            ResetToRoot();
        }
    }

    /// <summary>Reset navigation to the (new) library root — or clear everything if there's no root.</summary>
    private void ResetToRoot()
    {
        CurrentPlaylist = null;
        CurrentFolder = null;
        if (HasNoRoot)
        {
            FolderEntries.ReplaceAll([]);
            Items.ReplaceAll([]);
            BreadcrumbSegments.Clear();
            SelectedEntry = null;
            Selected = null;
        }
        else
        {
            NavigateToFolder(LibraryRoot);
        }
    }

    public int SeekStepSeconds => _settings.Current.SeekStepSeconds;

    public bool IsStep15 => SeekStepSeconds == 15;
    public bool IsStep20 => SeekStepSeconds == 20;
    public bool IsStep30 => SeekStepSeconds == 30;

    /// <summary>Footer legend fragment that tracks the configured step ("seek 30 s").</summary>
    public string SeekLegendText => $"seek {SeekStepSeconds} s";

    /// <summary>The footer legend entries for the shared <see cref="KeyLegend"/> bar. Rebuilt when the
    /// seek step changes (SetSeekStep) so the Shift ← → label tracks it.</summary>
    public IReadOnlyList<KeyHint> KeyHints =>
    [
        new("Enter", "play / open"),
        new("Space", "play / pause"),
        new("+ / A", "add to list"),
        new("Tab", "folders / files"),
        new("↑ ↓", "browse"),
        new("⌫", "up"),
        new("Shift ← →", SeekLegendText),
        new("L", "like"),
    ];

    // ── Auto-play on select (finder.settings.json) ───────────────────────────

    /// <summary>Off by default: Enter plays the selected song. On: settling on a row auto-auditions it
    /// after the debounce — the setting that "makes everyone happy" (Chloe 2026-07-06).</summary>
    public bool AutoPlayOnSelect
    {
        get => _settings.Current.AutoPlayOnSelect;
        set
        {
            if (value == _settings.Current.AutoPlayOnSelect) return;
            _settings.Save(_settings.Current with { AutoPlayOnSelect = value });
            OnPropertyChanged();
            if (!value) _playDebounce.Stop();
        }
    }

    // ── Column sorting — the STATE + the asc → desc → off cycle + the sort glyphs all live in the shared
    // Columns (TrackColumns); a header tap calls Columns.SortBy, which fires SortRequested → RebuildView
    // (wired in the constructor). The finder only owns the actual re-order (SortList, below).

    /// <summary>Reorder <see cref="Items"/> to the active sort (or back to load order). Same comparators
    /// as the JUST PLAY queue's <c>ApplySort</c> (NaturalComparer for text, Nullable.Compare for numbers).</summary>
    private void SortList(List<FinderItemViewModel> list)
    {
        if (list.Count < 2) return;

        // While a folder is still filling, stay in LOAD ORDER — sorting needs every row's data and is
        // locked in the UI until AllHydrated (SortDisabledTip / TrackDataHeader.CanSort). This also ignores
        // a sort column carried over from a previous folder until the new one has fully loaded.
        if (!AllHydrated || Columns.SortColumn is null)
        {
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return;
        }

        var d = Columns.SortDescending;
        list.Sort((a, b) =>
        {
            var (ta, tb) = (a.Track, b.Track);
            var c = Columns.SortColumn switch
            {
                "title"    => NaturalComparer.Instance.Compare(ta.Title, tb.Title),
                "artist"   => NaturalComparer.Instance.Compare(ta.Artist, tb.Artist),
                "genre"    => NaturalComparer.Instance.Compare(ta.GenreText, tb.GenreText),
                "key"      => NaturalComparer.Instance.Compare(ta.KeyText, tb.KeyText),
                "bpm"      => Nullable.Compare(ta.Bpm, tb.Bpm),
                "nrg"      => Nullable.Compare(ta.Energy, tb.Energy),
                "gain"     => Nullable.Compare(ta.ReplayGainDb, tb.ReplayGainDb),
                "lufs"     => Nullable.Compare(ta.LoudnessLufs, tb.LoudnessLufs),
                "comment"  => NaturalComparer.Instance.Compare(ta.CommentText, tb.CommentText),
                "duration" => Nullable.Compare(ta.Model.Metadata?.Duration, tb.Model.Metadata?.Duration),
                "like"     => ta.IsFavorite.CompareTo(tb.IsFavorite),
                "dark"     => ta.DarkScore.CompareTo(tb.DarkScore),
                "hypnotic" => ta.HypnoticScore.CompareTo(tb.HypnoticScore),
                "groove"   => ta.GrooveScore.CompareTo(tb.GrooveScore),
                "punch"    => ta.PunchScore.CompareTo(tb.PunchScore),
                "harsh"    => ta.HarshScore.CompareTo(tb.HarshScore),
                _          => 0,
            };
            return d ? -c : c;
        });
    }

    // ── Column visibility + sort state — the shared TrackColumns (right-click the header toggles a column
    // via Columns.ToggleColumnCommand → VisibilityChanged persists it; the TrackDataHeader + TrackRow bind
    // their cell IsVisible + sort glyphs straight to this). ONE definition, shared with the JUST PLAY queue.
    public TrackColumns Columns { get; }

    [RelayCommand]
    private void SetSeekStep(string? step)
    {
        if (!int.TryParse(step, out var s)) return;
        _settings.Save((_settings.Current with { SeekStepSeconds = s }).Normalized());
        OnPropertyChanged(nameof(SeekStepSeconds));
        OnPropertyChanged(nameof(IsStep15));
        OnPropertyChanged(nameof(IsStep20));
        OnPropertyChanged(nameof(IsStep30));
        OnPropertyChanged(nameof(SeekLegendText));
        OnPropertyChanged(nameof(KeyHints));
    }

    // ── FILTER tab — in-memory search/filter over the CURRENT file pane only (no library index) ───────
    // Shares the right pane with INFO via a header tab switch; everything narrows _masterItems live.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInfoContent))]
    private bool _filterTabActive;

    /// <summary>INFO content shows when the FILTER tab isn't active (the two share the right pane).</summary>
    public bool ShowInfoContent => !FilterTabActive;

    [RelayCommand] private void ShowInfoTab() => FilterTabActive = false;
    [RelayCommand] private void ShowFilterTab() { if (AllHydrated) FilterTabActive = true; } // locked until loaded

    /// <summary>Free-text search over title + artist (case-insensitive substring).</summary>
    [ObservableProperty] private string _filterName = "";
    partial void OnFilterNameChanged(string value) => RebuildView();

    // Camelot key selection (+ optional harmonic neighbours).
    private readonly HashSet<string> _selectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _effectiveKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bound to the KeyWheel — the codes she picked (strong highlight).</summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedKeyList = [];
    /// <summary>Bound to the KeyWheel — the extra codes a harmonic filter also lets through (soft glow).</summary>
    [ObservableProperty] private IReadOnlyList<string> _neighborKeyList = [];

    /// <summary>"+ harmonic": also match the neighbours (A/B flip + wheel ±1) of every picked key.</summary>
    [ObservableProperty] private bool _harmonicKeys;
    partial void OnHarmonicKeysChanged(bool value) { RecomputeKeys(); RebuildView(); }

    [RelayCommand]
    private void ToggleKey(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        if (!_selectedKeys.Remove(code)) _selectedKeys.Add(code);
        RecomputeKeys();
        RebuildView();
    }

    private void RecomputeKeys()
    {
        _effectiveKeys.Clear();
        foreach (var k in _selectedKeys) _effectiveKeys.Add(k);
        if (HarmonicKeys)
            foreach (var k in _selectedKeys)
                foreach (var nb in KeyNeighbors(k)) _effectiveKeys.Add(nb);

        SelectedKeyList = _selectedKeys.ToList();
        NeighborKeyList = _effectiveKeys.Where(k => !_selectedKeys.Contains(k)).ToList();
    }

    /// <summary>The Camelot codes a picked key mixes with: the A/B flip and the two wheel neighbours
    /// (±1, wrapping 12↔1) — the standard harmonic set.</summary>
    private static IEnumerable<string> KeyNeighbors(string code)
    {
        if (!TryParseCamelot(code, out var n, out var letter)) yield break;
        yield return $"{n}{(letter == 'A' ? 'B' : 'A')}";
        yield return $"{WrapWheel(n - 1)}{letter}";
        yield return $"{WrapWheel(n + 1)}{letter}";
    }

    private static int WrapWheel(int n) => ((n - 1 + 12) % 12) + 1;

    private static bool TryParseCamelot(string code, out int n, out char letter)
    {
        n = 0; letter = 'A';
        if (string.IsNullOrEmpty(code)) return false;
        var c = char.ToUpperInvariant(code[^1]);
        if (c is not ('A' or 'B')) return false;
        letter = c;
        return int.TryParse(code[..^1], out n) && n is >= 1 and <= 12;
    }

    /// <summary>"12 / 340" — matches / total in the current folder view.</summary>
    public string FilterMatchText => $"{Items.Count} / {_masterItems.Count}";

    /// <summary>Any filter is narrowing the view (drives the FILTER-tab dot + the Clear button).</summary>
    public bool HasActiveFilter =>
        FilterName.Length > 0 || _selectedKeys.Count > 0 || Ranges.Any(r => r.IsActive);

    [RelayCommand]
    private void ClearFilters()
    {
        FilterName = ""; // triggers RebuildView via OnFilterNameChanged
        _selectedKeys.Clear();
        RecomputeKeys();
        foreach (var r in Ranges) r.Reset();
        RebuildView();
    }

    // ── The view pipeline: Items = sort(filter(master)) ──────────────────────────────────────────────

    private bool PassesFilter(FinderItemViewModel item)
    {
        var t = item.Track;
        if (FilterName.Length > 0
            && t.Title.IndexOf(FilterName, StringComparison.OrdinalIgnoreCase) < 0
            && t.Artist.IndexOf(FilterName, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        foreach (var r in Ranges)
            if (!r.Passes(t)) return false;

        if (_effectiveKeys.Count > 0)
        {
            var k = t.KeyText;
            if (string.IsNullOrEmpty(k) || !_effectiveKeys.Contains(k)) return false;
        }
        return true;
    }

    /// <summary>Rebuild <see cref="Items"/> from the master list through the active filter + sort, keeping
    /// the cursor on the same row when it survives. The single funnel every filter/sort change flows through.</summary>
    private void RebuildView()
    {
        // Filtering needs every row's data too — while a folder is still filling, show ALL rows (the
        // FILTER tab is disabled meanwhile); once AllHydrated, apply the filter. Mirrors the SortList gate.
        var filtered = AllHydrated ? _masterItems.Where(PassesFilter).ToList() : _masterItems.ToList();
        SortList(filtered);

        // Detach the selection BEFORE the Reset. A ListBox in SelectionMode=Multiple with a bound
        // SelectedItem crashes inside Avalonia's SelectionModel when the source collection resets while a
        // selection is live — it enumerates SelectedItems against now-stale indices (the crash report from
        // clicking a key on the wheel). Clearing against the still-valid OLD collection, then reselecting
        // in the NEW one, sidesteps it. Multi-selection collapses to the cursor on any filter/sort — fine,
        // the old selection was over the old view anyway. Chloe 2026-07-07.
        var keep = Selected;
        Selected = null;
        ClearFileSelection?.Invoke(); // clear the ListBox's FULL selection (multi included) before the Reset
        Items.ReplaceAll(filtered);
        Selected = keep is not null && filtered.Contains(keep) ? keep : filtered.FirstOrDefault();

        OnPropertyChanged(nameof(FilterMatchText));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ShowEmptyFolderHint));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    /// <summary>Auto-scale every range's domain to the values present in the current view and reset its
    /// handles to full span. Called on folder load (see the tag-load note in PopulateItems).</summary>
    private void RecomputeRangeDomains()
    {
        const int Bins = 40;
        var vals = new List<double>(_masterItems.Count);
        foreach (var r in Ranges)
        {
            vals.Clear();
            double min = double.MaxValue, max = double.MinValue;
            foreach (var it in _masterItems)
                if (r.ValueOf(it.Track) is { } x)
                {
                    vals.Add(x);
                    if (x < min) min = x;
                    if (x > max) max = x;
                }

            if (min > max) { r.SetDomain(0, 0); continue; } // no values → collapsed → the row hides itself

            // Bucket the values into a normalised distribution (peak bucket = 1) the slider paints behind
            // its track — "see the shape of the crate, then dial into the dense part" (Chloe 2026-07-07).
            double[]? hist = null;
            if (max > min)
            {
                hist = new double[Bins];
                var span = max - min;
                foreach (var x in vals)
                    hist[Math.Clamp((int)((x - min) / span * (Bins - 1)), 0, Bins - 1)]++;
                var peak = 0.0;
                foreach (var c in hist) if (c > peak) peak = c;
                if (peak > 0) for (var i = 0; i < Bins; i++) hist[i] /= peak;
            }
            r.SetDomain(min, max, hist);
        }
    }
}
