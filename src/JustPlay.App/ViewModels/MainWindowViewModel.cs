using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
using JustPlay.Core.Playlists;
using JustPlay.Core.Theming;
using JustPlay.Metadata;
using BroadcastState = JustPlay.Core.Abstractions.BroadcastState;

namespace JustPlay.App.ViewModels;

/// <summary>One of the three analysis fields a per-cell action targets.</summary>
public enum AnalysisField { Bpm, Key, Energy }

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif"
    };

    private readonly PlaybackController _controller;
    private readonly IAudioEngine _engine;
    private readonly IPreListenEngine _preListen;
    private readonly IMetadataReader _metadata;
    private readonly IMetadataWriter _writer;
    private readonly ITrackAnalysisService _analysis;
    private readonly ISettingsService _settings;

    /// <summary>Title-bar update badge state + background polling. Bound by MaxView/MiniView.</summary>
    public UpdateViewModel Update { get; }
    private readonly IThemeService _themes;
    private readonly IBroadcastService _broadcast;
    private readonly DispatcherTimer _timer;
    private bool _suppressSeek;

    // ── Pre-cue seek reconciliation (mirrors main engine's _pendingSeek/_pendingSeekTicks) ───────
    private bool _suppressPreCueSeek;
    private double? _pendingPreCueSeek;
    private int _pendingPreCueSeekTicks;

    // ── Pre-cue v2: saved-by-NAME headphone device (auto-reconnect "CLOU") ─────────────────────
    // Independent of the live SelectedHeadphoneDevice binding, which drops to null the moment the
    // device disappears (e.g. Chloe's Bluetooth headphones going idle) — if we persisted THAT, the
    // saved name would be wiped the instant the device vanished, defeating the whole point of
    // remembering it. This field is set only on an explicit/successful bind (never cleared to null
    // just because the live selection drops out) and is what PollPreCueDeviceRebind + PersistSettings
    // read from. See PreCueTransport.TryAutoRebind (JustPlay.Core.Playback) for the match logic.
    private string? _savedHeadphoneDeviceName;

    // User-seek reconciliation: when the user scrubs, the native engine takes a moment to
    // actually report the new position. Until it lands near the target, the timer must NOT
    // write the stale pre-seek position back to the slider (that's the "jumps back, then
    // forward" flicker). _pendingSeek holds the target; _pendingSeekTicks bounds the wait so
    // a never-reached target can't freeze the thumb forever.
    private double? _pendingSeek;
    private int _pendingSeekTicks;

    // ── Shuffle state ────────────────────────────────────────────────────
    // A "bag shuffle" with history: every track plays once per cycle (no
    // immediate repeats), and Previous walks back through the order we
    // actually played. _shuffleHistory is the ordered list of tracks visited
    // in the current cycle; _shufflePos points at the current one. Walking
    // forward past the frontier (_shufflePos == last) generates a fresh pick
    // from the not-yet-played pool. This is the platform-agnostic complement
    // to the linear Step path below — both feed PlayInternal.
    private readonly List<TrackViewModel> _shuffleHistory = [];
    private int _shufflePos = -1;
    private readonly Random _rng = new();

    // Monotonic insertion sequence stamped on each added track, so "unsort" (third header click)
    // can restore the original natural/Explorer drop order.
    private int _addSeq;

    // Previous restarts the current track instead of stepping back when more
    // than this far into it — direct port of the design (app.jsx:492,
    // `if (progress > 3) setProgress(0)`).
    private static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

    // Suppress persistence while the constructor seeds [ObservableProperty]
    // backing fields from the loaded settings. Without this guard, the very
    // first assignment of each tweak property would trigger an On…Changed
    // partial that writes the just-loaded value straight back to disk — an
    // unnecessary save on every cold start.
    private bool _settingsHydrated;

    public MainWindowViewModel(
        PlaybackController controller,
        IAudioEngine engine,
        IPreListenEngine preListen,
        IMetadataReader metadata,
        IMetadataWriter writer,
        ITrackAnalysisService analysis,
        ISettingsService settings,
        IThemeService themes,
        IBroadcastService broadcast,
        UpdateViewModel update)
    {
        _controller = controller;
        _engine = engine;
        _preListen = preListen;
        _metadata = metadata;
        _writer = writer;
        _analysis = analysis;
        _settings = settings;
        _themes = themes;
        _broadcast = broadcast;
        Update = update;

        // Subscribe to broadcast state changes; marshal to UI thread.
        _broadcast.StateChanged += OnBroadcastServiceStateChanged;

        // Bulk-action menu headers carry the selection count, e.g. "Write meta tags (12)".
        SelectedTracks.CollectionChanged += (_, _) => RaiseSelectionHeaders();

        // Keep HasNoStreamServers in sync with the collection.
        StreamServers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoStreamServers));

        // Seed the tweak properties from persisted settings BEFORE wiring
        // any change-listeners — so the seeding itself does not echo back to
        // disk via On…Changed → Save.
        var s = _settings.Current;
        _currentTheme = s.Theme;
        _vinylSpinEnabled = s.VinylSpinEnabled;
        _waveformEnabled = s.WaveformEnabled;
        _autoAnalyze = s.AutoAnalyze;
        _autoWriteOnAnalyze = s.AutoWriteOnAnalyze;
        _writeDjComment = s.WriteDjComment;
        _playbackNormalization = s.PlaybackNormalization;
        _normalizationLevel = s.NormalizationLevel;
        _controller.NormalizationEnabled = s.PlaybackNormalization;
        _controller.TargetLufs = LevelToLufs(s.NormalizationLevel);
        _crossfadeSeconds = s.CrossfadeSeconds;
        _limiterMode = s.LimiterMode;
        ApplyLimiterToEngine();   // engine exists at construction; this is silent (no persist)
        _eqLow = s.EqLowGain; _eqMid = s.EqMidGain; _eqHigh = s.EqHighGain;
        ApplyEqualizerToEngine();
        _transientPunch = s.TransientPunch;
        _autoTiltStrength = s.AutoTiltStrength;
        ApplyReviveToEngine();
        _analysisThreads = s.AnalysisThreads;

        // Seed column views from persisted settings (before _settingsHydrated — mirroring the tweak seed block).
        _viewA = new HashSet<string>(s.ColumnViewA);
        _viewB = new HashSet<string>(s.ColumnViewB);
        _viewC = new HashSet<string>(s.ColumnViewC);
        _activeColumnView = s.ActiveColumnView is "A" or "B" or "C" ? s.ActiveColumnView : "A";

        // Seed streaming profiles from persisted settings.
        foreach (var p in s.StreamServers)
            StreamServers.Add(p);
        if (s.SelectedStreamServerId is { } selectedId)
            _selectedStreamServer = StreamServers.FirstOrDefault(p => p.Id == selectedId);

        // Seed Sound presets from persisted settings (before _settingsHydrated so the adds don't echo
        // to disk). On a fresh install (never seeded) prepend the two built-in starting points Hard +
        // Neutral as ORDINARY, fully editable/deletable presets — seeded exactly once
        // (SoundPresetsSeeded), so deleting them does NOT bring them back. HasSoundPresets stays in
        // sync as the collection changes.
        var seedDefaults = !s.SoundPresetsSeeded;
        if (seedDefaults)
        {
            SoundPresets.Add(DspPreset.Hard);
            SoundPresets.Add(DspPreset.Neutral);
        }
        foreach (var p in s.SoundPresets)
            SoundPresets.Add(p);
        SoundPresets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSoundPresets));

        // ── Output device: populate list, select saved device (or default) ────
        // Enumerate devices BEFORE setting _settingsHydrated so the initial
        // SelectedOutputDevice assignment doesn't trigger a premature PersistSettings.
        PopulateOutputDevices(s.OutputDeviceName);

        // ── Pre-listen (PFL): seed volume + headphone device list ────────────
        // Seeded before _settingsHydrated so the initial assignments don't echo to disk.
        _preCueVolume = s.PreCueVolume;
        PopulateHeadphoneDevices(s.HeadphoneDeviceName);

        _settingsHydrated = true;

        // Persist the one-time built-in seed now that the whole VM is hydrated (so the write captures
        // complete state). Flips SoundPresetsSeeded true so the defaults are never re-seeded.
        if (seedDefaults) PersistSettings();

        _controller.StateChanged += OnEngineStateChanged;
        _controller.TrackEnded += OnTrackEnded;
        _preListen.StateChanged += OnPreCueStateChanged;
        _preListen.PlaybackEnded += OnPreCueEnded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public BulkObservableCollection<TrackViewModel> Tracks { get; } = [];

    /// <summary>The rows currently selected in the queue (multi-select). Kept in sync from the
    /// view's ListBox SelectionChanged; the tag-write / re-analyze / remove actions operate on it.</summary>
    public ObservableCollection<TrackViewModel> SelectedTracks { get; } = [];

    // Bulk context-menu headers — show "(N)" when more than one row is selected.
    public string WriteTagsHeader => WithCount("Write meta tags");
    public string FillMissingHeader => WithCount("Fill missing tags");
    // "Analyze" until the selection has been analysed at least once, then "Re-analyze".
    public string ReanalyzeHeader =>
        WithCount(SelectedTracks.Count > 0 && SelectedTracks.All(t => t.HasAnalysis) ? "Re-analyze" : "Analyze");
    public string RemoveHeader => WithCount("Remove from list");
    public bool HasSelection => SelectedTracks.Count > 0;

    /// <summary>"Write meta tags" has an effect only if some selected track has a detected value to
    /// write — otherwise the item is greyed out (analyse first).</summary>
    public bool CanWriteTags => SelectedTracks.Any(t => t.HasWritableAnalysis);

    /// <summary>"Fill missing tags" has an effect only if some selected track has a detected value for
    /// a tag it is currently missing.</summary>
    public bool CanFillMissing => SelectedTracks.Any(t => t.HasMissingTagToFill);

    /// <summary>Re-evaluate the bulk-menu enable/header state — called as the context menu opens so the
    /// flags reflect analysis that finished after the selection was made.</summary>
    public void RefreshMenuState()
    {
        OnPropertyChanged(nameof(CanWriteTags));
        OnPropertyChanged(nameof(CanFillMissing));
        OnPropertyChanged(nameof(ReanalyzeHeader));
    }

    // ── Undo (last tag write) ─────────────────────────────────────────────
    // Snapshot of every touched file's pre-write tag state, captured per user write action so
    // the whole batch — including a bulk "Write meta tags" — reverts in one go. Replaced on each
    // new write, cleared after an undo. See WithUndoCapture / UndoLastWrite.
    private readonly List<(TrackViewModel tvm, TagRestore prev)> _undoBatch = [];
    private bool _capturingUndo;

    public bool CanUndo => _undoBatch.Count > 0;
    public string UndoHeader => _undoBatch.Count > 1 ? $"Undo last write ({_undoBatch.Count})" : "Undo last write";

    /// <summary>The row a context menu is currently anchored on, but ONLY when exactly one row is
    /// selected — null under multi-selection. Per-field (single-cell) menu entries bind to it, so
    /// they vanish for multi-select where a single-field write would be ambiguous.</summary>
    [ObservableProperty] private TrackViewModel? _contextTarget;

    /// <summary>Which cell the right-click landed on ("Bpm"/"Key"/"Energy"), or null when the click
    /// was elsewhere in the row. Set from the clicked cell's Tag — this is what makes the field
    /// entries truly cell-targeted: only the right-clicked field's actions show.</summary>
    [ObservableProperty] private string? _contextField;

    // Per-field menu-item visibility = right cell was clicked AND that field is divergent (or has a
    // stashed original to restore). Recomputed whenever the target row or clicked cell changes.
    public bool ShowBpmField => ContextField == "Bpm" && (ContextTarget?.BpmConflict ?? false);
    public bool ShowKeyField => ContextField == "Key" && (ContextTarget?.KeyConflict ?? false);
    public bool ShowEnergyField => ContextField == "Energy" && (ContextTarget?.EnergyConflict ?? false);
    public bool ShowRestoreBpm => ContextField == "Bpm" && (ContextTarget?.CanRestoreBpm ?? false);
    public bool ShowRestoreKey => ContextField == "Key" && (ContextTarget?.CanRestoreKey ?? false);
    public bool ShowRestoreEnergy => ContextField == "Energy" && (ContextTarget?.CanRestoreEnergy ?? false);
    public bool ShowFieldSeparator =>
        ShowBpmField || ShowKeyField || ShowEnergyField || ShowRestoreBpm || ShowRestoreKey || ShowRestoreEnergy;

    partial void OnContextTargetChanged(TrackViewModel? value) => RaiseFieldVisibility();
    partial void OnContextFieldChanged(string? value) => RaiseFieldVisibility();

    private void RaiseFieldVisibility()
    {
        OnPropertyChanged(nameof(ShowBpmField));
        OnPropertyChanged(nameof(ShowKeyField));
        OnPropertyChanged(nameof(ShowEnergyField));
        OnPropertyChanged(nameof(ShowRestoreBpm));
        OnPropertyChanged(nameof(ShowRestoreKey));
        OnPropertyChanged(nameof(ShowRestoreEnergy));
        OnPropertyChanged(nameof(ShowFieldSeparator));
    }

    private string WithCount(string verb) =>
        SelectedTracks.Count > 1 ? $"{verb} ({SelectedTracks.Count})" : verb;

    // ── Column views (A | B | C) ──────────────────────────────────────────────────
    // Three named sets of visible optional columns. A = Mix (key/nrg), B = Levels (gain/lufs),
    // C = Minimal (bpm + duration). The active set determines which columns render; switching
    // views is instant. Each set is a HashSet so contains-check is O(1).
    // Structural columns (# and UP NEXT) are always visible and not part of these sets.
    private HashSet<string> _viewA = [];
    private HashSet<string> _viewB = [];
    private HashSet<string> _viewC = [];

    private HashSet<string> ActiveSet => ActiveColumnView switch { "B" => _viewB, "C" => _viewC, _ => _viewA };

    [ObservableProperty] private string _activeColumnView = "A";

    public bool IsViewA => ActiveColumnView == "A";
    public bool IsViewB => ActiveColumnView == "B";
    public bool IsViewC => ActiveColumnView == "C";

    public bool ShowGenre    => ActiveSet.Contains("genre");
    public bool ShowBpm      => ActiveSet.Contains("bpm");
    public bool ShowKey      => ActiveSet.Contains("key");
    public bool ShowNrg      => ActiveSet.Contains("nrg");
    public bool ShowGain     => ActiveSet.Contains("gain");
    public bool ShowLufs     => ActiveSet.Contains("lufs");
    public bool ShowDuration => ActiveSet.Contains("duration");
    public bool ShowLike     => ActiveSet.Contains("like");

    partial void OnActiveColumnViewChanged(string value)
    {
        RaiseColumnViewProps();
        if (_settingsHydrated) PersistSettings();
    }

    private void RaiseColumnViewProps()
    {
        OnPropertyChanged(nameof(IsViewA));
        OnPropertyChanged(nameof(IsViewB));
        OnPropertyChanged(nameof(IsViewC));
        OnPropertyChanged(nameof(ShowGenre));
        OnPropertyChanged(nameof(ShowBpm));
        OnPropertyChanged(nameof(ShowKey));
        OnPropertyChanged(nameof(ShowNrg));
        OnPropertyChanged(nameof(ShowGain));
        OnPropertyChanged(nameof(ShowLufs));
        OnPropertyChanged(nameof(ShowDuration));
        OnPropertyChanged(nameof(ShowLike));
    }

    /// <summary>Switch to a named column view. CommandParameter is "A", "B", or "C".</summary>
    [RelayCommand]
    private void SetColumnView(string which) => ActiveColumnView = which;

    /// <summary>Toggle a single column id in the active view's enabled set.</summary>
    [RelayCommand]
    private void ToggleColumn(string id)
    {
        var set = ActiveSet;
        if (!set.Remove(id)) set.Add(id);
        RaiseColumnViewProps();
        if (_settingsHydrated) PersistSettings();
    }

    // ── Column sorting (click a header to sort; click again to flip direction) ──
    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;

    // Per-column sort arrow (its own small TextBlock in the header, so it can be sized/spaced
    // independently of the label). Empty unless that column is the active sort.
    private string Glyph(string col) => SortColumn == col ? (SortDescending ? "▼" : "▲") : "";
    public string TitleSortGlyph => Glyph("Title");
    public string GenreSortGlyph => Glyph("Genre");
    public string BpmSortGlyph => Glyph("Bpm");
    public string KeySortGlyph => Glyph("Key");
    public string NrgSortGlyph => Glyph("Nrg");
    public string GainSortGlyph => Glyph("Gain");
    public string LufsSortGlyph => Glyph("Lufs");
    public string DurationSortGlyph => Glyph("Duration");
    public string LikeSortGlyph => Glyph("Like");

    partial void OnSortColumnChanged(string? value) => RaiseSortHeaders();
    partial void OnSortDescendingChanged(bool value) => RaiseSortHeaders();

    private void RaiseSortHeaders()
    {
        OnPropertyChanged(nameof(TitleSortGlyph));
        OnPropertyChanged(nameof(GenreSortGlyph));
        OnPropertyChanged(nameof(BpmSortGlyph));
        OnPropertyChanged(nameof(KeySortGlyph));
        OnPropertyChanged(nameof(NrgSortGlyph));
        OnPropertyChanged(nameof(GainSortGlyph));
        OnPropertyChanged(nameof(LufsSortGlyph));
        OnPropertyChanged(nameof(DurationSortGlyph));
        OnPropertyChanged(nameof(LikeSortGlyph));
    }

    /// <summary>Sort the queue by a column (toggles asc/desc when the same column is clicked again).
    /// Invoked from the clickable table headers.</summary>
    public void SortByColumn(string? column)
    {
        if (string.IsNullOrEmpty(column)) return;
        if (SortColumn == column)
        {
            // Same column: ascending → descending → off (back to the drop order).
            if (!SortDescending) SortDescending = true;
            else { SortColumn = null; SortDescending = false; }
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        ApplySort();
        MarkPlaylistEdited();
    }

    private void ApplySort()
    {
        if (Tracks.Count < 2) return;
        var d = SortDescending;
        var snap = Tracks.ToList();

        if (SortColumn is null)
        {
            // Unsorted: restore the original natural/Explorer drop order.
            snap.Sort((a, b) => a.AddOrder.CompareTo(b.AddOrder));
        }
        else
        {
            snap.Sort((a, b) =>
            {
                var c = SortColumn switch
                {
                    "Title"    => NaturalComparer.Instance.Compare(a.Title, b.Title),
                    "Genre"    => NaturalComparer.Instance.Compare(a.Model.Metadata?.Genre ?? "", b.Model.Metadata?.Genre ?? ""),
                    "Key"      => NaturalComparer.Instance.Compare(a.KeyText, b.KeyText),
                    "Bpm"      => Nullable.Compare(a.Bpm, b.Bpm),
                    "Nrg"      => Nullable.Compare(a.Energy, b.Energy),
                    "Gain"     => Nullable.Compare(a.ReplayGainDb, b.ReplayGainDb),
                    "Lufs"     => Nullable.Compare(a.LoudnessLufs, b.LoudnessLufs),
                    "Duration" => Nullable.Compare(a.Model.Metadata?.Duration, b.Model.Metadata?.Duration),
                    "Like"     => a.IsFavorite.CompareTo(b.IsFavorite),
                    _          => 0,
                };
                return d ? -c : c;
            });
        }

        Tracks.Clear();
        foreach (var t in snap) Tracks.Add(t);
        RecalcIndexes();
    }

    private void RaiseSelectionHeaders()
    {
        OnPropertyChanged(nameof(WriteTagsHeader));
        OnPropertyChanged(nameof(FillMissingHeader));
        OnPropertyChanged(nameof(ReanalyzeHeader));
        OnPropertyChanged(nameof(RemoveHeader));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanWriteTags));
        OnPropertyChanged(nameof(CanFillMissing));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBpmRange))]
    private TrackViewModel? _current;
    [ObservableProperty] private bool _isMini;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;

    [ObservableProperty] private double _volume = 1.0;

    // ── Transport mode (shuffle / repeat) ────────────────────────────────
    // Not persisted — JustPlay is "no memory, just play". Each session starts
    // shuffle-off, repeat-off (matches the design's useState defaults).
    [ObservableProperty] private bool _shuffle;

    [ObservableProperty] private RepeatMode _repeat;

    /// <summary>Repeat is engaged (All or One) — drives the button's accent highlight.</summary>
    public bool RepeatActive => Repeat != RepeatMode.Off;

    /// <summary>Repeat-one specifically — drives the little "1" badge on the repeat button.</summary>
    public bool RepeatOne => Repeat == RepeatMode.One;

    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatActive));
        OnPropertyChanged(nameof(RepeatOne));
    }

    partial void OnShuffleChanged(bool value)
    {
        // Toggling shuffle starts a fresh cycle anchored on whatever is playing
        // (so we don't immediately replay the current track), or clears the
        // history when switching back to linear.
        if (value) ResetShuffleFrom(Current);
        else { _shuffleHistory.Clear(); _shufflePos = -1; }
    }

    // ── Consume mode ("remove played tracks") ────────────────────────────
    // Momentary, like shuffle/repeat — deliberately NOT persisted, so every
    // cold start begins with it OFF. When on, a track is dropped from the queue
    // the moment we advance forward off it (natural end OR manual skip-forward),
    // but never when we step back or replay. This is mpd's "consume mode":
    // gespielt = weg, the list shrinks to what's still ahead. Toggled from the
    // "…" list menu, not the Tweaks panel — it's an in-the-moment mode, not a
    // saved preference.
    [ObservableProperty] private bool _removePlayed;

    // ── Streaming-panel state (S2: live BroadcastState + toggle command) ─
    [ObservableProperty] private bool _isStreamingOpen;

    /// <summary>
    /// Current broadcast lifecycle state — subscribed from IBroadcastService.StateChanged
    /// and always updated on the UI thread. Bindings in StreamingPanel drive the dot colour
    /// and connect-button label via the computed helper properties below.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonLabel))]
    [NotifyPropertyChangedFor(nameof(BroadcastStatusText))]
    [NotifyPropertyChangedFor(nameof(BroadcastDotColor))]
    [NotifyPropertyChangedFor(nameof(IsConnectedOrConnecting))]
    private BroadcastState _broadcastState = BroadcastState.Disconnected;

    /// <summary>Label for the connect/disconnect toggle button — reflects the current state.</summary>
    public string ConnectButtonLabel => BroadcastState switch
    {
        BroadcastState.Connected    => "Disconnect",
        BroadcastState.Connecting   => "Connecting…",
        BroadcastState.Reconnecting => "Reconnecting…",
        BroadcastState.Error        => "Retry",
        _                           => "Connect",
    };

    /// <summary>Human-readable one-line status string shown below the button.</summary>
    public string BroadcastStatusText => BroadcastState switch
    {
        BroadcastState.Connected    => $"Streaming to {SelectedStreamServer?.Host ?? "server"}",
        BroadcastState.Connecting   => "Connecting…",
        BroadcastState.Reconnecting => "Reconnecting…",
        // Surface the real BASS reason (host/cred/mount/encoder) instead of a generic message.
        BroadcastState.Error        => string.IsNullOrEmpty(_broadcast.LastError)
                                           ? "Error — check host/credentials"
                                           : $"Error — {_broadcast.LastError}",
        _                           => "Disconnected",
    };

    /// <summary>
    /// Status dot fill colour as a hex string — green/amber/red/grey.
    /// Used in StreamingPanel's Ellipse Fill binding with a no-op string converter.
    /// Kept as a plain string to stay compiled-binding-safe without a custom converter.
    /// </summary>
    public string BroadcastDotColor => BroadcastState switch
    {
        BroadcastState.Connected    => "#FF4ADE80",  // green
        BroadcastState.Connecting   => "#FFFBBF24",  // amber
        BroadcastState.Reconnecting => "#FFFBBF24",  // amber
        BroadcastState.Error        => "#FFF87171",  // red
        _                           => "#4DFFFFFF",  // grey (disconnected)
    };

    /// <summary>True while connected or connecting — disables the button mid-connect to avoid double-clicks.</summary>
    public bool IsConnectedOrConnecting =>
        BroadcastState == BroadcastState.Connected ||
        BroadcastState == BroadcastState.Connecting ||
        BroadcastState == BroadcastState.Reconnecting;

    private void OnBroadcastServiceStateChanged(object? sender, BroadcastState state)
        => Dispatcher.UIThread.Post(() => BroadcastState = state);

    // ── Tweaks-panel state (mirrors TWEAK_DEFAULTS in the design's app.jsx) ─
    [ObservableProperty] private bool _isTweaksOpen;
    [ObservableProperty] private string _currentTheme = "Aurora";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShouldSpin))]
    private bool _vinylSpinEnabled = true;
    [ObservableProperty] private bool _waveformEnabled = true;

    // ── Analysis preferences (Tweaks) ─────────────────────────────────────
    // AutoAnalyze off by default: adding tracks never auto-runs the CPU-heavy DSP — the user
    // triggers it via right-click → Analyze. AnalysisThreads bounds how many run at once.
    [ObservableProperty] private bool _autoAnalyze;
    [ObservableProperty] private bool _autoWriteOnAnalyze;
    [ObservableProperty] private bool _writeDjComment;

    // Playback loudness normalization (Tweaks). Applies each track's ReplayGain on output so the
    // queue plays at an even level (non-destructive, clip-protected). Off by default.
    [ObservableProperty] private bool _playbackNormalization;

    // Loudness target for normalization: "Quiet" −19 / "Normal" −14 / "Loud" −11 (streaming-style).
    [ObservableProperty] private string _normalizationLevel = "Normal";

    // Crossfade length in seconds for auto-advance (0 = off / hard cut). Allowed: 0, 2, 4, 8.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossfadeSecondsText))]
    private int _crossfadeSeconds;

    // Set true once we've fired the crossfade for the current track, so the 200ms tick fires it once.
    private bool _crossfadeArmed;

    // Master true-peak limiter / maximizer on the output bus: "Off"/"Soft"/"Club"/"Loud".
    // Off by default. Maps to (enabled, driveDb) via ApplyLimiterToEngine; ceiling fixed at −1 dBTP.
    [ObservableProperty] private string _limiterMode = "Off";

    // 3-band DJ EQ band gains (LINEAR): 1.0 = unity/flat, 0.0 = kill, 2.0 = +6 dB. The Tweaks sliders
    // bind here (range 0..2, centre detent at 1). All-flat → the engine bypasses the EQ DSP.
    [ObservableProperty] private double _eqLow  = 1.0;
    [ObservableProperty] private double _eqMid  = 1.0;
    [ObservableProperty] private double _eqHigh = 1.0;

    // "Revive" rack — anti-flat enhancement on the output bus. All neutral by default (each block
    // bypasses at its zero value, 0 = off). Sliders bind here; each shares its own apply path
    // (engine + persist) like the EQ bands.
    [ObservableProperty] private double _transientPunch  = 0.0;   // 0..1
    [ObservableProperty] private double _autoTiltStrength = 0.0;  // 0..1

    // User-saved Sound-tab bus presets (name + the six tone fields). Additive on top of the
    // built-in Hard / Neutral one-click presets. Seeded from settings in the constructor and
    // round-tripped through PersistSettings. Bound by the TweaksPanel "Preset" row.
    /// <summary>User-saved Sound presets. Bound to the Tweaks Sound-tab preset row.</summary>
    public ObservableCollection<DspPreset> SoundPresets { get; } = [];

    /// <summary>True when at least one user preset exists — drives the "Saved" sub-label visibility.</summary>
    public bool HasSoundPresets => SoundPresets.Count > 0;

    // Which Tweaks tab is showing: "Look" / "Sound" / "Library" / "Audio". Transient UI state,
    // deliberately NOT persisted — it just drives which group of controls is visible in the panel.
    [ObservableProperty] private string _tweaksTab = "Sound";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnalysisThreadsText))]
    private int _analysisThreads = 4;

    /// <summary>String form of the thread count, so the Tweaks radio buttons can drive their
    /// active state via the existing StringEquals converter.</summary>
    public string AnalysisThreadsText => AnalysisThreads.ToString();

    /// <summary>String form of the crossfade length in seconds (e.g. "0", "2", "4", "8") so the Tweaks
    /// segmented buttons can drive their active state via the StringEquals converter.</summary>
    public string CrossfadeSecondsText => CrossfadeSeconds.ToString();

    // ── Live analysis progress ────────────────────────────────────────────
    // Number of tracks currently mid-analysis across ALL in-flight batches.
    // Mutated ONLY on the UI thread (every change is marshalled through
    // Dispatcher) so the generated setter never races the background DSP
    // threads. Drives the "ANALYZING N" header badge and gates Harmonic Sort —
    // sequencing a half-analysed queue would mis-order the not-yet-scored rows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalyzing))]
    [NotifyPropertyChangedFor(nameof(AnalyzingText))]
    [NotifyPropertyChangedFor(nameof(BpmRangeText))]
    [NotifyPropertyChangedFor(nameof(ShowBpmRange))]
    private int _analyzingCount;

    /// <summary>True while any track is being analysed — shows the header badge and disables
    /// Harmonic Sort (the rows further down the list aren't visible, so the badge is the only
    /// cue that work is still happening).</summary>
    public bool IsAnalyzing => AnalyzingCount > 0;

    /// <summary>Header-badge text, e.g. "ANALYZING 5" — letterspaced caps to match the colheads.</summary>
    public string AnalyzingText => $"ANALYZING {AnalyzingCount}";

    partial void OnAnalyzingCountChanged(int value) => HarmonicSortCommand.NotifyCanExecuteChanged();

    /// <summary>Vinyl rotates only when actually playing AND spin is enabled in tweaks.</summary>
    public bool ShouldSpin => IsPlaying && VinylSpinEnabled;

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(ShouldSpin));

    // ── Tweaks persistence ───────────────────────────────────────────────
    // Each tweak property's partial On…Changed updates the in-memory settings
    // and writes them to disk. CurrentTheme additionally tells the theme
    // service to apply the new palette so the change is visible immediately.
    //
    // Guarded by _settingsHydrated so the constructor-time seeding of the
    // backing fields does not echo back into Save() — that would be both
    // wasteful and risk a startup-time write to a not-yet-writable location.

    partial void OnCurrentThemeChanged(string value)
    {
        if (!_settingsHydrated) return;
        _themes.Apply(Themes.ByNameOrDefault(value));
        PersistSettings();
    }

    partial void OnVinylSpinEnabledChanged(bool value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnWaveformEnabledChanged(bool value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnAutoAnalyzeChanged(bool value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnAutoWriteOnAnalyzeChanged(bool value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnWriteDjCommentChanged(bool value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnPlaybackNormalizationChanged(bool value)
    {
        // Apply to the controller (affects the next track) and re-apply to the CURRENT track so the
        // change is heard immediately. Always wire the controller; persist only after hydration.
        _controller.NormalizationEnabled = value;
        _controller.RefreshNormalization();
        if (!_settingsHydrated) return;
        PersistSettings();

        // Turning it ON couples in full analysis (the user's choice): measure any queued track that
        // lacks a ReplayGain yet, so normalization works without a manual Analyze pass.
        if (value) EnsureAnalyzedForNormalization();
    }

    partial void OnNormalizationLevelChanged(string value)
    {
        // Re-reference the playback target and re-apply to the current track so the level change is
        // heard immediately. The written ReplayGain TAG stays at the −18 standard — this only moves
        // the playback target.
        _controller.TargetLufs = LevelToLufs(value);
        _controller.RefreshNormalization();
        // The GAIN column shows the gain applied AT THE ACTIVE LEVEL — push the new target to every
        // row so the displayed numbers track the Quiet/Normal/Loud switch.
        var target = LevelToLufs(value);
        foreach (var t in Tracks) { t.NormalizationTargetDb = target; t.RaiseGainDisplay(); }
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnCrossfadeSecondsChanged(int value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    partial void OnLimiterModeChanged(string value)
    {
        // Apply to the engine immediately (heard live — the DSP swaps without dropping audio),
        // then persist after hydration.
        ApplyLimiterToEngine();
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    /// <summary>Map the Tweaks limiter mode to (enabled, driveDb) and push it to the engine's master
    /// bus. Ceiling is fixed at −1 dBTP inside the engine; only the maximizer drive varies.
    /// Off=bypass, Soft=0 dB (safety only), Club=+3 dB, Loud=+6 dB.</summary>
    private void ApplyLimiterToEngine()
    {
        // (enabled, driveDb, ceilingDbTp). Soft/Club stay broadcast-safe at −1 dBTP; Loud both pushes
        // +6 dB drive AND lifts the ceiling to −0.1 dBTP to wring out the last bit of level.
        var (enabled, driveDb, ceilingDbTp) = LimiterMode switch
        {
            "Soft" => (true, 0.0, -1.0),
            "Club" => (true, 3.0, -1.0),
            "Loud" => (true, 6.0, -0.1),
            _      => (false, 0.0, -1.0),   // "Off" / unknown → bypass
        };
        _engine.SetLimiter(enabled, driveDb, ceilingDbTp);
    }

    /// <summary>Set the limiter mode from the Tweaks segmented control ("Off"/"Soft"/"Club"/"Loud").</summary>
    [RelayCommand]
    private void SetLimiterMode(string which) => LimiterMode = which;

    // EQ band sliders → engine + persist. Each band shares one apply path; persist is guarded by
    // hydration so the constructor seed doesn't echo to disk.
    partial void OnEqLowChanged(double value)  => OnEqChanged();
    partial void OnEqMidChanged(double value)  => OnEqChanged();
    partial void OnEqHighChanged(double value) => OnEqChanged();

    private void OnEqChanged()
    {
        ApplyEqualizerToEngine();   // live; flat → DSP bypassed
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    /// <summary>Push the three EQ band gains (linear) to the engine bus. Flat (all 1.0) bypasses.</summary>
    private void ApplyEqualizerToEngine() => _engine.SetEqualizer(EqLow, EqMid, EqHigh);

    /// <summary>Reset all three EQ bands to unity (flat).</summary>
    [RelayCommand]
    private void ResetEqualizer()
    {
        EqLow = 1.0; EqMid = 1.0; EqHigh = 1.0;
    }

    // ── "Revive" rack sliders → engine + persist. Each neutral value bypasses its DSP. ──────────
    partial void OnTransientPunchChanged(double value)   { _engine.SetTransientDesigner(value); PersistIfHydrated(); }
    partial void OnAutoTiltStrengthChanged(double value) { _engine.SetAdaptiveTilt(value);     PersistIfHydrated(); }

    /// <summary>Push the revive blocks to the engine (used once at construction; each is a no-op /
    /// true bypass at its neutral value).</summary>
    private void ApplyReviveToEngine()
    {
        _engine.SetTransientDesigner(TransientPunch);
        _engine.SetAdaptiveTilt(AutoTiltStrength);
    }

    /// <summary>Reset the whole revive rack to neutral (everything off).</summary>
    [RelayCommand]
    private void ResetRevive()
    {
        TransientPunch = 0.0; AutoTiltStrength = 0.0;
    }

    // Hard / Neutral are no longer hardcoded one-click commands — they are seeded into the user's
    // editable preset list (DspPreset.Hard / DspPreset.Neutral, seeded once on a fresh install) and
    // applied via ApplyPreset like any saved preset. See the constructor's seedDefaults block.

    // ── User-saved Sound presets (additive to the built-in Hard / Neutral) ──────────────────────
    //
    // Save  — snapshot the CURRENT six Sound-tab bus fields under a user-chosen name (InputDialog).
    // Apply — push a saved preset's fields back onto the bus exactly like ApplyHardPreset does, by
    //         assigning the [ObservableProperty] setters: each On…Changed partial pushes the value
    //         to the engine (SetEqualizer / SetAdaptiveTilt / SetTransientDesigner / SetLimiter) AND
    //         persists. No separate engine/persist call here — the property path is the single source.
    // Delete — drop a saved preset and persist.

    /// <summary>Capture the current Sound-tab bus state, prompt for a name, and save it as a user
    /// preset. A name that matches an existing preset (case-insensitive) overwrites it (rename-as-save
    /// = update). Built-ins (Hard/Neutral) are unaffected. Cancel / empty name = no-op.</summary>
    [RelayCommand]
    private async Task SavePreset()
    {
        // The ViewModel has no Window handle; resolve the live main window the same way ErrorReporter
        // does. If there's no owner (shouldn't happen with the panel open) we can't prompt → bail.
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var name = await Views.InputDialog.AskAsync(owner, "Name this Sound preset", DefaultPresetName());
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = new DspPreset
        {
            Name             = name,
            EqLowGain        = EqLow,
            EqMidGain        = EqMid,
            EqHighGain       = EqHigh,
            AutoTiltStrength = AutoTiltStrength,
            TransientPunch   = TransientPunch,
            LimiterMode      = LimiterMode,
        };

        // Overwrite a same-named preset in place (update); otherwise append.
        var existingIdx = -1;
        for (var i = 0; i < SoundPresets.Count; i++)
            if (string.Equals(SoundPresets[i].Name, name, StringComparison.OrdinalIgnoreCase)) { existingIdx = i; break; }

        if (existingIdx >= 0) SoundPresets[existingIdx] = preset;
        else                  SoundPresets.Add(preset);

        PersistIfHydrated();
    }

    /// <summary>Apply a saved Sound preset to the bus. Mirrors ApplyHardPreset/ApplyNeutralPreset:
    /// assigns the six observable bus properties, so each On…Changed partial pushes to the engine
    /// (SetEqualizer / SetAdaptiveTilt / SetTransientDesigner / SetLimiter) AND persists.</summary>
    [RelayCommand]
    private void ApplyPreset(DspPreset? preset)
    {
        if (preset is null) return;
        EqLow = preset.EqLowGain; EqMid = preset.EqMidGain; EqHigh = preset.EqHighGain;
        AutoTiltStrength = preset.AutoTiltStrength;
        TransientPunch   = preset.TransientPunch;
        LimiterMode      = preset.LimiterMode;
    }

    /// <summary>Delete a saved Sound preset and persist. Built-ins are not in this list, so they're
    /// never affected.</summary>
    [RelayCommand]
    private void DeletePreset(DspPreset? preset)
    {
        if (preset is null) return;
        if (SoundPresets.Remove(preset)) PersistIfHydrated();
    }

    /// <summary>Rename a saved preset in place (right-click → "Rename…"). Prompts for a new name
    /// (prefilled), keeps the DSP values + list position. Cancel / empty / unchanged = no-op. Reuses
    /// the same InputDialog as the "+" Save, so no new dialog infra is needed.</summary>
    [RelayCommand]
    private async Task RenamePreset(DspPreset? preset)
    {
        if (preset is null) return;
        var idx = SoundPresets.IndexOf(preset);
        if (idx < 0) return;

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var name = await Views.InputDialog.AskAsync(owner, "Rename preset", preset.Name);
        if (string.IsNullOrWhiteSpace(name) || name == preset.Name) return;

        SoundPresets[idx] = preset with { Name = name };
        PersistIfHydrated();
    }

    /// <summary>Overwrite a saved preset's stored values with the CURRENT bus state, keeping its name
    /// and position (right-click → "Replace with current"). No prompt — the name is reused. No-op if gone.</summary>
    [RelayCommand]
    private void ReplacePreset(DspPreset? preset)
    {
        if (preset is null) return;
        var idx = SoundPresets.IndexOf(preset);
        if (idx < 0) return;
        SoundPresets[idx] = new DspPreset
        {
            Name             = preset.Name,
            EqLowGain        = EqLow,
            EqMidGain        = EqMid,
            EqHighGain       = EqHigh,
            AutoTiltStrength = AutoTiltStrength,
            TransientPunch   = TransientPunch,
            LimiterMode      = LimiterMode,
        };
        PersistIfHydrated();
    }

    /// <summary>A sensible default name for a new preset: "Preset N" where N avoids a collision.</summary>
    private string DefaultPresetName()
    {
        var n = SoundPresets.Count + 1;
        string candidate;
        do { candidate = $"Preset {n++}"; }
        while (SoundPresets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }

    /// <summary>Persist only once settings are hydrated (so constructor seeds don't echo to disk).</summary>
    private void PersistIfHydrated()
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    /// <summary>Switch the visible Tweaks tab ("Look"/"Sound"/"Library"/"Audio").</summary>
    [RelayCommand]
    private void SetTweaksTab(string which) => TweaksTab = which;

    /// <summary>Set the normalization level from the Tweaks segmented control ("Quiet"/"Normal"/"Loud").</summary>
    [RelayCommand]
    private void SetNormalizationLevel(string which) => NormalizationLevel = which;

    /// <summary>Set the crossfade length from the Tweaks segmented control (0/2/4/8 seconds).</summary>
    [RelayCommand]
    private void SetCrossfadeLength(string which)
    {
        if (int.TryParse(which, out var secs)) CrossfadeSeconds = secs;
    }

    /// <summary>Map the streaming-style level name to its LUFS target. Unknown → Normal (−14).</summary>
    private static double LevelToLufs(string level) => level switch
    {
        "Quiet" => -19.0,
        "Loud"  => -11.0,
        _       => -14.0,
    };

    /// <summary>Run full analysis on queued tracks that have no ReplayGain yet, so playback
    /// normalization has a gain to apply. No-op when normalization is off or nothing needs it.</summary>
    private void EnsureAnalyzedForNormalization()
    {
        if (!PlaybackNormalization) return;
        var need = Tracks
            .Where(t => t.Model.Analysis?.ReplayGainDb is null && t.Model.AnalysisStatus != AnalysisStatus.Running)
            .ToList();
        if (need.Count > 0) _ = AnalyzeTracksAsync(need);
    }

    partial void OnAnalysisThreadsChanged(int value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    private void PersistSettings() => _settings.Save(new UserSettings
    {
        Theme             = CurrentTheme,
        VinylSpinEnabled  = VinylSpinEnabled,
        WaveformEnabled   = WaveformEnabled,
        AutoAnalyze        = AutoAnalyze,
        AutoWriteOnAnalyze = AutoWriteOnAnalyze,
        WriteDjComment     = WriteDjComment,
        PlaybackNormalization = PlaybackNormalization,
        NormalizationLevel    = NormalizationLevel,
        CrossfadeSeconds      = CrossfadeSeconds,
        LimiterMode           = LimiterMode,
        EqLowGain             = EqLow,
        EqMidGain             = EqMid,
        EqHighGain            = EqHigh,
        TransientPunch        = TransientPunch,
        AutoTiltStrength      = AutoTiltStrength,
        // Sound presets — persist the full list so they survive restart, and record that the built-in
        // defaults were seeded (always true once the VM is constructed) so they're never re-seeded.
        SoundPresets          = [.. SoundPresets],
        SoundPresetsSeeded    = true,
        AnalysisThreads    = AnalysisThreads,
        // Auto-update prefs have no Tweaks UI — preserve so a tweak save never resets them
        // (the update flow writes IgnoredUpdateVersion; a wipe here would un-ignore it).
        CheckForUpdates      = _settings.Current.CheckForUpdates,
        IgnoredUpdateVersion = _settings.Current.IgnoredUpdateVersion,
        // Output device — persist by name so it survives a reboot (index can change).
        OutputDeviceName = SelectedOutputDevice?.Name,
        // Streaming profiles — persist the full list and selected profile id.
        StreamServers          = [.. StreamServers],
        SelectedStreamServerId = SelectedStreamServer?.Id,
        // Column view sets — persisted so view A/B/C customisations survive restarts.
        ColumnViewA      = [.. _viewA],
        ColumnViewB      = [.. _viewB],
        ColumnViewC      = [.. _viewC],
        ActiveColumnView = ActiveColumnView,
        // Pre-listen (PFL) headphone device and volume. Persist the SAVED name (survives the
        // device briefly disappearing), not the live selection — see _savedHeadphoneDeviceName.
        HeadphoneDeviceName = _savedHeadphoneDeviceName,
        PreCueVolume        = PreCueVolume,
    });

    public string PositionText => Format(PositionSeconds);
    public string DurationText => Format(DurationSeconds);
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>
    /// Total runtime of the loaded tracks — shown in the Up-Next header so the user can see
    /// at a glance how much music is queued.
    /// </summary>
    public string TotalDurationText
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var t in Tracks)
                total += t.Model.Metadata?.Duration ?? TimeSpan.Zero;
            return total == TimeSpan.Zero
                ? "—"
                : total.TotalHours >= 1 ? total.ToString(@"h\:mm\:ss") : total.ToString(@"m\:ss");
        }
    }

    public string TrackCountText => Tracks.Count == 1 ? "1 TRACK" : $"{Tracks.Count} TRACKS";

    // ── Loaded-playlist memory ────────────────────────────────────────────────
    // Path of the .m3u/.m3u8 most recently opened via LoadPlaylistAsync. Null when
    // no playlist is loaded (raw drops, or after ClearList/ClearTracks). Cleared
    // automatically when the queue goes empty so there is never a stale reference
    // to a playlist whose tracks are all gone.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadedPlaylistName))]
    [NotifyPropertyChangedFor(nameof(HasLoadedPlaylist))]
    [NotifyPropertyChangedFor(nameof(QueueTitle))]
    [NotifyCanExecuteChangedFor(nameof(SavePlaylistCommand))]
    private string? _loadedPlaylistPath;

    [ObservableProperty] private bool _playlistDirty;

    public string? LoadedPlaylistName => LoadedPlaylistPath is null ? null : Path.GetFileNameWithoutExtension(LoadedPlaylistPath);
    public bool HasLoadedPlaylist => LoadedPlaylistPath is not null;
    // Header always reads "UP NEXT" — the loaded-playlist NAME would overflow the column header on
    // long names, so it lives ONLY in the "…" menu (LoadedPlaylistName). The dirty dot + count stay.
    public string QueueTitle => "UP NEXT";

    /// <summary>Tempo span of the whole set — min–max BPM across every track with a known BPM
    /// (analysed or tagged). Empty unless at least TWO tracks have a BPM (none or only one → no
    /// span, per design). A single distinct value collapses to just that value ("128 BPM").</summary>
    public string BpmRangeText
    {
        get
        {
            var count = 0;
            double min = double.MaxValue, max = double.MinValue;
            foreach (var t in Tracks)
            {
                if (t.Bpm is { } b && b > 0)
                {
                    count++;
                    if (b < min) min = b;
                    if (b > max) max = b;
                }
            }
            if (count < 2) return "";
            int lo = (int)Math.Round(min), hi = (int)Math.Round(max);
            return lo == hi ? $"{lo} BPM" : $"{lo}–{hi} BPM";
        }
    }

    /// <summary>The BPM-range chip on the NOW PLAYING line shows only with a track playing, a known
    /// multi-track span, and no analysis in flight — while analysing it would flicker as each row
    /// resolves and run the line too long, so it stays hidden until the queue settles.</summary>
    public bool ShowBpmRange => Current is not null && !IsAnalyzing && BpmRangeText.Length > 0;

    // ---- Commands -------------------------------------------------------

    [RelayCommand]
    private void PlayTrack(TrackViewModel? track)
    {
        if (track is null) return;
        PlayInternal(track);
        // Explicit user pick (double-click a row): restart the shuffle cycle
        // anchored on this track so the next bag is drawn around it.
        if (Shuffle) ResetShuffleFrom(track);
    }

    /// <summary>Load + play a track without disturbing shuffle bookkeeping — the shared
    /// path for user picks and internal next/prev navigation alike.</summary>
    private void PlayInternal(TrackViewModel track, int crossfadeMs = 0)
    {
        var prev = Current;
        SetCurrent(track);

        // Crossfade only when: a fade was requested, something is actually playing, and we're moving
        // to a DIFFERENT file. The same-file guard makes repeat-one / re-pick safe (no self-overlap).
        var canFade = crossfadeMs > 0
            && prev is not null
            && !ReferenceEquals(prev, track)
            && !string.Equals(prev.Model.FilePath, track.Model.FilePath, StringComparison.OrdinalIgnoreCase)
            && _controller.State == PlaybackState.Playing;

        var fade = canFade ? SmartCrossfadeMs(prev!, track, crossfadeMs) : 0;
        if (fade > 0) _controller.CrossfadeTo(track.Model, fade);
        else          _controller.Play(track.Model);
        _controller.Volume = Volume;
    }

    /// <summary>Trim the user's crossfade length when the two tracks would clash through a long blanket
    /// blend: a big tempo gap (beat stumble) or harmonically incompatible keys (dissonance) each halve
    /// it; below ~1.2s a clash-blend sounds worse than a clean cut, so hard-cut (return 0).</summary>
    private static int SmartCrossfadeMs(TrackViewModel from, TrackViewModel to, int requestedMs)
    {
        var ms = requestedMs;
        if (from.Bpm is { } a and > 0 && to.Bpm is { } b and > 0)
        {
            var ratio = System.Math.Abs(a - b) / System.Math.Min(a, b);
            if (ratio > 0.12) ms /= 2;   // >12% tempo gap — beyond easy beat-matching
        }
        if (from.Model.Analysis?.Key is { } k1 && to.Model.Analysis?.Key is { } k2
            && !k1.IsHarmonicallyCompatibleWith(k2))
            ms /= 2;                      // key clash
        return ms < 1200 ? 0 : ms;
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_controller.CurrentTrack is null && Tracks.Count > 0)
        {
            PlayTrack(Current ?? Tracks[0]);
            return;
        }
        _controller.TogglePlayPause();
    }

    [RelayCommand]
    private void Stop() => _controller.Stop();

    [RelayCommand]
    private void Next() => Advance(forward: true, auto: false);

    [RelayCommand]
    private void Previous()
    {
        // Design behaviour (app.jsx:492): if we're more than a few seconds into
        // the current track, "previous" restarts it rather than stepping back.
        if (_controller.CurrentTrack is not null && _controller.Position > PreviousRestartThreshold)
        {
            _controller.Position = TimeSpan.Zero;
            return;
        }
        Advance(forward: false, auto: false);
    }

    [RelayCommand]
    private void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _              => RepeatMode.Off,
    };

    [RelayCommand]
    private void ToggleViewMode() => IsMini = !IsMini;

    [RelayCommand]
    private void ToggleTweaks() => IsTweaksOpen = !IsTweaksOpen;

    [RelayCommand]
    private void ToggleStreaming() => IsStreamingOpen = !IsStreamingOpen;

    /// <summary>
    /// Switch to one of the built-in palettes (Aurora / Sunset / Midnight / Neon).
    /// The actual swap and persistence happen in <see cref="OnCurrentThemeChanged"/> —
    /// this command exists so the Tweaks-panel swatch buttons can fire it via
    /// <c>CommandParameter</c>; setting the property directly from a Binding would
    /// also work but the swatch buttons are click-based.
    /// </summary>
    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (!string.IsNullOrEmpty(name)) CurrentTheme = name;
    }

    [RelayCommand]
    private void SetAnalysisThreads(string? n)
    {
        if (int.TryParse(n, out var t)) AnalysisThreads = Math.Clamp(t, 1, 16);
    }

    // ── Streaming: server profile management (S1) ────────────────────────
    //
    // StreamServers is the live ObservableCollection; SelectedStreamServer tracks
    // which profile is being edited. Editing a field replaces the record in-place
    // (records are immutable) and immediately persists. This is the same stateless
    // immutable-record-in-settings pattern the analysis preferences use.
    //
    // The "Connect" command is a stub; the real Icecast source-client (JustPlay.Streaming)
    // arrives in S2 and replaces it without touching the profile-management code here.

    /// <summary>All configured Icecast server profiles. Bound to the StreamingPanel list.</summary>
    public ObservableCollection<StreamServerProfile> StreamServers { get; } = [];

    /// <summary>True when the profile list is empty — drives the "No servers yet" hint in the panel.</summary>
    public bool HasNoStreamServers => StreamServers.Count == 0;

    // ── Output device selection ───────────────────────────────────────────
    // Populated once at startup (PopulateOutputDevices) from IAudioEngine.GetOutputDevices().
    // Users change the selection via a ComboBox in the Tweaks panel; the On…Changed
    // partial calls SetOutputDevice on the engine and persists the name.
    //
    // We use ObservableCollection<AudioOutputDevice> so the ComboBox's ItemsSource
    // reflects the list without needing a converter. The list is NOT refreshed live
    // (devices can appear/disappear), but the UI is only shown in the Tweaks panel which
    // is opened manually — a session restart picks up new/removed devices automatically.

    /// <summary>The available audio output devices, populated at startup.</summary>
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = [];

    /// <summary>True when no output devices were found (BASS not yet initialised, running headless).
    /// Drives the "No output devices found" hint in the Tweaks panel.</summary>
    public bool HasNoOutputDevices => OutputDevices.Count == 0;

    /// <summary>The currently selected output device, or null if none is selected / list is empty.</summary>
    [ObservableProperty]
    private AudioOutputDevice? _selectedOutputDevice;

    partial void OnSelectedOutputDeviceChanged(AudioOutputDevice? value)
    {
        if (!_settingsHydrated || value is null) return;
        _engine.SetOutputDevice(value.Index);
        PersistSettings();
    }

    /// <summary>
    /// Populate <see cref="OutputDevices"/> from the engine and set the initial selection.
    /// Called once from the constructor (before _settingsHydrated = true) so no premature
    /// PersistSettings is triggered.
    /// <paramref name="savedName"/> is the device name from settings (null = use default).
    /// </summary>
    private void PopulateOutputDevices(string? savedName)
    {
        var devices = _engine.GetOutputDevices();
        foreach (var d in devices)
            OutputDevices.Add(d);

        if (OutputDevices.Count == 0) return;

        // Match by name first (stable across reboots), fall back to the default device,
        // then the first device in the list if no default is flagged.
        AudioOutputDevice? match = null;
        if (savedName is not null)
            match = OutputDevices.FirstOrDefault(d => d.Name == savedName);
        match ??= OutputDevices.FirstOrDefault(d => d.IsDefault);
        match ??= OutputDevices[0];

        // Use the generated property so the toolkit doesn't warn about direct field access.
        // OnSelectedOutputDeviceChanged is guarded by _settingsHydrated (still false here),
        // so this assignment is silent — no PersistSettings call is triggered.
        SelectedOutputDevice = match;

        // Apply the resolved device to the engine at startup (explicit, not via the On…Changed).
        _engine.SetOutputDevice(match.Index);
    }

    /// <summary>
    /// Re-enumerate audio devices after the user plugs/unplugs hardware — no restart needed (the ↻
    /// button by the device pickers, mirroring JUST STREAM). Rebuilds <see cref="OutputDevices"/> + the
    /// PRE-CUE <see cref="HeadphoneDevices"/>, keeping each selection by NAME where it still exists
    /// (indices shift as devices come and go). Output falls back to default/first; the headphone pick
    /// never auto-selects (cue must stay off the speakers).
    /// </summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        var outName = SelectedOutputDevice?.Name;
        OutputDevices.Clear();
        foreach (var d in _engine.GetOutputDevices()) OutputDevices.Add(d);
        OnPropertyChanged(nameof(HasNoOutputDevices));
        if (OutputDevices.Count > 0)
            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Name == outName)
                                   ?? OutputDevices.FirstOrDefault(d => d.IsDefault)
                                   ?? OutputDevices[0];

        // Fall back to the saved-by-name device (not just the live selection) so a manual refresh
        // also recovers a device that disappeared and reappeared while nothing was actively bound —
        // same source of truth the auto-reconnect poll uses (PollPreCueDeviceRebind).
        var hpName = SelectedHeadphoneDevice?.Name ?? _savedHeadphoneDeviceName;
        HeadphoneDevices.Clear();
        foreach (var d in _preListen.GetOutputDevices()) HeadphoneDevices.Add(d);
        OnPropertyChanged(nameof(HasNoHeadphoneDevices));
        SelectedHeadphoneDevice = hpName is null ? null : HeadphoneDevices.FirstOrDefault(d => d.Name == hpName);
    }

    /// <summary>
    /// Pre-cue v2 auto-reconnect "CLOU": while the PRE-CUE tab is open (gated in <see cref="Tick"/>
    /// by <see cref="IsPreCueTab"/>), poll for the saved-by-name headphone device reappearing (e.g.
    /// Chloe's Bluetooth headphones waking up) and bind it automatically — no Settings detour.
    /// Never overrides an already-bound device, and never falls back to the system default (cue
    /// audio must never land on the speakers) — see <see cref="PreCueTransport.TryAutoRebind"/>.
    /// </summary>
    private void PollPreCueDeviceRebind()
    {
        var fresh = _preListen.GetOutputDevices();
        var pick = PreCueTransport.TryAutoRebind(SelectedHeadphoneDevice, _savedHeadphoneDeviceName, fresh);
        if (pick is null) return;

        // Refresh the bound list so the ComboBox shows the (re)appeared device, then bind it.
        // OnSelectedHeadphoneDeviceChanged does the rest (_preListen.OutputDevice + persist).
        HeadphoneDevices.Clear();
        foreach (var d in fresh) HeadphoneDevices.Add(d);
        OnPropertyChanged(nameof(HasNoHeadphoneDevices));
        SelectedHeadphoneDevice = pick;
    }

    // ── Pre-listen (PFL) headphone device selection ───────────────────────────
    // Mirrors the output device pattern. The pre-listen engine does NOT auto-select the
    // system default (that would put cue audio on the speakers). If no saved device name
    // matches, HeadphoneDevices is populated but nothing is selected (null) and the PRE-CUE
    // panel shows a "choose a headphone device" hint. The user selects explicitly.

    /// <summary>Audio output devices available for headphone cue monitoring, populated at startup.</summary>
    public ObservableCollection<AudioOutputDevice> HeadphoneDevices { get; } = [];

    /// <summary>True when the list is empty — drives the "no devices found" hint in the PRE-CUE panel.</summary>
    public bool HasNoHeadphoneDevices => HeadphoneDevices.Count == 0;

    /// <summary>True when no headphone device is selected — drives the "choose a device" hint.</summary>
    public bool HeadphoneDeviceNotSelected => SelectedHeadphoneDevice is null;

    /// <summary>The currently selected headphone device, or null when none is configured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadphoneDeviceNotSelected))]
    private AudioOutputDevice? _selectedHeadphoneDevice;

    partial void OnSelectedHeadphoneDeviceChanged(AudioOutputDevice? value)
    {
        // Remember the bind by name — but only forward (never clear to null just because the live
        // selection drops out, e.g. the device disappearing). See _savedHeadphoneDeviceName's field
        // comment: this is what makes the auto-reconnect poll (PollPreCueDeviceRebind) possible.
        if (value is not null) _savedHeadphoneDeviceName = value.Name;

        if (!_settingsHydrated) return;
        _preListen.OutputDevice = value?.Index ?? -1;
        PersistSettings();
    }

    private void PopulateHeadphoneDevices(string? savedName)
    {
        _savedHeadphoneDeviceName = savedName;

        // Enumerate from the pre-listen engine (same underlying BASS call as main engine, but returns
        // all enabled devices — the user picks which one is their headphones).
        var devices = _preListen.GetOutputDevices();
        foreach (var d in devices)
            HeadphoneDevices.Add(d);

        if (HeadphoneDevices.Count == 0 || savedName is null) return;

        // Only auto-select when we have a saved name — never auto-select the default device
        // because that would route cue audio to the speakers without the user asking.
        var match = HeadphoneDevices.FirstOrDefault(d => d.Name == savedName);
        if (match is null) return;

        SelectedHeadphoneDevice = match;
        // Apply at startup (guarded by !_settingsHydrated in On…Changed, so explicit call here).
        _preListen.OutputDevice = match.Index;
    }

    // ── Pre-listen transport state (Pre-Cue v2 — single slot) ──────────────────────
    // PreCueCurrent: the ONE track currently loaded in the cue engine, or null when the slot is
    // empty. "es muss schnell gehen" — loading a track REPLACES the slot and autoplays immediately;
    // there is no audition list and no pause. Add/Kick + ±30s live below (── Pre-cue commands ──).

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreCueIsPlaying))]
    [NotifyPropertyChangedFor(nameof(PreCuePositionText))]
    [NotifyPropertyChangedFor(nameof(PreCueDurationText))]
    [NotifyPropertyChangedFor(nameof(HasPreCueCurrent))]
    [NotifyCanExecuteChangedFor(nameof(CueAddCommand))]
    [NotifyCanExecuteChangedFor(nameof(CueKickCommand))]
    [NotifyCanExecuteChangedFor(nameof(CueJumpForwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(CueJumpBackCommand))]
    private TrackViewModel? _preCueCurrent;

    /// <summary>True while the pre-cue slot has a track loaded — drives the Add/Kick/±30s buttons'
    /// enabled state (via CanExecute) and the empty-slot hint text in the PRE-CUE panel.</summary>
    public bool HasPreCueCurrent => PreCueCurrent is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreCuePositionText))]
    private double _preCuePositionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreCueDurationText))]
    private double _preCueDurationSeconds;

    /// <summary>Cue/PFL volume (0..1), persisted across sessions so headphone level is remembered.</summary>
    [ObservableProperty]
    private double _preCueVolume = 0.8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreCueIsPlaying))]
    private PlaybackState _preCueState = PlaybackState.Stopped;

    /// <summary>True while the cue engine is playing — reflects the engine's live state (autoplay
    /// on load, no pause in v2; goes false again on Kick or natural end-of-track).</summary>
    public bool PreCueIsPlaying => PreCueState == PlaybackState.Playing;

    public string PreCuePositionText => Format(PreCuePositionSeconds);
    public string PreCueDurationText => Format(PreCueDurationSeconds);

    partial void OnPreCueVolumeChanged(double value)
    {
        _preListen.Volume = value;
        PersistIfHydrated();
    }

    partial void OnPreCuePositionSecondsChanged(double value)
    {
        if (_suppressPreCueSeek) return;
        _pendingPreCueSeek = value;
        _pendingPreCueSeekTicks = 0;
        _preListen.Position = TimeSpan.FromSeconds(value);
    }

    private void OnPreCueStateChanged(object? sender, PlaybackState state)
        => Dispatcher.UIThread.Post(() => PreCueState = state);

    private void OnPreCueEnded(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            // Single-slot v2: just stop — no auto-advance (there's nothing queued to advance to).
            // The slot stays loaded (PreCueCurrent unchanged) so Chloe can still Add/Kick it or
            // scrub back in with ±30s; only Kick clears the slot.
            PreCueState = PlaybackState.Stopped;
        });

    // ── Right-column tab ("UP NEXT" | "PRE-CUE") ─────────────────────────────
    // Transient — not persisted; each session starts on the queue tab.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueTab))]
    [NotifyPropertyChangedFor(nameof(IsPreCueTab))]
    [NotifyPropertyChangedFor(nameof(ShowQueueHeader))]
    private string _rightColumnTab = "Queue";

    public bool IsQueueTab  => RightColumnTab == "Queue";
    public bool IsPreCueTab => RightColumnTab == "PreCue";

    /// <summary>Show the column header row only when the queue tab is active AND there are tracks.
    /// When the PRE-CUE tab is showing, the queue header would be misleading.</summary>
    public bool ShowQueueHeader => HasTracks && IsQueueTab;

    [RelayCommand]
    private void SetRightColumnTab(string which) => RightColumnTab = which;

    // ── Pre-cue commands (v2: single slot, autoplay, no pause) ─────────────────

    /// <summary>Load ONE file into the pre-cue slot and autoplay it immediately — replaces whatever
    /// was cued before ("es muss schnell gehen": no confirmation, no pause). Called from the
    /// file-picker code-behind (<c>OnAddPreCueFilesClicked</c>) or a queue row's
    /// <see cref="TrackViewModel.PlayInPreCueCommand"/> (wired in <see cref="AddPathsAsync"/>).</summary>
    public async Task LoadPreCueTrackAsync(string filePath)
    {
        if (SelectedHeadphoneDevice is null) return; // no device → ignore (never routes to speakers)

        var tvm = new TrackViewModel(new Track(filePath));
        _preListen.Stop();
        _preListen.Load(filePath);       // replaces any previously-loaded source — single slot by construction
        _preListen.Volume = PreCueVolume;
        _preListen.Play();               // autoplay — no pause state in v2
        PreCueCurrent = tvm;

        // Load metadata async so the title/artist appear without blocking the UI.
        await Task.Run(() =>
        {
            tvm.Model.Metadata = _metadata.Read(filePath);
            Dispatcher.UIThread.Post(tvm.Refresh);
        });
    }

    /// <summary>Send a queue row into the pre-cue slot — the callback wired onto every
    /// <see cref="TrackViewModel"/> added to <see cref="Tracks"/> (see <see cref="AddPathsAsync"/>).</summary>
    private void LoadPreCueTrackFromRow(TrackViewModel row) => _ = LoadPreCueTrackAsync(row.Model.FilePath);

    /// <summary>Seek the cue track by ±<paramref name="delta"/>, clamped to [0, Duration] via the
    /// shared <see cref="PreCueTransport.ClampedJump"/> helper (unit-tested in
    /// PreListenEngineTests.cs). Routes through the <see cref="PreCuePositionSeconds"/> setter so the
    /// existing seek-hold bookkeeping (Tick doesn't snap the slider back to a stale position) applies
    /// here too — same as a manual slider drag.</summary>
    private void JumpPreCue(TimeSpan delta)
    {
        var target = PreCueTransport.ClampedJump(_preListen.Position, delta, _preListen.Duration);
        PreCuePositionSeconds = target.TotalSeconds;
    }

    /// <summary>Jump the cue track forward 30 seconds.</summary>
    [RelayCommand(CanExecute = nameof(HasPreCueCurrent))]
    private void CueJumpForward() => JumpPreCue(TimeSpan.FromSeconds(30));

    /// <summary>Jump the cue track back 30 seconds.</summary>
    [RelayCommand(CanExecute = nameof(HasPreCueCurrent))]
    private void CueJumpBack() => JumpPreCue(TimeSpan.FromSeconds(-30));

    /// <summary>"Add": append the cued track to the MAIN queue. Does not clear the slot — Chloe can
    /// still Kick it, jump around, or Add it again.</summary>
    [RelayCommand(CanExecute = nameof(HasPreCueCurrent))]
    private void CueAdd()
    {
        if (PreCueCurrent is not { } cued) return;

        var tvm = new TrackViewModel(new Track(cued.Model.FilePath))
        {
            AddOrder = _addSeq++,
            ToggleFavoriteCallback = ToggleFavoriteForTrack,
            PlayInPreCueCallback = LoadPreCueTrackFromRow,
            NormalizationTargetDb = LevelToLufs(NormalizationLevel),
        };
        Tracks.Add(tvm);
        RaiseTrackListChanged();
        MarkPlaylistEdited();
        // Load metadata async so the queue row shows title/artist/duration without blocking.
        _ = Task.Run(() =>
        {
            tvm.Model.Metadata = _metadata.Read(tvm.Model.FilePath);
            Dispatcher.UIThread.Post(() => { tvm.Refresh(); OnPropertyChanged(nameof(TotalDurationText)); });
        });
    }

    /// <summary>"Kick": discard the cued track — stop cue playback, release the file handle, and
    /// clear the slot. Fast audition workflow: Add what you like, Kick what you don't.</summary>
    [RelayCommand(CanExecute = nameof(HasPreCueCurrent))]
    private void CueKick()
    {
        if (PreCueCurrent is null) return;
        _preListen.Stop();
        _preListen.Unload();
        PreCueCurrent = null;
    }

    /// <summary>The profile currently open in the editor, or null when none is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedServerMp3))]
    [NotifyPropertyChangedFor(nameof(IsSelectedServerOpus))]
    [NotifyPropertyChangedFor(nameof(SelectedServerBitrateText))]
    [NotifyPropertyChangedFor(nameof(SelectedServerName))]
    [NotifyPropertyChangedFor(nameof(SelectedServerHost))]
    [NotifyPropertyChangedFor(nameof(SelectedServerPort))]
    [NotifyPropertyChangedFor(nameof(SelectedServerMount))]
    [NotifyPropertyChangedFor(nameof(SelectedServerUsername))]
    [NotifyPropertyChangedFor(nameof(SelectedServerPassword))]
    [NotifyPropertyChangedFor(nameof(SelectedServerUseTls))]
    private StreamServerProfile? _selectedStreamServer;

    // ── Editor pass-through properties ───────────────────────────────────
    // Each property reads from / writes to the selected profile. Writing replaces the
    // record in the collection and persists. Null-safe: returns empty/defaults when
    // no profile is selected so the UI fields remain harmlessly blank.

    public string SelectedServerName
    {
        get => SelectedStreamServer?.Name ?? string.Empty;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { Name = value }); }
    }

    public string SelectedServerHost
    {
        get => SelectedStreamServer?.Host ?? string.Empty;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { Host = value }); }
    }

    public string SelectedServerPort
    {
        get => SelectedStreamServer?.Port.ToString() ?? string.Empty;
        set
        {
            if (SelectedStreamServer is { } p && int.TryParse(value, out var port))
                ReplaceSelected(p with { Port = port });
        }
    }

    public string SelectedServerMount
    {
        get => SelectedStreamServer?.Mount ?? string.Empty;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { Mount = value }); }
    }

    public string SelectedServerUsername
    {
        get => SelectedStreamServer?.Username ?? string.Empty;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { Username = value }); }
    }

    public string SelectedServerPassword
    {
        get => SelectedStreamServer?.Password ?? string.Empty;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { Password = value }); }
    }

    public bool SelectedServerUseTls
    {
        get => SelectedStreamServer?.UseTls ?? false;
        set { if (SelectedStreamServer is { } p) ReplaceSelected(p with { UseTls = value }); }
    }

    /// <summary>String bitrate for the active-state binding in the bitrate radio buttons.</summary>
    public string SelectedServerBitrateText => SelectedStreamServer?.BitrateKbps.ToString() ?? string.Empty;

    public bool IsSelectedServerMp3 => SelectedStreamServer?.Format == StreamFormat.Mp3;
    public bool IsSelectedServerOpus => SelectedStreamServer?.Format == StreamFormat.Opus;

    /// <summary>Whether in-progress / not-yet-working UI is shown — the headphone Pre-Cue tab and the
    /// Opus stream format. True only in DEBUG builds, so RELEASE installers ship without the dead UI.
    /// See <see cref="AppInfo.Experimental"/>.</summary>
    public bool ShowExperimentalUi => AppInfo.Experimental;

    /// <summary>
    /// Replace the currently selected profile (immutable record swap) in the collection,
    /// update the selection pointer, and persist.
    /// </summary>
    private void ReplaceSelected(StreamServerProfile updated)
    {
        if (!_settingsHydrated) return;
        var idx = StreamServers.IndexOf(SelectedStreamServer!);
        if (idx < 0) return;
        StreamServers[idx] = updated;
        // Setting the generated property raises the [NotifyPropertyChangedFor] attributes
        // declared on _selectedStreamServer (Format, Bitrate, Name, etc.) so no manual
        // OnPropertyChanged calls are needed here. Also raises the computed editor props below.
        SelectedStreamServer = updated;
        // Raise the editor pass-through properties (they read directly from SelectedStreamServer).
        OnPropertyChanged(nameof(SelectedServerName));
        OnPropertyChanged(nameof(SelectedServerHost));
        OnPropertyChanged(nameof(SelectedServerPort));
        OnPropertyChanged(nameof(SelectedServerMount));
        OnPropertyChanged(nameof(SelectedServerUsername));
        OnPropertyChanged(nameof(SelectedServerPassword));
        OnPropertyChanged(nameof(SelectedServerUseTls));
        PersistSettings();
    }

    [RelayCommand]
    private void AddStreamServer()
    {
        var profile = new StreamServerProfile { Name = "New Server" };
        StreamServers.Add(profile);
        SelectedStreamServer = profile;
        PersistSettings();
    }

    [RelayCommand]
    private void RemoveStreamServer(StreamServerProfile? profile)
    {
        if (profile is null) return;
        StreamServers.Remove(profile);
        if (SelectedStreamServer == profile)
            SelectedStreamServer = StreamServers.Count > 0 ? StreamServers[^1] : null;
        PersistSettings();
    }

    [RelayCommand]
    private void SelectStreamServer(StreamServerProfile? profile)
    {
        SelectedStreamServer = profile;
        PersistSettings();
    }

    [RelayCommand]
    private void SetSelectedServerFormat(string? formatName)
    {
        if (SelectedStreamServer is not { } p) return;
        if (Enum.TryParse<StreamFormat>(formatName, out var fmt))
            ReplaceSelected(p with { Format = fmt });
    }

    [RelayCommand]
    private void SetSelectedServerBitrate(string? kbps)
    {
        if (SelectedStreamServer is not { } p) return;
        if (int.TryParse(kbps, out var b))
            ReplaceSelected(p with { BitrateKbps = b });
    }

    /// <summary>
    /// Toggle the Icecast broadcast connection. If disconnected (or in Error state), starts
    /// streaming to <see cref="SelectedStreamServer"/>. If connected, disconnects cleanly.
    ///
    /// CanExecute: a profile must be selected with a non-empty Host, AND we must not be
    /// mid-connect (Connecting/Reconnecting — let the current attempt finish).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ToggleConnect()
    {
        if (SelectedStreamServer is not { } profile) return;

        if (BroadcastState == BroadcastState.Connected)
        {
            await _broadcast.DisconnectAsync();
        }
        else
        {
            await _broadcast.ConnectAsync(profile);
        }
    }

    private bool CanToggleConnect()
        => SelectedStreamServer is { Host.Length: > 0 }
           && BroadcastState != BroadcastState.Connecting
           && BroadcastState != BroadcastState.Reconnecting;

    // Re-evaluate CanExecute when state or selection changes.
    partial void OnBroadcastStateChanged(BroadcastState value)
        => ToggleConnectCommand.NotifyCanExecuteChanged();

    partial void OnSelectedStreamServerChanged(StreamServerProfile? value)
        => ToggleConnectCommand.NotifyCanExecuteChanged();

    // ── Streaming quick-actions (radio cap-button right-click menu) ───────
    // Surfaced from MaxView's streaming-button context menu so the user can go
    // on/off air — and pick WHICH radio — without opening the panel. They drive
    // the same IBroadcastService the Connect button uses.

    /// <summary>Open the streaming panel (from the "Add a radio…" / "Streaming settings…" items).</summary>
    public void OpenStreaming() => IsStreamingOpen = true;

    /// <summary>Disconnect the live broadcast (no-op when not connected).</summary>
    public Task DisconnectBroadcastAsync() => _broadcast.DisconnectAsync();

    /// <summary>Go on air to a specific radio profile, making it the selected one. If a broadcast is
    /// already live it's dropped first so we cleanly switch stations.</summary>
    public async Task ConnectBroadcastAsync(StreamServerProfile profile)
    {
        SelectedStreamServer = profile;
        if (BroadcastState == BroadcastState.Connected || BroadcastState == BroadcastState.Reconnecting)
            await _broadcast.DisconnectAsync();
        await _broadcast.ConnectAsync(profile);
    }

    [RelayCommand]
    private void ClearTracks()
    {
        _controller.Stop();
        Tracks.Clear();
        SetCurrent(null);
        _shuffleHistory.Clear();
        _shufflePos = -1;
        RaiseTrackListChanged();
    }

    private void RaiseTrackListChanged()
    {
        RecalcIndexes();
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackCountText));
        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(BpmRangeText));
        OnPropertyChanged(nameof(ShowBpmRange));
        OnPropertyChanged(nameof(ShowQueueHeader));
        // Harmonic Sort's CanExecute depends on the queue having ≥ 2 tracks.
        HarmonicSortCommand.NotifyCanExecuteChanged();
        // If the queue is now empty and a playlist was loaded, break the reference —
        // "select all + delete" or any drain path that empties the queue is treated as
        // a Clear, which severs the playlist association.
        if (Tracks.Count == 0 && LoadedPlaylistPath != null)
        {
            LoadedPlaylistPath = null;
            PlaylistDirty = false;
        }
    }

    /// <summary>Number the rows 1..N — these are positions in the current session list,
    /// NOT the MP3 metadata track number.</summary>
    private void RecalcIndexes()
    {
        for (var i = 0; i < Tracks.Count; i++)
            Tracks[i].Index = i + 1;
    }

    // ---- Tag persistence actions (consent-gated; invoked from the queue context menus) ----
    //
    // Nothing is ever written until the user picks one of these. "Write" overwrites the
    // standard tag with our detected value (Applied); "Keep" records that the user reviewed
    // and kept the claimed value (Kept) — both stamp the JUSTPLAY blob so the picture survives
    // a restart and the field stops flagging. The pre-overwrite foreign value is stashed in
    // the blob's Original so a write is reversible. See memory analysis-tag-persistence-design.

    private enum FieldAction { None, Write, Keep }

    /// <summary>Write detected values for every selected track. <paramref name="fillMissingOnly"/>
    /// only touches fields the file doesn't already have (non-destructive); otherwise it overwrites.</summary>
    public void WriteSelectedTags(IReadOnlyList<TrackViewModel> sel, bool fillMissingOnly) => WithUndoCapture(() =>
    {
        foreach (var tvm in sel)
        {
            var a = tvm.Model.Analysis;
            var md = tvm.Model.Metadata;
            if (a is null) continue;

            if (fillMissingOnly)
                Persist(tvm,
                    a.Bpm is > 0 && md?.TaggedBpm is not > 0 ? FieldAction.Write : FieldAction.None,
                    a.Key is not null && string.IsNullOrWhiteSpace(md?.TaggedKey) ? FieldAction.Write : FieldAction.None,
                    a.Energy is not null && md?.TaggedEnergy is null ? FieldAction.Write : FieldAction.None);
            else
                Persist(tvm,
                    a.Bpm is > 0 ? FieldAction.Write : FieldAction.None,
                    a.Key is not null ? FieldAction.Write : FieldAction.None,
                    a.Energy is not null ? FieldAction.Write : FieldAction.None);
        }
        Dispatcher.UIThread.Post(RaiseTrackListChanged);
    });

    /// <summary>Write just one field of one track (per-cell action).</summary>
    public void WriteField(TrackViewModel tvm, AnalysisField f) => WithUndoCapture(() => Persist(tvm,
        f == AnalysisField.Bpm ? FieldAction.Write : FieldAction.None,
        f == AnalysisField.Key ? FieldAction.Write : FieldAction.None,
        f == AnalysisField.Energy ? FieldAction.Write : FieldAction.None));

    /// <summary>Record that the user reviewed one field and kept the claimed value (stops flagging).</summary>
    public void KeepField(TrackViewModel tvm, AnalysisField f) => WithUndoCapture(() => Persist(tvm,
        f == AnalysisField.Bpm ? FieldAction.Keep : FieldAction.None,
        f == AnalysisField.Key ? FieldAction.Keep : FieldAction.None,
        f == AnalysisField.Energy ? FieldAction.Keep : FieldAction.None));

    /// <summary>Undo a prior write: put the stashed original foreign value back into the
    /// standard tag and mark the field Kept. No-op if no original was stored for that field.</summary>
    public void RestoreField(TrackViewModel tvm, AnalysisField f)
    {
        var st = tvm.Model.Metadata?.StoredAnalysis;
        var orig = st?.Original;
        var hasOrig = f switch
        {
            AnalysisField.Bpm => orig?.Bpm is > 0,
            AnalysisField.Key => orig?.Key is not null,
            AnalysisField.Energy => orig?.Energy is not null,
            _ => false,
        };
        if (st is null || orig is null || !hasOrig) return;

        var write = new TagWrite
        {
            Bpm = f == AnalysisField.Bpm ? orig.Bpm : null,
            Key = f == AnalysisField.Key ? orig.Key : null,
            Energy = f == AnalysisField.Energy ? orig.Energy : null,
            State = st with
            {
                BpmDecision = f == AnalysisField.Bpm ? FieldDecision.Kept : st.BpmDecision,
                KeyDecision = f == AnalysisField.Key ? FieldDecision.Kept : st.KeyDecision,
                EnergyDecision = f == AnalysisField.Energy ? FieldDecision.Kept : st.EnergyDecision,
                Original = ClearOriginal(orig, f),
            },
        };
        WithUndoCapture(() => DoWrite(tvm, write));
        Dispatcher.UIThread.Post(RaiseTrackListChanged);
    }

    /// <summary>Build the state + tag write for a track and persist it. Captures (and preserves)
    /// the original foreign value for any field being overwritten so the write is reversible.</summary>
    private void Persist(TrackViewModel tvm, FieldAction bpm, FieldAction key, FieldAction energy)
    {
        if (bpm == FieldAction.None && key == FieldAction.None && energy == FieldAction.None)
            return; // nothing to do (e.g. fill-missing on a fully-tagged track)

        var a = tvm.Model.Analysis;
        if (a is null) return;
        var md = tvm.Model.Metadata;
        var prev = md?.StoredAnalysis;

        FieldDecision Dec(FieldAction act, FieldDecision? prior) => act switch
        {
            FieldAction.Write => FieldDecision.Applied,
            FieldAction.Keep => FieldDecision.Kept,
            _ => prior ?? FieldDecision.Pending,
        };

        // Stash the pre-overwrite foreign value the first time we overwrite a field; keep any
        // already-stored original so a second write never loses the true origin.
        double? origBpm = prev?.Original?.Bpm ?? (bpm == FieldAction.Write ? md?.TaggedBpm : null);
        MusicalKey? origKey = prev?.Original?.Key ?? (key == FieldAction.Write ? MusicalKey.TryParse(md?.TaggedKey) : null);
        int? origEnergy = prev?.Original?.Energy ?? (energy == FieldAction.Write ? md?.TaggedEnergy : null);
        AnalysisResult? original = origBpm is null && origKey is null && origEnergy is null
            ? null
            : new AnalysisResult { Bpm = origBpm, Key = origKey, Energy = origEnergy };

        var state = new TrackAnalysisState
        {
            Version = TrackAnalysisState.CurrentVersion,
            Detected = a,
            Original = original,
            BpmDecision = Dec(bpm, prev?.BpmDecision),
            KeyDecision = Dec(key, prev?.KeyDecision),
            EnergyDecision = Dec(energy, prev?.EnergyDecision),
        };

        // When "DJ Software compatible" comment is enabled, build the new comment value from the
        // key/energy values as they will be in the tag after this write:
        //   Write  → the detected value from analysis
        //   Keep   → the existing tagged value (user confirmed it is correct)
        //   None   → the existing tagged value (field not touched by this write)
        string? newComment = null;
        if (WriteDjComment)
        {
            var effectiveKey = key == FieldAction.Write ? a.Key : MusicalKey.TryParse(md?.TaggedKey);
            var effectiveEnergy = energy == FieldAction.Write ? a.Energy : md?.TaggedEnergy;
            newComment = DjCommentBuilder.Build(effectiveKey, effectiveEnergy, md?.Comment);
        }

        var write = new TagWrite
        {
            Bpm = bpm == FieldAction.Write ? a.Bpm : null,
            Key = key == FieldAction.Write ? a.Key : null,
            Energy = energy == FieldAction.Write ? a.Energy : null,
            // ReplayGain rides along with every write (it's the mp3gain/MIK replacement): the
            // REPLAYGAIN_TRACK_GAIN/_PEAK fields are non-destructive standards that don't collide
            // with BPM/key/energy, so they need no per-field Write/Keep decision — whenever we have
            // a loudness measurement, stamp it. Null (un-analysed) simply writes nothing.
            ReplayGainDb = a.ReplayGainDb,
            Peak = a.Peak,
            State = state,
            Comment = newComment,
        };
        DoWrite(tvm, write);
    }

    /// <summary>The single point that touches the file. For a track that ISN'T playing the write runs
    /// immediately; for the CURRENTLY-PLAYING track it is DEFERRED by the controller (BASS holds the
    /// file open) and runs the instant the track stops being current — a track change — or on app exit.
    /// Playback is never interrupted. The deferred action re-reads the tags so the row reflects the new
    /// values + decisions once it lands. Robust: a write failure logs, never crashes the app.</summary>
    private bool DoWrite(TrackViewModel tvm, TagWrite write)
    {
        // Record undo BEFORE the write. Since the playing track's write is deferred, we can't gate undo
        // on a synchronous success any more — capture upfront. Restore writes the captured pre-state
        // back, which is a harmless no-op if a deferred write ultimately failed.
        var snapshot = _capturingUndo ? SnapshotOf(tvm) : null;
        if (snapshot is not null)
            lock (_undoBatch) _undoBatch.Add((tvm, snapshot));

        _controller.WithFileReleased(tvm.Model, () =>
        {
            try
            {
                _writer.Write(tvm.Model.FilePath, write);
                tvm.Model.Metadata = _metadata.Read(tvm.Model.FilePath);
                Dispatcher.UIThread.Post(tvm.Refresh);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tag write FAIL] {tvm.Model.FilePath}: {ex.GetType().Name}: {ex.Message}");
            }
        });

        return true;   // optimistic: the write runs now (other track) or is queued for the playing track
    }

    /// <summary>Run a user write action as one undoable batch: clears the prior undo set, lets the
    /// nested DoWrite calls record their pre-state, then publishes the new undo availability.</summary>
    private void WithUndoCapture(Action write)
    {
        lock (_undoBatch) _undoBatch.Clear();
        _capturingUndo = true;
        try { write(); }
        finally
        {
            _capturingUndo = false;
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(UndoHeader));
            });
        }
    }

    /// <summary>Capture the tag fields JustPlay may overwrite, so the write can be reverted exactly
    /// (null field = was empty → Undo clears it). The comment is only captured when the
    /// "DJ Software compatible" comment feature is on — otherwise Restore leaves it untouched.</summary>
    private TagRestore SnapshotOf(TrackViewModel tvm)
    {
        var md = tvm.Model.Metadata;
        return new TagRestore
        {
            Bpm = md?.TaggedBpm,
            Key = MusicalKey.TryParse(md?.TaggedKey),
            Energy = md?.TaggedEnergy,
            State = md?.StoredAnalysis,
            // ReplayGain prior value comes from the last stamped blob (the REPLAYGAIN_* tag fields
            // aren't read into Metadata): restore the previously-written gain/peak, or remove the
            // fields if none was stored. Mirrors how State is captured.
            ReplayGainDb = md?.StoredAnalysis?.Detected.ReplayGainDb,
            Peak = md?.StoredAnalysis?.Detected.Peak,
            // Capture the raw comment (before any DJ prefix is prepended) so Undo can restore it
            // verbatim. When the feature is off, CommentCaptured stays false → Restore skips it.
            Comment = WriteDjComment ? md?.Comment : null,
            CommentCaptured = WriteDjComment,
        };
    }

    /// <summary>Revert the most recent tag write (single-field, per-cell, or bulk) — restores every
    /// touched file to its captured pre-write state, releasing the playing file's handle as needed.</summary>
    public void UndoLastWrite()
    {
        (TrackViewModel tvm, TagRestore prev)[] batch;
        lock (_undoBatch)
        {
            if (_undoBatch.Count == 0) return;
            batch = _undoBatch.ToArray();
            _undoBatch.Clear();
        }
        foreach (var (tvm, prev) in Enumerable.Reverse(batch))
        {
            _controller.WithFileReleased(tvm.Model, () =>
            {
                try
                {
                    _writer.Restore(tvm.Model.FilePath, prev);
                    tvm.Model.Metadata = _metadata.Read(tvm.Model.FilePath);
                    Dispatcher.UIThread.Post(tvm.Refresh);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Undo FAIL] {tvm.Model.FilePath}: {ex.Message}");
                }
            });
        }
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoHeader));
            RaiseTrackListChanged();
        });
    }

    private static AnalysisResult? ClearOriginal(AnalysisResult orig, AnalysisField f)
    {
        var next = orig with
        {
            Bpm = f == AnalysisField.Bpm ? null : orig.Bpm,
            Key = f == AnalysisField.Key ? null : orig.Key,
            Energy = f == AnalysisField.Energy ? null : orig.Energy,
        };
        return next.Bpm is null && next.Key is null && next.Energy is null ? null : next;
    }

    /// <summary>Re-run BPM/key/energy analysis for the selected tracks (explicit user action, so it
    /// always re-runs even if a stored blob exists). Bounded by the AnalysisThreads preference.</summary>
    public void ReanalyzeSelected()
    {
        var targets = SelectedTracks.ToList();
        if (targets.Count == 0) return;
        _ = AnalyzeTracksAsync(targets);
    }

    /// <summary>Analyse a set of tracks with bounded concurrency (the AnalysisThreads preference, default
    /// 4). Each row flips to Running (spinner) → Done/Failed and refreshes as it goes. Runs off the UI
    /// thread; <see cref="ITrackAnalysisService.AnalyzeAsync"/> already offloads to the pool, so this
    /// just caps how many run at once.</summary>
    private async Task AnalyzeTracksAsync(IReadOnlyList<TrackViewModel> targets)
    {
        // Bump the live-progress counter for the whole batch up front (so the header badge
        // appears the instant analysis is requested), then drop it one-per-track as each row
        // finishes. Posted to the UI thread because AnalyzingCount is UI-thread-only state.
        Dispatcher.UIThread.Post(() => AnalyzingCount += targets.Count);

        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, AnalysisThreads) };
        await Parallel.ForEachAsync(targets, opts, async (tvm, ct) =>
        {
            tvm.Model.AnalysisStatus = AnalysisStatus.Running;
            Dispatcher.UIThread.Post(() => tvm.Refresh()); // show the spinner immediately
            try
            {
                var result = await _analysis.AnalyzeAsync(tvm.Model.FilePath, null, ct);
                tvm.Model.Analysis = result;
                tvm.Model.AnalysisStatus = AnalysisStatus.Done;
            }
            catch (Exception ex)
            {
                tvm.Model.AnalysisStatus = AnalysisStatus.Failed;
                Console.WriteLine($"[Analyze FAIL] {tvm.Model.FilePath}: {ex.Message}");
            }
            Dispatcher.UIThread.Post(() => { tvm.Refresh(); AnalyzingCount--; });

            // "New truth on analyse": optionally stamp our detected values into the file's tags
            // right away (consent given once, in settings). Runs HERE on the background analysis
            // thread (NOT marshalled to the UI thread) so slow file I/O — e.g. a network drive —
            // never blocks the UI; DoWrite marshals its own VM updates back via Dispatcher.Post.
            if (AutoWriteOnAnalyze && tvm.Model.AnalysisStatus == AnalysisStatus.Done)
                WriteDetected(tvm);
        });
    }

    /// <summary>
    /// Toggle-favourite callback wired onto every <see cref="TrackViewModel"/>.
    /// Runs the file I/O on a background thread (Task.Run) so a slow network drive
    /// never freezes the UI — mirrors the same pattern as WriteTags / FillMissing.
    /// On success the metadata is re-read and the VM refreshed via Dispatcher.Post.
    /// </summary>
    private Task ToggleFavoriteForTrack(TrackViewModel tvm, bool liked)
        => Task.Run(() =>
        {
            var write = new TagWrite { Favorite = liked };
            _controller.WithFileReleased(tvm.Model, () =>
            {
                try
                {
                    _writer.Write(tvm.Model.FilePath, write);
                    tvm.Model.Metadata = _metadata.Read(tvm.Model.FilePath);
                    Dispatcher.UIThread.Post(() => tvm.IsFavorite = tvm.Model.Metadata?.IsFavorite ?? false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Favorite write FAIL] {tvm.Model.FilePath}: {ex.GetType().Name}: {ex.Message}");
                    // Revert the optimistic flip the command applied in TrackViewModel.ToggleFavorite.
                    Dispatcher.UIThread.Post(() => tvm.IsFavorite = !liked);
                }
            });
        });

    /// <summary>Write all of a track's detected values into its tags (used by auto-write-on-analyse).
    /// Goes through the same Persist path, so decisions are stamped Applied and the conflict flags
    /// clear. Not wrapped in undo capture — auto-writes aren't a discrete user action.</summary>
    private void WriteDetected(TrackViewModel tvm)
    {
        var a = tvm.Model.Analysis;
        if (a is null) return;
        Persist(tvm,
            a.Bpm is > 0 ? FieldAction.Write : FieldAction.None,
            a.Key is not null ? FieldAction.Write : FieldAction.None,
            a.Energy is not null ? FieldAction.Write : FieldAction.None);
    }

    /// <summary>Remove the selected rows from the session queue. Does NOT delete files from disk,
    /// and does not stop playback of a removed current track (the engine holds its own reference).</summary>
    public void RemoveSelected()
    {
        if (SelectedTracks.Count == 0) return;
        // Snapshot the selection as a set, then remove in ONE pass + a single Reset notification.
        // The old per-item Tracks.Remove loop was O(n²) (each Remove is an O(n) search) AND re-rendered
        // the bound queue list once per row — Ctrl+A → Delete on a 10-hour queue took seconds.
        var removeSet = new HashSet<TrackViewModel>(SelectedTracks);
        Tracks.RemoveRange(removeSet);
        _shuffleHistory.RemoveAll(removeSet.Contains);
        if (_shufflePos >= _shuffleHistory.Count) _shufflePos = _shuffleHistory.Count - 1;
        SelectedTracks.Clear();
        RaiseTrackListChanged();
        MarkPlaylistEdited();
    }

    /// <summary>Consume-mode removal: drop a single just-played track from the queue and any shuffle
    /// bookkeeping. Mirrors <see cref="RemoveSelected"/> for one row; never touches the file and never
    /// the still-playing track (the caller guarantees this row is no longer Current). The ListBox drops
    /// the row from its own selection when the item leaves the collection — no SelectedTracks fiddling.</summary>
    private void ConsumePlayed(TrackViewModel played)
    {
        Tracks.Remove(played);
        _shuffleHistory.Remove(played);
        if (_shufflePos >= _shuffleHistory.Count) _shufflePos = _shuffleHistory.Count - 1;
        RaiseTrackListChanged();
        MarkPlaylistEdited();
    }

    // ── Context-menu commands (bound from the queue's ListBox.ContextMenu) ────
    // Bulk actions operate on the multi-selection; the per-field ones target ContextTarget
    // (the single right-clicked row). Field is passed as a "Bpm"/"Key"/"Energy" CommandParameter.

    // File-touching commands run their I/O on a background thread (Task.Run) so slow writes —
    // e.g. on a network drive — never freeze the UI. The selection is snapshotted on the UI
    // thread first; the inner methods marshal their VM updates back via Dispatcher.Post.
    [RelayCommand] private Task WriteTags() => RunWriteAsync(fillMissingOnly: false);
    [RelayCommand] private Task FillMissing() => RunWriteAsync(fillMissingOnly: true);
    [RelayCommand] private void Reanalyze() => ReanalyzeSelected();
    [RelayCommand] private void RemoveRows() => RemoveSelected();
    [RelayCommand] private Task UndoWrite() => Task.Run(UndoLastWrite);

    private Task RunWriteAsync(bool fillMissingOnly)
    {
        var sel = SelectedTracks.ToList();           // snapshot selection on the UI thread
        return Task.Run(() => WriteSelectedTags(sel, fillMissingOnly));
    }

    [RelayCommand]
    private Task WriteOneField(string? field)
        => ContextTarget is { } t && TryField(field, out var f) ? Task.Run(() => WriteField(t, f)) : Task.CompletedTask;

    [RelayCommand]
    private Task KeepOneField(string? field)
        => ContextTarget is { } t && TryField(field, out var f) ? Task.Run(() => KeepField(t, f)) : Task.CompletedTask;

    [RelayCommand]
    private Task RestoreOneField(string? field)
        => ContextTarget is { } t && TryField(field, out var f) ? Task.Run(() => RestoreField(t, f)) : Task.CompletedTask;

    private static bool TryField(string? s, out AnalysisField f)
    {
        switch (s)
        {
            case "Bpm": f = AnalysisField.Bpm; return true;
            case "Key": f = AnalysisField.Key; return true;
            case "Energy": f = AnalysisField.Energy; return true;
            default: f = default; return false;
        }
    }

    // ---- Three-dots list menu commands ----------------------------------
    //
    // HarmonicSort, ClearList, WriteAllTags are surfaced from the "…" menu
    // in the transport bar. They operate on the full Tracks collection, not
    // just the current selection.

    /// <summary>
    /// Reorder the queue for the smoothest possible mix using
    /// <see cref="HarmonicSequencer"/> (greedy nearest-neighbour + 2-opt).
    ///
    /// <para>Start track: the currently-playing track if it is in the queue,
    /// otherwise the first track (index 0). The start track is always pinned to
    /// position 0 in the new order so playback is not disrupted.</para>
    ///
    /// <para>Unanalyzed tracks (no Key, BPM, or Energy) are moved to the END of the
    /// queue in their original relative order — they cannot contribute to a
    /// compatibility score.</para>
    ///
    /// <para>All four axes (Beat/groove, Tempo, Harmonic, Energy) are now active.
    /// The beat fingerprint is computed during the normal analysis pass (v4 blob) and
    /// stored on <see cref="AnalysisResult.Fingerprint"/>. Tracks analysed before v4
    /// will have Fingerprint = null and degrade gracefully (Beat axis excluded, remaining
    /// weights renormalised). Anti-monotony / energy-arc shaping is planned for a
    /// later iteration.</para>
    ///
    /// <para>Runs the sequencer on a background thread (it is O(n²) but queues are
    /// small). The collection reorder is marshalled back to the UI thread.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanHarmonicSort))]
    private Task HarmonicSort()
    {
        if (Tracks.Count < 2) return Task.CompletedTask;

        // Snapshot on the UI thread before going async.
        var snapshot = Tracks.ToList();

        // Pin the currently-playing track as the anchor — you mix OUT of it, so it must
        // stay first and not get re-sorted under your feet. When nothing is playing (set
        // prep: drop tracks in, sort), pass -1 so the sequencer picks its own best start
        // (highest average compatibility) instead of arbitrarily nailing down row 1.
        var startIndex = Current is { } cur ? snapshot.IndexOf(cur) : -1;

        return Task.Run(() =>
        {
            // Build TrackFeatures for each track.
            // Fingerprint comes from the stored AnalysisResult (computed during the normal
            // analysis pass by BeatFingerprintExtractor and persisted in the JUSTPLAY blob).
            // Tracks that have not yet been analysed or are too short have Fingerprint = null;
            // MixCompatibility degrades gracefully — the Beat axis is excluded and the
            // remaining weights (Tempo 0.30, Harmonic 0.20, Energy 0.10) are renormalised.
            var features = snapshot
                .Select(tvm =>
                {
                    var a = tvm.Model.Analysis;
                    if (a is null) return new TrackFeatures(null, null, null, null);
                    return new TrackFeatures(a.Bpm, a.Key, a.Energy, a.Fingerprint);
                })
                .ToList();

            var result = HarmonicSequencer.Sequence(features, startIndex);

            // Reorder the ObservableCollection on the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                // Build the reordered list from the original snapshot.
                var ordered = result.Order.Select(i => snapshot[i]).ToList();

                // Apply in-place: match what ApplySort does (Clear + re-add).
                Tracks.Clear();
                foreach (var tvm in ordered) Tracks.Add(tvm);
                RecalcIndexes();
                RaiseTrackListChanged();
            });
        });
    }

    /// <summary>Harmonic Sort is available only when there are at least two tracks AND no analysis
    /// is in flight — sequencing a queue whose rows are still being scored would order the
    /// not-yet-analysed tracks as if they had no features (dumping them to the end). The "…" menu
    /// item greys out automatically while <see cref="IsAnalyzing"/> is true.</summary>
    private bool CanHarmonicSort() => !IsAnalyzing && Tracks.Count >= 2;

    /// <summary>
    /// Empty the queue. If the current track is playing, stops playback first
    /// (same behaviour as the existing drag-off path when the last track is removed).
    /// Mirrors <c>ClearTracksCommand</c> (Ctrl+Delete / internal), surfaced here
    /// for the "…" list menu without needing a duplicate private method.
    /// </summary>
    [RelayCommand]
    private void ClearList()
    {
        _controller.Stop();
        Tracks.Clear();
        SetCurrent(null);
        _shuffleHistory.Clear();
        _shufflePos = -1;
        LoadedPlaylistPath = null;
        PlaylistDirty = false;
        RaiseTrackListChanged();
    }

    /// <summary>
    /// Remove duplicate tracks from the queue, keeping the first occurrence of each.
    /// Duplicate key = normalised "Artist | Title" (case-insensitive) so the same song
    /// added twice — even from different file copies / folders — collapses to one, while
    /// distinct remixes (different titles) survive. Falls back to the file path when
    /// artist+title are both empty. The currently-playing track is always kept.
    /// </summary>
    [RelayCommand]
    private void RemoveDuplicates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Current is not null) seen.Add(DuplicateKey(Current));

        var dupes = new List<TrackViewModel>();
        foreach (var t in Tracks)
        {
            if (ReferenceEquals(t, Current)) continue;   // never drop the playing track
            if (!seen.Add(DuplicateKey(t))) dupes.Add(t);
        }

        if (dupes.Count == 0) return;
        Tracks.RemoveRange(dupes);                 // one Reset, not N per-item removes
        _shuffleHistory.RemoveAll(dupes.Contains);
        if (_shufflePos >= _shuffleHistory.Count) _shufflePos = _shuffleHistory.Count - 1;
        RaiseTrackListChanged();
        MarkPlaylistEdited();
    }

    /// <summary>Normalised dedup key: "ArtistTitle" (case-insensitive), or the file path
    /// when both are empty. Title falls back to the filename, so two copies of the same file in
    /// different folders still collapse, while different remixes keep distinct titles.</summary>
    private static string DuplicateKey(TrackViewModel t)
    {
        var artist = (t.Artist ?? string.Empty).Trim();
        var title = (t.Title ?? string.Empty).Trim();
        if (artist.Length > 0 || title.Length > 0)
            return artist + "" + title;
        return t.Model.FilePath ?? string.Empty;
    }

    /// <summary>
    /// Write detected BPM / Key / Energy tags for every track in the queue —
    /// the same fields and consent logic as the per-selection "Write meta tags"
    /// in the context menu, just scoped to ALL tracks rather than the selection.
    ///
    /// <para>Runs on a background thread via <see cref="Task.Run"/> (file I/O on
    /// a network drive can block for hundreds of ms — the exact past freeze bug
    /// that prompted the off-thread pattern). The undo batch is populated so a
    /// single Ctrl+Z reverts the whole write.</para>
    /// </summary>
    [RelayCommand]
    private Task WriteAllTags()
    {
        // Snapshot the full queue on the UI thread before going async.
        var all = Tracks.ToList();
        return Task.Run(() => WriteSelectedTags(all, fillMissingOnly: false));
    }

    // ---- File intake ----------------------------------------------------

    /// <summary>Add dropped files/folders. Tracks appear instantly; tags load in the background.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths, bool preserveOrder = false)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            // Per-path guard: a flaky/slow network share (NAS, UNC) can throw on Exists/EnumerateFiles
            // (IO / access / path errors). Skip the bad path + report it rather than abort the whole drop.
            try
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories));
                else if (File.Exists(p))
                    files.Add(p);
            }
            catch (Exception ex)
            {
                ErrorReporter.Report(ex, $"Scanning dropped path (network/NAS?): {p}");
            }
        }

        // Folder drops get Explorer-like natural order ("track2" before "track10") instead of the OS's raw
        // enumeration order. But an EXPLICIT ordered list — a loaded playlist / sequenced DJ set — MUST keep
        // its order (preserveOrder); otherwise the carefully-built set gets alphabetised (the m3u-load bug:
        // a set dropped in started on whatever track sorted first by filename).
        IEnumerable<string> audioFiles = files.Where(IsAudio);
        if (!preserveOrder)
            audioFiles = audioFiles.OrderBy(f => f, NaturalComparer.Instance);
        var added = new List<TrackViewModel>();
        foreach (var f in audioFiles)
        {
            var tvm = new TrackViewModel(new Track(f))
            {
                AddOrder = _addSeq++,
                ToggleFavoriteCallback = ToggleFavoriteForTrack,
                PlayInPreCueCallback = LoadPreCueTrackFromRow,
                NormalizationTargetDb = LevelToLufs(NormalizationLevel),
            };
            Tracks.Add(tvm);
            added.Add(tvm);
        }

        if (added.Count == 0) return;
        RaiseTrackListChanged();
        MarkPlaylistEdited();

        // Read tags off the UI thread, then refresh each row. Two passes:
        //   pass 1 — metadata (fast, ~ms): titles/artists/duration show immediately.
        //   pass 2 — apply any stored analysis blob (free), then run the DSP for the rest —
        //            but ONLY when auto-analyze is on. Otherwise the user triggers analysis
        //            explicitly (right-click → Analyze). Concurrency = AnalysisThreads.
        await Task.Run(async () =>
        {
            foreach (var tvm in added)
            {
                var md = _metadata.Read(tvm.Model.FilePath);
                tvm.Model.Metadata = md;
                Dispatcher.UIThread.Post(() =>
                {
                    tvm.Refresh();
                    OnPropertyChanged(nameof(TotalDurationText));
                });
            }

            // Reproduce-the-picture: if a file already carries OUR analysis blob at the current
            // engine version, trust it and apply it for free — the file is the memory, the DSP is
            // deterministic. Collect the rest for (optional) DSP analysis.
            var needAnalysis = new List<TrackViewModel>();
            foreach (var tvm in added)
            {
                var stored = tvm.Model.Metadata?.StoredAnalysis;
                if (stored is not null)
                {
                    // Show OUR last-known detection (Camelot key, our BPM/energy) and TRUST it as-is.
                    // We do NOT auto-re-analyze a track just because its blob predates the current
                    // detection version — refreshing to a newer version is an explicit, user-driven
                    // action (right-click → Re-analyze). This keeps a cleanly-tagged file from being
                    // silently re-analysed (and, with AutoWriteOnAnalyze on, rewritten on disk) merely
                    // by loading it. (User decision, 2026-06-12.)
                    tvm.Model.Analysis = stored.Detected;
                    tvm.Model.AnalysisStatus = AnalysisStatus.Done;
                    Dispatcher.UIThread.Post(() => tvm.Refresh());
                    // One exception: loudness normalization needs a ReplayGain figure. An older blob
                    // that predates ReplayGain has none, so it's still measured when normalization is on.
                    if (PlaybackNormalization && stored.Detected.ReplayGainDb is null)
                        needAnalysis.Add(tvm);
                }
                else
                {
                    needAnalysis.Add(tvm);
                }
            }

            // Auto-analyze on add when the user opted in — OR when loudness normalization is on,
            // since that needs each track's ReplayGain to do anything (the user chose to couple the
            // two: turning normalization on implies "measure my tracks so it just works").
            if ((AutoAnalyze || PlaybackNormalization) && needAnalysis.Count > 0)
                await AnalyzeTracksAsync(needAnalysis);
        });
    }

    /// <summary>Route files handed to us by a launch / file-association / single-instance forward.
    /// A playlist (.m3u8/.m3u) is a COMPLETE set, so it REPLACES the queue; plain audio files are
    /// ADDED (the existing consume-friendly behaviour). Mixed input favours the playlist.</summary>
    public async Task OpenIncomingAsync(IReadOnlyList<string> paths, bool addOnly)
    {
        var playlist = paths.FirstOrDefault(M3uPlaylist.IsPlaylist);
        if (playlist is not null)
        {
            await LoadPlaylistAsync(playlist);
            return;
        }
        await OpenFilesAsync(paths, play: !addOnly);
    }

    /// <summary>Add audio files to the queue (file-association / "Add to JustPlay"). When
    /// <paramref name="play"/> is set (the "open" verb — e.g. double-clicking a file in Explorer),
    /// the first newly-added track starts playing IMMEDIATELY, like every other player — even if a
    /// set is already playing (opening a file means "play this"). The "Add to JustPlay" verb and
    /// drag-drop pass play=false (append only, never interrupt).</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths, bool play)
    {
        var before = Tracks.Count;
        await AddPathsAsync(paths);
        if (play && Tracks.Count > before)
            PlayTrack(Tracks[before]);   // auto-play the opened file (double-click → it just plays)
    }

    /// <summary>Load an M3U/M3U8 playlist, REPLACING the whole queue with its tracks — a playlist is
    /// a complete set, not tracks to append, so opening one swaps the list and starts from the top.
    /// Reading happens off the UI thread; the missing/foreign entries are silently skipped.</summary>
    public async Task LoadPlaylistAsync(string playlistPath)
    {
        var audio = (await Task.Run(() => M3uPlaylist.ReadPaths(playlistPath)))
            .Where(IsAudio).ToList();
        if (audio.Count == 0) return;

        ClearList();                 // stop playback, clear queue + shuffle bookkeeping
        await AddPathsAsync(audio, preserveOrder: true);   // a playlist IS the order — never re-sort it
        if (Tracks.Count > 0) PlayTrack(Tracks[0]);
        // Remember the loaded playlist so Save can write back in place.
        // _loadedPlaylistPath was null during AddPathsAsync (ClearList nulled it), so
        // MarkPlaylistEdited inside AddPathsAsync was a no-op — the load itself is NOT dirty.
        LoadedPlaylistPath = playlistPath;
        PlaylistDirty = false;
    }

    /// <summary>Write the current queue order back into the loaded playlist file (in place).
    /// Only callable when a playlist is loaded (<see cref="HasLoadedPlaylist"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasLoadedPlaylist))]
    private async Task SavePlaylistAsync()
    {
        if (LoadedPlaylistPath is null) return;
        await ExportPlaylistM3uAsync(LoadedPlaylistPath);
        PlaylistDirty = false;
    }

    /// <summary>Mark the loaded playlist as having unsaved edits. No-op when no playlist is loaded
    /// or the queue is empty — emptying the queue clears the reference entirely via
    /// <see cref="RaiseTrackListChanged"/>.</summary>
    private void MarkPlaylistEdited()
    {
        if (LoadedPlaylistPath != null && Tracks.Count > 0) PlaylistDirty = true;
    }

    /// <summary>Export the current queue order to an M3U8 playlist — the universal format Traktor,
    /// rekordbox, Serato &amp; co. all import. Snapshots on the UI thread, writes off it (a network
    /// drive can block). Analysis travels via the files' tags; we never touch the other tool's library.</summary>
    public Task ExportPlaylistM3uAsync(string destPath)
    {
        var entries = Tracks.Select(t => new M3uPlaylist.Entry(
            t.Model.FilePath, t.Model.Metadata?.Duration, BuildPlaylistTitle(t))).ToList();
        return Task.Run(() => M3uPlaylist.Write(destPath, entries));
    }

    /// <summary>Export the queue as a self-contained .zip — every audio file (numbered in set order)
    /// plus an .m3u8 listing them — for sharing / upload / USB-stick handoff into other DJ tools.
    /// Snapshots on the UI thread, zips off it (copying audio can take a while). <paramref name="playlistName"/>
    /// names the .m3u8 inside the archive. Returns the number of files written.</summary>
    public Task<int> ExportPlaylistZipAsync(string destPath, string playlistName,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var entries = Tracks.Select(t => new PlaylistBundle.Entry(
            t.Model.FilePath, t.Model.Metadata?.Duration, BuildPlaylistTitle(t))).ToList();
        return Task.Run(() => PlaylistBundle.WriteZip(destPath, playlistName, entries, progress, ct), ct);
    }

    /// <summary>Export the queue into a folder — every audio file (numbered in set order) copied in, plus
    /// an .m3u8 listing them — the unpacked sibling of <see cref="ExportPlaylistZipAsync"/>, ideal for a
    /// USB stick. Snapshots on the UI thread, copies off it. Returns the number of files written.</summary>
    public Task<int> ExportPlaylistFolderAsync(string destFolder, string playlistName,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var entries = Tracks.Select(t => new PlaylistBundle.Entry(
            t.Model.FilePath, t.Model.Metadata?.Duration, BuildPlaylistTitle(t))).ToList();
        return Task.Run(() => PlaylistBundle.WriteToFolder(destFolder, playlistName, entries, progress, ct), ct);
    }

    private static string BuildPlaylistTitle(TrackViewModel t)
    {
        var artist = (t.Artist ?? string.Empty).Trim();
        var title = (t.Title ?? string.Empty).Trim();
        return artist.Length > 0 ? $"{artist} - {title}" : title;
    }

    private static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    /// <summary>Explorer-style natural/logical string ordering: compares digit runs by numeric value
    /// ("track2" &lt; "track10") and the rest case-insensitively. Managed (no P/Invoke) so it stays
    /// portable and trim/AOT-safe.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;   // longer number = larger
                    var cmp = na.CompareTo(nb, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }

    // ---- Internals ------------------------------------------------------

    /// <summary>
    /// The one navigation entry-point. Resolves the next track, then — in consume mode —
    /// drops the track we just left. Splitting the navigation into <see cref="AdvanceCore"/>
    /// keeps every early-return path in the core logic intact while the consume step runs
    /// exactly once, after we know whether we actually moved.
    /// </summary>
    private void Advance(bool forward, bool auto, int crossfadeMs = 0)
    {
        var outgoing = Current;
        AdvanceCore(forward, auto, crossfadeMs);

        // Consume mode: once we've stepped FORWARD off a track (natural end or manual skip),
        // remove it from the queue. Guarded so we never consume when nothing moved — a
        // repeat-one replay or an end-of-queue stop leaves Current pointing at the same track.
        // Backward navigation never consumes (stepping back means you want it again).
        if (forward && RemovePlayed && outgoing is not null && !ReferenceEquals(outgoing, Current))
            ConsumePlayed(outgoing);
    }

    /// <summary><paramref name="auto"/> distinguishes a track ending on its own (respects Repeat
    /// fully) from a user pressing next/prev (always moves — Repeat-One never traps the user on
    /// one track).</summary>
    private void AdvanceCore(bool forward, bool auto, int crossfadeMs = 0)
    {
        if (Tracks.Count == 0) return;

        // Repeat-One only fires on natural end: replay the current track.
        if (auto && Repeat == RepeatMode.One && Current is not null)
        {
            PlayInternal(Current);
            return;
        }

        if (Shuffle) { AdvanceShuffle(forward, auto, crossfadeMs); return; }

        // ── Linear navigation ──
        var index = Current is null ? -1 : Tracks.IndexOf(Current);
        if (forward)
        {
            var next = index + 1;
            if (next >= Tracks.Count)
            {
                // End of queue. Manual next wraps; auto-advance only wraps under
                // Repeat-All, otherwise it stops at the end.
                if (auto && Repeat != RepeatMode.All) { _controller.Stop(); return; }
                next = 0;
            }
            PlayInternal(Tracks[next], crossfadeMs);
        }
        else
        {
            var prev = index - 1;
            if (prev < 0) prev = Tracks.Count - 1; // wrap to the end
            PlayInternal(Tracks[prev]);
        }
    }

    // ── Shuffle navigation ───────────────────────────────────────────────

    private void AdvanceShuffle(bool forward, bool auto, int crossfadeMs = 0)
    {
        if (forward)
        {
            // Re-tread forward through history first (user went back, now going
            // forward again) before generating a new pick at the frontier.
            if (_shufflePos < _shuffleHistory.Count - 1)
            {
                _shufflePos++;
                PlayInternal(_shuffleHistory[_shufflePos], crossfadeMs);
                return;
            }

            var pick = PickUnplayed();
            if (pick is null)
            {
                // Cycle exhausted — every track has played once this cycle.
                // Start a new cycle on Repeat-All, OR on a manual next (the user
                // explicitly asked to move — always honour that, like the linear
                // wrap). Auto-advance with Repeat-Off is the only case that stops.
                if (Repeat == RepeatMode.All || !auto)
                {
                    // New cycle anchored on the current track so it isn't the
                    // immediate next pick.
                    ResetShuffleFrom(Current);
                    pick = PickUnplayed();
                    // Nothing else to pick (1-track queue): replay the current.
                    if (pick is null) { if (Current is not null) PlayInternal(Current); return; }
                }
                else
                {
                    _controller.Stop();
                    return;
                }
            }

            _shuffleHistory.Add(pick);
            _shufflePos = _shuffleHistory.Count - 1;
            PlayInternal(pick, crossfadeMs);
        }
        else
        {
            // Step back through what we actually played; at the start, stay put
            // (the >3s restart in Previous() already handled the common case).
            if (_shufflePos > 0)
            {
                _shufflePos--;
                PlayInternal(_shuffleHistory[_shufflePos]);
            }
        }
    }

    /// <summary>Pick a random track not yet played in the current shuffle cycle, or null
    /// when the bag is empty.</summary>
    private TrackViewModel? PickUnplayed()
    {
        var played = new HashSet<TrackViewModel>(_shuffleHistory);
        var pool = Tracks.Where(t => !played.Contains(t)).ToList();
        return pool.Count == 0 ? null : pool[_rng.Next(pool.Count)];
    }

    /// <summary>Begin a fresh shuffle cycle anchored on <paramref name="anchor"/> (the track
    /// already playing), so it counts as "played" and won't be the next pick.</summary>
    private void ResetShuffleFrom(TrackViewModel? anchor)
    {
        _shuffleHistory.Clear();
        _shufflePos = -1;
        if (anchor is not null)
        {
            _shuffleHistory.Add(anchor);
            _shufflePos = 0;
        }
    }

    private void SetCurrent(TrackViewModel? track)
    {
        _crossfadeArmed = false;
        if (Current is not null) Current.IsCurrent = false;
        Current = track;
        if (track is not null)
        {
            track.IsCurrent = true;

            // Push now-playing metadata to the Icecast cast (no-op when not connected).
            if (_broadcast.State == BroadcastState.Connected)
            {
                var md = track.Model.Metadata;
                var artist = md?.Artist ?? string.Empty;
                var title  = md?.Title  ?? track.Title;
                var nowPlaying = string.IsNullOrWhiteSpace(artist)
                    ? title
                    : $"{artist} - {title}";
                _ = _broadcast.UpdateNowPlayingAsync(nowPlaying);
            }
        }
    }

    private void Tick()
    {
        DurationSeconds = _controller.Duration.TotalSeconds;
        var pos = _controller.Position.TotalSeconds;

        // Hold the slider at the user's seek target until the engine reports a position near
        // it — otherwise one tick of stale pre-seek position snaps the thumb backwards. Clear
        // the hold once we're within tolerance, or after a short timeout as a safety valve.
        if (_pendingSeek is { } target)
        {
            if (Math.Abs(pos - target) <= 0.75 || ++_pendingSeekTicks >= 5)
                _pendingSeek = null;
            else
                return; // keep showing the target; don't write the stale position back
        }

        _suppressSeek = true;
        PositionSeconds = pos;
        _suppressSeek = false;
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));

        MaybeStartCrossfade(pos, DurationSeconds);

        // ── Pre-cue position polling (same 200 ms tick) ────────────────────
        // Mirrors the main engine's seek-hold pattern so the slider doesn't snap back
        // to a stale position immediately after the user scrubs.
        var cuePos = _preListen.Position.TotalSeconds;
        if (_pendingPreCueSeek is { } cueTarget)
        {
            if (Math.Abs(cuePos - cueTarget) <= 0.75 || ++_pendingPreCueSeekTicks >= 5)
                _pendingPreCueSeek = null;
        }
        else
        {
            _suppressPreCueSeek = true;
            PreCuePositionSeconds = cuePos;
            _suppressPreCueSeek = false;
            OnPropertyChanged(nameof(PreCuePositionText));
        }
        PreCueDurationSeconds = _preListen.Duration.TotalSeconds;
        OnPropertyChanged(nameof(PreCueDurationText));

        // Auto-reconnect "CLOU": only worth polling device availability while the PRE-CUE tab is
        // actually open (cheap either way — a handful of Bass.GetDeviceInfo calls — but no reason
        // to run it in the background).
        if (IsPreCueTab) PollPreCueDeviceRebind();
    }

    private void MaybeStartCrossfade(double posSeconds, double durationSeconds)
    {
        var secs = CrossfadeSeconds;
        if (secs <= 0 || durationSeconds <= 0 || !IsPlaying) return;
        if (Repeat == RepeatMode.One) return;                    // self-overlap makes no sense

        // Trigger a touch before (duration - fade) so the 200ms tick can't miss the window / clip the end.
        var triggerAt = durationSeconds - secs - 0.25;
        if (posSeconds < triggerAt) { _crossfadeArmed = false; return; }  // re-arm if user seeked back out
        if (_crossfadeArmed) return;
        if (!HasNextForAutoAdvance()) { _crossfadeArmed = true; return; } // last track → let it play out & stop

        _crossfadeArmed = true;
        Advance(forward: true, auto: true, crossfadeMs: secs * 1000);
    }

    /// <summary>Would an auto-advance from the current track move to another track (vs. stop at the end
    /// of the queue)? Mirrors the stop conditions in AdvanceCore/AdvanceShuffle. Used so the crossfade
    /// window-trigger never fires on the final track (which would cut it short by the fade length).</summary>
    private bool HasNextForAutoAdvance()
    {
        if (Tracks.Count == 0 || Current is null) return false;
        if (Repeat == RepeatMode.All) return true;               // always wraps
        if (Shuffle) return PickUnplayed() is not null;          // unplayed remaining this cycle
        return Tracks.IndexOf(Current) + 1 < Tracks.Count;       // a later track exists
    }

    private void OnEngineStateChanged(object? sender, PlaybackState state)
        => Dispatcher.UIThread.Post(() => IsPlaying = state == PlaybackState.Playing);

    private void OnTrackEnded(object? sender, Track? ended)
        => Dispatcher.UIThread.Post(() => Advance(forward: true, auto: true)); // auto-advance (honours shuffle/repeat)

    partial void OnVolumeChanged(double value) => _controller.Volume = value;

    partial void OnPositionSecondsChanged(double value)
    {
        if (_suppressSeek) return; // change came from the timer, not the user
        _pendingSeek = value;      // hold the slider here until the engine catches up (see Tick)
        _pendingSeekTicks = 0;
        _controller.Position = TimeSpan.FromSeconds(value);
    }

    private static string Format(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>
    /// Graceful-quit helper called by <see cref="MainWindow"/> during <c>OnClosing</c>.
    /// Fades the master output to silence before the window (and therefore the engine) is
    /// disposed, preventing the hard digital click/buzz of an abrupt BASS free mid-playback.
    ///
    /// <para>Only meaningful when the engine is actively playing — the engine's own no-op guard
    /// handles the stopped/paused case so callers do not need to check first.</para>
    ///
    /// <para>200 ms is long enough for the human ear to perceive a smooth fade (no click), short
    /// enough that quit never feels laggy. The engine clamps the value to 50–500 ms.</para>
    /// </summary>
    public async Task FadeBeforeQuitAsync()
    {
        await _engine.FadeOutAsync(200);    // graceful fade to silence first (avoids the hard-stop click)
        _controller.FlushPendingWrites();   // then release the handle + land any deferred tag writes
    }

    public void Dispose()
    {
        _timer.Stop();
        _controller.StateChanged -= OnEngineStateChanged;
        _controller.TrackEnded -= OnTrackEnded;
        _broadcast.StateChanged -= OnBroadcastServiceStateChanged;
        _preListen.StateChanged -= OnPreCueStateChanged;
        _preListen.PlaybackEnded -= OnPreCueEnded;
        _preListen.Dispose();
        _controller.Dispose();
    }
}
