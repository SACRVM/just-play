using JustPlay.Core.Abstractions;
using ManagedBass;
using ManagedBass.Mix;
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

    // ── Per-track decode source (one at a time) ───────────────────────────
    private int _source;

    private double _volume = 1.0;
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

        // Wire the end-of-track sync on the source channel.
        // BassMix.ChannelSetSync is the mixer-aware sync — it fires when the source
        // in the mixer is exhausted, not when the mixer itself ends (it never does,
        // thanks to MixerNonStop). SyncFlags.End fires when the source decode reaches EOF.
        // Source: managedbass.github.io/api — BassMix.ChannelSetSync.
        _endSync = (_, _, _, _) =>
        {
            SetState(CorePlaybackState.Stopped);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };
        BassMix.ChannelSetSync(_source, SyncFlags.End, 0, _endSync);

        SetState(CorePlaybackState.Stopped);
    }

    public void Play()
    {
        if (_source == 0) return;

        // Un-pause the source (clear MixerPause flag) so the mixer starts feeding from it.
        // BassMix.ChannelFlags(handle, flags, mask): flags = value to set, mask = bits to affect.
        // To CLEAR MixerPause: set=0, mask=MixerPause.
        // Source: managedbass.github.io/api — BassMix.ChannelFlags, BassFlags.MixerChanPause.
        BassMix.ChannelFlags(_source, BassFlags.Default, BassFlags.MixerChanPause);

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

        SetState(CorePlaybackState.Paused);
    }

    public void Stop()
    {
        if (_source == 0) return;
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

    public void Dispose()
    {
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
