namespace JustPlay.Audio.Bass;

/// <summary>
/// Which BASS devices a device picker offers.
///
/// <para>On Windows and macOS BASS lists the OS's audio endpoints - every entry is a device. On Linux
/// it lists ALSA's PCM definitions, and most of those are not devices at all: resampler, upmix and
/// downmix plugins, JACK and OSS bridges (measured on a PipeWire desktop: 19 outputs, of which the
/// eight plugin entries fail BASS_Init with BASS_ERROR_ILLPARAM). What is left is worth offering:
/// "default" - the desktop's sound server, i.e. whatever output or input the user picked in the
/// system settings - and the hw: cards, which is how a dedicated DJ interface is addressed directly
/// (exclusively: the sound server cannot use that card while BASS holds it). The "pipewire" and
/// "pulse" entries are aliases of "default" on a desktop with a sound server and only add a second
/// name for the same output.</para>
/// </summary>
internal static class BassDevices
{
    /// <summary>True when a device with this BASS driver string belongs in a picker.</summary>
    public static bool IsListed(string? driver) =>
        !OperatingSystem.IsLinux()
        || driver == "default"
        || (driver?.StartsWith("hw:", StringComparison.Ordinal) ?? false);
}
