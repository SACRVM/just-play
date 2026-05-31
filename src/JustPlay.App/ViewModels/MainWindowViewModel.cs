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
    private void Next() => Step(+1);

    [RelayCommand]
    private void Previous() => Step(-1);

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

    private void Step(int delta)
    {
        if (Tracks.Count == 0) return;
        var index = Current is null ? -1 : Tracks.IndexOf(Current);
        var next = ((index + delta) % Tracks.Count + Tracks.Count) % Tracks.Count;
        PlayTrack(Tracks[next]);
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
        => Dispatcher.UIThread.Post(() => Step(+1)); // auto-advance through the list

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
