using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
using JustPlay.Core.Theming;

namespace JustPlay.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif"
    };

    private readonly PlaybackController _controller;
    private readonly IMetadataReader _metadata;
    private readonly ITrackAnalysisService _analysis;
    private readonly ISettingsService _settings;
    private readonly IThemeService _themes;
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
        IMetadataReader metadata,
        ITrackAnalysisService analysis,
        ISettingsService settings,
        IThemeService themes)
    {
        _controller = controller;
        _metadata = metadata;
        _analysis = analysis;
        _settings = settings;
        _themes = themes;

        // Seed the tweak properties from persisted settings BEFORE wiring
        // any change-listeners — so the seeding itself does not echo back to
        // disk via On…Changed → Save.
        var s = _settings.Current;
        _currentTheme = s.Theme;
        _vinylSpinEnabled = s.VinylSpinEnabled;
        _waveformEnabled = s.WaveformEnabled;
        _defaultTab = s.DefaultTab;
        _settingsHydrated = true;

        _controller.StateChanged += OnEngineStateChanged;
        _controller.TrackEnded += OnTrackEnded;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

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

    // ── Tweaks-panel state (mirrors TWEAK_DEFAULTS in the design's app.jsx) ─
    [ObservableProperty] private bool _isTweaksOpen;
    [ObservableProperty] private string _currentTheme = "Aurora";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShouldSpin))]
    private bool _vinylSpinEnabled = true;
    [ObservableProperty] private bool _waveformEnabled = true;
    [ObservableProperty] private string _defaultTab = "Up Next";

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

    partial void OnDefaultTabChanged(string value)
    {
        if (!_settingsHydrated) return;
        PersistSettings();
    }

    private void PersistSettings() => _settings.Save(new UserSettings
    {
        Theme            = CurrentTheme,
        VinylSpinEnabled = VinylSpinEnabled,
        WaveformEnabled  = WaveformEnabled,
        DefaultTab       = DefaultTab,
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
    private void SetDefaultTab(string? tab)
    {
        if (!string.IsNullOrEmpty(tab)) DefaultTab = tab;
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
    }

    /// <summary>Number the rows 1..N — these are positions in the current session list,
    /// NOT the MP3 metadata track number.</summary>
    private void RecalcIndexes()
    {
        for (var i = 0; i < Tracks.Count; i++)
            Tracks[i].Index = i + 1;
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

        var added = new List<TrackViewModel>();
        foreach (var f in files.Where(IsAudio))
        {
            var tvm = new TrackViewModel(new Track(f));
            Tracks.Add(tvm);
            added.Add(tvm);
        }

        if (added.Count == 0) return;
        RaiseTrackListChanged();

        // Read tags off the UI thread, then refresh each row, then kick off
        // BPM/key/energy analysis. Two passes per track:
        //   pass 1 — metadata (fast, ~milliseconds): so titles/artists/duration
        //            show up immediately and the queue feels responsive.
        //   pass 2 — analysis (slow, ~seconds via BASS_FX): so the BPM cell
        //            fills in as soon as each track is done, not at the end of
        //            the whole batch.
        // Pass 2 runs serially per track on purpose — BASS_FX BPMDecodeGet pegs
        // a core for the duration of a track; parallelising would only swap
        // serial slowness for thrash. Future: throughput batching if needed.
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

            foreach (var tvm in added)
            {
                tvm.Model.AnalysisStatus = Core.Models.AnalysisStatus.Running;
                try
                {
                    var result = await _analysis.AnalyzeAsync(tvm.Model.FilePath);
                    tvm.Model.Analysis = result;
                    tvm.Model.AnalysisStatus = Core.Models.AnalysisStatus.Done;
                }
                catch (Exception ex)
                {
                    tvm.Model.AnalysisStatus = Core.Models.AnalysisStatus.Failed;
                    Console.WriteLine($"[Analysis FAIL] {tvm.Model.FilePath}: {ex.Message}");
                }

                Dispatcher.UIThread.Post(() => tvm.Refresh());
            }
        });
    }

    private static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    // ---- Internals ------------------------------------------------------

    /// <summary>
    /// The one navigation entry-point. <paramref name="auto"/> distinguishes a track
    /// ending on its own (respects Repeat fully) from a user pressing next/prev
    /// (always moves — Repeat-One never traps the user on one track).
    /// </summary>
    private void Advance(bool forward, bool auto)
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
        if (track is not null) track.IsCurrent = true;
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
        _controller.Dispose();
    }
}
