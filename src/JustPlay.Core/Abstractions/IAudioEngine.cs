using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// The playback engine. One track loaded at a time — load, play, seek, stop.
/// Implemented per platform (ManagedBass today); the UI only ever sees this interface.
/// </summary>
public interface IAudioEngine : IDisposable
{
    PlaybackState State { get; }

    /// <summary>Linear volume, 0..1.</summary>
    double Volume { get; set; }

    /// <summary>Current playhead position.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Length of the loaded track (zero if nothing loaded).</summary>
    TimeSpan Duration { get; }

    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised when the loaded track reaches its end naturally.</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>Load a file and make it ready to play. Fast path — no analysis here.</summary>
    void Load(string filePath);

    void Play();
    void Pause();
    void Stop();
}
