namespace JustPlay.Core.Models;

/// <summary>
/// Describes one audio INPUT (recording / loopback) device available to BASS.
/// Returned by <see cref="Abstractions.IAudioInputEngine.GetInputDevices"/>.
///
/// This is the JUST STREAM counterpart of <see cref="AudioOutputDevice"/>: instead of
/// where audio leaves the machine, it is where the stream's audio comes FROM — a sound
/// card line-in, a virtual audio cable, or a Windows "Stereo Mix" / loopback device that
/// captures whatever the DJ software is playing.
///
/// <paramref name="Index"/> is the BASS recording-device index (0-based for recording,
/// unlike output devices). It can change across reboots, so persistence stores
/// <paramref name="Name"/> (stable per hardware) and resolves the index at runtime.
/// </summary>
/// <param name="Index">BASS recording-device index.</param>
/// <param name="Name">Human-readable device name, e.g. "Line In (Realtek)" or "Stereo Mix".</param>
/// <param name="IsLoopback">
/// True when this is a loopback / "what-you-hear" device (captures system output rather than a
/// physical input). The most useful source for a DJ streaming their software's audio.
/// </param>
/// <param name="IsDefault">True when this is the system's current default recording device.</param>
public sealed record AudioInputDevice(int Index, string Name, bool IsLoopback, bool IsDefault);
