using System;
using System.Collections.Generic;
using JustPlay.Core.Audio;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Platform service that captures ONE application's audio via per-process loopback (Windows
/// Application Loopback / macOS Core-Audio process taps) and hands the PCM to a sink. This is the
/// driverless "capture a specific APP" Phase-0 source for JUST STREAM (see
/// <c>.claude/skills/just-route/references/just-route-vision.md</c>): the target app renders to its
/// own device normally, unaware, while we tap its stream invisibly — no virtual cable, no
/// system-sound leak, no reconfiguration.
///
/// Lives behind this Core abstraction (CLAUDE.md layering). The Windows implementation
/// (<c>WasapiProcessLoopbackCapture</c>) is in the audio adapter; a <c>NullProcessAudioCapture</c>
/// is the safe default where the OS has no support, so DI always resolves and the "App" source
/// simply reports unsupported.
/// </summary>
public interface IProcessAudioCapture : IDisposable
{
    /// <summary>True when this platform/OS build can actually capture per-process audio. When false,
    /// <see cref="GetCapturableApps"/> returns empty and <see cref="Start"/> throws.</summary>
    bool IsSupported { get; }

    /// <summary>True while a capture is running.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// The apps whose audio can be captured right now, enumerated fresh each call (processes come and
    /// go). Empty when <see cref="IsSupported"/> is false. Already filtered/ranked (DJ apps first).
    /// </summary>
    IReadOnlyList<CaptureApp> GetCapturableApps();

    /// <summary>
    /// Begin capturing the given process's audio, delivered as interleaved-stereo 32-bit float PCM at
    /// <paramref name="sampleRate"/> Hz via <see cref="FramesAvailable"/> on a capture thread.
    /// <paramref name="format"/> selects how a multi-channel source is reduced to that stereo pair:
    /// <see cref="AppCaptureFormat.CaptureChannels"/> = 2 downmixes the whole output; > 2 captures
    /// that many channels and extracts only the Master pair at
    /// <see cref="AppCaptureFormat.MasterChannelOffset"/> (so a DJ device's headphone Cue is dropped
    /// rather than mixed into the broadcast). Starting while already capturing switches to the new
    /// process. Throws <see cref="NotSupportedException"/> when <see cref="IsSupported"/> is false,
    /// or on a hard OS failure (e.g. the process exited).
    /// </summary>
    void Start(int processId, int sampleRate, AppCaptureFormat format);

    /// <summary>Stop capturing. No-op when idle. The mixer/encoder downstream keep running.</summary>
    void Stop();

    /// <summary>
    /// Raised on a capture thread with a block of interleaved-stereo float PCM at the started
    /// sample rate. The buffer is only valid for the duration of the call — the handler MUST consume
    /// (copy / push) it synchronously and not retain it. Never touch UI APIs from here.
    /// The int is the number of VALID floats in the buffer (may be shorter than the array).
    /// </summary>
    event Action<float[], int>? FramesAvailable;
}
