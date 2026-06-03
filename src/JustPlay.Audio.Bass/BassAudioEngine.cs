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

    public void Unload()
    {
        FreeStream();                       // releases the OS file handle
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

    /// <summary>
    /// FFT magnitudes aggregated into four perceptual bands. Bin ranges assume
    /// the stream is at ~44.1 kHz (1024 bins → ~21.5 Hz/bin) — bands skew
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
    /// </summary>
    public void GetFftBands(Span<float> destination)
    {
        if (destination.Length < 4)
            throw new ArgumentException("destination must have length >= 4", nameof(destination));

        if (_stream == 0 || _state != CorePlaybackState.Playing)
        {
            for (var i = 0; i < 4; i++)
                _smoothBands[i] *= 0.85f;
            new ReadOnlySpan<float>(_smoothBands).CopyTo(destination);
            return;
        }

        // length parameter encodes the FFT request: size flag | FFTRemoveDC.
        // Negative return = BASS error (stream not ready, etc.) → emit zeros.
        var result = ManagedBass.Bass.ChannelGetData(
            _stream, _fftBuffer,
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
