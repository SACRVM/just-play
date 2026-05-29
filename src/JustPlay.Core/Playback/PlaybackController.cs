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

    public void Dispose() => _engine.Dispose();
}
