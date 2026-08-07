using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using ManagedBass;
using ManagedBass.Mix;
using AudioOutputDevice = JustPlay.Core.Models.AudioOutputDevice;
using CorePlaybackState = JustPlay.Core.Models.PlaybackState;

namespace JustPlay.Audio.Bass;

/// <summary>
/// <see cref="IAudioEngine"/> backed by un4seen BASS via ManagedBass.
///
/// S2 MIXER REFACTOR: the engine now maintains a PERSISTENT output mixer stream
/// that lives for the process lifetime once created. Track sources are loaded as
/// DECODE streams and plugged into the mixer. This means:
///   - The Icecast encoder (BassBroadcastService) can stay attached to the mixer
///     across track changes - the stream never drops between songs.
///   - The mixer runs continuously even when paused (outputting silence via
///     BassFlags.MixerNonStop) so the cast connection never stalls.
///
/// ManagedBass.Mix API references (verified against managedbass.github.io/api):
///   BassMix.CreateMixerStream(freq, chans, BassFlags) -> int
///   BassMix.MixerAddChannel(mixer, source, BassFlags) -> bool
///   BassMix.MixerRemoveChannel(source) -> bool    [BASS_Mixer_ChannelRemove]
///   BassMix.ChannelFlags(source, set, mask) -> BassFlags  [toggle flags on source]
///   BassMix.ChannelGetPosition(source, PositionFlags) -> long
///   BassMix.ChannelSetSync(source, SyncFlags, param, proc) -> int
///   BassFlags.MixerNonStop - mixer keeps producing silence when no active sources
///   BassFlags.MixerChanBuffer  - enables accurate position tracking for mixer sources
///   BassFlags.MixerChanPause   - pauses a source within the mixer without stopping it
/// </summary>
public sealed class BassAudioEngine : IAudioEngine, IBassMixerSource, IDuckableAudioOutput
{
    // -- Mixer (persistent output, process lifetime once created) ---------
    // Created on first Load; never freed until Dispose. The Icecast encoder
    // attaches to this handle; exposing it as internal so BassBroadcastService
    // (same assembly) can read it without a public API change.
    private int _mixer;

    // Guard so a second FadeOutAsync call during teardown returns immediately
    // rather than stomping on a fade already in progress.
    private int _fading; // 0 = idle, 1 = fading (Interlocked.Exchange flag)

    // -- Active output device index (updated by SetOutputDevice) ---------
    // Initialised to -1 (not yet set). After Bass.Init() in the constructor,
    // the current device is effectively whatever Bass.Init() used (device 0 =
    // default). We leave _currentDevice as -1 until the first explicit
    // SetOutputDevice call or startup hydration so the VM can tell "not yet applied".
    private int _currentDevice = -1;

    // -- N26 cue-ducking (IDuckableAudioOutput) ----------------------------
    // True while BassPreListenEngine's CueArbiter has us suppressed (same-device cue playing).
    // Guards SetDucked against a redundant re-duck/re-restore, and gates the Volume setter below
    // so a live master-volume change WHILE ducked is remembered in _volume but not pushed to the
    // audible channel attribute until the duck is lifted (otherwise a slider drag mid-cue would
    // silently un-mute the speakers while the cue is still playing).
    private bool _ducked;

    // Short, click-free ramp for duck/restore - same family as FadeOutAsync's 50-500 ms clamp,
    // just fast enough that engaging/releasing the duck is inaudible as a "pop".
    private const int DuckFadeMs = 60;

    // -- Per-track decode source (the current / incoming track) ------------
    private int _source;

    // -- Crossfade: the outgoing (old) source fades out here while _source holds the
    // incoming track. Both overlap on the mixer during the blend. A wall-clock handoff
    // timer frees the outgoing once the fade completes. 0 = no crossfade in flight.
    // All mutation goes through _xfadeLock + a generation counter so a late handoff
    // timer can never free a source that a newer crossfade has already reassigned.
    private int _outgoing;
    private SyncProcedure? _outgoingEndSync;          // kept alive so the native sync ptr stays valid
    private System.Threading.Timer? _handoffTimer;
    private int _xfadeGen;
    private readonly object _xfadeLock = new();

    private double _volume = 1.0;
    private double _normGainDb = 0.0;
    private CorePlaybackState _state = CorePlaybackState.Stopped;

    // BASS invokes sync callbacks on its own thread; keep the delegate alive so the
    // GC can't collect it while BASS still holds the pointer.
    // (CallbackOnCollectedDelegate risk - pin as a field.)
    private SyncProcedure? _endSync;

    // -- Master true-peak limiter / maximizer (DSP on the mixer output) --------
    // The limiter processes the post-mix signal, so it shapes BOTH local playback and the
    // Icecast encoder (BASSenc attaches at DSP priority -1000, i.e. AFTER this limiter, so the
    // stream is limited too - no special ordering needed). The limiter math is platform-agnostic
    // (JustPlay.Analysis.MasteringLimiter); the engine only owns the BASS wiring.
    //
    // _limiter is swapped (atomic reference write) when the drive changes - the DSP callback reads
    // whatever instance is current each call, so a drive change never drops the stream. Null when
    // bypassed. The DSPProcedure delegate is pinned as a field (same CallbackOnCollectedDelegate
    // risk as the sync callbacks). _limiterDspHandle is the ChannelSetDSP handle for removal.
    private volatile MasteringLimiter? _limiter;
    private DSPProcedure? _limiterDsp;
    private int _limiterDspHandle;
    private readonly object _limiterLock = new();

    // -- 3-band DJ EQ / isolator (DSP on the mixer, at the HEAD of the chain) --------------------
    // Wired at DSP priority 200 (highest) so the bus order is:
    //   EQ (200) -> AutoTilt (180) -> Transient (140) -> limiter (0) -> BASSenc encoder (-1000).
    // Tone shaping first, then the limiter as the final true-peak stage.
    private volatile ThreeBandEqualizer? _equalizer;
    private DSPProcedure? _equalizerDsp;
    private int _equalizerDspHandle;
    private readonly object _equalizerLock = new();

    // -- "Revive" rack: anti-flat blocks that sit just after the EQ ------------------------------
    // Full bus order (higher DSP priority runs first):
    //   EQ (200) -> AutoTilt (180) -> Transient (140) -> limiter (0) -> BASSenc encoder (-1000).
    // Each uses the same pinned-delegate + atomic instance-swap + true-bypass-when-neutral pattern
    // as the limiter/EQ above.

    // Adaptive spectral tilt (auto-master): priority 180.
    private volatile AdaptiveTilt? _tilt;
    private DSPProcedure? _tiltDsp;
    private int _tiltDspHandle;
    private readonly object _tiltLock = new();

    // Transient designer (punch): priority 140.
    private volatile TransientDesigner? _transient;
    private DSPProcedure? _transientDsp;
    private int _transientDspHandle;
    private readonly object _transientLock = new();

    // FFT scratch + smoothing state. FFT2048 returns 1024 floats; we collapse those
    // into 4 perceptual bands (bass / lowMid / mid / treble) and apply EMA smoothing
    // so consecutive frames don't flicker. All visualizer-facing - never used for
    // analysis (use IAudioDecoder for that).
    private readonly float[] _fftBuffer = new float[1024];
    private readonly float[] _smoothBands = new float[4];

    // -- Spectrum taps: DRY pre-bus + WET post-bus capture -----------------------------------------
    // TWO passive read-only DSPs straddle the rack so DRY and WET are captured block-synchronously
    // and measured the SAME way (same Hann window + same 2048-pt FFT + same band collapse):
    //   DRY @ priority 1000 - runs BEFORE EQ(200)/AutoTilt(180)/Punch(140)/Limiter(0)/Encoder(-1000),
    //                         so it sees the raw summed mix before the rack shapes it.
    //   WET @ priority -500 - runs BELOW the limiter (0) and ABOVE the encoder (-1000), so it sees
    //                         the fully-processed signal that is actually heard / streamed.
    // Both callbacks are read-only (never modify the buffer), cheap (mono mixdown + array shift), and
    // run on the BASS audio thread; GetSpectrum reads the snapshots from the UI thread. The only
    // residual DRY->WET offset is the limiter's internal look-ahead - the real, correct latency.
    // The two taps are gated together: _spectrumTapLock guards BOTH handle/delegate pairs; each
    // snapshot has its own lock guarding only its bytes.
    private readonly object _spectrumTapLock = new();
    private DSPProcedure? _dryTapProc;
    private int _dryTapHandle;
    /// <summary>Most recent 2048 mono-mixdown samples from the pre-rack bus.
    /// Written on the BASS thread; read on the UI thread - always under <see cref="_drySnapshotLock"/>.</summary>
    private readonly float[] _drySnapshot = new float[2048];
    private readonly object _drySnapshotLock = new();
    private DSPProcedure? _wetTapProc;
    private int _wetTapHandle;
    /// <summary>Most recent 2048 mono-mixdown samples from the post-rack bus (what is heard / streamed).
    /// Written on the BASS thread; read on the UI thread - always under <see cref="_wetSnapshotLock"/>.</summary>
    private readonly float[] _wetSnapshot = new float[2048];
    private readonly object _wetSnapshotLock = new();
    // FFT work buffers - accessed only from the UI thread in GetSpectrum; no lock needed. One pair per
    // tap so the two transforms don't trample each other within a single GetSpectrum call.
    private readonly float[] _dryFftRe = new float[2048];
    private readonly float[] _dryFftIm = new float[2048];
    private readonly float[] _wetFftRe = new float[2048];
    private readonly float[] _wetFftIm = new float[2048];
    // Pre-computed Hann window for a 2048-point frame (static: one allocation per process).
    private static readonly float[] _hannWindow = BuildHannWindow(2048);
    // Reusable two-element buffer for ChannelGetLevel - avoids per-call allocation.
    private readonly float[] _levelBuf = new float[2];
    private static float[] BuildHannWindow(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    public BassAudioEngine()
    {
        // Default device, 44.1 kHz. Throws if the device can't be opened.
        if (!ManagedBass.Bass.Init())
        {
            var err = ManagedBass.Bass.LastError;
            // Already-initialised is fine (e.g. a second engine in tests); anything else is fatal.
            if (err != Errors.Already)
                throw new InvalidOperationException($"BASS init failed: {err}");
        }

        // Create the persistent output mixer EAGERLY (not lazily on first Load). It runs
        // continuously outputting silence (MixerNonStop), so the broadcast can connect and go
        // live BEFORE any track is loaded - the natural DJ workflow (go on air, then drop the
        // first track). Without this, Connect fails with "load a track first" until playback starts.
        EnsureMixer();
    }

    // -- Internal output handle exposed to BassBroadcastService -----------
    // The broadcast service (same assembly) reads this to attach the LAME encoder.
    // Zero until the first Load() call. After that it is valid for the process lifetime.
    public int OutputChannel => _mixer;

    // -- IAudioEngine: output device selection -----------------------------

    /// <inheritdoc/>
    public int CurrentOutputDevice => _currentDevice;

    /// <inheritdoc cref="IDuckableAudioOutput.OutputDeviceChanged"/>
    public event EventHandler? OutputDeviceChanged;

    /// <summary>
    /// Enumerate enabled, non-"No sound" BASS output devices.
    ///
    /// Enumeration API (verified against managedbass.github.io/api):
    ///   Bass.GetDeviceInfo(int device, out DeviceInfo info) -> bool
    ///   Returns false when <paramref name="device"/> is out of range.
    ///   Index 0 is always "No sound" - skip it.
    ///   DeviceInfo.IsEnabled: device is present and ready.
    ///   DeviceInfo.IsDefault: this is the system default output.
    ///   DeviceInfo.Name: human-readable name string.
    /// </summary>
    /// <summary>Friendly name for BASS device 0 ("No sound") - streaming-only, bypasses the OS audio stack.</summary>
    internal const string NoOutputDeviceName = "No output (stream only)";

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var result = new List<AudioOutputDevice>();
        // Real playback devices start at index 1 (index 0 is BASS's "No sound" device).
        for (var i = 1; ManagedBass.Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            result.Add(new AudioOutputDevice(i, info.Name, info.IsDefault));
        }
        // Append device 0 = BASS "No sound" as an explicit STREAMING-ONLY option: the mixer runs
        // on BASS's own clock and the encoder still captures the pristine 44.1k signal, while NOTHING
        // touches the OS audio stack (no Windows resampling / APO effects). Pick it when you don't
        // monitor locally (you'll hear the stream itself). Switching to it works live like any device.
        result.Add(new AudioOutputDevice(0, NoOutputDeviceName, false));
        return result;
    }

    /// <summary>
    /// Move the persistent mixer output to a different BASS device.
    ///
    /// ManagedBass API calls (verified against managedbass.github.io/api):
    ///   Bass.Init(int device, ...) - initialise a device; Errors.Already = already done, treat as success.
    ///   Bass.ChannelSetDevice(int handle, int device) - move a channel (including a mixer stream)
    ///     to a different already-initialised device. The channel continues playing on the new device.
    ///
    /// STREAM-CONTINUITY NOTE:
    ///   The Icecast encoder (BASSenc, via BassBroadcastService) is attached to _mixer as a DSP
    ///   callback. BASSenc reads PCM from the mixer via the DSP chain - it is NOT tied to the
    ///   device the mixer outputs to. Moving the mixer's device with ChannelSetDevice changes
    ///   WHERE the audio is heard but does NOT remove or disrupt the encoder DSP. The stream
    ///   therefore continues uninterrupted across a device switch. This is by design in BASS:
    ///   a channel's DSP/FX chain is channel-scoped, not device-scoped.
    ///   (Ref: un4seen BASS docs - BASS_ChannelSetDevice; BASSenc manual - encoder as channel DSP.)
    ///
    /// On failure the engine logs and keeps the previous device rather than throwing.
    /// </summary>
    public void SetOutputDevice(int index)
    {
        // Initialise the target device if it hasn't been initialised yet in this process.
        // Passing -1 for freq uses BASS's default sample rate.
        if (!ManagedBass.Bass.Init(index))
        {
            var err = ManagedBass.Bass.LastError;
            if (err != Errors.Already)
            {
                Console.WriteLine($"[SetOutputDevice] Bass.Init({index}) failed: {err}");
                return;
            }
        }

        // Move the persistent mixer to the new device. The mixer keeps playing and the
        // encoder DSP (if attached) is not disturbed - see doc comment above.
        if (_mixer != 0)
        {
            if (!ManagedBass.Bass.ChannelSetDevice(_mixer, index))
            {
                Console.WriteLine($"[SetOutputDevice] ChannelSetDevice({_mixer}, {index}) failed: {ManagedBass.Bass.LastError}");
                return;
            }
        }

        _currentDevice = index;
        OutputDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public CorePlaybackState State => _state;

    /// <remarks>
    /// Applies BASS_ATTRIB_VOL on the MIXER (master volume) - the channel's audible/device-output
    /// level. Per BASS's own docs this attribute "is not present in the sample data returned by
    /// BASS_ChannelGetData", i.e. it does NOT reach the DSP chain - so it affects all attached
    /// sources' local playback but NOT the Icecast encoder (BASSenc taps the mixer via a DSP at
    /// priority -1000; DSPs read pre-BASS_ATTRIB_VOL data). This is exactly what makes
    /// <see cref="SetDucked"/> below safe: it rides the same attribute.
    ///
    /// While <see cref="SetDucked"/> has us ducked, a live Volume change is remembered in
    /// <see cref="_volume"/> (so a slider drag "sticks") but withheld from the channel until the
    /// duck is released - otherwise the user's own volume touch would silently un-mute the
    /// speakers mid-cue (N26).
    /// </remarks>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (_mixer != 0 && !_ducked)
                ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _volume);
        }
    }

    /// <summary>
    /// N26 "cue wins on a shared device": duck (<paramref name="ducked"/> = true) or restore
    /// (false) the mixer's audible output. Idempotent - a repeated call with the same value is a
    /// no-op, so a debounced double cue-start can never re-capture an already-ducked level.
    ///
    /// <para><b>Verified BASS-doc citation (why this is safe for the stream):</b> BASS_ATTRIB_VOL
    /// ("is not present in the sample data returned by BASS_ChannelGetData, so it has no direct
    /// effect on decoding channels") is a device-output-only gain - it never reaches the DSP
    /// chain, and the Icecast encoder is wired as a DSP on this same mixer (<c>OutputChannel</c>,
    /// tapped by <c>BassBroadcastService</c>/<c>BASS_Encode_*_Start</c> at priority -1000). A
    /// separate attribute, BASS_ATTRIB_VOLDSP ("DSP chain volume level"), exists specifically for
    /// gain that DOES reach DSPs/encoding - this method deliberately does NOT use it. Sliding
    /// straight to/from 0 also never touches <see cref="Play"/>/<see cref="Pause"/> or
    /// <see cref="_source"/>'s normalization gain, so a main engine that was paused before the cue
    /// started stays paused after it stops - restore never forces playback.</para>
    /// </summary>
    public void SetDucked(bool ducked)
    {
        if (_mixer == 0 || ducked == _ducked) return;
        _ducked = ducked;
        var target = ducked ? 0f : (float)_volume;
        ManagedBass.Bass.ChannelSlideAttribute(_mixer, ChannelAttribute.Volume, target, DuckFadeMs);
    }

    public double NormalizationGainDb
    {
        get => _normGainDb;
        set
        {
            _normGainDb = value;
            ApplyNormalization();
        }
    }

    // Apply the per-track normalization gain on the SOURCE channel (not the mixer) so it's
    // independent of the master Volume and the same normalised signal feeds the Icecast encoder.
    // dB -> linear factor (10^(dB/20)); 0 dB = factor 1.0 = unity. The controller caps positive
    // gain by the track's peak, so the factor never pushes the source past full scale.
    private void ApplyNormalization()
    {
        if (_source == 0) return;
        var factor = _normGainDb == 0.0 ? 1.0 : System.Math.Pow(10.0, _normGainDb / 20.0);
        ManagedBass.Bass.ChannelSetAttribute(_source, ChannelAttribute.Volume, factor);
    }

    public TimeSpan Position
    {
        get
        {
            if (_source == 0) return TimeSpan.Zero;
            // Use BassMix.ChannelGetPosition on the source - this is the mixer-aware
            // variant that tracks buffered position correctly when MixerBuffer is set.
            // Source: managedbass.github.io/api/ManagedBass.Mix.BassMix.html
            var bytes = BassMix.ChannelGetPosition(_source, PositionFlags.Bytes);
            if (bytes < 0) return TimeSpan.Zero;
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_source, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
        set
        {
            if (_source == 0) return;
            // Seek on the decode source. PositionFlags.MixerReset (BASS_POS_MIXER_RESET) flushes the
            // mixer's buffer of audio already pulled from the OLD position - without it the mixer keeps
            // playing up to ~0.5s of stale buffered audio after a seek, which feels sluggish/laggy
            // (especially noticeable scrubbing FLAC). With the flush the new position is heard at once.
            var bytes = ManagedBass.Bass.ChannelSeconds2Bytes(_source, value.TotalSeconds);
            ManagedBass.Bass.ChannelSetPosition(_source, bytes, PositionFlags.Bytes | PositionFlags.MixerReset);
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_source == 0) return TimeSpan.Zero;
            // Length/bytes2seconds operate on the decode source, not the mixer.
            var bytes = ManagedBass.Bass.ChannelGetLength(_source);
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_source, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
    }

    public event EventHandler<CorePlaybackState>? StateChanged;
    public event EventHandler? PlaybackEnded;

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // A hard Load cancels any in-flight crossfade - drop the outgoing source too.
        FreeOutgoing();
        // Remove and free the old decode source (if any) before loading the new one.
        FreeSource();

        // Ensure the persistent mixer exists. Created once per process; subsequent
        // Load calls reuse it so the encoder stays attached.
        EnsureMixer();

        // Load the file as a DECODE stream (no device output - the mixer drives it).
        // BassFlags.Float for clean float PCM; BassFlags.Decode skips device routing.
        // Source: BASS API / ManagedBass.Bass.CreateStream docs.
        _source = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (_source == 0)
            throw new InvalidOperationException($"Could not load '{filePath}': {ManagedBass.Bass.LastError}");

        // Plug the decode source into the mixer.
        // BassFlags.MixerChanBuffer enables accurate position reporting via BassMix.ChannelGetPosition.
        // Source: managedbass.github.io/api - BassMix.MixerAddChannel, BassFlags.MixerChanBuffer.
        var added = BassMix.MixerAddChannel(_mixer, _source, BassFlags.MixerChanBuffer);
        if (!added)
            throw new InvalidOperationException($"MixerAddChannel failed: {ManagedBass.Bass.LastError}");

        // Add the source PAUSED. The mixer runs continuously (MixerNonStop), so a freshly
        // added, un-paused source would start playing immediately on Load - before Play()
        // is called (State would say Stopped while audio is heard). Pausing it here makes
        // Load silent; Play() clears MixerChanPause to start it. (Reviewed fix, 2026-06-04.)
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        // Apply the current normalization gain to the freshly-created source (re-applied here so a
        // reload - e.g. WithFileReleased around a tag write - keeps the same per-track gain).
        ApplyNormalization();

        // Wire the end-of-track sync on the source channel.
        // BassMix.ChannelSetSync is the mixer-aware sync - it fires when the source
        // in the mixer is exhausted, not when the mixer itself ends (it never does,
        // thanks to MixerNonStop). SyncFlags.End fires when the source decode reaches EOF.
        // Source: managedbass.github.io/api - BassMix.ChannelSetSync.
        WireEndSync(_source);

        SetState(CorePlaybackState.Stopped);
    }

    /// <summary>
    /// Wire the end-of-track sync on <paramref name="source"/> so it raises <see cref="PlaybackEnded"/>
    /// when that source's decode hits EOF. The captured handle is compared against the live
    /// <see cref="_source"/> at fire time: a sync from a source that is no longer current (e.g. an
    /// outgoing crossfade tail that reached EOF before the handoff timer freed it) is ignored, so the
    /// outgoing track never triggers a spurious advance.
    /// </summary>
    private void WireEndSync(int source)
    {
        _endSync = (_, _, _, _) =>
        {
            if (source != _source) return;
            SetState(CorePlaybackState.Stopped);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };
        BassMix.ChannelSetSync(source, SyncFlags.End, 0, _endSync);
    }

    /// <summary>
    /// Crossfade into <paramref name="filePath"/> over <paramref name="fadeMs"/> ms: the current
    /// source fades out (becoming the "outgoing" deck) while the new track fades in to its
    /// <paramref name="normGainDb"/> level - both overlap on the persistent mixer. The new track
    /// becomes current immediately (Position/Duration/seek track it). When nothing is playing or
    /// fadeMs <= 0 this degrades to a hard Load + gain + Play. The outgoing source is muted and
    /// freed by a handoff timer ~150 ms after the blend completes, and deliberately raises NO
    /// <see cref="PlaybackEnded"/> (the queue already advanced when the crossfade began).
    /// </summary>
    public void CrossfadeTo(string filePath, double normGainDb, int fadeMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureMixer();

        // Nothing to fade FROM, or crossfade disabled -> behave exactly like the hard path.
        if (_source == 0 || fadeMs <= 0)
        {
            Load(filePath);
            NormalizationGainDb = normGainDb;
            Play();
            return;
        }

        var clampedMs = Math.Clamp(fadeMs, 250, 12000);
        // Target volume for the incoming track = its normalization factor (same math as ApplyNormalization).
        var target = normGainDb == 0.0 ? 1.0f : (float)System.Math.Pow(10.0, normGainDb / 20.0);

        lock (_xfadeLock)
        {
            // Invalidate any pending handoff timer and clear a still-fading prior outgoing
            // (rapid re-trigger / very short tracks) before reusing the slot.
            _xfadeGen++;
            FreeOutgoingCore();

            // Create the incoming source FIRST so a load failure leaves the current playback untouched.
            var incoming = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
            if (incoming == 0)
                throw new InvalidOperationException($"Could not load '{filePath}': {ManagedBass.Bass.LastError}");

            // Demote the current source to "outgoing" and fade it out. Keep its delegate alive.
            _outgoing = _source;
            _outgoingEndSync = _endSync;
            ManagedBass.Bass.ChannelSlideAttribute(_outgoing, ChannelAttribute.Volume, 0f, clampedMs);

            // Plug the incoming source into the mixer, start it silent and un-paused, then slide it up.
            BassMix.MixerAddChannel(_mixer, incoming, BassFlags.MixerChanBuffer);
            ManagedBass.Bass.ChannelSetAttribute(incoming, ChannelAttribute.Volume, 0f);
            BassMix.ChannelFlags(incoming, BassFlags.Default, BassFlags.MixerChanPause); // clear pause -> start playing

            _source = incoming;
            _normGainDb = normGainDb;
            ManagedBass.Bass.ChannelSlideAttribute(_source, ChannelAttribute.Volume, target, clampedMs);
            WireEndSync(_source);
            SetState(CorePlaybackState.Playing);

            // Free the outgoing source once the blend has finished. The captured generation guards
            // against a newer crossfade having reassigned _outgoing in the meantime.
            var gen = _xfadeGen;
            _handoffTimer?.Dispose();
            _handoffTimer = new System.Threading.Timer(
                _ => { lock (_xfadeLock) { if (gen == _xfadeGen) FreeOutgoingCore(); } },
                null, clampedMs + 150, System.Threading.Timeout.Infinite);
        }
    }

    public void Play()
    {
        if (_source == 0) return;

        // Un-pause the source (clear MixerPause flag) so the mixer starts feeding from it.
        // BassMix.ChannelFlags(handle, flags, mask): flags = value to set, mask = bits to affect.
        // To CLEAR MixerPause: set=0, mask=MixerPause.
        // Source: managedbass.github.io/api - BassMix.ChannelFlags, BassFlags.MixerChanPause.
        BassMix.ChannelFlags(_source, BassFlags.Default, BassFlags.MixerChanPause);
        // Resume the outgoing deck too, if a crossfade is mid-blend (keeps both sides moving).
        if (_outgoing != 0) BassMix.ChannelFlags(_outgoing, BassFlags.Default, BassFlags.MixerChanPause);

        // The mixer itself must be playing (started once; subsequent Play calls are no-ops
        // because the mixer never stops - it just outputs silence while sources are paused).
        if (ManagedBass.Bass.ChannelIsActive(_mixer) != PlaybackState.Playing)
            ManagedBass.Bass.ChannelPlay(_mixer, false);

        SetState(CorePlaybackState.Playing);
    }

    public void Pause()
    {
        if (_source == 0) return;

        // Pause the source WITHIN the mixer by setting MixerPause on it.
        // The mixer itself keeps running (outputting silence) so the Icecast cast stays alive.
        // BassMix.ChannelFlags: set=MixerPause, mask=MixerPause.
        // Source: managedbass.github.io/api - BassMix.ChannelFlags, BassFlags.MixerChanPause.
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        if (_outgoing != 0) BassMix.ChannelFlags(_outgoing, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        SetState(CorePlaybackState.Paused);
    }

    public void Stop()
    {
        if (_source == 0) return;
        // Cancel any in-flight crossfade - there is no longer a "next" to blend into.
        FreeOutgoing();
        // Pause the source and rewind it.
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        ManagedBass.Bass.ChannelSetPosition(_source, 0);
        SetState(CorePlaybackState.Stopped);
    }

    public void Unload()
    {
        FreeSource();                       // releases the OS file handle
        SetState(CorePlaybackState.Stopped);
    }

    private void SetState(CorePlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Create the persistent mixer output stream if it doesn't exist yet.
    /// 44100 Hz / stereo / Float - matches the stream format used throughout the app.
    /// BassFlags.MixerNonStop: the mixer outputs silence when no sources are active,
    /// preventing the cast connection from stalling during track transitions.
    /// Source: managedbass.github.io/api - BassMix.CreateMixerStream, BassFlags.MixerNonStop.
    /// </summary>
    private void EnsureMixer()
    {
        if (_mixer != 0) return;

        _mixer = BassMix.CreateMixerStream(44100, 2, BassFlags.MixerNonStop | BassFlags.Float);
        if (_mixer == 0)
            throw new InvalidOperationException($"CreateMixerStream failed: {ManagedBass.Bass.LastError}");

        // Start the mixer immediately and keep it running for the process lifetime.
        // Volume will be applied when Volume setter is called.
        ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _volume);
        ManagedBass.Bass.ChannelPlay(_mixer, false);
    }

    private void FreeSource()
    {
        if (_source == 0) return;

        // Remove from the mixer before freeing the channel.
        // BassMix.MixerRemoveChannel (wraps BASS_Mixer_ChannelRemove).
        // Source: verified in BassMix.cs raw source search result.
        BassMix.MixerRemoveChannel(_source);
        ManagedBass.Bass.StreamFree(_source);
        _source = 0;
        _endSync = null;
    }

    /// <summary>Cancel any in-flight crossfade and free the outgoing source now. Bumps the
    /// generation so a pending handoff timer becomes a no-op. Safe to call when idle.</summary>
    private void FreeOutgoing()
    {
        lock (_xfadeLock)
        {
            _xfadeGen++;
            FreeOutgoingCore();
        }
    }

    /// <summary>Free the outgoing source. MUST be called while holding <see cref="_xfadeLock"/>.</summary>
    private void FreeOutgoingCore()
    {
        if (_outgoing == 0) return;
        BassMix.MixerRemoveChannel(_outgoing);
        ManagedBass.Bass.StreamFree(_outgoing);
        _outgoing = 0;
        _outgoingEndSync = null;
    }

    /// <summary>
    /// Graceful-quit fade: slide the MIXER output volume to 0 over <paramref name="fadeMs"/>
    /// milliseconds, then return so the caller can call Dispose.
    ///
    /// <para>WHY _mixer and not _source:</para>
    /// The master output channel for ALL audio leaving the app is <c>_mixer</c>.
    /// Per-track volume lives on <c>_source</c> (normalization gain - set by
    /// <see cref="ApplyNormalization"/>).  Sliding <c>_source</c> would only silence the
    /// current track; the mixer keeps running and any future sound (e.g. a brief click from
    /// the Icecast encoder DSP) would still be audible at full volume. Sliding <c>_mixer</c>
    /// silences EVERYTHING that goes to the hardware - the same handle that the master
    /// <see cref="Volume"/> property already controls, so this is the correct master-level
    /// fade. The slide happens on BASS's internal audio thread (non-blocking for us); we
    /// await Task.Delay so the UI thread yields rather than spinning.</para>
    ///
    /// <para>The slide does NOT write back to <c>_volume</c> or user settings. The process
    /// exits immediately after - there is nothing to restore.</para>
    /// </summary>
    public async Task FadeOutAsync(int fadeMs = 200)
    {
        // Already fading or already silent/stopped - nothing to do.
        if (Interlocked.Exchange(ref _fading, 1) != 0)
            return;

        // No-op when not playing (nothing heard, no click risk).
        if (_mixer == 0 || _state != CorePlaybackState.Playing)
        {
            _fading = 0;
            return;
        }

        // Clamp to a sane range: fast enough not to feel sluggish, long enough to be smooth.
        var clampedMs = Math.Clamp(fadeMs, 50, 500);

        // BASS ChannelSlideAttribute: smoothly interpolates from the current channel attribute
        // value to the target over the given number of milliseconds, running on BASS's own
        // audio thread. Returns immediately - we then await the duration so the audio has time
        // to fade before we free the channel in Dispose.
        // API ref: managedbass.github.io/api - Bass.ChannelSlideAttribute
        //   handle  = _mixer  (the persistent stereo output mixer - the master volume point)
        //   attrib  = ChannelAttribute.Volume
        //   value   = 0f      (target: silence)
        //   time    = clampedMs (ramp duration)
        ManagedBass.Bass.ChannelSlideAttribute(_mixer, ChannelAttribute.Volume, 0f, clampedMs);

        // Await the ramp plus a tiny margin so BASS finishes the slide before Dispose frees
        // the channel.  A hard Task.Delay is the simplest cross-platform approach here; polling
        // Bass.ChannelIsSliding would also work but adds complexity for no user-visible gain.
        await Task.Delay(clampedMs + 30);

        _fading = 0;
    }

    /// <summary>
    /// Configure the master true-peak limiter / maximizer on the mixer output. See
    /// <see cref="IAudioEngine.SetLimiter"/>. Ceiling is fixed at -1 dBTP (broadcast-safe);
    /// <paramref name="driveDb"/> is the maximizer makeup gain (0 = transparent safety limiter).
    ///
    /// Disabling removes the DSP from the chain (true bypass - no per-sample cost). Enabling wires the
    /// DSP once and then only swaps the limiter instance on subsequent drive changes, so toggling the
    /// drive while live never interrupts the audio or the Icecast stream.
    /// </summary>
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
                _limiter    = null;
                return;
            }

            // (Re)create the limiter at the mixer's fixed format (44.1 kHz stereo float). Swapping the
            // reference is atomic; the DSP callback picks up the new drive/ceiling on its next call.
            _limiter = new MasteringLimiter(sampleRate: 44100, ceilingDbTp: ceilingDbTp, driveDb: driveDb);

            // Wire the DSP exactly once. Default priority (0) places it ahead of the BASSenc encoder
            // (priority -1000), so the stream is limited too. Pin the delegate as a field.
            if (_limiterDspHandle == 0)
            {
                _limiterDsp = LimiterDspCallback;
                _limiterDspHandle = ManagedBass.Bass.ChannelSetDSP(_mixer, _limiterDsp, IntPtr.Zero, 0);
            }
        }
    }

    /// <inheritdoc/>
    public double GetLimiterGainReductionDb()
    {
        // _limiter is volatile - one read, no lock needed (same pattern as LimiterDspCallback).
        // When bypassed (null) or not playing, return 0 so the meter stays dark.
        var lim = _limiter;
        if (lim is null || _state != CorePlaybackState.Playing)
            return 0.0;
        return lim.LastGainReductionDb;
    }

    /// <inheritdoc/>
    public void GetOutputLevels(out float leftPeak, out float rightPeak)
    {
        leftPeak = rightPeak = 0f;
        if (_mixer == 0 || _state != CorePlaybackState.Playing) return;
        // Peak over a ~20 ms window on the post-DSP mixer output (what is heard / streamed).
        // Stereo (per-channel) peak - RMS flag omitted => peak retrieval, one value per channel.
        // Mirrors BassInputCaptureEngine.GetLevels - same BASS call, same _levelBuf pattern.
        if (ManagedBass.Bass.ChannelGetLevel(_mixer, _levelBuf, 0.02f, LevelRetrievalFlags.Stereo))
        {
            leftPeak = _levelBuf[0];
            rightPeak = _levelBuf[1];
        }
    }

    /// <summary>
    /// BASS DSP callback (fires on BASS's audio thread). The mixer is float PCM, so the native buffer
    /// is a block of interleaved-stereo floats; wrap it in a <see cref="Span{T}"/> with no copy and let
    /// the limiter process it in place. Reads <see cref="_limiter"/> once so a concurrent SetLimiter
    /// swap is seen atomically (and a disable mid-call is a harmless no-op).
    /// </summary>
    private unsafe void LimiterDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var lim = _limiter;
        if (lim is null || length <= 0) return;
        var samples = new Span<float>((void*)buffer, length / sizeof(float));
        lim.ProcessInterleavedStereo(samples);
    }

    /// <summary>
    /// Configure the 3-band DJ EQ on the mixer output. See <see cref="IAudioEngine.SetEqualizer"/>.
    /// Wired at priority 200 so it runs at the head of the bus chain. Flat (all unity) -> DSP removed.
    /// </summary>
    public void SetEqualizer(double lowGain, double midGain, double highGain)
    {
        EnsureMixer();
        lock (_equalizerLock)
        {
            // Flat within a hair -> true bypass (no DSP, perfectly transparent).
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
                _equalizer    = null;
                return;
            }

            _equalizer = new ThreeBandEqualizer(sampleRate: 44100, lowGain, midGain, highGain);

            // Priority 200 > limiter (0) -> EQ processes first, limiter is the final stage.
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

    /// <summary>
    /// Configure the adaptive spectral tilt ("auto-master") on the mixer output. See
    /// <see cref="IAudioEngine.SetAdaptiveTilt"/>. Priority 180 (just after the EQ). Strength 0 -> bypass.
    /// </summary>
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
                _tilt    = null;
                return;
            }

            _tilt = new AdaptiveTilt(sampleRate: 44100, strength: strength);

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

    /// <summary>
    /// Configure the transient designer (punch) on the mixer output. See <see cref="IAudioEngine.SetTransientDesigner"/>.
    /// Priority 140 (after the tilt, before the limiter). Punch 0 -> bypass.
    /// </summary>
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
                _transient    = null;
                return;
            }

            _transient = new TransientDesigner(sampleRate: 44100, punch: punch);

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
        _handoffTimer?.Dispose();
        FreeOutgoing();
        FreeSource();
        if (_mixer != 0)
        {
            // Remove both spectrum taps before freeing the mixer (ChannelRemoveDSP needs a valid handle).
            lock (_spectrumTapLock)
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
            ManagedBass.Bass.StreamFree(_mixer);
            _mixer = 0;
        }
        ManagedBass.Bass.Free();
    }

    /// <summary>
    /// FFT magnitudes aggregated into four perceptual bands. Bin ranges assume
    /// the mixer is at 44100 Hz (1024 bins -> ~21.5 Hz/bin) - bands skew
    /// slightly at other sample rates but stay perceptually correct.
    ///
    ///   bass    bins   1..7    (~21..150 Hz)
    ///   lowMid  bins   7..28   (~150..600 Hz)
    ///   mid     bins  28..120  (~600..2580 Hz)
    ///   treble  bins 120..480  (~2580..10330 Hz)
    ///
    /// EMA smoothing (alpha=0.35) so visualizer frames don't strobe; idle decays
    /// the smoothed values by 0.85x per call so the lines settle gently when
    /// playback stops instead of snapping to zero.
    ///
    /// S2: FFT is read from the MIXER (what's actually heard / sent to encoder),
    /// not from the per-track source. This is correct - it reflects the actual
    /// audio output at all times.
    /// </summary>
    public void GetFftBands(Span<float> destination)
    {
        if (destination.Length < 4)
            throw new ArgumentException("destination must have length >= 4", nameof(destination));

        if (_mixer == 0 || _state != CorePlaybackState.Playing)
        {
            for (var i = 0; i < 4; i++)
                _smoothBands[i] *= 0.85f;
            new ReadOnlySpan<float>(_smoothBands).CopyTo(destination);
            return;
        }

        // length parameter encodes the FFT request: size flag | FFTRemoveDC.
        // Negative return = BASS error (stream not ready, etc.) -> emit zeros.
        // Reading from _mixer so we see the post-mix signal (what the encoder sends).
        var result = ManagedBass.Bass.ChannelGetData(
            _mixer, _fftBuffer,
            (int)(DataFlags.FFT2048 | DataFlags.FFTRemoveDC));

        if (result < 0)
        {
            destination[..4].Clear();
            return;
        }

        var bass   = AggregateMean(_fftBuffer,   1,   7);
        var lowMid = AggregateMean(_fftBuffer,   7,  28);
        var mid    = AggregateMean(_fftBuffer,  28, 120);
        var treble = AggregateMean(_fftBuffer, 120, 480);

        // Per-band sensitivity multipliers - raw FFT magnitudes are heavily
        // bass-weighted; without these the bass line saturates and the treble
        // line barely moves. Tuned visually on EDM-ish material.
        bass   *= 4.0f;
        lowMid *= 8.0f;
        mid    *= 14.0f;
        treble *= 24.0f;

        const float alpha = 0.35f;
        _smoothBands[0] = _smoothBands[0] * (1 - alpha) + bass   * alpha;
        _smoothBands[1] = _smoothBands[1] * (1 - alpha) + lowMid * alpha;
        _smoothBands[2] = _smoothBands[2] * (1 - alpha) + mid    * alpha;
        _smoothBands[3] = _smoothBands[3] * (1 - alpha) + treble * alpha;

        new ReadOnlySpan<float>(_smoothBands).CopyTo(destination);
    }

    private static float AggregateMean(float[] bins, int fromInclusive, int toExclusive)
    {
        var count = toExclusive - fromInclusive;
        if (count <= 0) return 0f;
        var sum = 0f;
        for (var i = fromInclusive; i < toExclusive; i++)
            sum += bins[i];
        return sum / count;
    }

    // -- Dual-tap spectrum (DRY pre-bus + WET post-bus) --------------------------------------------

    /// <summary>
    /// Enable or disable BOTH spectrum capture taps together (DRY @ priority 1000, pre-rack; WET @
    /// priority -500, post-rack). They are passive read-only DSPs straddling the rack so DRY and WET
    /// are captured block-synchronously. Idempotent.
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

    /// <summary>
    /// BASS DSP callback for the DRY spectrum tap (priority 1000, pre-rack). Runs on BASS's audio
    /// thread. Snapshots a mono mixdown of the most recent 2048 frames into <see cref="_drySnapshot"/>
    /// for <see cref="GetSpectrum"/>. Does NOT modify the buffer - purely observational.
    /// </summary>
    private unsafe void DryTapDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        CaptureMonoSnapshot(new ReadOnlySpan<float>((void*)buffer, length / sizeof(float)), _drySnapshot, _drySnapshotLock);
    }

    /// <summary>
    /// BASS DSP callback for the WET spectrum tap (priority -500, post-rack - below the limiter, above
    /// the encoder). Runs on BASS's audio thread. Snapshots a mono mixdown of the most recent 2048
    /// frames into <see cref="_wetSnapshot"/>. Does NOT modify the buffer - purely observational.
    /// </summary>
    private unsafe void WetTapDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0) return;
        CaptureMonoSnapshot(new ReadOnlySpan<float>((void*)buffer, length / sizeof(float)), _wetSnapshot, _wetSnapshotLock);
    }

    /// <summary>
    /// Mono-mixdown the most recent up-to-2048 interleaved-stereo frames in <paramref name="samples"/>
    /// into <paramref name="snapshot"/> (shifting older content left when the block is shorter). Held
    /// under <paramref name="snapshotLock"/> so <see cref="GetSpectrum"/> can read a consistent frame.
    /// Shared by both taps so DRY and WET are captured identically.
    /// </summary>
    private static void CaptureMonoSnapshot(ReadOnlySpan<float> samples, float[] snapshot, object snapshotLock)
    {
        var frameCount = samples.Length / 2;   // stereo interleaved: 2 floats per frame
        if (frameCount <= 0) return;

        lock (snapshotLock)
        {
            if (frameCount >= 2048)
            {
                // Block large enough to fill the snapshot entirely: copy last 2048 frames.
                var offset = frameCount - 2048;
                for (var i = 0; i < 2048; i++)
                {
                    var s = (offset + i) * 2;
                    snapshot[i] = (samples[s] + samples[s + 1]) * 0.5f;
                }
            }
            else
            {
                // Shift the existing snapshot left to make room for the new block.
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
    /// with 60 log-spaced 1/6-octave summed-power bands from 20 Hz to 20 kHz, matching
    /// <c>SpectralProfile.BandCount</c>. Both taps are measured identically (same Hann window + same
    /// 2048-pt FFT + same band collapse) from block-synchronous snapshots, so the only DRY->WET offset
    /// is the limiter's internal look-ahead. Returns zeros for BOTH when not playing or the taps are
    /// disabled. UI-thread safe; cheap enough to call every render frame.
    /// </summary>
    public void GetSpectrum(Span<float> dryMagnitudes, Span<float> wetMagnitudes)
    {
        const double BinHz = 44100.0 / 2048.0;   // bin width ~ 21.53 Hz for a 2048-pt FFT @ 44100
        var fillW = Math.Min(wetMagnitudes.Length, 60);
        var fillD = Math.Min(dryMagnitudes.Length, 60);

        bool tapsEnabled;
        lock (_spectrumTapLock)
            tapsEnabled = _dryTapHandle != 0 && _wetTapHandle != 0;
        bool active = tapsEnabled && _mixer != 0 && _state == CorePlaybackState.Playing;

        if (!active)
        {
            dryMagnitudes[..fillD].Clear();
            wetMagnitudes[..fillW].Clear();
            return;
        }

        // Identical processing for both taps -> DRY and WET are directly comparable.
        SnapshotToBands(_drySnapshot, _drySnapshotLock, _dryFftRe, _dryFftIm, BinHz, dryMagnitudes[..fillD]);
        SnapshotToBands(_wetSnapshot, _wetSnapshotLock, _wetFftRe, _wetFftIm, BinHz, wetMagnitudes[..fillW]);
    }

    /// <summary>
    /// Copy a mono snapshot (under its lock), apply the Hann window, run a 2048-pt forward FFT, and
    /// collapse the positive-frequency bins to log-spaced power bands. All FFT work happens outside the
    /// snapshot lock. <paramref name="fftRe"/>/<paramref name="fftIm"/> are per-tap scratch buffers.
    /// </summary>
    private static void SnapshotToBands(float[] snapshot, object snapshotLock, float[] fftRe, float[] fftIm, double binHz, Span<float> bands)
    {
        // Copy the snapshot under lock; everything below operates on the local scratch buffers.
        lock (snapshotLock)
            Array.Copy(snapshot, fftRe, 2048);

        // Apply Hann window to reduce spectral leakage, then zero the imaginary part.
        for (var i = 0; i < 2048; i++)
            fftRe[i] *= _hannWindow[i];
        Array.Clear(fftIm, 0, 2048);

        // In-place forward FFT (JustPlay.Analysis.Fft - radix-2 Cooley-Tukey, no allocations).
        Fft.Forward(fftRe, fftIm);

        // Linear magnitude for bins 0..1023 (positive-frequency half). Reuse fftRe as magnitude scratch.
        for (var i = 0; i < 1024; i++)
            fftRe[i] = MathF.Sqrt(fftRe[i] * fftRe[i] + fftIm[i] * fftIm[i]);

        BinsToBands(new ReadOnlySpan<float>(fftRe, 0, 1024), binHz, bands);
    }

    /// <summary>
    /// Collapse <paramref name="magnitudeBins"/> into 60 log-spaced 1/6-octave summed-power bands
    /// from 20 Hz to 20 kHz. Band <c>b</c>: lo = 20-2^(b/6) Hz, hi = 20-2^((b+1)/6) Hz.
    /// Power contribution of each bin = magnitude^2 (converting linear-magnitude bins to power domain).
    /// Fills min(<paramref name="bands"/>.Length, 60) output entries.
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
}
