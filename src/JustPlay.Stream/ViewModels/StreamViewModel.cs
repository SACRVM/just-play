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
/// A single DispatcherTimer (~33 ms) polls meters + stream time.
/// </summary>
public sealed partial class StreamViewModel : ObservableObject, IDisposable
{
    private readonly IAudioInputEngine _engine;
    private readonly IBroadcastService _broadcast;
    private readonly JsonStreamSettingsService _settings;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _streamClock = new();
    private bool _loading; // suppress persistence/engine writes while hydrating from settings

    /// <summary>Limiter drive options for the DSP strip ComboBox (maps in <see cref="ApplyLimiter"/>).</summary>
    public string[] LimiterDrives { get; } = { "Off", "Soft", "Club", "Loud" };

    // ── Server profiles ──────────────────────────────────────────────────
    public ObservableCollection<StreamServerProfile> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectCommand))]
    private StreamServerProfile? _selectedProfile;

    // ── Input devices ────────────────────────────────────────────────────
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty]
    private AudioInputDevice? _selectedInputDevice;

    // ── Connection state ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnAir))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
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

    // ── Live readouts (direct fields, never the log — §3a) ────────────────
    [ObservableProperty] private string _codecText = "MP3";
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty] private string _samplerateText = "44100 Hz";
    [ObservableProperty] private string _mountText = "—";
    [ObservableProperty] private string _listenersText = "—"; // needs an Icecast stats poll (future)
    [ObservableProperty] private string _streamTimeText = "00:00:00";
    [ObservableProperty] private string _nowPlayingText = "";

    // ── Meters (0..1 linear + dB readout) ────────────────────────────────
    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;
    [ObservableProperty] private string _leftDb = "-∞";
    [ObservableProperty] private string _rightDb = "-∞";

    // ── Bus DSP rack ─────────────────────────────────────────────────────
    [ObservableProperty] private double _eqLow = 1.0;
    [ObservableProperty] private double _eqMid = 1.0;
    [ObservableProperty] private double _eqHigh = 1.0;
    [ObservableProperty] private double _autoTilt;
    [ObservableProperty] private double _punch;
    [ObservableProperty] private string _limiterDrive = "Soft"; // Off | Soft | Club | Loud

    // ── Levels / monitor ─────────────────────────────────────────────────
    [ObservableProperty] private double _inputGainDb;
    [ObservableProperty] private bool _monitorOn;
    [ObservableProperty] private double _monitorVolume = 0.8;

    // ── Stream / privacy ─────────────────────────────────────────────────
    [ObservableProperty] private bool _sendSongInfo = true;

    // ── Errors-only log (small, collapsible — §3a) ───────────────────────
    [ObservableProperty] private bool _logVisible;
    public ObservableCollection<string> LogEntries { get; } = new();

    public StreamViewModel(IAudioInputEngine engine, IBroadcastService broadcast, JsonStreamSettingsService settings)
    {
        _engine = engine;
        _broadcast = broadcast;
        _settings = settings;

        _broadcast.StateChanged += OnBroadcastStateChanged;

        Hydrate();

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ── Hydration from settings ──────────────────────────────────────────

    private void Hydrate()
    {
        _loading = true;
        var s = _settings.Current;

        foreach (var p in s.Servers) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == s.SelectedServerId) ?? Profiles.FirstOrDefault();

        RefreshDevices();
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Name == s.InputDeviceName)
                              ?? InputDevices.FirstOrDefault(d => d.IsLoopback)
                              ?? InputDevices.FirstOrDefault();

        EqLow = s.EqLow; EqMid = s.EqMid; EqHigh = s.EqHigh;
        AutoTilt = s.AutoTilt; Punch = s.Punch;
        LimiterDrive = s.LimiterDrive;
        InputGainDb = s.InputGainDb;
        MonitorOn = s.MonitorOn;
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
        s.EqLow = EqLow; s.EqMid = EqMid; s.EqHigh = EqHigh;
        s.AutoTilt = AutoTilt; s.Punch = Punch;
        s.LimiterDrive = LimiterDrive;
        s.InputGainDb = InputGainDb;
        s.MonitorOn = MonitorOn;
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

    // ── DSP property changes → engine + persist ──────────────────────────

    partial void OnEqLowChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnEqMidChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnEqHighChanged(double value) { ApplyEqualizer(); Persist(); }
    partial void OnAutoTiltChanged(double value) { ApplyTilt(); Persist(); }
    partial void OnPunchChanged(double value) { ApplyTransient(); Persist(); }
    partial void OnLimiterDriveChanged(string value) { ApplyLimiter(); Persist(); }
    partial void OnInputGainDbChanged(double value) { if (!_loading) _engine.InputGainDb = value; Persist(); }
    partial void OnMonitorOnChanged(bool value) { ApplyMonitor(); Persist(); }
    partial void OnMonitorVolumeChanged(double value) { ApplyMonitor(); Persist(); }
    partial void OnSendSongInfoChanged(bool value) => Persist();
    partial void OnLogVisibleChanged(bool value) => Persist();

    private void ApplyEqualizer() => _engine.SetEqualizer(EqLow, EqMid, EqHigh);
    private void ApplyTilt() => _engine.SetAdaptiveTilt(AutoTilt);
    private void ApplyTransient() => _engine.SetTransientDesigner(Punch);
    private void ApplyMonitor() => _engine.MonitorVolume = MonitorOn ? MonitorVolume : 0.0;

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

    private void OnTick(object? sender, EventArgs e)
    {
        // Meters
        _engine.GetLevels(out var l, out var r);
        LeftLevel = l;
        RightLevel = r;
        LeftDb = ToDb(l);
        RightDb = ToDb(r);

        // Stream time
        if (_streamClock.IsRunning)
        {
            var t = _streamClock.Elapsed;
            StreamTimeText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    private static string ToDb(double linear)
    {
        if (linear <= 0.00001) return "-∞";
        var db = 20.0 * Math.Log10(linear);
        return db.ToString("0.0");
    }

    // ── Log (errors/warnings only — §3a) ─────────────────────────────────

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogEntries.Add(line);
        if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        if (!LogVisible) LogVisible = true; // surface the strip when something goes wrong
        Console.WriteLine("[JUST STREAM] " + line);
    }

    [RelayCommand]
    private void ClearLog() => LogEntries.Clear();

    public void Dispose()
    {
        _timer.Stop();
        _broadcast.StateChanged -= OnBroadcastStateChanged;
    }
}
