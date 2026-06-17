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
    /// Crossfade into <paramref name="filePath"/> over <paramref name="fadeMs"/> ms: the currently
    /// playing source fades out while the new track fades in to its <paramref name="normGainDb"/>
    /// level, both overlapping on the mixer. The new track becomes the current track immediately
    /// (<see cref="Position"/>/<see cref="Duration"/>/seek follow it). When nothing is playing or
    /// <paramref name="fadeMs"/> ≤ 0 this is equivalent to <see cref="Load"/> + set gain + <see cref="Play"/>.
    /// The outgoing source does NOT raise <see cref="PlaybackEnded"/> — the queue already advanced when
    /// the crossfade began, so only the new track's natural end fires it.
    /// </summary>
    void CrossfadeTo(string filePath, double normGainDb, int fadeMs);

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

    /// <summary>
    /// Smoothly ramp the master output volume from its current level to silence over
    /// <paramref name="fadeMs"/> milliseconds, then return. Intended for graceful-quit:
    /// call this <em>before</em> <see cref="IDisposable.Dispose"/> to avoid the hard
    /// digital click/buzz that occurs when BASS channels are freed mid-playback.
    ///
    /// <para>Implementation contract:</para>
    /// <list type="bullet">
    ///   <item>No-op (returns immediately) when nothing is playing.</item>
    ///   <item>Guard against double-fade (second call while one is already running returns immediately).</item>
    ///   <item>Does NOT persist the volume change — Volume property and user settings are left untouched;
    ///         only the hardware output level slides to zero. The process exits right after, so there is
    ///         nothing to restore.</item>
    ///   <item>The slide is non-blocking on the audio thread; the method awaits a
    ///         <see cref="System.Threading.Tasks.Task.Delay(int)"/> of roughly <paramref name="fadeMs"/>
    ///         so the caller does not have to block the UI thread.</item>
    /// </list>
    /// </summary>
    /// <param name="fadeMs">Target ramp duration in milliseconds. Clamped to 50–500 ms.</param>
    Task FadeOutAsync(int fadeMs = 200);

    /// <summary>
    /// Configure the master true-peak limiter / maximizer on the output bus. Sits on the persistent
    /// mixer, so it shapes BOTH the local playback and the Icecast stream (the encoder taps the mixer
    /// after the DSP chain). True-peak ceiling is fixed at −1 dBTP (broadcast-safe) inside the engine.
    ///
    /// <para><paramref name="enabled"/> false removes the DSP entirely (true bypass — zero cost).
    /// <paramref name="driveDb"/> is the maximizer makeup gain pushed in before limiting:
    /// 0 dB = transparent safety limiter (peaks only); higher = louder/denser "club" level.
    /// <paramref name="ceilingDbTp"/> is the true-peak ceiling: −1 dBTP is broadcast-safe; the Tweaks
    /// "Loud" mode raises it toward −0.1 dBTP to squeeze out the last bit of level. The engine maps
    /// its Off/Soft/Club/Loud control onto these arguments.</para>
    ///
    /// <para>Idempotent and live: safe to call repeatedly while playing; changing drive/ceiling swaps
    /// the limiter without dropping the stream. Non-destructive — files are never touched.</para>
    /// </summary>
    void SetLimiter(bool enabled, double driveDb, double ceilingDbTp);

    /// <summary>
    /// Configure the 3-band DJ EQ / isolator on the output bus (Low / Mid / High). Gains are LINEAR:
    /// 1.0 = unity (flat), 0.0 = full kill, 2.0 = +6 dB. Sits at the HEAD of the bus DSP chain (ahead
    /// of virtual bass and the limiter), so tone shaping happens first and feeds both local playback
    /// and the Icecast stream.
    ///
    /// <para>When all three gains are unity (flat) the engine removes the DSP entirely (true bypass —
    /// zero cost, perfectly transparent). Live and idempotent; safe to call while playing. The
    /// crossovers are fixed at 200 Hz and 4 kHz. Non-destructive.</para>
    /// </summary>
    void SetEqualizer(double lowGain, double midGain, double highGain);

    // ── "Revive" rack — anti-flat enhancement, all on the output bus just after the EQ, so they
    //    shape both local playback and the Icecast stream. Each is neutral (true bypass) at its zero
    //    value, live/idempotent, and non-destructive (files are never touched). ──

    /// <summary>
    /// Adaptive spectral "tilt" (a gentle auto-master): slowly nudges a track's low/high balance toward
    /// a target so dull or thin tracks sit more evenly. <paramref name="strength"/> 0 = off/bypass, up to
    /// 1.0 = full correction (total tilt clamped internally to a few dB). Priority 180 (just after the EQ).
    /// </summary>
    void SetAdaptiveTilt(double strength);

    /// <summary>
    /// Transient designer: boosts attacks (kick/snare snap) level-independently to bring punch back to
    /// brickwall-flat masters, without making them louder or more compressed. <paramref name="punch"/>
    /// 0 = off/bypass, up to 1.0 = max punch. Stereo-linked so the image is preserved. Priority 140.
    /// </summary>
    void SetTransientDesigner(double punch);
}
