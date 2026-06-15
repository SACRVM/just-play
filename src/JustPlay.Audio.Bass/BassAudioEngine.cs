using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
///   • The Icecast encoder (BassBroadcastService) can stay attached to the mixer
///     across track changes — the stream never drops between songs.
///   • The mixer runs continuously even when paused (outputting silence via
///     BassFlags.MixerNonStop) so the cast connection never stalls.
///
/// ManagedBass.Mix API references (verified against managedbass.github.io/api):
///   BassMix.CreateMixerStream(freq, chans, BassFlags) → int
///   BassMix.MixerAddChannel(mixer, source, BassFlags) → bool
///   BassMix.MixerRemoveChannel(source) → bool    [BASS_Mixer_ChannelRemove]
///   BassMix.ChannelFlags(source, set, mask) → BassFlags  [toggle flags on source]
///   BassMix.ChannelGetPosition(source, PositionFlags) → long
///   BassMix.ChannelSetSync(source, SyncFlags, param, proc) → int
///   BassFlags.MixerNonStop — mixer keeps producing silence when no active sources
///   BassFlags.MixerChanBuffer  — enables accurate position tracking for mixer sources
///   BassFlags.MixerChanPause   — pauses a source within the mixer without stopping it
/// </summary>
public sealed class BassAudioEngine : IAudioEngine
{
    // ── Mixer (persistent output, process lifetime once created) ─────────
    // Created on first Load; never freed until Dispose. The Icecast encoder
    // attaches to this handle; exposing it as internal so BassBroadcastService
    // (same assembly) can read it without a public API change.
    private int _mixer;

    // Guard so a second FadeOutAsync call during teardown returns immediately
    // rather than stomping on a fade already in progress.
    private int _fading; // 0 = idle, 1 = fading (Interlocked.Exchange flag)

    // ── Active output device index (updated by SetOutputDevice) ─────────
    // Initialised to -1 (not yet set). After Bass.Init() in the constructor,
    // the current device is effectively whatever Bass.Init() used (device 0 =
    // default). We leave _currentDevice as -1 until the first explicit
    // SetOutputDevice call or startup hydration so the VM can tell "not yet applied".
    private int _currentDevice = -1;

    // ── Per-track decode source (the current / incoming track) ────────────
    private int _source;

    // ── Crossfade: the outgoing (old) source fades out here while _source holds the
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
    // (CallbackOnCollectedDelegate risk — pin as a field.)
    private SyncProcedure? _endSync;

    // FFT scratch + smoothing state. FFT2048 returns 1024 floats; we collapse those
    // into 4 perceptual bands (bass / lowMid / mid / treble) and apply EMA smoothing
    // so consecutive frames don't flicker. All visualizer-facing — never used for
    // analysis (use IAudioDecoder for that).
    private readonly float[] _fftBuffer = new float[1024];
    private readonly float[] _smoothBands = new float[4];

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
        // live BEFORE any track is loaded — the natural DJ workflow (go on air, then drop the
        // first track). Without this, Connect fails with "load a track first" until playback starts.
        EnsureMixer();
    }

    // ── Internal output handle exposed to BassBroadcastService ───────────
    // The broadcast service (same assembly) reads this to attach the LAME encoder.
    // Zero until the first Load() call. After that it is valid for the process lifetime.
    internal int OutputChannel => _mixer;

    // ── IAudioEngine: output device selection ─────────────────────────────

    /// <inheritdoc/>
    public int CurrentOutputDevice => _currentDevice;

    /// <summary>
    /// Enumerate enabled, non-"No sound" BASS output devices.
    ///
    /// Enumeration API (verified against managedbass.github.io/api):
    ///   Bass.GetDeviceInfo(int device, out DeviceInfo info) → bool
    ///   Returns false when <paramref name="device"/> is out of range.
    ///   Index 0 is always "No sound" — skip it.
    ///   DeviceInfo.IsEnabled: device is present and ready.
    ///   DeviceInfo.IsDefault: this is the system default output.
    ///   DeviceInfo.Name: human-readable name string.
    /// </summary>
    /// <summary>Friendly name for BASS device 0 ("No sound") — streaming-only, bypasses the OS audio stack.</summary>
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
    ///   Bass.Init(int device, ...) — initialise a device; Errors.Already = already done, treat as success.
    ///   Bass.ChannelSetDevice(int handle, int device) — move a channel (including a mixer stream)
    ///     to a different already-initialised device. The channel continues playing on the new device.
    ///
    /// STREAM-CONTINUITY NOTE:
    ///   The Icecast encoder (BASSenc, via BassBroadcastService) is attached to _mixer as a DSP
    ///   callback. BASSenc reads PCM from the mixer via the DSP chain — it is NOT tied to the
    ///   device the mixer outputs to. Moving the mixer's device with ChannelSetDevice changes
    ///   WHERE the audio is heard but does NOT remove or disrupt the encoder DSP. The stream
    ///   therefore continues uninterrupted across a device switch. This is by design in BASS:
    ///   a channel's DSP/FX chain is channel-scoped, not device-scoped.
    ///   (Ref: un4seen BASS docs — BASS_ChannelSetDevice; BASSenc manual — encoder as channel DSP.)
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
        // encoder DSP (if attached) is not disturbed — see doc comment above.
        if (_mixer != 0)
        {
            if (!ManagedBass.Bass.ChannelSetDevice(_mixer, index))
            {
                Console.WriteLine($"[SetOutputDevice] ChannelSetDevice({_mixer}, {index}) failed: {ManagedBass.Bass.LastError}");
                return;
            }
        }

        _currentDevice = index;
    }

    public CorePlaybackState State => _state;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            // Apply volume on the MIXER (master volume) so it affects all attached sources
            // and the encoder output simultaneously.
            if (_mixer != 0)
                ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _volume);
        }
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
    // dB → linear factor (10^(dB/20)); 0 dB = factor 1.0 = unity. The controller caps positive
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
            // Use BassMix.ChannelGetPosition on the source — this is the mixer-aware
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
            // Seek on the decode source; the mixer will pick up from the new position.
            var bytes = ManagedBass.Bass.ChannelSeconds2Bytes(_source, value.TotalSeconds);
            ManagedBass.Bass.ChannelSetPosition(_source, bytes);
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

        // A hard Load cancels any in-flight crossfade — drop the outgoing source too.
        FreeOutgoing();
        // Remove and free the old decode source (if any) before loading the new one.
        FreeSource();

        // Ensure the persistent mixer exists. Created once per process; subsequent
        // Load calls reuse it so the encoder stays attached.
        EnsureMixer();

        // Load the file as a DECODE stream (no device output — the mixer drives it).
        // BassFlags.Float for clean float PCM; BassFlags.Decode skips device routing.
        // Source: BASS API / ManagedBass.Bass.CreateStream docs.
        _source = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (_source == 0)
            throw new InvalidOperationException($"Could not load '{filePath}': {ManagedBass.Bass.LastError}");

        // Plug the decode source into the mixer.
        // BassFlags.MixerChanBuffer enables accurate position reporting via BassMix.ChannelGetPosition.
        // Source: managedbass.github.io/api — BassMix.MixerAddChannel, BassFlags.MixerChanBuffer.
        var added = BassMix.MixerAddChannel(_mixer, _source, BassFlags.MixerChanBuffer);
        if (!added)
            throw new InvalidOperationException($"MixerAddChannel failed: {ManagedBass.Bass.LastError}");

        // Add the source PAUSED. The mixer runs continuously (MixerNonStop), so a freshly
        // added, un-paused source would start playing immediately on Load — before Play()
        // is called (State would say Stopped while audio is heard). Pausing it here makes
        // Load silent; Play() clears MixerChanPause to start it. (Reviewed fix, 2026-06-04.)
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        // Apply the current normalization gain to the freshly-created source (re-applied here so a
        // reload — e.g. WithFileReleased around a tag write — keeps the same per-track gain).
        ApplyNormalization();

        // Wire the end-of-track sync on the source channel.
        // BassMix.ChannelSetSync is the mixer-aware sync — it fires when the source
        // in the mixer is exhausted, not when the mixer itself ends (it never does,
        // thanks to MixerNonStop). SyncFlags.End fires when the source decode reaches EOF.
        // Source: managedbass.github.io/api — BassMix.ChannelSetSync.
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
    /// <paramref name="normGainDb"/> level — both overlap on the persistent mixer. The new track
    /// becomes current immediately (Position/Duration/seek track it). When nothing is playing or
    /// fadeMs ≤ 0 this degrades to a hard Load + gain + Play. The outgoing source is muted and
    /// freed by a handoff timer ~150 ms after the blend completes, and deliberately raises NO
    /// <see cref="PlaybackEnded"/> (the queue already advanced when the crossfade began).
    /// </summary>
    public void CrossfadeTo(string filePath, double normGainDb, int fadeMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureMixer();

        // Nothing to fade FROM, or crossfade disabled → behave exactly like the hard path.
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
            BassMix.ChannelFlags(incoming, BassFlags.Default, BassFlags.MixerChanPause); // clear pause → start playing

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
        // Source: managedbass.github.io/api — BassMix.ChannelFlags, BassFlags.MixerChanPause.
        BassMix.ChannelFlags(_source, BassFlags.Default, BassFlags.MixerChanPause);
        // Resume the outgoing deck too, if a crossfade is mid-blend (keeps both sides moving).
        if (_outgoing != 0) BassMix.ChannelFlags(_outgoing, BassFlags.Default, BassFlags.MixerChanPause);

        // The mixer itself must be playing (started once; subsequent Play calls are no-ops
        // because the mixer never stops — it just outputs silence while sources are paused).
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
        // Source: managedbass.github.io/api — BassMix.ChannelFlags, BassFlags.MixerChanPause.
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        if (_outgoing != 0) BassMix.ChannelFlags(_outgoing, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        SetState(CorePlaybackState.Paused);
    }

    public void Stop()
    {
        if (_source == 0) return;
        // Cancel any in-flight crossfade — there is no longer a "next" to blend into.
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
    /// 44100 Hz / stereo / Float — matches the stream format used throughout the app.
    /// BassFlags.MixerNonStop: the mixer outputs silence when no sources are active,
    /// preventing the cast connection from stalling during track transitions.
    /// Source: managedbass.github.io/api — BassMix.CreateMixerStream, BassFlags.MixerNonStop.
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
    /// Per-track volume lives on <c>_source</c> (normalization gain — set by
    /// <see cref="ApplyNormalization"/>).  Sliding <c>_source</c> would only silence the
    /// current track; the mixer keeps running and any future sound (e.g. a brief click from
    /// the Icecast encoder DSP) would still be audible at full volume. Sliding <c>_mixer</c>
    /// silences EVERYTHING that goes to the hardware — the same handle that the master
    /// <see cref="Volume"/> property already controls, so this is the correct master-level
    /// fade. The slide happens on BASS's internal audio thread (non-blocking for us); we
    /// await Task.Delay so the UI thread yields rather than spinning.</para>
    ///
    /// <para>The slide does NOT write back to <c>_volume</c> or user settings. The process
    /// exits immediately after — there is nothing to restore.</para>
    /// </summary>
    public async Task FadeOutAsync(int fadeMs = 200)
    {
        // Already fading or already silent/stopped — nothing to do.
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
        // audio thread. Returns immediately — we then await the duration so the audio has time
        // to fade before we free the channel in Dispose.
        // API ref: managedbass.github.io/api — Bass.ChannelSlideAttribute
        //   handle  = _mixer  (the persistent stereo output mixer — the master volume point)
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

    public void Dispose()
    {
        _handoffTimer?.Dispose();
        FreeOutgoing();
        FreeSource();
        if (_mixer != 0)
        {
            ManagedBass.Bass.StreamFree(_mixer);
            _mixer = 0;
        }
        ManagedBass.Bass.Free();
    }

    /// <summary>
    /// FFT magnitudes aggregated into four perceptual bands. Bin ranges assume
    /// the mixer is at 44100 Hz (1024 bins → ~21.5 Hz/bin) — bands skew
    /// slightly at other sample rates but stay perceptually correct.
    ///
    ///   bass    bins   1..7    (~21..150 Hz)
    ///   lowMid  bins   7..28   (~150..600 Hz)
    ///   mid     bins  28..120  (~600..2580 Hz)
    ///   treble  bins 120..480  (~2580..10330 Hz)
    ///
    /// EMA smoothing (α=0.35) so visualizer frames don't strobe; idle decays
    /// the smoothed values by 0.85× per call so the lines settle gently when
    /// playback stops instead of snapping to zero.
    ///
    /// S2: FFT is read from the MIXER (what's actually heard / sent to encoder),
    /// not from the per-track source. This is correct — it reflects the actual
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
        // Negative return = BASS error (stream not ready, etc.) → emit zeros.
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

        // Per-band sensitivity multipliers — raw FFT magnitudes are heavily
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
}
