using JustPlay.Core.Abstractions;
using ManagedBass;
using CorePlaybackState = JustPlay.Core.Models.PlaybackState;

namespace JustPlay.Audio.Bass;

/// <summary>
/// <see cref="IAudioEngine"/> backed by un4seen BASS via ManagedBass.
/// One stream loaded at a time. BASS is initialised once for the process here.
/// </summary>
public sealed class BassAudioEngine : IAudioEngine
{
    private int _stream;
    private double _volume = 1.0;
    private CorePlaybackState _state = CorePlaybackState.Stopped;

    // BASS invokes sync callbacks on its own thread; keep the delegate alive so the
    // GC can't collect it while BASS still holds the pointer.
    private SyncProcedure? _endSync;

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
    }

    public CorePlaybackState State => _state;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (_stream != 0)
                ManagedBass.Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_stream == 0) return TimeSpan.Zero;
            var bytes = ManagedBass.Bass.ChannelGetPosition(_stream);
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_stream, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
        set
        {
            if (_stream == 0) return;
            var bytes = ManagedBass.Bass.ChannelSeconds2Bytes(_stream, value.TotalSeconds);
            ManagedBass.Bass.ChannelSetPosition(_stream, bytes);
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_stream == 0) return TimeSpan.Zero;
            var bytes = ManagedBass.Bass.ChannelGetLength(_stream);
            var secs = ManagedBass.Bass.ChannelBytes2Seconds(_stream, bytes);
            return secs > 0 ? TimeSpan.FromSeconds(secs) : TimeSpan.Zero;
        }
    }

    public event EventHandler<CorePlaybackState>? StateChanged;
    public event EventHandler? PlaybackEnded;

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FreeStream();

        // Float output for clean volume/seek; the actual decode is handled by BASS.
        _stream = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Float);
        if (_stream == 0)
            throw new InvalidOperationException($"Could not load '{filePath}': {ManagedBass.Bass.LastError}");

        ManagedBass.Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);

        _endSync = (_, _, _, _) =>
        {
            SetState(CorePlaybackState.Stopped);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };
        ManagedBass.Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        SetState(CorePlaybackState.Stopped);
    }

    public void Play()
    {
        if (_stream == 0) return;
        if (ManagedBass.Bass.ChannelPlay(_stream, false))
            SetState(CorePlaybackState.Playing);
    }

    public void Pause()
    {
        if (_stream == 0) return;
        if (ManagedBass.Bass.ChannelPause(_stream))
            SetState(CorePlaybackState.Paused);
    }

    public void Stop()
    {
        if (_stream == 0) return;
        ManagedBass.Bass.ChannelStop(_stream);
        Position = TimeSpan.Zero;
        SetState(CorePlaybackState.Stopped);
    }

    private void SetState(CorePlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void FreeStream()
    {
        if (_stream == 0) return;
        ManagedBass.Bass.StreamFree(_stream);
        _stream = 0;
        _endSync = null;
    }

    public void Dispose()
    {
        FreeStream();
        ManagedBass.Bass.Free();
    }
}
