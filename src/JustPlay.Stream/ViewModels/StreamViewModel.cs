using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Stream.Settings;

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
    private readonly JsonStreamSettingsService _settings;
    private readonly Stopwatch _streamClock = new();
    private bool _loading; // suppress persistence/engine writes while hydrating from settings

    /// <summary>Limiter drive options for the DSP strip ComboBox (maps in <see cref="ApplyLimiter"/>).</summary>
    public string[] LimiterDrives { get; } = { "Off", "Soft", "Club", "Loud" };

    /// <summary>Codec options for the main-page CODEC dropdown (quality setting, editable while offline).</summary>
    public StreamFormat[] Formats { get; } = { StreamFormat.Mp3, StreamFormat.Opus };

    /// <summary>Bitrate (kbps) options for the main-page QUALITY dropdown.</summary>
    public int[] Bitrates { get; } = { 128, 192, 256, 320 };

    /// <summary>Sample rate options for the RATE dropdown. 48 kHz = Opus native; 44.1 kHz = most DJ software.</summary>
    public int[] SampleRates { get; } = { 44100, 48000 };

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
        BroadcastState.Connected => "DISCONNECT",
        BroadcastState.Connecting => "CONNECTING…",
        BroadcastState.Reconnecting => "RECONNECTING…",
        _ => "CONNECT",
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

    // ── Live readouts (direct fields, never the log — §3a) ────────────────
    [ObservableProperty] private string _codecText = "MP3";
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty] private string _mountText = "—";
    [ObservableProperty] private string _listenersText = "—"; // needs an Icecast stats poll (future)
    [ObservableProperty] private string _streamTimeText = "00:00:00";
    [ObservableProperty] private string _nowPlayingText = "";

    // ── Meters (0..1 linear) ─────────────────────────────────────────────
    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;

    // ── Limiter gain-reduction lamp (right of each L/R meter) ─────────────
    // Lit = that channel's true-peak is hitting the limiter ceiling. Colour = bus-wide health:
    // amber = occasional, shallow catching (transparent, healthy); red = deep/sustained (crushing →
    // turn the input gain down). Held ~0.25 s so a 5 ms catch is actually visible.
    [ObservableProperty] private bool _leftLimitActive;
    [ObservableProperty] private bool _rightLimitActive;
    [ObservableProperty] private bool _limiterHard;

    // ── Sample rate (RATE dropdown) ───────────────────────────────────────
    [ObservableProperty] private int _selectedSampleRate = 44100;

    // ── Bus DSP rack ─────────────────────────────────────────────────────
    [ObservableProperty] private double _eqLow = 1.0;
    [ObservableProperty] private double _eqMid = 1.0;
    [ObservableProperty] private double _eqHigh = 1.0;
    [ObservableProperty] private double _autoTilt;
    [ObservableProperty] private double _punch;
    [ObservableProperty] private string _limiterDrive = "Soft"; // Off | Soft | Club | Loud

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
    [ObservableProperty] private bool _hasUnreadLogs;      // drives the unread marker on the chrome log button

    /// <summary>Mini-player view (mirrors JUST PLAY): only server-select + ON-AIR + output level, narrower.</summary>
    [ObservableProperty] private bool _isMini;
    public ObservableCollection<string> LogEntries { get; } = new();

    /// <summary>All log lines joined — bound read-only by the LogWindow's selectable text box + Copy button.</summary>
    public string LogText => string.Join(System.Environment.NewLine, LogEntries);

    public StreamViewModel(IAudioInputEngine engine, IBroadcastService broadcast, JsonStreamSettingsService settings)
    {
        _engine = engine;
        _broadcast = broadcast;
        _settings = settings;

        // Keep the selectable LogText in sync as entries are added/cleared (UI thread — Log() is marshalled).
        LogEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));

        _broadcast.StateChanged += OnBroadcastStateChanged;

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

        RefreshDevices();
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Name == s.InputDeviceName)
                              ?? InputDevices.FirstOrDefault(d => d.IsLoopback)
                              ?? InputDevices.FirstOrDefault();
        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Name == s.MonitorDeviceName)
                               ?? OutputDevices.FirstOrDefault(d => d.Index == 0); // default = No output (stream only)

        EqLow = s.EqLow; EqMid = s.EqMid; EqHigh = s.EqHigh;
        AutoTilt = s.AutoTilt; Punch = s.Punch;
        LimiterDrive = s.LimiterDrive;
        InputGainDb = s.InputGainDb;
        MonitorVolume = s.MonitorVolume;
        SendSongInfo = s.SendSongInfo;
        LogVisible = s.LogVisible;

        _loading = false;

        // Push the hydrated DSP state into the engine once.
        ApplyEqualizer();
        ApplyTilt();
        ApplyTransient();
        ApplyLimiter();
        ApplyMonitor();
        _engine.InputGainDb = InputGainDb;
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
    private void RefreshDeviceList() => RefreshDevices();

    // ── Persistence ──────────────────────────────────────────────────────

    private void Persist()
    {
        if (_loading) return;
        var s = _settings.Current;
        s.Servers = Profiles.ToList();
        s.SelectedServerId = SelectedProfile?.Id;
        s.InputDeviceName = SelectedInputDevice?.Name;
        s.SampleRate = SelectedSampleRate;
        s.EqLow = EqLow; s.EqMid = EqMid; s.EqHigh = EqHigh;
        s.AutoTilt = AutoTilt; s.Punch = Punch;
        s.LimiterDrive = LimiterDrive;
        s.InputGainDb = InputGainDb;
        s.MonitorDeviceName = IsMonitorDeviceSelected ? SelectedOutputDevice?.Name : null;
        s.MonitorVolume = MonitorVolume;
        s.SendSongInfo = SendSongInfo;
        s.LogVisible = LogVisible;
        _settings.Save();
    }

    /// <summary>Re-persist all settings — called by the settings window after profile edits.</summary>
    public void SaveSettings() => Persist();

    // ── Capture (driven by device selection) ─────────────────────────────

    partial void OnSelectedInputDeviceChanged(AudioInputDevice? value)
    {
        if (value is null) return;
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

    /// <summary>
    /// Sample-rate change: rebuild the engine at the new rate, restart capture if it was active,
    /// and re-apply the full DSP rack (the old mixer was freed so all processors need re-registration).
    /// Guarded by <see cref="_loading"/> so Hydrate's property assignment doesn't trigger a restart.
    /// </summary>
    partial void OnSelectedSampleRateChanged(int value)
    {
        if (_loading) return;
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

    /// <summary>
    /// "Normal" preset — clean & transparent: EQ flat, no tilt, no punch, gentle safety limiter.
    /// The neutral counterpart to <see cref="ApplyHardPreset"/> so the DJ has something to toggle to.
    /// </summary>
    [RelayCommand]
    private void ApplyNormalPreset()
    {
        EqLow = 1.0; EqMid = 1.0; EqHigh = 1.0;
        AutoTilt = 0.0;
        Punch = 0.0;
        LimiterDrive = "Soft";
    }

    /// <summary>
    /// One-click "Hard" preset for brickwalled hard-dance, validated on Chloe's library
    /// (blueprint §5): EQ High 0.72 (≈ −3 dB @ 4 kHz), AutoTilt 0.65, Limiter LOUD.
    /// </summary>
    [RelayCommand]
    private void ApplyHardPreset()
    {
        EqLow = 1.0; EqMid = 1.0; EqHigh = 0.72;
        AutoTilt = 0.65;
        LimiterDrive = "Loud";
    }

    // ── Connect / disconnect ─────────────────────────────────────────────

    private bool CanToggleConnect() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ToggleConnectAsync()
    {
        if (State is BroadcastState.Connected or BroadcastState.Connecting)
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

        // On success, push the station/now-playing title if allowed.
        if (State == BroadcastState.Connected && SendSongInfo && !string.IsNullOrWhiteSpace(NowPlayingText))
            await _broadcast.UpdateNowPlayingAsync(NowPlayingText);
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
            switch (state)
            {
                case BroadcastState.Connected:
                    _streamClock.Restart();
                    Log("Connected.");
                    break;
                case BroadcastState.Disconnected:
                    _streamClock.Reset();
                    StreamTimeText = "00:00:00";
                    break;
                case BroadcastState.Error:
                    _streamClock.Reset();
                    if (_broadcast.LastError is { } e) Log(e);
                    break;
            }
        });
    }

    // Meter ballistics: one-pole filter (fast attack, smooth release) so the bars GLIDE. The K values
    // are tuned for a 16 ms step; PumpFrame rescales them to the real frame dt, so the feel is identical
    // at 60 / 120 / 144 Hz (frame-rate independent).
    private const double MeterAttackK = 0.40;
    private const double MeterReleaseK = 0.12;
    private const double RefStepSeconds = 0.016; // step the K values were tuned for
    private static double SmoothMeter(double current, double target, double attackK, double releaseK)
        => current + (target - current) * (target > current ? attackK : releaseK);

    // GR-lamp peak-hold: keep a channel's lamp (and the "hard" colour) lit ~0.25 s after a catch so a
    // sub-frame event stays visible. Time-based (seconds) → same at any refresh rate.
    private const double LampHoldSeconds = 0.25;
    private double _lLampHoldS, _rLampHoldS, _hardHoldS;

    /// <summary>
    /// Pump meters + GR lamp + stream time once per RENDER FRAME — driven by MainWindow's
    /// RequestAnimationFrame loop (vsync-synced). A free-running DispatcherTimer beat against vsync and
    /// visibly juddered the meter on the narrower mini bar; frame-synced updates are smooth at any size.
    /// <paramref name="dt"/> = seconds since the previous frame, so the ballistics + hold are
    /// refresh-rate independent.
    /// </summary>
    public void PumpFrame(double dt)
    {
        // Meters: convert the 16ms-tuned one-pole K to this frame's dt — applying a one-pole ~dt/16ms
        // times ≈ 1-(1-K)^(dt/16ms). Glides identically whatever the monitor's refresh rate.
        _engine.GetLevels(out var l, out var r);
        double steps = dt / RefStepSeconds;
        double aK = 1.0 - Math.Pow(1.0 - MeterAttackK, steps);
        double rK = 1.0 - Math.Pow(1.0 - MeterReleaseK, steps);
        LeftLevel = SmoothMeter(LeftLevel, l, aK, rK);
        RightLevel = SmoothMeter(RightLevel, r, aK, rK);

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
    }

    // ── Log (errors/warnings only — §3a) ─────────────────────────────────

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogEntries.Add(line);
        if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        HasUnreadLogs = true; // light up the chrome log button marker
        Console.WriteLine("[JUST STREAM] " + line);
    }

    /// <summary>Called by the LogWindow on open — clears the unread marker.</summary>
    public void MarkLogsRead() => HasUnreadLogs = false;

    /// <summary>Toggle the mini-player view (mirrors JUST PLAY's ToggleViewMode).</summary>
    [RelayCommand]
    private void ToggleViewMode() => IsMini = !IsMini;

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    public void Dispose()
    {
        _broadcast.StateChanged -= OnBroadcastStateChanged;
    }
}
