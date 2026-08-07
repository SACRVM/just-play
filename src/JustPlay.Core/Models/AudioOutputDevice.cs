namespace JustPlay.Core.Models;

/// <summary>
/// Describes one audio output device available to BASS.
/// Returned by <see cref="Abstractions.IAudioEngine.GetOutputDevices"/>.
///
/// <paramref name="Index"/> is the BASS device index (starts at 1; 0 is "No sound").
/// It can change across reboots (e.g. if devices are added/removed), so persistence
/// uses <paramref name="Name"/> - which is stable for a given piece of hardware -
/// and resolves the index at runtime.
/// </summary>
/// <param name="Index">BASS device index (1-based).</param>
/// <param name="Name">Human-readable device name, e.g. "Speakers (Realtek High Definition Audio)".</param>
/// <param name="IsDefault">True when this is the system's current default output device.</param>
public sealed record AudioOutputDevice(int Index, string Name, bool IsDefault);
