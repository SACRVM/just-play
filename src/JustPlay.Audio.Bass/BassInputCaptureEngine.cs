using System;
using System.Collections.Generic;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using ManagedBass;
using ManagedBass.Mix;

namespace JustPlay.Audio.Bass;

/// <summary>
/// JUST STREAM capture engine: captures a live audio INPUT (sound card / loopback), runs it
/// through the shared bus DSP rack, and exposes a persistent mixer the Icecast encoder taps.
///
/// Signal path:
/// <code>
///   [BASS recording device]
///     → RecordProc (copies PCM into a Push decode stream, with the input-gain trim)
///     → Push decode stream  ──► added to ──►  persistent mixer (MixerNonStop, 44.1k stereo float)
///          on the mixer: ThreeBandEqualizer(200) → AdaptiveTilt(180) → TransientDesigner(140) → MasteringLimiter(0)
///     → mixer plays on the default OUTPUT device (clock source; local monitor, volume default 0)
///     → BassBroadcastService attaches BASSenc at priority −1000 → taps the mixer AFTER all DSP → Icecast
/// </code>
///
/// This deliberately mirrors <see cref="BassAudioEngine"/>'s mixer + DSP wiring (same processors,
/// same priorities, same pinned-delegate pattern) so the bus chain and the "Hard" preset behave
/// identically to JustPlay's playout. The ONE difference is the source: a recording push stream
/// instead of a file decode stream.
///
/// <para>Clock note: when the input is a LOOPBACK device it shares the output device's clock, so
/// capture and playback are sample-locked (no drift — the primary DJ use case: streaming the
/// audio your DJ software is already outputting). A physical input feeding a DIFFERENT output
/// device runs on two clocks and can drift very slowly over a long stream; the generous push
/// buffer absorbs it for typical sessions. (A future hardening pass could add a drift-resampling
/// bridge or WASAPI loopback with event sync — see just-stream-blueprint.md §7.)</para>
///
/// API signatures verified against managedbass.github.io/api:
///   Bass.RecordGetDeviceInfo(int, out DeviceInfo) · Bass.RecordInit(int) · Bass.RecordStart(int,int,BassFlags,RecordProcedure,IntPtr)
///   Bass.CreateStream(int,int,BassFlags,StreamProcedureType) [Push] · Bass.StreamPutData(int,IntPtr,int)
///   BassMix.CreateMixerStream / MixerAddChannel · Bass.ChannelGetLevel(int,float[],float,LevelRetrievalFlags)
/// </summary>
public sealed class BassInputCaptureEngine : IAudioInputEngine, IBassMixerSource
{
    private int _mixer;    // persistent output mixer (encoder taps this) — created lazily
    private int _record;   // HRECORD handle from RecordStart, 0 when not capturing
    private int _push;     // Push decode stream bridging RecordProc → mixer, 0 when not capturing
    private int _currentDevice = -1;
    private bool _capturing;
    private int _outputDevice;   // local monitor device; 0 = "No output (stream only)" (default)
    private int _sampleRate = 44100; // 44100 or 48000; rebuilt on change via SampleRate setter

    private double _monitorVolume; // 0..1, local only (default 0 = no monitor)
    private double _inputGainDb;    // dB trim applied to the capture source (stream + monitor)

    // Pinned callback delegates — BASS holds native function pointers; if these are GC'd while
    // BASS still references them we get a CallbackOnCollectedDelegate crash. Same rule as
    // BassAudioEngine._endSync / BassBroadcastService._notifyProc.
    private RecordProcedure? _recordProc;

    // ── DSP rack (identical processors + priorities to BassAudioEngine) ───
    private readonly object _limiterLock = new();
    private readonly object _equalizerLock = new();
    private readonly object _tiltLock = new();
    private readonly object _transientLock = new();

    private MasteringLimiter? _limiter;
    private ThreeBandEqualizer? _equalizer;
    private AdaptiveTilt? _tilt;
    private TransientDesigner? _transient;

    private DSPProcedure? _limiterDsp;
    private DSPProcedure? _equalizerDsp;
    private DSPProcedure? _tiltDsp;
    private DSPProcedure? _transientDsp;

    private int _limiterDspHandle;
    private int _equalizerDspHandle;
    private int _tiltDspHandle;
    private int _transientDspHandle;

    private readonly float[] _levels = new float[2];

    public BassInputCaptureEngine()
    {
        // Initialise the default OUTPUT device so the mixer has a clock to play on (the encoder
        // taps the mixer; the device just drives it — monitor volume defaults to 0). Errors.Already
        // is success (some other component may have init'd already).
        if (!ManagedBass.Bass.Init() && ManagedBass.Bass.LastError != Errors.Already)
            Console.WriteLine($"[Capture] Bass.Init failed: {ManagedBass.Bass.LastError}");
    }

    public int OutputChannel => _mixer;

    public bool IsCapturing => _capturing;

    public int CurrentInputDevice => _currentDevice;

    /// <summary>
    /// Capture/mixer sample rate: 44100 (default, matches most DJ software) or 48000 Hz
    /// (Opus native rate, best for Opus streams). Setting this while capturing tears down the
    /// current mixer and push stream; the next StartCapture recreates them at the new rate.
    /// The VM is responsible for restarting capture and re-applying the DSP rack after a change.
    /// </summary>
    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            var clamped = (value == 48000) ? 48000 : 44100;
            if (clamped == _sampleRate) return;
            _sampleRate = clamped;
            // Tear down capture + mixer — next StartCapture rebuilds at the new rate.
            TeardownCapture();
            if (_mixer != 0)
            {
                ManagedBass.Bass.StreamFree(_mixer);
                _mixer = 0;
            }
            // Reset DSP handles so SetLimiter/SetEqualizer/etc. re-register their callbacks on
            // the new mixer when EnsureMixer recreates it.
            _limiterDspHandle = 0;
            _equalizerDspHandle = 0;
            _tiltDspHandle = 0;
            _transientDspHandle = 0;
            // Null processor objects — they were tuned to the old sample rate.
            _limiter = null;
            _equalizer = null;
            _tilt = null;
            _transient = null;
            // Null pinned delegates — they are no longer registered with BASS.
            _limiterDsp = null;
            _equalizerDsp = null;
            _tiltDsp = null;
            _transientDsp = null;
            SetCapturing(false);
        }
    }

    public event EventHandler<bool>? CaptureStateChanged;

    // ── Device enumeration ───────────────────────────────────────────────

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var list = new List<AudioInputDevice>();
        for (int i = 0; ManagedBass.Bass.RecordGetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            list.Add(new AudioInputDevice(i, info.Name ?? $"Device {i}", info.IsLoopback, info.IsDefault));
        }
        return list;
    }

    /// <summary>Friendly name for BASS device 0 ("No sound") — the stream-only / no-monitor option.</summary>
    internal const string NoOutputDeviceName = "No output (stream only)";

    /// <summary>
    /// Enumerate enabled output devices for LOCAL monitoring, plus device 0 = "No output (stream
    /// only)". Mirrors <see cref="BassAudioEngine.GetOutputDevices"/>: real devices start at index 1
    /// (0 is BASS's "No sound" device, appended as the explicit no-monitor option — the stream still
    /// goes out, nothing touches the OS audio stack).
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var list = new List<AudioOutputDevice>();
        for (int i = 1; ManagedBass.Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            list.Add(new AudioOutputDevice(i, info.Name ?? $"Device {i}", info.IsDefault));
        }
        list.Add(new AudioOutputDevice(0, NoOutputDeviceName, false));
        return list;
    }

    // ── Capture lifecycle ────────────────────────────────────────────────

    public void StartCapture(int deviceIndex)
    {
        EnsureMixer();

        // Switching device while live: tear down the old record + push, keep the mixer (and any
        // active Icecast stream) running so the switch is seamless.
        TeardownCapture();

        // Select + init the recording device for this thread, then start it.
        if (!ManagedBass.Bass.RecordInit(deviceIndex) && ManagedBass.Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"RecordInit({deviceIndex}) failed: {ManagedBass.Bass.LastError}");
        ManagedBass.Bass.CurrentRecordingDevice = deviceIndex;

        // Bridge: a Push DECODE stream the RecordProc feeds and the mixer pulls from.
        _push = ManagedBass.Bass.CreateStream(_sampleRate, 2, BassFlags.Decode | BassFlags.Float, StreamProcedureType.Push);
        if (_push == 0)
            throw new InvalidOperationException($"Push stream create failed: {ManagedBass.Bass.LastError}");
        ApplyInputGain();

        // Capture at the mixer's format; BASS resamples the device to the target rate for us.
        _recordProc = RecordCallback;
        _record = ManagedBass.Bass.RecordStart(_sampleRate, 2, BassFlags.Float, _recordProc);
        if (_record == 0)
        {
            var err = ManagedBass.Bass.LastError;
            ManagedBass.Bass.StreamFree(_push);
            _push = 0;
            _recordProc = null;
            throw new InvalidOperationException($"RecordStart failed: {err}");
        }

        // Feed the bridge into the mixer (playing — no MixerChanPause). MixerChanBuffer keeps the
        // source buffered so level/position reads on it stay accurate.
        if (!BassMix.MixerAddChannel(_mixer, _push, BassFlags.MixerChanBuffer))
        {
            var err = ManagedBass.Bass.LastError;
            TeardownCapture();
            throw new InvalidOperationException($"MixerAddChannel failed: {err}");
        }

        _currentDevice = deviceIndex;
        SetCapturing(true);
    }

    public void StopCapture()
    {
        if (!_capturing && _record == 0) return;
        TeardownCapture();
        _currentDevice = -1;
        SetCapturing(false);
    }

    /// <summary>
    /// RecordProc — fires on a BASS recording thread with a block of interleaved-stereo float PCM.
    /// We hand it straight to the push stream; the mixer pulls it through the DSP chain. Returning
    /// true keeps recording. Must NOT touch UI APIs.
    /// </summary>
    private bool RecordCallback(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (_push != 0 && length > 0)
            ManagedBass.Bass.StreamPutData(_push, buffer, length);
        return true; // continue recording
    }

    private void TeardownCapture()
    {
        if (_push != 0)
            BassMix.MixerRemoveChannel(_push);
        if (_record != 0)
        {
            ManagedBass.Bass.ChannelStop(_record);
            ManagedBass.Bass.StreamFree(_record);
            _record = 0;
        }
        if (_push != 0)
        {
            ManagedBass.Bass.StreamFree(_push);
            _push = 0;
        }
        _recordProc = null;
    }

    private void SetCapturing(bool value)
    {
        if (_capturing == value) return;
        _capturing = value;
        CaptureStateChanged?.Invoke(this, value);
    }

    // ── Levels ───────────────────────────────────────────────────────────

    public void GetLevels(out float leftPeak, out float rightPeak)
    {
        leftPeak = rightPeak = 0f;
        if (_mixer == 0 || !_capturing) return;
        // Peak over a ~20 ms window on the post-DSP mixer output (what the stream gets).
        // Stereo (per-channel) peak — RMS flag omitted ⇒ peak retrieval, one value per channel.
        if (ManagedBass.Bass.ChannelGetLevel(_mixer, _levels, 0.02f, LevelRetrievalFlags.Stereo))
        {
            leftPeak = _levels[0];
            rightPeak = _levels[1];
        }
    }

    // ── Output / gain ────────────────────────────────────────────────────

    public double MonitorVolume
    {
        get => _monitorVolume;
        set
        {
            _monitorVolume = Math.Clamp(value, 0.0, 1.0);
            if (_mixer != 0)
                ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _monitorVolume);
        }
    }

    /// <summary>
    /// Route the monitor (the mixer's playback) to a BASS output device. 0 = "No output (stream
    /// only)". The encoder is attached to the mixer as channel-scoped DSP, so moving the device
    /// changes ONLY where the local monitor is heard — the stream is never disturbed (same proven
    /// behaviour as <see cref="BassAudioEngine.SetOutputDevice"/>).
    /// </summary>
    public void SetOutputDevice(int index)
    {
        _outputDevice = index;
        ApplyOutputDevice();
    }

    private void ApplyOutputDevice()
    {
        if (_mixer == 0) return; // applied later by EnsureMixer once the mixer exists
        if (!ManagedBass.Bass.Init(_outputDevice) && ManagedBass.Bass.LastError != Errors.Already)
        {
            Console.WriteLine($"[Capture] Bass.Init(device {_outputDevice}) failed: {ManagedBass.Bass.LastError}");
            return;
        }
        if (!ManagedBass.Bass.ChannelSetDevice(_mixer, _outputDevice))
            Console.WriteLine($"[Capture] ChannelSetDevice({_outputDevice}) failed: {ManagedBass.Bass.LastError}");
    }

    public double InputGainDb
    {
        get => _inputGainDb;
        set
        {
            _inputGainDb = Math.Clamp(value, -24.0, 24.0);
            ApplyInputGain();
        }
    }

    private void ApplyInputGain()
    {
        if (_push == 0) return;
        var linear = (float)Math.Pow(10.0, _inputGainDb / 20.0);
        ManagedBass.Bass.ChannelSetAttribute(_push, ChannelAttribute.Volume, linear);
    }

    // ── Mixer ────────────────────────────────────────────────────────────

    private void EnsureMixer()
    {
        if (_mixer != 0) return;
        _mixer = BassMix.CreateMixerStream(_sampleRate, 2, BassFlags.MixerNonStop | BassFlags.Float);
        if (_mixer == 0)
            throw new InvalidOperationException($"CreateMixerStream failed: {ManagedBass.Bass.LastError}");
        ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _monitorVolume);
        ManagedBass.Bass.ChannelPlay(_mixer, false);
        ApplyOutputDevice(); // honour the chosen monitor device (default 0 = no local output)
    }

    // ── Bus DSP rack — identical wiring to BassAudioEngine (priorities 200/180/140/0) ─────────────

    public void SetLimiter(bool enabled, double driveDb, double ceilingDbTp)
    {
        EnsureMixer();
        lock (_limiterLock)
        {
            if (!enabled)
            {
                if (_limiterDspHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _limiterDspHandle);
                    _limiterDspHandle = 0;
                }
                _limiterDsp = null;
                _limiter = null;
                return;
            }

            _limiter = new MasteringLimiter(sampleRate: _sampleRate, ceilingDbTp: ceilingDbTp, driveDb: driveDb);
            if (_limiterDspHandle == 0)
            {
                _limiterDsp = LimiterDspCallback;
                _limiterDspHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _limiterDsp, IntPtr.Zero, 0);
            }
        }
    }

    private unsafe void LimiterDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var lim = _limiter;
        if (lim is null || length <= 0) return;
        var samples = new Span<float>((void*)buffer, length / sizeof(float));
        lim.ProcessInterleavedStereo(samples);
    }

    public bool TryGetLimiterActivity(out double gainReductionDb, out double dutyCycle, out bool leftAtCeiling, out bool rightAtCeiling)
    {
        var lim = _limiter; // ref read is atomic; SetLimiter swaps it under _limiterLock
        if (lim is null)
        {
            gainReductionDb = 0; dutyCycle = 0; leftAtCeiling = rightAtCeiling = false;
            return false;
        }
        lim.ReadTelemetry(out gainReductionDb, out dutyCycle, out leftAtCeiling, out rightAtCeiling);
        return true;
    }

    public void SetEqualizer(double lowGain, double midGain, double highGain)
    {
        EnsureMixer();
        lock (_equalizerLock)
        {
            const double eps = 0.001;
            bool flat = Math.Abs(lowGain - 1.0) < eps
                     && Math.Abs(midGain - 1.0) < eps
                     && Math.Abs(highGain - 1.0) < eps;
            if (flat)
            {
                if (_equalizerDspHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _equalizerDspHandle);
                    _equalizerDspHandle = 0;
                }
                _equalizerDsp = null;
                _equalizer = null;
                return;
            }

            _equalizer = new ThreeBandEqualizer(sampleRate: _sampleRate, lowGain, midGain, highGain);
            if (_equalizerDspHandle == 0)
            {
                _equalizerDsp = EqualizerDspCallback;
                _equalizerDspHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _equalizerDsp, IntPtr.Zero, 200);
            }
        }
    }

    private unsafe void EqualizerDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var eq = _equalizer;
        if (eq is null || length <= 0) return;
        var samples = new Span<float>((void*)buffer, length / sizeof(float));
        eq.ProcessInterleavedStereo(samples);
    }

    public void SetAdaptiveTilt(double strength)
    {
        EnsureMixer();
        lock (_tiltLock)
        {
            if (strength <= 0.001)
            {
                if (_tiltDspHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _tiltDspHandle);
                    _tiltDspHandle = 0;
                }
                _tiltDsp = null;
                _tilt = null;
                return;
            }

            _tilt = new AdaptiveTilt(sampleRate: _sampleRate, strength: strength);
            if (_tiltDspHandle == 0)
            {
                _tiltDsp = TiltDspCallback;
                _tiltDspHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _tiltDsp, IntPtr.Zero, 180);
            }
        }
    }

    private unsafe void TiltDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var t = _tilt;
        if (t is null || length <= 0) return;
        var samples = new Span<float>((void*)buffer, length / sizeof(float));
        t.ProcessInterleavedStereo(samples);
    }

    public void SetTransientDesigner(double punch)
    {
        EnsureMixer();
        lock (_transientLock)
        {
            if (punch <= 0.001)
            {
                if (_transientDspHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _transientDspHandle);
                    _transientDspHandle = 0;
                }
                _transientDsp = null;
                _transient = null;
                return;
            }

            _transient = new TransientDesigner(sampleRate: _sampleRate, punch: punch);
            if (_transientDspHandle == 0)
            {
                _transientDsp = TransientDspCallback;
                _transientDspHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _transientDsp, IntPtr.Zero, 140);
            }
        }
    }

    private unsafe void TransientDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var tr = _transient;
        if (tr is null || length <= 0) return;
        var samples = new Span<float>((void*)buffer, length / sizeof(float));
        tr.ProcessInterleavedStereo(samples);
    }

    public void Dispose()
    {
        TeardownCapture();
        if (_mixer != 0)
        {
            ManagedBass.Bass.StreamFree(_mixer);
            _mixer = 0;
        }
        ManagedBass.Bass.RecordFree();
        ManagedBass.Bass.Free();
    }
}
