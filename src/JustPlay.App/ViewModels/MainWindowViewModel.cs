using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
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
    private readonly IMetadataReader _metadata;
    private readonly IMetadataWriter _writer;
    private readonly ITrackAnalysisService _analysis;
    private readonly ISettingsService _settings;
    private readonly IThemeService _themes;
    private readonly IBroadcastService _broadcast;
    private readonly DispatcherTimer _timer;
    private bool _suppressSeek;

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
        IMetadataReader metadata,
        IMetadataWriter writer,
        ITrackAnalysisService analysis,
        ISettingsService settings,
        IThemeService themes,
        IBroadcastService broadcast)
    {
        _controller = controller;
        _engine = engine;
        _metadata = metadata;
        _writer = writer;
        _analysis = analysis;
        _settings = settings;
        _themes = themes;
        _broadcast = broadcast;

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
        _analysisThreads = s.AnalysisThreads;

        // Seed streaming profiles from persisted settings.
        foreach (var p in s.StreamServers)
            StreamServers.Add(p);
        if (s.SelectedStreamServerId is { } selectedId)
            _selectedStreamServer = StreamServers.FirstOrDefault(p => p.Id == selectedId);

        // ── Output device: populate list, select saved device (or default) ────
        // Enumerate devices BEFORE setting _settingsHydrated so the initial
        // SelectedOutputDevice assignment doesn't trigger a premature PersistSettings.
        PopulateOutputDevices(s.OutputDeviceName);

        _settingsHydrated = true;

        _controller.StateChanged += OnEngineStateChanged;
        _controller.TrackEnded += OnTrackEnded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

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
    public string DurationSortGlyph => Glyph("Duration");

    partial void OnSortColumnChanged(string? value) => RaiseSortHeaders();
    partial void OnSortDescendingChanged(bool value) => RaiseSortHeaders();

    private void RaiseSortHeaders()
    {
        OnPropertyChanged(nameof(TitleSortGlyph));
        OnPropertyChanged(nameof(GenreSortGlyph));
        OnPropertyChanged(nameof(BpmSortGlyph));
        OnPropertyChanged(nameof(KeySortGlyph));
        OnPropertyChanged(nameof(NrgSortGlyph));
        OnPropertyChanged(nameof(DurationSortGlyph));
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
                    "Duration" => Nullable.Compare(a.Model.Metadata?.Duration, b.Model.Metadata?.Duration),
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

    [ObservableProperty] private TrackViewModel? _current;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnalysisThreadsText))]
    private int _analysisThreads = 4;

    /// <summary>String form of the thread count, so the Tweaks radio buttons can drive their
    /// active state via the existing StringEquals converter.</summary>
    public string AnalysisThreadsText => AnalysisThreads.ToString();

    // ── Live analysis progress ────────────────────────────────────────────
    // Number of tracks currently mid-analysis across ALL in-flight batches.
    // Mutated ONLY on the UI thread (every change is marshalled through
    // Dispatcher) so the generated setter never races the background DSP
    // threads. Drives the "ANALYZING N" header badge and gates Harmonic Sort —
    // sequencing a half-analysed queue would mis-order the not-yet-scored rows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalyzing))]
    [NotifyPropertyChangedFor(nameof(AnalyzingText))]
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
        AnalysisThreads    = AnalysisThreads,
        UseAiKeyDetection  = _settings.Current.UseAiKeyDetection, // preserve (no UI yet)
        // Output device — persist by name so it survives a reboot (index can change).
        OutputDeviceName = SelectedOutputDevice?.Name,
        // Streaming profiles — persist the full list and selected profile id.
        StreamServers          = [.. StreamServers],
        SelectedStreamServerId = SelectedStreamServer?.Id,
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
    private void PlayInternal(TrackViewModel track)
    {
        SetCurrent(track);
        _controller.Play(track.Model);
        _controller.Volume = Volume;
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
        // Harmonic Sort's CanExecute depends on the queue having ≥ 2 tracks.
        HarmonicSortCommand.NotifyCanExecuteChanged();
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
            State = state,
            Comment = newComment,
        };
        DoWrite(tvm, write);
    }

    /// <summary>The single point that touches the file. If the track is currently playing, BASS
    /// holds its file open, so we pause (release the handle) → write → resume at the same spot.
    /// Writes, then re-reads the tags so the row reflects the new values + decisions (the bold/dot
    /// conflict clears). Robust: a write failure logs and leaves the row untouched (the flag stays,
    /// honestly signalling nothing was written), never crashes the app. Returns whether it stuck.</summary>
    private bool DoWrite(TrackViewModel tvm, TagWrite write)
    {
        // Snapshot the pre-write tag state for Undo BEFORE touching the file; only committed below
        // if the write actually succeeds.
        var snapshot = _capturingUndo ? SnapshotOf(tvm) : null;
        var ok = false;

        _controller.WithFileReleased(tvm.Model, () =>
        {
            try
            {
                _writer.Write(tvm.Model.FilePath, write);
                tvm.Model.Metadata = _metadata.Read(tvm.Model.FilePath);
                Dispatcher.UIThread.Post(tvm.Refresh);
                ok = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Tag write FAIL] {tvm.Model.FilePath}: {ex.GetType().Name}: {ex.Message}");
            }
        });

        if (ok && snapshot is not null)
            lock (_undoBatch) _undoBatch.Add((tvm, snapshot));
        return ok;
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
        var targets = SelectedTracks.ToList();
        if (targets.Count == 0) return;
        foreach (var tvm in targets)
        {
            Tracks.Remove(tvm);
            _shuffleHistory.Remove(tvm);
        }
        if (_shufflePos >= _shuffleHistory.Count) _shufflePos = _shuffleHistory.Count - 1;
        SelectedTracks.Clear();
        RaiseTrackListChanged();
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

        // The pinned start is the current track if it is in the queue; otherwise 0.
        var startIndex = Current is { } cur ? snapshot.IndexOf(cur) : 0;
        if (startIndex < 0) startIndex = 0;

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
        foreach (var d in dupes) Tracks.Remove(d);
        RaiseTrackListChanged();
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
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories));
            else if (File.Exists(p))
                files.Add(p);
        }

        // Sort like Explorer (natural/logical order: "track2" before "track10"), so a dropped
        // folder lands in a sensible, predictable order instead of the OS's raw enumeration order.
        var added = new List<TrackViewModel>();
        foreach (var f in files.Where(IsAudio).OrderBy(f => f, NaturalComparer.Instance))
        {
            var tvm = new TrackViewModel(new Track(f))
            {
                AddOrder = _addSeq++,
                ToggleFavoriteCallback = ToggleFavoriteForTrack,
            };
            Tracks.Add(tvm);
            added.Add(tvm);
        }

        if (added.Count == 0) return;
        RaiseTrackListChanged();

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
                    // Show OUR last-known detection IMMEDIATELY (Camelot key, our BPM/energy) instead of
                    // flashing the foreign tag value. Current-version blobs are trusted outright; a stale
                    // blob (e.g. after a detection-version bump) is still displayed now and quietly refined
                    // by re-analysis below — so the row never first shows the old/foreign key.
                    tvm.Model.Analysis = stored.Detected;
                    tvm.Model.AnalysisStatus = AnalysisStatus.Done;
                    Dispatcher.UIThread.Post(() => tvm.Refresh());
                    if (stored.Version != TrackAnalysisState.CurrentVersion)
                        needAnalysis.Add(tvm);
                }
                else
                {
                    needAnalysis.Add(tvm);
                }
            }

            if (AutoAnalyze && needAnalysis.Count > 0)
                await AnalyzeTracksAsync(needAnalysis);
        });
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
    private void Advance(bool forward, bool auto)
    {
        var outgoing = Current;
        AdvanceCore(forward, auto);

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
    private void AdvanceCore(bool forward, bool auto)
    {
        if (Tracks.Count == 0) return;

        // Repeat-One only fires on natural end: replay the current track.
        if (auto && Repeat == RepeatMode.One && Current is not null)
        {
            PlayInternal(Current);
            return;
        }

        if (Shuffle) { AdvanceShuffle(forward, auto); return; }

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
            PlayInternal(Tracks[next]);
        }
        else
        {
            var prev = index - 1;
            if (prev < 0) prev = Tracks.Count - 1; // wrap to the end
            PlayInternal(Tracks[prev]);
        }
    }

    // ── Shuffle navigation ───────────────────────────────────────────────

    private void AdvanceShuffle(bool forward, bool auto)
    {
        if (forward)
        {
            // Re-tread forward through history first (user went back, now going
            // forward again) before generating a new pick at the frontier.
            if (_shufflePos < _shuffleHistory.Count - 1)
            {
                _shufflePos++;
                PlayInternal(_shuffleHistory[_shufflePos]);
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
            PlayInternal(pick);
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
        _suppressSeek = true;
        PositionSeconds = _controller.Position.TotalSeconds;
        _suppressSeek = false;
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
    }

    private void OnEngineStateChanged(object? sender, PlaybackState state)
        => Dispatcher.UIThread.Post(() => IsPlaying = state == PlaybackState.Playing);

    private void OnTrackEnded(object? sender, Track? ended)
        => Dispatcher.UIThread.Post(() => Advance(forward: true, auto: true)); // auto-advance (honours shuffle/repeat)

    partial void OnVolumeChanged(double value) => _controller.Volume = value;

    partial void OnPositionSecondsChanged(double value)
    {
        if (_suppressSeek) return; // change came from the timer, not the user
        _controller.Position = TimeSpan.FromSeconds(value);
    }

    private static string Format(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _timer.Stop();
        _controller.StateChanged -= OnEngineStateChanged;
        _controller.TrackEnded -= OnTrackEnded;
        _broadcast.StateChanged -= OnBroadcastServiceStateChanged;
        _controller.Dispose();
    }
}
