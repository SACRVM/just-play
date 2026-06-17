using System.Collections.Generic;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Headphone pre-listen (PFL) engine: an independent audition player that runs on a separate audio
/// device (the DJ's headphones) and is structurally isolated from the main output bus and the
/// Icecast stream. This makes it architecturally impossible for the cue signal to leak into either
/// the speaker mix or the broadcast.
///
/// <para><b>v1 — PFL only.</b> The cue signal is sent entirely to the headphone device at the
/// configured <see cref="Volume"/> level. The cue/master BLEND knob is a v2 feature.</para>
///
/// <para>When <see cref="OutputDevice"/> is −1 (the default) the engine accepts calls but
/// produces no audio — Load/Play are silent no-ops until a device is configured.</para>
///
/// <para>The implementation (BassPreListenEngine) maintains a separate BASS mixer on the headphone
/// device with NO BASSenc, NO limiter/EQ/DSP — it is a bare audition player. The structural
/// separation (distinct BASS mixer on a distinct BASS device) guarantees the cue path cannot
/// reach the main engine's output or its encoder DSP.</para>
/// </summary>
public interface IPreListenEngine : IDisposable
{
    /// <summary>Current playback state of the cue player.</summary>
    PlaybackState State { get; }

    /// <summary>
    /// Cue/PFL level, 0..1 (linear). Applied to the headphone mixer's master volume;
    /// completely independent of the main engine's <see cref="IAudioEngine.Volume"/>.
    /// </summary>
    double Volume { get; set; }

    /// <summary>Current playhead position in the loaded cue track. Settable for scrubbing.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Duration of the loaded cue track (zero when nothing is loaded).</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// BASS device index for headphone output.  Set this before calling <see cref="Load"/>.
    /// −1 = not configured / disabled (no audio produced).
    ///
    /// <para>Setting a new value will:</para>
    /// <list type="bullet">
    ///   <item>Call <c>Bass.Init(index)</c> for the target device if not yet initialised;
    ///         <c>Errors.Already</c> is treated as success.</item>
    ///   <item>Move the existing cue mixer to the new device via
    ///         <c>Bass.ChannelSetDevice</c>, or create a fresh one on the first valid index.</item>
    ///   <item>On failure: log and keep the previous device (never throws).</item>
    /// </list>
    /// </summary>
    int OutputDevice { get; set; }

    /// <summary>Raised on BASS's internal thread when the playback state changes.
    /// Marshal to the UI thread with <c>Dispatcher.UIThread.Post</c> before touching UI state.</summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised on BASS's internal thread when the loaded track reaches its natural end.
    /// v1 behaviour: just stop — no auto-advance. Marshal to UI before touching UI state.</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// Load a file into the cue player and make it ready to play. A previously loaded track is
    /// released. Silently ignores the call when <see cref="OutputDevice"/> is −1.
    /// </summary>
    void Load(string filePath);

    /// <summary>Start or resume cue playback.</summary>
    void Play();

    /// <summary>Pause cue playback (retains position).</summary>
    void Pause();

    /// <summary>Stop cue playback and rewind to the beginning.</summary>
    void Stop();

    /// <summary>
    /// Fully release the loaded stream and its underlying OS file handle. Unlike
    /// <see cref="Stop"/>, which keeps the file open, this frees it completely (e.g. before a
    /// tag write). Call <see cref="Load"/> again to resume.
    /// </summary>
    void Unload();

    /// <summary>
    /// Returns all currently enabled audio output devices. Same enumeration as
    /// <see cref="IAudioEngine.GetOutputDevices"/> — reuses <see cref="AudioOutputDevice"/>.
    /// The list is fresh on each call; do not cache across user interactions.
    /// </summary>
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();
}
