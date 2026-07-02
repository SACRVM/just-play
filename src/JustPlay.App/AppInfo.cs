using System.Reflection;

namespace JustPlay.App;

/// <summary>
/// Central, build-stamped app identity — name, tagline, author, version. The About
/// dialog and the Tweaks footer both read from here so there is exactly one place
/// the version comes from (the <c>&lt;Version&gt;</c> in Directory.Build.props).
/// </summary>
public static class AppInfo
{
    public const string Name = "JustPlay";
    public const string Tagline = "Key-aware DJ music player";
    public const string Author = "Chloe Dream";
    public const string Copyright = "© 2026 Chloe Dream";

    /// <summary>
    /// True in DEBUG builds, false in RELEASE. Gates IN-PROGRESS / not-yet-working UI so release
    /// installers ship WITHOUT the dead bits, while development (dotnet run = Debug) still shows them.
    /// Currently gates ONLY the headphone <b>Pre-Cue</b> tab (drag-in not working yet — deferred to
    /// 0.5). Bind UI via <c>MainWindowViewModel.ShowExperimentalUi</c>. (Opus streaming is NO LONGER
    /// gated: the libopus encoder ships and works — bassenc_opus.dll — so the MP3/Opus toggle is
    /// always visible in both JUST PLAY's streaming panel and JUST STREAM.)
    /// </summary>
#if DEBUG
    public const bool Experimental = true;
#else
    public const bool Experimental = false;
#endif

    /// <summary>User-facing version, e.g. "0.1.0". Resolved once at first access.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Version with the conventional "v" prefix, e.g. "v0.1.0".</summary>
    public static string DisplayVersion => $"v{Version}";

    /// <summary>
    /// The <c>&lt;Version&gt;</c> from Directory.Build.props flows into
    /// AssemblyInformationalVersion at build time — that's the canonical user-facing
    /// version. Some build setups (SourceLink) append "+sha"; strip it for display.
    /// Falls back to the padded 3-part AssemblyVersion if the attribute is missing.
    /// (Ported from firepit-ai's AboutDialog.ResolveVersion.)
    /// </summary>
    private static string ResolveVersion()
    {
        var info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "?";
    }
}
