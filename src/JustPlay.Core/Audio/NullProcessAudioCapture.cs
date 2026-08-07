using System;
using System.Collections.Generic;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Core.Audio;

/// <summary>
/// The safe default <see cref="IProcessAudioCapture"/> for platforms/builds with no per-process
/// capture support (non-Windows, or before the native module is wired). Reports unsupported, lists
/// nothing, and throws on <see cref="Start"/> - so DI always resolves and the "App" source is simply
/// hidden/disabled in the UI (via <see cref="IsSupported"/>).
/// </summary>
public sealed class NullProcessAudioCapture : IProcessAudioCapture
{
    public bool IsSupported => false;
    public bool IsCapturing => false;
    public IReadOnlyList<CaptureApp> GetCapturableApps() => Array.Empty<CaptureApp>();

    public void Start(int processId, int sampleRate, AppCaptureFormat format) =>
        throw new NotSupportedException("Per-process app capture is not supported on this build.");

    public void Stop() { /* no-op */ }

    public event Action<float[], int>? FramesAvailable
    {
        add { /* never raised */ }
        remove { }
    }

    public void Dispose() { /* nothing to release */ }
}
