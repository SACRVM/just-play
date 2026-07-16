using System;

namespace JustPlay.Audio.Bass;

/// <summary>
/// Narrow seam (N26) so <see cref="BassPreListenEngine"/> can duck/restore the MAIN engine's
/// device-bound audible output for the "cue wins on a shared device" invariant, without taking a
/// concrete dependency on <see cref="BassAudioEngine"/> or its playback/DSP/broadcast surface.
///
/// Implemented by <see cref="BassAudioEngine"/>. Lives in this assembly (not JustPlay.Core) —
/// "device-output-only volume, decoupled from the DSP/encoder chain" is a BASS-specific concept
/// (BASS_ATTRIB_VOL vs BASS_ATTRIB_VOLDSP) with no platform-agnostic meaning; Core only sees the
/// pure decision logic (<see cref="JustPlay.Core.Playback.CueArbiter"/>).
/// </summary>
public interface IDuckableAudioOutput
{
    /// <summary>The BASS output device index the implementor currently renders to, or -1 if not
    /// yet resolved. Mirrors <see cref="JustPlay.Core.Abstractions.IAudioEngine.CurrentOutputDevice"/>.</summary>
    int CurrentOutputDevice { get; }

    /// <summary>Raised whenever <see cref="CurrentOutputDevice"/> changes (i.e. after a successful
    /// <see cref="JustPlay.Core.Abstractions.IAudioEngine.SetOutputDevice"/> call), so a listener
    /// can re-evaluate whether it still shares a device with this output.</summary>
    event EventHandler? OutputDeviceChanged;

    /// <summary>
    /// Smoothly ramp the device-bound audible output to silence (<paramref name="ducked"/> =
    /// true) or back to its normal level (false) over a short, click-free fade.
    ///
    /// <para>MUST NOT touch playback/pause state, per-track normalization, or anything the
    /// broadcast encoder taps — implementations must use a device-output-only attribute
    /// (BASS_ATTRIB_VOL), never anything that reaches the DSP chain (BASS_ATTRIB_VOLDSP). See
    /// <c>BassAudioEngine.SetDucked</c> for the verified BASS-doc citation. Idempotent: calling
    /// with the same value twice is a no-op.</para>
    /// </summary>
    void SetDucked(bool ducked);
}
