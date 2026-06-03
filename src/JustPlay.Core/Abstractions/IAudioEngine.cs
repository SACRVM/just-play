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

    /// <summary>
    /// Fully release the loaded stream and its underlying file handle (unlike
    /// <see cref="Stop"/>, which keeps the file open). Needed before another
    /// process — e.g. the tag writer — can open the file for writing. After this
    /// the engine has nothing loaded; call <see cref="Load"/> again to resume.
    /// </summary>
    void Unload();

    /// <summary>
    /// Sample the current FFT spectrum aggregated into four perceptual bands
    /// (bass, low-mid, mid, treble), normalised to roughly 0..1 with smoothing
    /// applied. Writes exactly 4 floats into <paramref name="destination"/>.
    ///
    /// Order: <c>[bass, lowMid, mid, treble]</c>. Returns zeros (and decays the
    /// internal smoothing state) when nothing is playing.
    ///
    /// Cheap enough to call at 60 fps from the UI thread. Visualizers only —
    /// not intended as an analysis source (use <see cref="IAudioDecoder"/> for
    /// offline analysis).
    /// </summary>
    void GetFftBands(Span<float> destination);
}
