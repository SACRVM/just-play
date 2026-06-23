using System.Collections.Generic;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// The JUST STREAM capture engine: the counterpart of <see cref="IAudioEngine"/> for a
/// broadcast tool. Instead of loading and playing a file, it captures a live audio INPUT
/// (sound card / loopback), runs it through the shared bus DSP rack (EQ → AutoTilt → Punch →
/// MasteringLimiter), and exposes a persistent mixer the Icecast encoder taps — so the
/// broadcast is loud-and-clean automatically, the DJ touches nothing.
///
/// Implemented per platform (ManagedBass today: BassInputCaptureEngine). The UI only ever
/// sees this interface (CLAUDE.md layering rule). The DSP setters mirror
/// <see cref="IAudioEngine"/> exactly, so JUST STREAM reuses the same Tweaks/DSP logic and the
/// "Hard" preset transfers 1:1.
/// </summary>
public interface IAudioInputEngine : IDisposable
{
    // ── Capture lifecycle ────────────────────────────────────────────────

    /// <summary>True while audio is being captured and fed to the mixer/encoder.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// The BASS recording-device index currently captured, or -1 when not capturing.
    /// </summary>
    int CurrentInputDevice { get; }

    /// <summary>
    /// Start capturing from the recording device with the given BASS index. Creates the
    /// persistent mixer on first call, attaches the capture source, and starts the audio
    /// flowing through the DSP chain. Idempotent: starting a different device while already
    /// capturing switches the source seamlessly (the mixer/encoder keep running so an active
    /// stream never drops). Throws on a hard BASS failure (device unavailable).
    /// </summary>
    void StartCapture(int deviceIndex);

    /// <summary>
    /// Stop capturing. The mixer keeps running (outputting silence) so a connected Icecast
    /// stream stays up; call again with <see cref="StartCapture"/> to resume. No-op when idle.
    /// </summary>
    void StopCapture();

    /// <summary>Raised when <see cref="IsCapturing"/> changes. May fire on a BASS thread — marshal to UI.</summary>
    event EventHandler<bool>? CaptureStateChanged;

    // ── Input device enumeration ─────────────────────────────────────────

    /// <summary>
    /// The recording devices currently available, enumerated fresh on each call (devices may
    /// appear/disappear at runtime — e.g. a USB interface). Disabled/absent devices are excluded.
    /// </summary>
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    // ── Levels / metering ────────────────────────────────────────────────

    /// <summary>
    /// Sample the current post-DSP output level as linear peak per channel (0..1), for the
    /// L/R meters. Returns zeros when not capturing. Cheap enough to poll at UI rate.
    /// </summary>
    void GetLevels(out float leftPeak, out float rightPeak);

    // ── Output / gain ────────────────────────────────────────────────────

    /// <summary>
    /// Local monitor volume, 0..1. This is ONLY the level you hear on this machine's output —
    /// it does NOT affect the encoded stream (the encoder taps the mixer pre-volume). Default 0
    /// (no local monitoring), because a DJ usually already hears their own audio directly and a
    /// monitor would double it / risk feedback.
    /// </summary>
    double MonitorVolume { get; set; }

    /// <summary>
    /// Stream gain trim in dB applied to the captured signal before the DSP chain, so a quiet
    /// input can be brought up to broadcast level. 0 = unity. Affects BOTH the stream and monitor.
    /// </summary>
    double InputGainDb { get; set; }

    // ── Bus DSP rack (mirrors IAudioEngine; shapes the stream + monitor) ──

    /// <summary>3-band DJ EQ (Low/Mid/High), linear gains; all unity → true bypass. See <see cref="IAudioEngine.SetEqualizer"/>.</summary>
    void SetEqualizer(double lowGain, double midGain, double highGain);

    /// <summary>True-peak limiter / maximizer. enabled=false → true bypass. See <see cref="IAudioEngine.SetLimiter"/>.</summary>
    void SetLimiter(bool enabled, double driveDb, double ceilingDbTp);

    /// <summary>Adaptive spectral tilt ("auto-master"). strength 0 → bypass. See <see cref="IAudioEngine.SetAdaptiveTilt"/>.</summary>
    void SetAdaptiveTilt(double strength);

    /// <summary>Transient designer (punch). punch 0 → bypass. See <see cref="IAudioEngine.SetTransientDesigner"/>.</summary>
    void SetTransientDesigner(double punch);
}
