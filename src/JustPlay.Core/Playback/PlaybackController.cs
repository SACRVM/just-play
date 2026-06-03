using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Core.Playback;

/// <summary>
/// Sits on top of <see cref="IAudioEngine"/> and adds the notion of a "current track".
/// Pure logic, no UI framework — the ViewModel subscribes to its events.
/// Embodies the JustPlay rule: double-click a track → it plays. No queue memory.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private readonly IAudioEngine _engine;

    public PlaybackController(IAudioEngine engine)
    {
        _engine = engine;
        _engine.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        _engine.PlaybackEnded += (_, _) => TrackEnded?.Invoke(this, CurrentTrack);
    }

    public Track? CurrentTrack { get; private set; }

    public PlaybackState State => _engine.State;

    public TimeSpan Position
    {
        get => _engine.Position;
        set => _engine.Position = value;
    }

    public TimeSpan Duration => _engine.Duration;

    public double Volume
    {
        get => _engine.Volume;
        set => _engine.Volume = value;
    }

    /// <summary>Raised on stop/play/pause transitions.</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised when the current track plays to its end. Carries the track that ended.</summary>
    public event EventHandler<Track?>? TrackEnded;

    public event EventHandler<Track>? CurrentTrackChanged;

    /// <summary>Load and immediately play a track. The core "just play it" path.</summary>
    public void Play(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        CurrentTrack = track;
        _engine.Load(track.FilePath);
        _engine.Play();
        CurrentTrackChanged?.Invoke(this, track);
    }

    /// <summary>Toggle play/pause on the current track; no-op if nothing is loaded.</summary>
    public void TogglePlayPause()
    {
        switch (_engine.State)
        {
            case PlaybackState.Playing:
                _engine.Pause();
                break;
            case PlaybackState.Paused:
                _engine.Play();
                break;
            case PlaybackState.Stopped when CurrentTrack is not null:
                _engine.Play();
                break;
        }
    }

    public void Stop() => _engine.Stop();

    /// <summary>
    /// Run <paramref name="whileReleased"/> with <paramref name="track"/>'s file handle
    /// released, then reload and restore the prior playhead + play/pause state. Used by the
    /// tag writer: BASS keeps the file open while it's the current track, so a write would
    /// fail with a sharing violation — this briefly unloads (pause), runs the write, and
    /// resumes from the same spot. If <paramref name="track"/> isn't the current track there
    /// is no handle to release, so the action just runs directly.
    /// </summary>
    public void WithFileReleased(Track track, Action whileReleased)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(whileReleased);

        if (!ReferenceEquals(CurrentTrack, track))
        {
            whileReleased();
            return;
        }

        var pos = _engine.Position;
        var wasPlaying = _engine.State == PlaybackState.Playing;
        _engine.Unload();
        try
        {
            whileReleased();
        }
        finally
        {
            _engine.Load(track.FilePath);
            _engine.Position = pos;
            if (wasPlaying) _engine.Play();
        }
    }

    public void Dispose() => _engine.Dispose();
}
