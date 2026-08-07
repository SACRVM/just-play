using System.Collections.Generic;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Playback;
using ManagedBass;
using ManagedBass.Mix;
using AudioOutputDevice = JustPlay.Core.Models.AudioOutputDevice;
using CorePlaybackState = JustPlay.Core.Models.PlaybackState;

namespace JustPlay.Audio.Bass;

/// <summary>
/// <see cref="IPreListenEngine"/> backed by un4seen BASS via ManagedBass.
///
/// ISOLATION GUARANTEE
///   This engine runs an independent BASS mixer on the headphone device (a separate BASS device
///   index from the main output). It has NO BASSenc, NO limiter, NO EQ/DSP. The cue signal is
///   therefore structurally incapable of reaching the main speaker output or the Icecast encoder
///   (which attaches to the MAIN engine's mixer handle, not to this one).
///
/// BASS MULTI-DEVICE NOTES (assumptions marked (!) - must verify by ear with two devices):
///   (!) ASSUMPTION 1: Bass.Init(n) is idempotent for already-initialised devices (Errors.Already
///     = success). Multiple devices can be initialised in one process - each call opens a separate
///     hardware audio path.
///   (!) ASSUMPTION 2: After Bass.Init(n), Bass.CurrentDevice for the calling thread is n. A
///     subsequent BassMix.CreateMixerStream() therefore creates the mixer on device n, not on
///     the default device. We call Bass.ChannelSetDevice(mixer, n) as belt-and-braces.
///   (!) ASSUMPTION 3: Decode streams (BassFlags.Decode) carry no device affinity - they are
///     in-memory pipelines. BassMix.MixerAddChannel() can attach them to any mixer on any device.
///   (!) ASSUMPTION 4: The two engines sharing global BASS state (same process) is safe as long as
///     they operate on distinct device indices. If Bass.CurrentDevice is truly thread-local (BASS
///     2.4+ documented behaviour) there is no race; if it is process-global the EnsureMixer path
///     must not run concurrently with the main engine's Load path. In practice both are called
///     from the UI thread, so there is no race.
///
/// Do NOT call Bass.Free() in Dispose - the main BassAudioEngine owns the global BASS state.
///
/// N26 CUE-WINS-ON-SHARED-DEVICE:
///   When this engine's headphone device is the SAME BASS device index as the main engine's
///   output, the two must never sound at once - starting a cue suppresses (ducks) the main
///   engine's audible output; stopping it (or the cue leaving Playing) restores it. The pure
///   decision is <see cref="JustPlay.Core.Playback.CueArbiter"/> (Core, unit-tested); this class
///   just feeds it (cue state, both device indices) via <see cref="ReevaluateCueGate"/> and
///   forwards Suppress/Restore to <see cref="_mainOutput"/> (<c>BassAudioEngine</c>, injected as
///   the narrow <see cref="IDuckableAudioOutput"/> seam so this class doesn't need the main
///   engine's whole playback/DSP/broadcast surface). See CueArbiter's class doc and
///   BassAudioEngine.SetDucked for why this is guaranteed not to reach the Icecast stream.
///   The constructor's <c>mainOutput</c> parameter is optional/nullable purely as a defensive
///   fallback (e.g. if ever constructed outside the DI container) - the composition root
///   registers the real BassAudioEngine singleton for it, so in the running app it is always present.
/// </summary>
public sealed class BassPreListenEngine : IPreListenEngine
{
    // -- Headphone mixer (persistent for process lifetime once created) --------
    private int _mixer;

    // -- Active headphone device index (-1 = not configured) ------------------
    private int _device = -1;

    // -- Current decode source (the cue track) --------------------------------
    private int _source;

    private double _volume = 0.8;
    private CorePlaybackState _state = CorePlaybackState.Stopped;

    // GC-pinned sync delegate - if the GC collects it while BASS holds the native pointer,
    // we get a CallbackOnCollectedDelegate crash. Keep it alive as a field.
    private SyncProcedure? _endSync;

    // -- N26 cue-ducking --------------------------------------------------------
    private readonly IDuckableAudioOutput? _mainOutput;
    private readonly CueArbiter _cueArbiter = new();

    public BassPreListenEngine(IDuckableAudioOutput? mainOutput = null)
    {
        _mainOutput = mainOutput;
        if (_mainOutput is not null)
            _mainOutput.OutputDeviceChanged += OnMainOutputDeviceChanged;

        // Ensure BASS is initialised for at least the default device. BassAudioEngine has already
        // done this in normal startup order, but this guard makes construction-order-independent.
        // Errors.Already = BASS already running on this device -> treat as success.
        if (!ManagedBass.Bass.Init())
        {
            var err = ManagedBass.Bass.LastError;
            if (err != Errors.Already)
                throw new InvalidOperationException($"[PreListen] BASS init failed: {err}");
        }
    }

    /// <summary>The main engine's output device moved - re-check whether we still share a device
    /// with it (e.g. cue was suppressing main on device 3, main switched to device 5 mid-cue).</summary>
    private void OnMainOutputDeviceChanged(object? sender, EventArgs e) => ReevaluateCueGate();

    /// <summary>
    /// Feed the current world (cue playing?, both device indices) to <see cref="_cueArbiter"/> and
    /// apply whatever it decides. Called after every state or device change that could flip the
    /// gate: <see cref="SetState"/> (cue Play/Pause/Stop/track-end) and the <see cref="OutputDevice"/>
    /// setter (cue device change), plus <see cref="OnMainOutputDeviceChanged"/> (main device change).
    /// No-op when no main-engine seam was wired in (see ctor remarks).
    /// </summary>
    private void ReevaluateCueGate()
    {
        if (_mainOutput is null) return;
        var action = _cueArbiter.Evaluate(
            cueIsPlaying: _state == CorePlaybackState.Playing,
            mainDeviceIndex: _mainOutput.CurrentOutputDevice,
            cueDeviceIndex: _device);
        if (action is { } a)
            _mainOutput.SetDucked(a == CueArbiterAction.Suppress);
    }

    // -- IPreListenEngine -----------------------------------------------------

    public CorePlaybackState State => _state;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            // Apply on the MIXER so it's the headphone master volume.
            if (_mixer != 0)
                ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, (float)_volume);
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_source == 0) return TimeSpan.Zero;
            // BassMix.ChannelGetPosition: mixer-aware position, accurate with MixerChanBuffer.
            // (Ref: managedbass.github.io/api - BassMix.ChannelGetPosition)
            var bytes = BassMix.ChannelGetPosition(_source, PositionFlags.Bytes);
            if (bytes < 0) return TimeSpan.Zero;
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_source, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
        set
        {
            if (_source == 0) return;
            var bytes = ManagedBass.Bass.ChannelSeconds2Bytes(_source, value.TotalSeconds);
            ManagedBass.Bass.ChannelSetPosition(_source, bytes);
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_source == 0) return TimeSpan.Zero;
            var bytes = ManagedBass.Bass.ChannelGetLength(_source);
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_source, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
    }

    /// <inheritdoc/>
    public int OutputDevice
    {
        get => _device;
        set
        {
            if (_device == value) return;

            if (value >= 0)
            {
                // (!) ASSUMPTION 1: Initialise the headphone device. After this call, BASS device
                // context for the calling thread is set to 'value' so a subsequent
                // CreateMixerStream() targets this device. (Errors.Already = already done, OK.)
                if (!ManagedBass.Bass.Init(value))
                {
                    var err = ManagedBass.Bass.LastError;
                    if (err != Errors.Already)
                    {
                        Console.WriteLine($"[PreListen] Bass.Init({value}) failed: {err}");
                        return;
                    }
                }
            }

            var prev = _device;
            _device = value;

            if (_mixer != 0)
            {
                if (value < 0)
                {
                    // Disabled: tear down the mixer (FreeSource was already called or is a no-op).
                    FreeMixer();
                    // Bug fix (N26): FreeMixer/FreeSource don't themselves flip _state, so without
                    // this a cue that was Playing would stay "Playing" with nothing left to play -
                    // and, worse, could leave a same-device main-engine suppression stuck forever
                    // (ReevaluateCueGate would never see cueIsPlaying go false). SetState is a no-op
                    // if already Stopped.
                    SetState(CorePlaybackState.Stopped);
                }
                else
                {
                    // Move the existing cue mixer to the new headphone device.
                    // (!) ASSUMPTION 2 (part): ChannelSetDevice works for mixer streams the same way
                    // it does for the main engine's mixer (documented in BassAudioEngine).
                    if (!ManagedBass.Bass.ChannelSetDevice(_mixer, value))
                    {
                        Console.WriteLine($"[PreListen] ChannelSetDevice({_mixer}, {value}) failed: {ManagedBass.Bass.LastError}");
                        _device = prev; // revert - keep the previous routing
                        return;
                    }
                }
            }
            // If _mixer is 0, EnsureMixer() will create it on the new device at the next Load().

            // N26: the device that just changed is one of the two gate inputs - re-check whether
            // suppression should engage/release. (SetState above already triggered this once for
            // the value<0 branch; re-evaluating here too is a harmless idempotent no-op there.)
            ReevaluateCueGate();
        }
    }

    public event EventHandler<CorePlaybackState>? StateChanged;
    public event EventHandler? PlaybackEnded;

    /// <summary>
    /// Enumerate enabled audio output devices. Reuses the same BASS enumeration API as the main
    /// engine but does NOT append the "No sound" virtual device - cue monitoring requires real
    /// hardware (you can't listen to headphones on a null device).
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var result = new List<AudioOutputDevice>();
        for (var i = 1; ManagedBass.Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            result.Add(new AudioOutputDevice(i, info.Name, info.IsDefault));
        }
        return result;
    }

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Silent no-op when no headphone device is configured.
        if (_device < 0) return;

        FreeSource();
        EnsureMixer();
        if (_mixer == 0) return; // EnsureMixer logged the error

        // BassFlags.Decode: in-memory PCM pipeline, not bound to any output device.
        // (!) ASSUMPTION 3: Decode streams have no device affinity and feed into any mixer.
        _source = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (_source == 0)
            throw new InvalidOperationException(
                $"[PreListen] Could not load '{filePath}': {ManagedBass.Bass.LastError}");

        // BassFlags.MixerChanBuffer: enables accurate position tracking via BassMix.ChannelGetPosition.
        if (!BassMix.MixerAddChannel(_mixer, _source, BassFlags.MixerChanBuffer))
            throw new InvalidOperationException(
                $"[PreListen] MixerAddChannel failed: {ManagedBass.Bass.LastError}");

        // Start the source paused inside the mixer - Play() will un-pause it.
        // Without this a freshly-loaded cue track would start audible output immediately.
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        // Wire the end-of-track sync on the decode source. GC-pin the delegate as a field.
        // BassMix.ChannelSetSync fires on BASS's internal thread - the VM must marshal to UI.
        var capturedSource = _source; // close over the current handle for the identity check
        _endSync = (_, _, _, _) =>
        {
            if (capturedSource != _source) return; // stale sync from a replaced source
            SetState(CorePlaybackState.Stopped);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };
        BassMix.ChannelSetSync(_source, SyncFlags.End, 0, _endSync);

        SetState(CorePlaybackState.Stopped);
    }

    public void Play()
    {
        if (_source == 0 || _mixer == 0) return;

        // Un-pause the decode source within the mixer (clear MixerChanPause).
        // BassMix.ChannelFlags(handle, flags=0, mask=MixerChanPause) clears the bit.
        BassMix.ChannelFlags(_source, BassFlags.Default, BassFlags.MixerChanPause);

        // The mixer must be playing (it may have been stopped if it was never started, or if
        // the headphone device was changed after mixer creation).
        if (ManagedBass.Bass.ChannelIsActive(_mixer) != PlaybackState.Playing)
            ManagedBass.Bass.ChannelPlay(_mixer, false);

        SetState(CorePlaybackState.Playing);
    }

    public void Pause()
    {
        if (_source == 0) return;
        if (_state != CorePlaybackState.Playing) return;
        // Same MixerChanPause bit Load() uses for the start-paused trick - position is kept,
        // Play() clears the bit and resumes exactly where the song stopped.
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        SetState(CorePlaybackState.Paused);
    }

    public void Stop()
    {
        if (_source == 0) return;
        BassMix.ChannelFlags(_source, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        ManagedBass.Bass.ChannelSetPosition(_source, 0);
        SetState(CorePlaybackState.Stopped);
    }

    public void Unload()
    {
        FreeSource();
        SetState(CorePlaybackState.Stopped);
    }

    // -- Private helpers -------------------------------------------------------

    /// <summary>
    /// Create the persistent headphone mixer if it doesn't exist yet.
    ///
    /// (!) ASSUMPTION 2: After Bass.Init(n), the calling thread's BASS device context is n.
    /// BassMix.CreateMixerStream() therefore creates on device n (the headphone device).
    /// Belt-and-braces: we call Bass.ChannelSetDevice(mixer, _device) after creation to
    /// confirm the binding regardless of thread-local assumptions.
    ///
    /// MixerNonStop keeps the mixer producing silence between cue tracks so the device
    /// clock stays alive; without it, the mixer would stall between loads.
    /// </summary>
    private void EnsureMixer()
    {
        if (_mixer != 0) return;
        if (_device < 0) return;

        _mixer = BassMix.CreateMixerStream(44100, 2, BassFlags.MixerNonStop | BassFlags.Float);
        if (_mixer == 0)
        {
            Console.WriteLine($"[PreListen] CreateMixerStream failed: {ManagedBass.Bass.LastError}");
            return;
        }

        ManagedBass.Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, (float)_volume);

        // Belt-and-braces: ensure the mixer is bound to the headphone device.
        // (!) ASSUMPTION 2: this is belt-and-braces; the mixer should already be on _device if
        // Bass.CurrentDevice was n after Bass.Init(n). Verify with two real devices.
        ManagedBass.Bass.ChannelSetDevice(_mixer, _device);

        ManagedBass.Bass.ChannelPlay(_mixer, false);
    }

    private void FreeSource()
    {
        if (_source == 0) return;
        BassMix.MixerRemoveChannel(_source);
        ManagedBass.Bass.StreamFree(_source);
        _source = 0;
        _endSync = null;
    }

    private void FreeMixer()
    {
        FreeSource();
        if (_mixer == 0) return;
        ManagedBass.Bass.StreamFree(_mixer);
        _mixer = 0;
    }

    private void SetState(CorePlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
        // N26: cue leaving/entering Playing is exactly the signal the gate cares about
        // (Play -> maybe suppress; Pause/Stop/track-end -> maybe restore).
        ReevaluateCueGate();
    }

    public void Dispose()
    {
        FreeMixer();
        // N26: release any main-engine suppression before we go - a shutdown mid-cue must not
        // leave the (still-alive-a-moment-longer) main engine permanently ducked.
        if (_mainOutput is not null)
        {
            _mainOutput.OutputDeviceChanged -= OnMainOutputDeviceChanged;
            if (_cueArbiter.IsSuppressed)
                _mainOutput.SetDucked(false);
        }
        // IMPORTANT: Do NOT call Bass.Free() here. The main BassAudioEngine owns the global BASS
        // state and calls Bass.Free() in its own Dispose(). Calling it here a second time would
        // free BASS while the main engine is still (or was recently) running.
    }
}
