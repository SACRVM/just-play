using System.Collections.Generic;
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

    /// <summary>
    /// Per-track loudness-normalization gain in dB, applied to the current source channel only
    /// (independent of <see cref="Volume"/>, which is the master). 0 = unity (no change). Set by
    /// the controller from the track's ReplayGain when playback normalization is on; re-applied
    /// automatically whenever a source is (re)loaded. Non-destructive — the file is never touched.
    /// </summary>
    double NormalizationGainDb { get; set; }

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

    // ── Output device selection ───────────────────────────────────────────

    /// <summary>
    /// Returns the list of enabled audio output devices currently available to
    /// the engine, ordered by BASS device index. Index 0 ("No sound") is excluded.
    ///
    /// The list is enumerated fresh on each call (devices may appear/disappear
    /// at runtime), so callers should not cache it across user interactions.
    /// </summary>
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    /// <summary>
    /// Move audio output to the device identified by <paramref name="index"/>
    /// (the <see cref="AudioOutputDevice.Index"/> value from <see cref="GetOutputDevices"/>).
    ///
    /// Implementation contract:
    ///   • If the target device is not yet initialised, <c>Bass.Init(index)</c> is
    ///     called first; <c>Errors.Already</c> is treated as success.
    ///   • The persistent mixer channel is then moved to the new device via
    ///     <c>Bass.ChannelSetDevice(_mixer, index)</c>. Because the Icecast encoder
    ///     (BassBroadcastService) is attached to the mixer, NOT to the device,
    ///     moving the mixer's output device does NOT affect the encoder — the stream
    ///     continues seamlessly across a device switch. See implementation comment
    ///     in BassAudioEngine for the BASS_Mixer / BASSenc interaction.
    ///   • On failure the engine logs and keeps the previous device; it does NOT throw
    ///     (the user might be unplugging headphones mid-session).
    /// </summary>
    void SetOutputDevice(int index);

    /// <summary>
    /// The BASS device index currently used for output.
    /// -1 means "not yet set" (engine just constructed, before the first explicit selection).
    /// </summary>
    int CurrentOutputDevice { get; }
}
