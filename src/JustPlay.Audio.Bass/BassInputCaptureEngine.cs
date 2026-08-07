using System;
using System.Collections.Generic;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
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
///     -> RecordProc (copies PCM into a Push decode stream, with the input-gain trim)
///     -> Push decode stream  --> added to -->  persistent mixer (MixerNonStop, 44.1k stereo float)
///          on the mixer: ThreeBandEqualizer(200) -> AdaptiveTilt(180) -> TransientDesigner(140) -> MasteringLimiter(0)
///     -> mixer plays on the default OUTPUT device (clock source; local monitor, volume default 0)
///     -> BassBroadcastService attaches BASSenc at priority -1000 -> taps the mixer AFTER all DSP -> Icecast
/// </code>
///
/// This deliberately mirrors <see cref="BassAudioEngine"/>'s mixer + DSP wiring (same processors,
/// same priorities, same pinned-delegate pattern) so the bus chain and the "Hard" preset behave
/// identically to JustPlay's playout. The ONE difference is the source: a recording push stream
/// instead of a file decode stream.
///
/// <para>Clock note: when the input is a LOOPBACK device it shares the output device's clock, so
/// capture and playback are sample-locked (no drift - the primary DJ use case: streaming the
/// audio your DJ software is already outputting). A physical input feeding a DIFFERENT output
/// device runs on two clocks and can drift very slowly over a long stream; the generous push
/// buffer absorbs it for typical sessions. (A future hardening pass could add a drift-resampling
/// bridge or WASAPI loopback with event sync - see just-stream-blueprint.md Sec.7.)</para>
///
/// API signatures verified against managedbass.github.io/api:
///   Bass.RecordGetDeviceInfo(int, out DeviceInfo) - Bass.RecordInit(int) - Bass.RecordStart(int,int,BassFlags,RecordProcedure,IntPtr)
///   Bass.CreateStream(int,int,BassFlags,StreamProcedureType) [Push] - Bass.StreamPutData(int,IntPtr,int)
///   BassMix.CreateMixerStream / MixerAddChannel - Bass.ChannelGetLevel(int,float[],float,LevelRetrievalFlags)
/// </summary>
public sealed class BassInputCaptureEngine : IAudioInputEngine, IBassMixerSource
{
    private int _mixer;    // persistent output mixer (encoder taps this) - created lazily
    private int _record;   // HRECORD handle from RecordStart, 0 when not capturing
    private int _push;     // Push decode stream bridging RecordProc -> mixer, 0 when not capturing
    private int _currentDevice = -1;
    private bool _capturing;
    private int _outputDevice;   // local monitor device; 0 = "No output (stream only)" (default)
    private int _sampleRate = 44100; // 44100 or 48000; rebuilt on change via SampleRate setter

    private double _monitorVolume; // 0..1, local only (default 0 = no monitor)
    private double _inputGainDb;    // dB trim applied to the capture source (stream + monitor)

    // Pinned callback delegates - BASS holds native function pointers; if these are GC'd while
    // BASS still references them we get a CallbackOnCollectedDelegate crash. Same rule as
    // BassAudioEngine._endSync / BassBroadcastService._notifyProc.
    private RecordProcedure? _recordProc;

    // -- Per-process "capture a specific APP" source (Phase 0, Path A) -----
    // Injected platform provider (Windows WASAPI process-loopback, or Null where unsupported). When
    // active it feeds float PCM straight into the SAME _push -> mixer -> DSP -> encoder path a device
    // source uses, so a captured app rides the identical broadcast chain.
    private readonly IProcessAudioCapture _appCapture;
    private Action<float[], int>? _onAppFrames;
    private bool _appActive;

    // -- DSP rack (identical processors + priorities to BassAudioEngine) ---
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

    // -- Spectrum taps: DRY pre-bus + WET post-bus capture -----------------------------------------
    // Mirrors BassAudioEngine exactly: two passive read-only DSPs straddling the bus rack.
    //   DRY @ priority 1000 - runs BEFORE EQ(200)/Tilt(180)/Punch(140)/Limiter(0)/Encoder(-1000)
    //   WET @ priority -500 - runs BELOW the limiter and ABOVE the encoder (what is actually streamed)
    // Taps are gated together via _spectrumTapLock; each snapshot has its own lock.
    private readonly object _spectrumTapLock = new();
    private DSPProcedure? _dryTapProc;
    private int _dryTapHandle;
    private readonly float[] _drySnapshot = new float[2048];
    private readonly object _drySnapshotLock = new();
    private DSPProcedure? _wetTapProc;
    private int _wetTapHandle;
    private readonly float[] _wetSnapshot = new float[2048];
    private readonly object _wetSnapshotLock = new();
    // FFT work buffers - accessed only from the UI thread in GetSpectrum; no lock needed.
    private readonly float[] _dryFftRe = new float[2048];
    private readonly float[] _dryFftIm = new float[2048];
    private readonly float[] _wetFftRe = new float[2048];
    private readonly float[] _wetFftIm = new float[2048];
    // Pre-computed Hann window for a 2048-point frame (static: one allocation per process).
    private static readonly float[] _hannWindow = BuildHannWindow(2048);
    private static float[] BuildHannWindow(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    public BassInputCaptureEngine(IProcessAudioCapture appCapture)
    {
        _appCapture = appCapture;
        // Initialise the default OUTPUT device so the mixer has a clock to play on (the encoder
        // taps the mixer; the device just drives it - monitor volume defaults to 0). Errors.Already
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
            // Tear down capture + mixer - next StartCapture rebuilds at the new rate.
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
            // Null spectrum tap handles - mixer is gone, BASS already removed the DSPs.
            _dryTapHandle = 0;
            _dryTapProc   = null;
            _wetTapHandle = 0;
            _wetTapProc   = null;
            // Null processor objects - they were tuned to the old sample rate.
            _limiter = null;
            _equalizer = null;
            _tilt = null;
            _transient = null;
            // Null pinned delegates - they are no longer registered with BASS.
            _limiterDsp = null;
            _equalizerDsp = null;
            _tiltDsp = null;
            _transientDsp = null;
            SetCapturing(false);
        }
    }

    public event EventHandler<bool>? CaptureStateChanged;

    // -- Device enumeration -----------------------------------------------

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

    /// <summary>Friendly name for BASS device 0 ("No sound") - the stream-only / no-monitor option.</summary>
    internal const string NoOutputDeviceName = "No output (stream only)";

    /// <summary>
    /// Enumerate enabled output devices for LOCAL monitoring, plus device 0 = "No output (stream
    /// only)". Mirrors <see cref="BassAudioEngine.GetOutputDevices"/>: real devices start at index 1
    /// (0 is BASS's "No sound" device, appended as the explicit no-monitor option - the stream still
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

    // -- Capture lifecycle ------------------------------------------------

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

        // Feed the bridge into the mixer (playing - no MixerChanPause). MixerChanBuffer keeps the
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
        if (!_capturing && _record == 0 && !_appActive) return;
        TeardownCapture();
        _currentDevice = -1;
        SetCapturing(false);
    }

    // -- Application (per-process) capture ---------------------------------

    public bool SupportsApplicationCapture => _appCapture.IsSupported;

    public IReadOnlyList<CaptureApp> GetCaptureApps() => _appCapture.GetCapturableApps();

    public void StartApplicationCapture(int processId, AppCaptureChannels channels = AppCaptureChannels.FullMix)
    {
        if (!_appCapture.IsSupported)
            throw new NotSupportedException("Per-process app capture is not supported on this build.");

        EnsureMixer();

        // Tear down any current source (a device record OR a previous app capture); keep the mixer
        // (and any active Icecast stream) running so the switch is seamless.
        TeardownCapture();

        // Bridge: a Push DECODE stream the app-capture frames feed and the mixer pulls from - the
        // SAME path a device source uses, so the captured app rides the identical DSP/limiter/encoder.
        _push = ManagedBass.Bass.CreateStream(_sampleRate, 2, BassFlags.Decode | BassFlags.Float, StreamProcedureType.Push);
        if (_push == 0)
            throw new InvalidOperationException($"Push stream create failed: {ManagedBass.Bass.LastError}");
        ApplyInputGain();

        if (!BassMix.MixerAddChannel(_mixer, _push, BassFlags.MixerChanBuffer))
        {
            var err = ManagedBass.Bass.LastError;
            ManagedBass.Bass.StreamFree(_push); _push = 0;
            throw new InvalidOperationException($"MixerAddChannel failed: {err}");
        }

        _onAppFrames ??= OnAppFrames;
        _appCapture.FramesAvailable += _onAppFrames;
        try
        {
            // The provider always DELIVERS interleaved stereo (it extracts the Master pair for a
            // multi-out target), so the push stream / mixer path below is unchanged.
            _appCapture.Start(processId, _sampleRate, AppCaptureFormat.From(channels));
        }
        catch
        {
            _appCapture.FramesAvailable -= _onAppFrames;
            TeardownCapture();
            throw;
        }

        _appActive = true;
        _currentDevice = -1;
        SetCapturing(true);
    }

    /// <summary>
    /// App-capture frame sink - fires on the provider's capture thread with interleaved-stereo float
    /// PCM. Hand it straight to the push stream; the mixer pulls it through the DSP chain. Must NOT
    /// touch UI APIs. Same discipline as <see cref="RecordCallback"/>.
    /// </summary>
    private void OnAppFrames(float[] buffer, int count)
    {
        if (_push != 0 && count > 0)
            ManagedBass.Bass.StreamPutData(_push, buffer, count * sizeof(float));
    }

    /// <summary>
    /// RecordProc - fires on a BASS recording thread with a block of interleaved-stereo float PCM.
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
        // Stop an active app-capture source first (unsubscribe + stop the provider). Never let a
        // provider fault break teardown.
        if (_appActive)
        {
            if (_onAppFrames != null) _appCapture.FramesAvailable -= _onAppFrames;
            try { _appCapture.Stop(); } catch { /* provider stop must not break teardown */ }
            _appActive = false;
        }
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

    // -- Levels -----------------------------------------------------------

    public void GetLevels(out float leftPeak, out float rightPeak)
    {
        leftPeak = rightPeak = 0f;
        if (_mixer == 0 || !_capturing) return;
        // Peak over a ~20 ms window on the post-DSP mixer output (what the stream gets).
        // Stereo (per-channel) peak - RMS flag omitted => peak retrieval, one value per channel.
        if (ManagedBass.Bass.ChannelGetLevel(_mixer, _levels, 0.02f, LevelRetrievalFlags.Stereo))
        {
            leftPeak = _levels[0];
            rightPeak = _levels[1];
        }
    }

    // -- Output / gain ----------------------------------------------------

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
    /// changes ONLY where the local monitor is heard - the stream is never disturbed (same proven
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

    // -- Mixer ------------------------------------------------------------

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

    // -- Bus DSP rack - identical wiring to BassAudioEngine (priorities 200/180/140/0) -------------

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

    // -- Spectrum source (ISpectrumSource) - DRY/WET tonal balance + limiter GR + output levels -----
    // Identical to BassAudioEngine so the SHARED spectrum window (JustPlay.UI) opens over the broadcast
    // bus exactly as it does over playout. Gated on _capturing (silence => dark) instead of a play state.

    /// <inheritdoc/>
    public double GetLimiterGainReductionDb()
    {
        // _limiter ref read is atomic (SetLimiter swaps under _limiterLock); 0 keeps the meter dark
        // when bypassed or idle.
        var lim = _limiter;
        if (lim is null || !_capturing) return 0.0;
        return lim.LastGainReductionDb;
    }

    /// <inheritdoc/>
    // Same post-DSP mixer peak as the L/R meters - GetLevels already does exactly this.
    public void GetOutputLevels(out float leftPeak, out float rightPeak) => GetLevels(out leftPeak, out rightPeak);

    /// <summary>
    /// Enable or disable BOTH spectrum capture taps together (DRY @ priority 1000, pre-rack; WET @
    /// priority -500, post-rack). Passive read-only DSPs straddling the rack so DRY and WET are
    /// captured block-synchronously. EnsureMixer() first because STREAM's mixer is lazy. Idempotent.
    /// </summary>
    public void SetSpectrumTapEnabled(bool enabled)
    {
        EnsureMixer();
        lock (_spectrumTapLock)
        {
            if (enabled)
            {
                if (_dryTapHandle == 0)
                {
                    _dryTapProc   = DryTapDspCallback;
                    _dryTapHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _dryTapProc, IntPtr.Zero, 1000);
                }
                if (_wetTapHandle == 0)
                {
                    _wetTapProc   = WetTapDspCallback;
                    _wetTapHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _wetTapProc, IntPtr.Zero, -500);
                }
            }
            else
            {
                if (_dryTapHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _dryTapHandle);
                    _dryTapHandle = 0;
                    _dryTapProc   = null;
                }
                if (_wetTapHandle != 0)
                {
                    ManagedBass.Bass.ChannelRemoveDSP(_mixer, _wetTapHandle);
                    _wetTapHandle = 0;
                    _wetTapProc   = null;
                }
            }
        }
    }

    // DRY tap (priority 1000, pre-rack): snapshot a mono mixdown of the raw summed mix. Read-only.
    private unsafe void DryTapDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        CaptureMonoSnapshot(new ReadOnlySpan<float>((void*)buffer, length / sizeof(float)), _drySnapshot, _drySnapshotLock);
    }

    // WET tap (priority -500, post-rack - below the limiter, above the encoder): snapshot what is streamed.
    private unsafe void WetTapDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        CaptureMonoSnapshot(new ReadOnlySpan<float>((void*)buffer, length / sizeof(float)), _wetSnapshot, _wetSnapshotLock);
    }

    /// <summary>
    /// Mono-mixdown the most recent up-to-2048 interleaved-stereo frames into <paramref name="snapshot"/>
    /// (shifting older content left when the block is shorter), under <paramref name="snapshotLock"/> so
    /// GetSpectrum reads a consistent frame. Shared by both taps so DRY and WET are captured identically.
    /// </summary>
    private static void CaptureMonoSnapshot(ReadOnlySpan<float> samples, float[] snapshot, object snapshotLock)
    {
        var frameCount = samples.Length / 2;   // stereo interleaved: 2 floats per frame
        if (frameCount <= 0) return;

        lock (snapshotLock)
        {
            if (frameCount >= 2048)
            {
                var offset = frameCount - 2048;
                for (var i = 0; i < 2048; i++)
                {
                    var s = (offset + i) * 2;
                    snapshot[i] = (samples[s] + samples[s + 1]) * 0.5f;
                }
            }
            else
            {
                var keep = 2048 - frameCount;
                for (var i = 0; i < keep; i++)
                    snapshot[i] = snapshot[i + frameCount];
                for (var i = 0; i < frameCount; i++)
                {
                    var s = i * 2;
                    snapshot[keep + i] = (samples[s] + samples[s + 1]) * 0.5f;
                }
            }
        }
    }

    /// <summary>
    /// Fill <paramref name="dryMagnitudes"/> (pre-rack) and <paramref name="wetMagnitudes"/> (post-rack)
    /// with 60 log-spaced summed-power bands, matching <c>SpectralProfile.BandCount</c>. Both taps are
    /// measured identically from block-synchronous snapshots, so the only DRY->WET offset is the limiter's
    /// look-ahead. Returns zeros when not capturing or the taps are disabled. UI-thread safe.
    /// </summary>
    public void GetSpectrum(Span<float> dryMagnitudes, Span<float> wetMagnitudes)
    {
        var binHz = _sampleRate / 2048.0;   // honour 44.1k OR 48k mixer rate
        var fillW = Math.Min(wetMagnitudes.Length, 60);
        var fillD = Math.Min(dryMagnitudes.Length, 60);

        bool tapsEnabled;
        lock (_spectrumTapLock)
            tapsEnabled = _dryTapHandle != 0 && _wetTapHandle != 0;
        bool active = tapsEnabled && _mixer != 0 && _capturing;

        if (!active)
        {
            dryMagnitudes[..fillD].Clear();
            wetMagnitudes[..fillW].Clear();
            return;
        }

        SnapshotToBands(_drySnapshot, _drySnapshotLock, _dryFftRe, _dryFftIm, binHz, dryMagnitudes[..fillD]);
        SnapshotToBands(_wetSnapshot, _wetSnapshotLock, _wetFftRe, _wetFftIm, binHz, wetMagnitudes[..fillW]);
    }

    /// <summary>
    /// Copy a mono snapshot (under its lock), apply the Hann window, run a 2048-pt forward FFT, and
    /// collapse the positive-frequency bins to log-spaced power bands. FFT work happens outside the lock.
    /// </summary>
    private static void SnapshotToBands(float[] snapshot, object snapshotLock, float[] fftRe, float[] fftIm, double binHz, Span<float> bands)
    {
        lock (snapshotLock)
            Array.Copy(snapshot, fftRe, 2048);

        for (var i = 0; i < 2048; i++)
            fftRe[i] *= _hannWindow[i];
        Array.Clear(fftIm, 0, 2048);

        Fft.Forward(fftRe, fftIm);

        for (var i = 0; i < 1024; i++)
            fftRe[i] = MathF.Sqrt(fftRe[i] * fftRe[i] + fftIm[i] * fftIm[i]);

        BinsToBands(new ReadOnlySpan<float>(fftRe, 0, 1024), binHz, bands);
    }

    /// <summary>
    /// Collapse <paramref name="magnitudeBins"/> into 60 log-spaced 1/6-octave summed-power bands from
    /// 20 Hz to 20 kHz (band b: 20-2^(b/6) .. 20-2^((b+1)/6) Hz; power = magnitude^2).
    /// </summary>
    private static void BinsToBands(ReadOnlySpan<float> magnitudeBins, double binHz, Span<float> bands)
    {
        const int BandCount = 60;
        var fillCount = Math.Min(bands.Length, BandCount);
        bands[..fillCount].Clear();
        for (var b = 0; b < fillCount; b++)
        {
            var loHz   = 20.0 * Math.Pow(2.0,  b      / 6.0);
            var hiHz   = 20.0 * Math.Pow(2.0, (b + 1) / 6.0);
            var loIdx  = Math.Max(0, (int)(loHz / binHz));
            var hiIdx  = Math.Min(magnitudeBins.Length - 1, (int)(hiHz / binHz));
            var power  = 0f;
            for (var i = loIdx; i <= hiIdx; i++)
            {
                var m = magnitudeBins[i];
                power += m * m;
            }
            bands[b] = power;
        }
    }

    public void Dispose()
    {
        TeardownCapture();
        // Remove the spectrum taps before freeing the mixer (StreamFree would drop them anyway, but
        // null the pinned delegates explicitly - same hygiene as the rest of the rack).
        lock (_spectrumTapLock)
        {
            if (_mixer != 0 && _dryTapHandle != 0) ManagedBass.Bass.ChannelRemoveDSP(_mixer, _dryTapHandle);
            if (_mixer != 0 && _wetTapHandle != 0) ManagedBass.Bass.ChannelRemoveDSP(_mixer, _wetTapHandle);
            _dryTapHandle = 0; _dryTapProc = null;
            _wetTapHandle = 0; _wetTapProc = null;
        }
        if (_mixer != 0)
        {
            ManagedBass.Bass.StreamFree(_mixer);
            _mixer = 0;
        }
        ManagedBass.Bass.RecordFree();
        ManagedBass.Bass.Free();
    }
}
