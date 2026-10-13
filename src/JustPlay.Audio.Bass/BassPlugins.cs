namespace JustPlay.Audio.Bass;

/// <summary>
/// The decoder plugins BASS needs on Linux, loaded once per process before the first file stream.
///
/// <para>On Windows and macOS BASS hands FLAC / AAC / M4A to the OS codecs (Media Foundation,
/// Core Audio), so no plugin is shipped there. Linux has no such layer: without these a FLAC file
/// fails with <c>Errors.FileFormat</c> - and a FLAC library is most of a DJ library. bassopus
/// rides along so .opus (which <c>AudioFiles.Extensions</c> lists) actually decodes here.
/// WMA stays unsupported on Linux - basswma is Windows-only.</para>
///
/// <para>Every site that opens a FILE stream calls <see cref="EnsureLoaded"/> first. The work sits
/// in the static constructor: the runtime runs it exactly once and makes a concurrent caller wait
/// for it, which matters for the CLI's parallel analysis (24 threads opening files at once).</para>
/// </summary>
internal static class BassPlugins
{
    private static readonly string[] LinuxDecoders = ["libbassflac.so", "libbass_aac.so", "libbassopus.so"];

    static BassPlugins()
    {
        if (!OperatingSystem.IsLinux()) return;

        foreach (var name in LinuxDecoders)
        {
            // A full path, not the bare name: BASS dlopen()s what it is given, and a bare name
            // searches the system library paths, never the folder the app ships in.
            var path = Path.Combine(AppContext.BaseDirectory, name);
            try
            {
                if (ManagedBass.Bass.PluginLoad(path) == 0
                    && ManagedBass.Bass.LastError != ManagedBass.Errors.Already)
                    Console.WriteLine($"[BASS] decoder plugin {name} not loaded: {ManagedBass.Bass.LastError}");
            }
            catch (DllNotFoundException ex)
            {
                // libbass.so itself is missing - the first CreateStream reports that more usefully.
                Console.WriteLine($"[BASS] decoder plugins skipped: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>Loads the platform's decoder plugins if that has not happened yet. Cheap after the first call.</summary>
    public static void EnsureLoaded() { }
}
