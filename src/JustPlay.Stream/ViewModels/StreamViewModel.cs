using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
using JustPlay.Core.Logging;
using JustPlay.Core.Models;
using JustPlay.Stream.Settings;
using JustPlay.UI.Controls;
using JustPlay.UI.Logging;
using JustPlay.UI.Views;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// Main-window ViewModel for JUST STREAM. Owns the whole live broadcast surface:
/// input-device selection (→ capture), the bus DSP rack, connect/disconnect, the live readouts,
/// L/R meters, and the errors-only log. Layout it drives: just-stream-blueprint.md §3a / §7.3.
///
/// Threading: broadcast state changes arrive on a BASS thread and are marshalled to the UI thread.
/// Meters + stream time are pumped once per render frame (vsync-synced) via PumpFrame(), driven by
/// the View's RequestAnimationFrame loop — no free-running timer (it juddered against vsync).
/// </summary>
public sealed partial class StreamViewModel : ObservableObject, IDisposable
{
    private readonly IAudioInputEngine _engine;
    private readonly IBroadcastService _broadcast;
    private readonly IRecordingService _recording;
    private readonly JsonStreamSettingsService _settings;
    private readonly Stopwatch _streamClock = new();
    private bool _recAutoStarted; // recording was started BY auto-record → auto-stop on disconnect
    private bool _loading; // suppress persistence/engine writes while hydrating from settings

    /// <summary>Limiter drive options for the DSP strip ComboBox (maps in <see cref="ApplyLimiter"/>).</summary>
    public string[] LimiterDrives { get; } = { "Off", "Soft", "Club", "Loud" };

    /// <summary>Codec options for the main-page CODEC dropdown (quality setting, editable while offline).</summary>
    public StreamFormat[] Formats { get; } = { StreamFormat.Mp3, StreamFormat.Opus };

    /// <summary>Bitrate (kbps) options for the main-page QUALITY dropdown.</summary>
    public int[] Bitrates { get; } = { 128, 192, 256, 320 };

    /// <summary>Sample rate options for the RATE dropdown. 48 kHz = Opus native; 44.1 kHz = most DJ software.</summary>
    public int[] SampleRates { get; } = { 44100, 48000 };

    /// <summary>Format options for the Settings → Recording FORMAT dropdown
    /// (labels via <see cref="RecordingFormatLabelConverter"/>).</summary>
    public RecordingFormat[] RecordingFormats { get; } =
    {
        Core.Models.RecordingFormat.SameAsStream,
        Core.Models.RecordingFormat.Mp3_320,
        Core.Models.RecordingFormat.Flac,
        Core.Models.RecordingFormat.Aiff,
        Core.Models.RecordingFormat.Wav,
    };

    // ── Server profiles ──────────────────────────────────────────────────
    public ObservableCollection<StreamServerProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedProfileFormat))]
    [NotifyPropertyChangedFor(nameof(SelectedProfileBitrateKbps))]
    [NotifyPropertyChangedFor(nameof(CanBroadcastSongInfo))]
    [NotifyPropertyChangedFor(nameof(IsSongInfoActive))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    private StreamServerProfile? _selectedProfile;

    // ── Input devices ────────────────────────────────────────────────────
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty]
    private AudioInputDevice? _selectedInputDevice;

    // ── App capture (Phase 0 "capture a specific APP" source) ─────────────
    /// <summary>True when this build can capture a single app's audio directly — gates the SOURCE toggle.</summary>
    public bool SupportsAppCapture => _engine.SupportsApplicationCapture;

    /// <summary>Off = capture a device/loopback (default); On = capture ONE application's audio directly.</summary>
    [ObservableProperty]
    private bool _isAppSourceMode;

    /// <summary>The apps whose audio can be captured (DJ apps first). Populated on demand in app mode.</summary>
    public ObservableCollection<CaptureApp> CaptureApps { get; } = new();

    [ObservableProperty]
    private CaptureApp? _selectedCaptureApp;

    /// <summary>Broadcast-channel options for a captured app, in picker order: channels 1-2 (the
    /// default — on multi-out DJ gear that's the Master, dropping the Cue on 3-4; lossless for a plain
    /// stereo app), channels 3-4, then the full stereo mix (see <see cref="AppCaptureChannels"/>).</summary>
    public AppCaptureChannels[] AppChannelOptions { get; } =
        { AppCaptureChannels.Master12, AppCaptureChannels.Master34, AppCaptureChannels.FullMix };

    /// <summary>Selected broadcast-channel handling for the captured app. Default = channels 1-2
    /// (isolates the Master pair on multi-out DJ gear; lossless for a plain stereo app — zero config
    /// for the common case).</summary>
    [ObservableProperty]
    private AppCaptureChannels _selectedAppChannels = AppCaptureChannels.Master12;

    // ── Monitor output device (local listen-back; "No output (stream only)" = off) ─────────
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonitorDeviceSelected))]
    private AudioOutputDevice? _selectedOutputDevice;

    /// <summary>True when a real monitor device is chosen (not "No output") — gates the VOL slider.</summary>
    public bool IsMonitorDeviceSelected => (SelectedOutputDevice?.Index ?? 0) > 0;

    // ── Connection state ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnAir))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    [NotifyPropertyChangedFor(nameof(ConnectLabel))]
    private BroadcastState _state = BroadcastState.Disconnected;

    [ObservableProperty]
    private string? _lastError;

    public bool OnAir => State == BroadcastState.Connected;
    public bool IsConnected => State == BroadcastState.Connected;
    public string ConnectLabel => State switch
    {
        // Transport glyphs on the shared quiet-glass CTA (Button.cta in App.axaml): ▶ = go live,
        // ■ = stop. Same pill + fixed width in every state; only colour + glyph change (accent ↔ red).
        // The green tally lamp stays the live indicator.
        BroadcastState.Connected => "■  DISCONNECT",
        BroadcastState.Connecting => "CONNECTING…",
        BroadcastState.Reconnecting => "RECONNECTING…",
        _ => "▶  CONNECT",
    };
    public string StatusText => State switch
    {
        BroadcastState.Connected => "ON AIR",
        BroadcastState.Connecting => "Connecting…",
        BroadcastState.Reconnecting => "Reconnecting…",
        BroadcastState.Error => LastError ?? "Error",
        _ => "Offline",
    };

    private string CodecLabel => SelectedProfileFormat == StreamFormat.Opus ? "Opus" : "MP3";

    /// <summary>Dynamic sub-line under the big status word — the useful detail the headline can't say:
    /// the armed target when offline, the host while connecting, the confirmed format when on air.</summary>
    public string StatusDetail => State switch
    {
        BroadcastState.Connected =>
            $"{SelectedProfile?.Name ?? "—"} · {CodecLabel} {SelectedProfileBitrateKbps} kbps",
        BroadcastState.Connecting or BroadcastState.Reconnecting =>
            SelectedProfile is { } p ? $"{p.Host}:{p.Port}{p.Mount}" : "connecting…",
        BroadcastState.Error =>
            "Check host, mount & credentials",
        _ =>
            SelectedProfile is { } s ? $"→ {s.Name} · {CodecLabel} {SelectedProfileBitrateKbps} kbps" : "No server selected",
    };

    // ── Recording ("record your set" — second, independent encoder on the master bus) ────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    [NotifyPropertyChangedFor(nameof(RecordTooltip))]
    private RecordingState _recState = RecordingState.Idle;

    public bool IsRecording => RecState == RecordingState.Recording;

    /// <summary>REC-pill text: "REC" when idle, "ARMED" while the auto-trim gate waits for the
    /// first signal, then the recorded duration (updated per frame in <see cref="PumpFrame"/> —
    /// the setter's equality check keeps PropertyChanged at 1/s, same pattern as
    /// <see cref="StreamTimeText"/>). The clock counts what's actually IN THE FILE (the
    /// service's gated stopwatch), so a DJ can SEE the trim working: it doesn't tick until
    /// the music does.</summary>
    [ObservableProperty] private string _recordLabel = "REC";

    public string RecordTooltip => IsRecording
        ? _recording.IsWaitingForSignal
            ? $"Armed — recording starts with the first signal. Click to cancel.\n{_recording.CurrentFilePath}"
            : $"Recording — click to stop.\n{_recording.CurrentFilePath}"
        : "Record your set to a local file (format & folder in Settings) · right-click: open the recordings folder";

    /// <summary>Auto-trim silence (Settings → Recording; default ON) — see StreamSettings.TrimSilence.</summary>
    [ObservableProperty] private bool _trimSilence = true;

    /// <summary>Persisted recording format (policy; resolved to a concrete codec at record start).</summary>
    [ObservableProperty] private RecordingFormat _recordingFormat = Core.Models.RecordingFormat.SameAsStream;

    /// <summary>Persisted recording folder override; null/empty = <see cref="DefaultRecordingFolder"/>.</summary>
    [ObservableProperty] private string? _recordingFolder;

    /// <summary>Start/stop the recorder automatically with the broadcast connection.</summary>
    [ObservableProperty] private bool _autoRecord;

    /// <summary>Keep the display awake while on air / recording (Settings → Advanced; default ON) —
    /// see StreamSettings.KeepScreenAwake and <see cref="JustPlay.UI.KeepAwake"/>.</summary>
    [ObservableProperty] private bool _keepScreenAwake = true;

    /// <summary>Where recordings land when no folder is configured — shown as the folder box's placeholder.</summary>
    public string DefaultRecordingFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "JUST STREAM Recordings");

    /// <summary>The folder recordings actually go to right now: the override if set, else the
    /// default. Public — the Settings window's Browse… starts the folder picker here.</summary>
    public string EffectiveRecordingFolder =>
        string.IsNullOrWhiteSpace(RecordingFolder) ? DefaultRecordingFolder : RecordingFolder!;

    // ── Interface ─────────────────────────────────────────────────────────
    /// <summary>Show the bottom keyboard-hint bar. Off = pros hide it (Settings → Look). Chloe 2026-07-06.</summary>
    [ObservableProperty] private bool _showKeyHints = true;

    /// <summary>The keyboard-hint bar entries — rendered by the shared <see cref="KeyLegend"/> (the SAME
    /// control the PRE CUE FINDER uses). Static: the two in-app hotkeys handled in MainWindow.</summary>
    public IReadOnlyList<KeyHint> KeyHints { get; } =
    [
        new("C", "on air / off air"),
        new("R", "record / stop"),
    ];

    // ── Live readouts (direct fields, never the log — §3a) ────────────────
    [ObservableProperty] private string _codecText = "MP3";
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty] private string _mountText = "—";
    [ObservableProperty] private string _listenersText = "—"; // needs an Icecast stats poll (future)
    [ObservableProperty] private string _streamTimeText = "00:00:00";
    [ObservableProperty] private string _nowPlayingText = "";

    // ── Meters (0..1 linear) ─────────────────────────────────────────────
    // Raw output peak (0..1 linear). The shared LevelMeter control owns the ballistics + peak-hold + display.
    public double OutLevelLeft { get; private set; }
    public double OutLevelRight { get; private set; }

    // ── Limiter gain-reduction lamp (right of each L/R meter) ─────────────
    // Lit = that channel's true-peak is hitting the limiter ceiling. Colour = bus-wide health:
    // amber = occasional, shallow catching (transparent, healthy); red = deep/sustained (crushing →
    // turn the input gain down). Held ~0.25 s so a 5 ms catch is actually visible.
    [ObservableProperty] private bool _leftLimitActive;
    [ObservableProperty] private bool _rightLimitActive;
    [ObservableProperty] private bool _limiterHard;

    // ── Input-signal presence (drives the chrome spectrum-glyph pulse) ────
    // True while audio is actually ARRIVING from the source — independent of whether we're on air. The
    // glyph reads as "we're receiving music", which is what a DJ expects even before hitting CONNECT
    // (Chloe 2026-07-02: the old on-air-only pulse fooled her). Computed per frame in PumpFrame.
    [ObservableProperty] private bool _hasInputSignal;

    // ── Sample rate (RATE dropdown) ───────────────────────────────────────
    [ObservableProperty] private int _selectedSampleRate = 44100;

    // ── Bus DSP rack ─────────────────────────────────────────────────────
    [ObservableProperty] private double _eqLow = 1.0;
    [ObservableProperty] private double _eqMid = 1.0;
    [ObservableProperty] private double _eqHigh = 1.0;
    [ObservableProperty] private double _autoTilt;
    [ObservableProperty] private double _punch;
    [ObservableProperty] private string _limiterDrive = "Soft"; // Off | Soft | Club | Loud

    /// <summary>Saved Sound presets (built-in Normal/Hard seeded once + user presets). Bound to the
    /// DSP preset chip row; click = apply, (+) = save current, right-click = replace/rename/delete.</summary>
    public ObservableCollection<DspPreset> SoundPresets { get; } = new();

    // ── Levels / monitor ─────────────────────────────────────────────────
    [ObservableProperty] private double _inputGainDb;
    [ObservableProperty] private double _monitorVolume = 0.8;

    // ── Stream / privacy ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSongInfoActive))]
    private bool _sendSongInfo = true;

    /// <summary>Opus/Ogg streams can't carry live ICY now-playing → song-info broadcast is MP3-only.</summary>
    public bool CanBroadcastSongInfo => SelectedProfileFormat != StreamFormat.Opus;

    /// <summary>Now-playing input + SEND are usable only when broadcasting song info AND the codec supports it.</summary>
    public bool IsSongInfoActive => SendSongInfo && CanBroadcastSongInfo;

    // ── Log (now in its own LogWindow) ───────────────────────────────────
    [ObservableProperty] private bool _logVisible;         // kept for settings back-compat

    /// <summary>Mini-player view (mirrors JUST PLAY): only server-select + ON-AIR + output level, narrower.</summary>
    [ObservableProperty] private bool _isMini;
    /// <summary>The SHARED event log (JustPlay.UI) — the LogWindow binds to this; <see cref="Log"/> feeds it.</summary>
    public LogViewModel EventLog { get; }

    public StreamViewModel(IAudioInputEngine engine, IBroadcastService broadcast,
        IRecordingService recording, JsonStreamSettingsService settings, ISessionLog sessionLog)
    {
        _engine = engine;
        _broadcast = broadcast;
        _recording = recording;
        _settings = settings;

        // The shared event log (JustPlay.UI) persists to the daily session file and wires its own
        // OnWriteFailed → window-only reporting.
        EventLog = new LogViewModel(sessionLog);

        _broadcast.StateChanged += OnBroadcastStateChanged;
        _recording.StateChanged += OnRecordingStateChanged;

        // Storage-never-crashes rule: a settings.json save failure must NOT die silently — surface it in
        // the log WINDOW only (AppendMemoryOnly does NOT re-persist, so the very failure can't recurse).
        _settings.OnSaveFailed = EventLog.AppendMemoryOnly;

        Hydrate();
        // No meter timer: the View pumps meters/lamp/time once per render frame (vsync-synced) via
        // PumpFrame() — see MainWindow.OnRenderFrame. Frame-synced = no timer-vs-vsync judder.
    }

    // ── Hydration from settings ──────────────────────────────────────────

    private void Hydrate()
    {
        _loading = true;
        var s = _settings.Current;

        foreach (var p in s.Servers) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == s.SelectedServerId) ?? Profiles.FirstOrDefault();

        // Sample rate must be set BEFORE device selection so the engine uses the correct rate when
        // OnSelectedInputDeviceChanged triggers StartCapture. The change handler is guarded by
        // _loading so it won't restart capture — we set the engine directly here.
        SelectedSampleRate = s.SampleRate;
        _engine.SampleRate = SelectedSampleRate;

        // Set the source mode BEFORE device selection so a restored app-mode session doesn't also
        // kick off device capture (OnSelectedInputDeviceChanged early-returns in app mode).
        IsAppSourceMode = s.AppSourceMode && _engine.SupportsApplicationCapture;

        RefreshDevices();
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Name == s.InputDeviceName)
                              ?? InputDevices.FirstOrDefault(d => d.IsLoopback)
                              ?? InputDevices.FirstOrDefault();
        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Name == s.MonitorDeviceName)
                               ?? OutputDevices.FirstOrDefault(d => d.Index == 0); // default = No output (stream only)

        // Broadcast-channel handling BEFORE selecting the app, so the restored capture starts with it.
        // Legacy "Auto" (3) and explicit "1-2" (1) both restore as channels 1-2; anything else → 1-2.
        SelectedAppChannels = s.AppMasterChannels switch
        {
            0 => AppCaptureChannels.FullMix,
            2 => AppCaptureChannels.Master34,
            _ => AppCaptureChannels.Master12,
        };

        if (IsAppSourceMode)
        {
            RefreshCaptureApps();
            // Selecting the app starts its capture (OnSelectedCaptureAppChanged) — the app-mode restore.
            SelectedCaptureApp = CaptureApps.FirstOrDefault(a => a.ExecutableName == s.CaptureAppExe)
                                 ?? CaptureApps.FirstOrDefault();
        }

        EqLow = s.EqLow; EqMid = s.EqMid; EqHigh = s.EqHigh;
        AutoTilt = s.AutoTilt; Punch = s.Punch;
        LimiterDrive = s.LimiterDrive;
        InputGainDb = s.InputGainDb;
        MonitorVolume = s.MonitorVolume;
        SendSongInfo = s.SendSongInfo;
        LogVisible = s.LogVisible;
        ShowKeyHints = s.ShowKeyHints;

        // Recording prefs — the format string round-trips the enum name; unknown → SameAsStream.
        RecordingFolder = s.RecordingFolder;
        RecordingFormat = Enum.TryParse<RecordingFormat>(s.RecordingFormat, out var recFmt)
            ? recFmt : Core.Models.RecordingFormat.SameAsStream;
        AutoRecord = s.AutoRecord;
        TrimSilence = s.TrimSilence;
        KeepScreenAwake = s.KeepScreenAwake;

        // Sound presets: restore the user's saved presets, then TOP UP any missing built-in genre
        // starting points (DspPreset.StreamDefaults — same tonal identity as JUST PLAY, broadcast-
        // loudness tuned) — gated by SoundPresetsSeedVersion so it runs once per built-in set: a fresh
        // install seeds all; an existing install gains only new ones; a deleted preset stays deleted.
        foreach (var p in s.SoundPresets) SoundPresets.Add(p);
        var seedDefaults = s.SoundPresetsSeedVersion < DspPreset.BuiltInSeedVersion;
        if (seedDefaults)
            foreach (var d in DspPreset.StreamDefaults)
                if (!SoundPresets.Any(p => p.Name == d.Name))
                    SoundPresets.Add(d);

        _loading = false;

        // Push the hydrated DSP state into the engine once.
        ApplyEqualizer();
        ApplyTilt();
        ApplyTransient();
        ApplyLimiter();
        ApplyMonitor();
        _engine.InputGainDb = InputGainDb;

        // Persist the one-time built-in seed now that hydration is complete (flips SoundPresetsSeeded).
        if (seedDefaults) Persist();
    }

    public void RefreshDevices()
    {
        var current = SelectedInputDevice?.Name;
        InputDevices.Clear();
        foreach (var d in _engine.GetInputDevices()) InputDevices.Add(d);
        if (current is not null)
            SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Name == current) ?? SelectedInputDevice;

        var currentOut = SelectedOutputDevice?.Name;
        OutputDevices.Clear();
        foreach (var d in _engine.GetOutputDevices()) OutputDevices.Add(d);
        if (currentOut is not null)
            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Name == currentOut) ?? SelectedOutputDevice;
    }

    [RelayCommand]
    private void RefreshDeviceList()
    {
        RefreshDevices();
        if (IsAppSourceMode) RefreshCaptureApps();
    }

    /// <summary>Re-enumerate the capturable apps, keeping the current pick (by executable) if still present.</summary>
    public void RefreshCaptureApps()
    {
        var currentExe = SelectedCaptureApp?.ExecutableName;
        CaptureApps.Clear();
        foreach (var a in _engine.GetCaptureApps()) CaptureApps.Add(a);
        if (currentExe is not null)
            SelectedCaptureApp = CaptureApps.FirstOrDefault(a => a.ExecutableName == currentExe) ?? SelectedCaptureApp;
    }

    private void StartDeviceCaptureSafe(AudioInputDevice dev)
    {
        try { _engine.StartCapture(dev.Index); }
        catch (Exception ex) { Log($"Capture failed on '{dev.Name}': {ex.Message}"); }
    }

    private void StartAppCaptureSafe(CaptureApp app)
    {
        // The default (channels 1-2) isolates the Master pair on a multi-out DJ device (dropping the Cue
        // on 3/4) and is lossless for a plain stereo app — Windows' 2→4 upmix preserves ch1/2 at unity
        // (measured 2026-07-02). If a 4-ch capture fails, the provider falls back to stereo. A full
        // downmix is served by the explicit "Full mix" choice. DJ detection only sorts the picker now;
        // it no longer gates the capture, so even an unrecognised DJ app gets channels 1-2 by default.
        try { _engine.StartApplicationCapture(app.ProcessId, SelectedAppChannels); }
        catch (Exception ex) { Log($"App capture failed on '{app.DisplayName}': {ex.Message}"); }
    }

    partial void OnIsAppSourceModeChanged(bool value)
    {
        if (_loading) return;
        if (value)
        {
            RefreshCaptureApps();
            if (SelectedCaptureApp is null) SelectedCaptureApp = CaptureApps.FirstOrDefault();
            else StartAppCaptureSafe(SelectedCaptureApp);
        }
        else if (SelectedInputDevice is { } dev)
        {
            StartDeviceCaptureSafe(dev);
        }
        Persist();
    }

    partial void OnSelectedCaptureAppChanged(CaptureApp? value)
    {
        if (value is null || !IsAppSourceMode) return;
        StartAppCaptureSafe(value);
        Persist();
    }

    partial void OnSelectedAppChannelsChanged(AppCaptureChannels value)
    {
        if (_loading) return;
        // Re-arm the capture with the new Master-channel handling if an app is live.
        if (IsAppSourceMode && SelectedCaptureApp is { } app) StartAppCaptureSafe(app);
        Persist();
    }

    // ── Persistence ──────────────────────────────────────────────────────

    private void Persist()
    {
        if (_loading) return;
        var s = _settings.Current;
        s.Servers = Profiles.ToList();
        s.SelectedServerId = SelectedProfile?.Id;
        s.InputDeviceName = SelectedInputDevice?.Name;
        s.AppSourceMode = IsAppSourceMode;
        s.CaptureAppExe = SelectedCaptureApp?.ExecutableName;
        s.AppMasterChannels = (int)SelectedAppChannels;
        s.SampleRate = SelectedSampleRate;
        s.EqLow = EqLow; s.EqMid = EqMid; s.EqHigh = EqHigh;
        s.AutoTilt = AutoTilt; s.Punch = Punch;
        s.LimiterDrive = LimiterDrive;
        s.InputGainDb = InputGainDb;
        s.MonitorDeviceName = IsMonitorDeviceSelected ? SelectedOutputDevice?.Name : null;
        s.MonitorVolume = MonitorVolume;
        s.SendSongInfo = SendSongInfo;
        s.LogVisible = LogVisible;
        s.ShowKeyHints = ShowKeyHints;
        s.RecordingFolder = RecordingFolder;
        s.RecordingFormat = RecordingFormat.ToString();
        s.AutoRecord = AutoRecord;
        s.TrimSilence = TrimSilence;
        s.KeepScreenAwake = KeepScreenAwake;
        s.SoundPresets = SoundPresets.ToList();
        s.SoundPresetsSeeded = true;
        s.SoundPresetsSeedVersion = DspPreset.BuiltInSeedVersion;
        _settings.Save();
    }

    /// <summary>Re-persist all settings — called by the settings window after profile edits.</summary>
    public void SaveSettings() => Persist();

    // ── Capture (driven by device selection) ─────────────────────────────

    partial void OnSelectedInputDeviceChanged(AudioInputDevice? value)
    {
        if (value is null || IsAppSourceMode) return; // in app mode the device combo doesn't drive capture
        try
        {
            _engine.StartCapture(value.Index);
        }
        catch (Exception ex)
        {
            Log($"Capture failed on '{value.Name}': {ex.Message}");
        }
        Persist();
    }

    partial void OnSelectedProfileChanged(StreamServerProfile? value) => Persist();

    // ── Per-profile codec / bitrate — editable on the main page ─────────────
    // These proxy through to the selected profile record (records are immutable, so we
    // replace the entry in Profiles and re-point SelectedProfile). Setting SelectedProfile
    // triggers OnSelectedProfileChanged → Persist(), so no extra save call is needed.
    // The [NotifyPropertyChangedFor] attributes above ensure the editor refreshes when
    // the user switches to a different server profile.

    /// <summary>
    /// Live-editable codec of the selected profile. Two-way bound to the main-page CODEC
    /// dropdown. Replacing SelectedProfile persists via OnSelectedProfileChanged.
    /// </summary>
    public StreamFormat SelectedProfileFormat
    {
        get => SelectedProfile?.Format ?? StreamFormat.Mp3;
        set
        {
            if (SelectedProfile is null || SelectedProfile.Format == value) return;
            var updated = SelectedProfile with { Format = value };
            var idx = IndexOfProfileById(SelectedProfile.Id);
            if (idx >= 0) Profiles[idx] = updated;
            SelectedProfile = updated; // triggers Persist()
        }
    }

    /// <summary>
    /// Live-editable bitrate (kbps) of the selected profile. Same persistence pattern as
    /// <see cref="SelectedProfileFormat"/>.
    /// </summary>
    public int SelectedProfileBitrateKbps
    {
        get => SelectedProfile?.BitrateKbps ?? 320;
        set
        {
            if (SelectedProfile is null || SelectedProfile.BitrateKbps == value) return;
            var updated = SelectedProfile with { BitrateKbps = value };
            var idx = IndexOfProfileById(SelectedProfile.Id);
            if (idx >= 0) Profiles[idx] = updated;
            SelectedProfile = updated; // triggers Persist()
        }
    }

    private int IndexOfProfileById(string id)
    {
        for (var i = 0; i < Profiles.Count; i++)
            if (Profiles[i].Id == id) return i;
        return -1;
    }

    // ── DSP property changes → engine + persist ──────────────────────────

    partial void OnEqLowChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnEqMidChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnEqHighChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnAutoTiltChanged(double value) { ApplyTilt(); Persist(); }
    partial void OnPunchChanged(double value) { ApplyTransient(); Persist(); }
    partial void OnLimiterDriveChanged(string value) { ApplyLimiter(); Persist(); }
    partial void OnInputGainDbChanged(double value) { if (!_loading) _engine.InputGainDb = value; Persist(); }
    partial void OnSelectedOutputDeviceChanged(AudioOutputDevice? value) { ApplyMonitor(); Persist(); }
    partial void OnMonitorVolumeChanged(double value) { ApplyMonitor(); Persist(); }
    partial void OnSendSongInfoChanged(bool value) => Persist();
    partial void OnLogVisibleChanged(bool value) => Persist();
    partial void OnShowKeyHintsChanged(bool value) => Persist();
    partial void OnRecordingFormatChanged(RecordingFormat value) => Persist();
    partial void OnRecordingFolderChanged(string? value) => Persist();
    partial void OnAutoRecordChanged(bool value) => Persist();
    partial void OnTrimSilenceChanged(bool value) => Persist();
    partial void OnKeepScreenAwakeChanged(bool value) { UpdateKeepAwake(); Persist(); }

    /// <summary>Hold/release the display-sleep guard from the CURRENT session state: awake while
    /// the broadcast is live (incl. connecting/reconnecting — mid-gig states) or a recording runs,
    /// and only with the setting on. Called from the UI thread only (both state handlers post
    /// there), which Windows' per-thread ES_CONTINUOUS flag requires — see KeepAwake remarks.</summary>
    private void UpdateKeepAwake()
    {
        var sessionHot = State is BroadcastState.Connected or BroadcastState.Connecting
                                  or BroadcastState.Reconnecting
                         || IsRecording;
        if (KeepScreenAwake && sessionHot)
            JustPlay.UI.KeepAwake.Enable("JUST STREAM is on air");
        else
            JustPlay.UI.KeepAwake.Disable();
    }

    /// <summary>
    /// Sample-rate change: rebuild the engine at the new rate, restart capture if it was active,
    /// and re-apply the full DSP rack (the old mixer was freed so all processors need re-registration).
    /// Guarded by <see cref="_loading"/> so Hydrate's property assignment doesn't trigger a restart.
    /// </summary>
    partial void OnSelectedSampleRateChanged(int value)
    {
        if (_loading) return;

        // The rate switch frees + rebuilds the mixer — a recording encoder attached to the old
        // mixer would die mid-file. Stop cleanly first (finalizes the headers) and say why.
        if (IsRecording)
        {
            _recording.StopAsync().GetAwaiter().GetResult(); // completes synchronously (BASS EncodeStop)
            _recAutoStarted = false;
            Log(_recording.CurrentFilePath is { } recPath
                ? $"Recording stopped by the sample-rate change — saved: {recPath}"
                : "Recording stopped by the sample-rate change — discarded (no audio ever arrived).");
        }

        var wasCapturing = _engine.IsCapturing;
        var dev = SelectedInputDevice;
        _engine.SampleRate = value; // tears down mixer + capture internally
        if (wasCapturing && dev is not null)
        {
            try { _engine.StartCapture(dev.Index); }
            catch (Exception ex) { Log($"Capture restart after sample-rate change failed: {ex.Message}"); }
            // Re-apply the full DSP rack — the old mixer was freed so all processor handles are gone.
            ApplyEqualizer();
            ApplyTilt();
            ApplyTransient();
            ApplyLimiter();
            ApplyMonitor();
            _engine.InputGainDb = InputGainDb;
        }
        Persist();
    }

    private void ApplyEqualizer() => _engine.SetEqualizer(EqLow, EqMid, EqHigh);
    private void ApplyTilt() => _engine.SetAdaptiveTilt(AutoTilt);
    private void ApplyTransient() => _engine.SetTransientDesigner(Punch);
    private void ApplyMonitor()
    {
        // Route the local monitor to the chosen device (0 = "No output (stream only)" = no monitor).
        // Volume is harmless on device 0; the slider is gated by IsMonitorDeviceSelected in the UI.
        _engine.SetOutputDevice(SelectedOutputDevice?.Index ?? 0);
        _engine.MonitorVolume = MonitorVolume;
    }

    /// <summary>
    /// Limiter/maximizer drive mapping (just-stream-blueprint.md §4): Off = bypass / Soft = 0 dB
    /// transparent safety / Club = +3 dB / Loud = +6 dB pushed to −0.1 dBTP. Ceiling −1 dBTP otherwise.
    /// </summary>
    private void ApplyLimiter()
    {
        switch (LimiterDrive)
        {
            case "Off": _engine.SetLimiter(false, 0, -1.0); break;
            case "Club": _engine.SetLimiter(true, 3, -1.0); break;
            case "Loud": _engine.SetLimiter(true, 6, -0.1); break;
            default: _engine.SetLimiter(true, 0, -1.0); break; // "Soft"
        }
    }

    /// <summary>Set the limiter drive from the segmented control (Off/Soft/Club/Loud).</summary>
    [RelayCommand]
    private void SetLimiterDrive(string drive) => LimiterDrive = drive;

    // ── Sound presets (ported from JUST PLAY's DspPreset CRUD) ───────────────────────────────────
    // Normal + Hard are seeded into SoundPresets as ORDINARY, editable chips (see Hydrate). Click a chip
    // = apply; (+) = save the current bus state as a new named preset; right-click = replace / rename /
    // delete. Same UX + the shared InputDialog as JUST PLAY's Sound tab.

    /// <summary>Capture the current Sound-rack state, prompt for a name, save it as a preset. A name
    /// matching an existing preset (case-insensitive) overwrites it. Cancel / empty = no-op.</summary>
    [RelayCommand]
    private async Task SavePreset()
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var name = await InputDialog.AskAsync(owner, "Name this Sound preset", DefaultPresetName());
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = CaptureCurrent(name);
        var idx = IndexOfPresetByName(name);
        if (idx >= 0) SoundPresets[idx] = preset;
        else          SoundPresets.Add(preset);
        Persist();
    }

    /// <summary>Apply a saved preset to the bus — assigns the DSP properties, so each On…Changed pushes
    /// to the engine (SetEqualizer / SetAdaptiveTilt / SetTransientDesigner / SetLimiter) AND persists.</summary>
    [RelayCommand]
    private void ApplyPreset(DspPreset? preset)
    {
        if (preset is null) return;
        EqLow = preset.EqLowGain; EqMid = preset.EqMidGain; EqHigh = preset.EqHighGain;
        AutoTilt = preset.AutoTiltStrength;
        Punch = preset.TransientPunch;
        LimiterDrive = preset.LimiterMode;
    }

    /// <summary>Delete a saved preset (right-click → Delete) and persist.</summary>
    [RelayCommand]
    private void DeletePreset(DspPreset? preset)
    {
        if (preset is not null && SoundPresets.Remove(preset)) Persist();
    }

    /// <summary>Rename a saved preset in place (right-click → Rename…); prompts with the current name.</summary>
    [RelayCommand]
    private async Task RenamePreset(DspPreset? preset)
    {
        if (preset is null) return;
        var idx = SoundPresets.IndexOf(preset);
        if (idx < 0) return;

        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var name = await InputDialog.AskAsync(owner, "Rename preset", preset.Name);
        if (string.IsNullOrWhiteSpace(name) || name == preset.Name) return;

        SoundPresets[idx] = preset with { Name = name };
        Persist();
    }

    /// <summary>Overwrite a saved preset's values with the CURRENT bus state, keeping its name + slot
    /// (right-click → Replace with current). No prompt.</summary>
    [RelayCommand]
    private void ReplacePreset(DspPreset? preset)
    {
        if (preset is null) return;
        var idx = SoundPresets.IndexOf(preset);
        if (idx < 0) return;
        SoundPresets[idx] = CaptureCurrent(preset.Name);
        Persist();
    }

    private DspPreset CaptureCurrent(string name) => new()
    {
        Name = name,
        EqLowGain = EqLow, EqMidGain = EqMid, EqHighGain = EqHigh,
        AutoTiltStrength = AutoTilt, TransientPunch = Punch,
        LimiterMode = LimiterDrive,
    };

    private int IndexOfPresetByName(string name)
    {
        for (var i = 0; i < SoundPresets.Count; i++)
            if (string.Equals(SoundPresets[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private string DefaultPresetName()
    {
        var n = SoundPresets.Count + 1;
        string candidate;
        do { candidate = $"Preset {n++}"; }
        while (SoundPresets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }

    // ── Connect / disconnect ─────────────────────────────────────────────

    private bool CanToggleConnect() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ToggleConnectAsync()
    {
        // Reconnecting counts as "live" here: one click while RECONNECTING… stops the
        // auto-retry loop cleanly (instead of accidentally racing a fresh manual connect).
        if (State is BroadcastState.Connected or BroadcastState.Connecting or BroadcastState.Reconnecting)
        {
            await _broadcast.DisconnectAsync();
            return;
        }

        var profile = SelectedProfile;
        if (profile is null) return;

        if (!_engine.IsCapturing)
        {
            Log("Select an input device before connecting — there is no audio to stream yet.");
            return;
        }

        // Reflect the profile in the readouts immediately.
        CodecText = profile.Format == StreamFormat.Opus ? "Opus" : "MP3";
        BitrateText = $"{profile.BitrateKbps} kbps";
        MountText = profile.Mount;

        await _broadcast.ConnectAsync(profile);
        // Now-playing push happens in OnBroadcastStateChanged(Connected) — one code path for
        // manual connects AND auto-reconnects (Icecast forgets the title either way).
    }

    // ── Recording ────────────────────────────────────────────────────────
    // A SECOND, independent encoder on the same post-DSP master bus the broadcast taps —
    // NOT a tee of the cast encoder (design: Chloe 2026-07-04). So: record off-air, survive
    // stream reconnects, choose your own format. "Same as stream" mirrors the live codec +
    // bitrate for the honest self-check. Its failure NEVER touches the broadcast.

    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (IsRecording)
        {
            await _recording.StopAsync();
            _recAutoStarted = false;
            // The service nulls CurrentFilePath when an armed-but-never-opened (all-silence)
            // recording was discarded — report honestly which of the two happened.
            Log(_recording.CurrentFilePath is { } saved
                ? $"Recording saved: {saved}"
                : "Recording discarded — no audio ever arrived.");
            return;
        }
        await StartRecordingAsync(auto: false);
    }

    private async Task StartRecordingAsync(bool auto)
    {
        if (!_engine.IsCapturing)
        {
            Log("Select an input source before recording — there is no audio to record yet.");
            return;
        }

        var (codec, kbps) = Recording.Resolve(RecordingFormat, SelectedProfileFormat, SelectedProfileBitrateKbps);
        var path = Path.Combine(EffectiveRecordingFolder,
            Recording.BuildFileName(DateTime.Now, SelectedProfile?.Name, codec));

        await _recording.StartAsync(new RecordingJob(codec, kbps, path, TrimSilence));
        // Remember auto-started ONLY if the start actually succeeded, so a failed auto-start
        // can't later auto-stop a recording the DJ started by hand.
        _recAutoStarted = auto && _recording.State == RecordingState.Recording;
    }

    private void OnRecordingStateChanged(object? sender, RecordingState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RecState = state;
            UpdateKeepAwake(); // a manual recording holds the screen awake even off air
            switch (state)
            {
                case RecordingState.Recording:
                    // PumpFrame drives the label from here on (ARMED → recorded duration).
                    RecordLabel = _recording.IsWaitingForSignal ? "ARMED" : "0:00:00";
                    Log(_recording.IsWaitingForSignal
                        ? $"Recording armed — starts with the first signal: {_recording.CurrentFilePath}"
                        : $"Recording to {_recording.CurrentFilePath}");
                    break;
                case RecordingState.Idle:
                    RecordLabel = "REC";
                    break;
                case RecordingState.Error:
                    RecordLabel = "REC";
                    if (_recording.LastError is { } err) Log(err);
                    break;
            }
        });
    }

    [RelayCommand]
    private async Task SendTitleAsync()
    {
        if (State != BroadcastState.Connected) return;
        if (!SendSongInfo) { Log("Now-playing is off (privacy mode) — title not sent."); return; }
        await _broadcast.UpdateNowPlayingAsync(NowPlayingText ?? "");
    }

    // ── Broadcast state + timer ──────────────────────────────────────────

    private void OnBroadcastStateChanged(object? sender, BroadcastState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = state;
            LastError = _broadcast.LastError;
            UpdateKeepAwake(); // screen stays awake across Connected/Connecting/Reconnecting
            switch (state)
            {
                case BroadcastState.Connected:
                    // The stream is back — stop a pending Dock bounce (macOS) before anyone
                    // had to look at it.
                    JustPlay.UI.MacDock.CancelAttention();
                    // Cumulative on-air time: after an auto-reconnect the clock is still
                    // running — don't reset the session to 00:00:00 over a router hiccup.
                    if (_streamClock.IsRunning)
                    {
                        Log("Reconnected — stream is back.");
                    }
                    else
                    {
                        _streamClock.Restart();
                        Log("Connected.");
                    }
                    // (Re-)push the now-playing title — Icecast forgets it with a dropped
                    // connection, and this also covers the manual-connect case.
                    if (SendSongInfo && CanBroadcastSongInfo && !string.IsNullOrWhiteSpace(NowPlayingText))
                        _ = _broadcast.UpdateNowPlayingAsync(NowPlayingText);
                    // Auto-record: arm the recorder with the connection — nobody remembers to
                    // hit record at gig start. Never touches a recording started by hand.
                    if (AutoRecord && !IsRecording)
                        _ = StartRecordingAsync(auto: true);
                    break;
                case BroadcastState.Reconnecting:
                    // Keep the clock running (see Connected) and keep recording — a network
                    // hiccup must never cost the DJ their set recording.
                    Log($"{_broadcast.LastError ?? "Connection lost."} Auto-reconnecting…");
                    // Ladiocast behaviour: bounce the Dock icon until the DJ looks over —
                    // fullscreen Traktor hides everything else. Cancelled on Connected.
                    JustPlay.UI.MacDock.RequestAttention();
                    break;
                case BroadcastState.Disconnected:
                    // Clean, user-initiated — nothing to draw attention to.
                    JustPlay.UI.MacDock.CancelAttention();
                    _streamClock.Reset();
                    StreamTimeText = "00:00:00";
                    // Auto-stop ONLY what auto-record started (a manual recording outlives the
                    // stream on purpose), and only on a CLEAN disconnect — see the Error case.
                    if (_recAutoStarted && IsRecording)
                        _ = StopAutoRecordingAsync();
                    break;
                case BroadcastState.Error:
                    _streamClock.Reset();
                    if (_broadcast.LastError is { } e) Log(e);
                    // Deliberately KEEP recording on a connection error: the music is still
                    // playing — a network hiccup must never cost the DJ their set recording.
                    // Dead stream + no auto-retry pending = the loudest case for the bounce.
                    JustPlay.UI.MacDock.RequestAttention();
                    break;
            }
        });
    }

    private async Task StopAutoRecordingAsync()
    {
        await _recording.StopAsync();
        _recAutoStarted = false;
        Log(_recording.CurrentFilePath is { } saved
            ? $"Recording saved: {saved}"
            : "Recording discarded — no audio ever arrived.");
    }

    // (Meter ballistics moved into the shared JustPlay.UI LevelMeter control — it owns attack/release +
    // peak-hold now, so JUST PLAY and STREAM glide identically from the same code.)

    // GR-lamp peak-hold: keep a channel's lamp (and the "hard" colour) lit ~0.25 s after a catch so a
    // sub-frame event stays visible. Time-based (seconds) → same at any refresh rate.
    private const double LampHoldSeconds = 0.25;
    private double _lLampHoldS, _rLampHoldS, _hardHoldS;

    // Input-signal detector for the chrome glyph: a channel peak above ~-48 dBFS counts as "signal",
    // held ~0.5 s so the pulse rides through the gaps between beats / short breakdowns without strobing.
    private const double SignalOnThreshold = 0.004; // ~ -48 dBFS peak — above the noise floor, below music
    private const double SignalHoldSeconds = 0.5;
    private double _signalHoldS;

    /// <summary>
    /// Pump meters + GR lamp + stream time once per RENDER FRAME — driven by MainWindow's
    /// RequestAnimationFrame loop (vsync-synced). A free-running DispatcherTimer beat against vsync and
    /// visibly juddered the meter on the narrower mini bar; frame-synced updates are smooth at any size.
    /// <paramref name="dt"/> = seconds since the previous frame, so the ballistics + hold are
    /// refresh-rate independent.
    /// </summary>
    public void PumpFrame(double dt)
    {
        // Output level for the meters — RAW; the shared LevelMeter control does the ballistics + peak-hold.
        _engine.GetLevels(out var l, out var r);
        OutLevelLeft = l;
        OutLevelRight = r;

        // Input-signal presence for the chrome spectrum glyph — lit while audio arrives, held across gaps
        // so it doesn't strobe between beats. Setter only raises PropertyChanged on an actual flip.
        var peak = l > r ? l : r;
        if (peak >= SignalOnThreshold) _signalHoldS = SignalHoldSeconds;
        else if (_signalHoldS > 0) _signalHoldS -= dt;
        HasInputSignal = _signalHoldS > 0;

        // Limiter lamp: drain activity, refresh holds. grDb ≤ −4 OR duty ≥ 50% = crushing (red),
        // else a healthy occasional catch (amber). Limiter off → all dark.
        if (_engine.TryGetLimiterActivity(out var grDb, out var duty, out var lHit, out var rHit))
        {
            if (lHit) _lLampHoldS = LampHoldSeconds;
            if (rHit) _rLampHoldS = LampHoldSeconds;
            if (grDb <= -4.0 || duty >= 0.5) _hardHoldS = LampHoldSeconds;
        }
        else { _lLampHoldS = _rLampHoldS = _hardHoldS = 0; }
        if (_lLampHoldS > 0) _lLampHoldS -= dt;
        if (_rLampHoldS > 0) _rLampHoldS -= dt;
        if (_hardHoldS  > 0) _hardHoldS  -= dt;
        LeftLimitActive  = _lLampHoldS > 0;
        RightLimitActive = _rLampHoldS > 0;
        LimiterHard      = _hardHoldS  > 0;

        // Stream time
        if (_streamClock.IsRunning)
        {
            var t = _streamClock.Elapsed;
            StreamTimeText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        // Recording time on the REC pill — the service's GATED duration (what's actually in
        // the file), so with auto-trim the clock visibly doesn't tick until the music does.
        // Setter equality-checks → PropertyChanged only on the 1/s tick, same as StreamTimeText.
        if (IsRecording)
        {
            if (_recording.IsWaitingForSignal)
            {
                RecordLabel = "ARMED";
            }
            else
            {
                var rt = _recording.RecordedDuration;
                RecordLabel = $"{(int)rt.TotalHours:0}:{rt.Minutes:00}:{rt.Seconds:00}";
            }
        }
    }

    // ── Log (errors/warnings only — §3a) — feeds the shared EventLog (JustPlay.UI) ────────

    /// <summary>Append one broadcast event to the shared log window + session file (thread-safe).</summary>
    private void Log(string message) => EventLog.Append(message);

    /// <summary>Toggle the mini-player view (mirrors JUST PLAY's ToggleViewMode).</summary>
    [RelayCommand]
    private void ToggleViewMode() => IsMini = !IsMini;

    public void Dispose()
    {
        _broadcast.StateChanged -= OnBroadcastStateChanged;
        _recording.StateChanged -= OnRecordingStateChanged;

        // Finalize a running recording on exit — EncodeStop completes the WAV/AIFF/FLAC headers;
        // killing the process mid-write would leave an unplayable file. Completes synchronously.
        // (An armed recording that never saw audio is discarded by the service instead.)
        if (_recording.State == RecordingState.Recording)
        {
            _recording.StopAsync().GetAwaiter().GetResult();
            if (_recording.CurrentFilePath is { } path)
                Console.WriteLine($"[JUST STREAM] Recording finalized on exit: {path}");
        }
    }
}
