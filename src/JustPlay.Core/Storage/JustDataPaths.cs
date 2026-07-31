using System;
using System.IO;

namespace JustPlay.Core.Storage;

/// <summary>
/// Where the J.U.S.T. suite keeps ITS OWN data on this machine — settings, logs, the library index,
/// the window layout. ONE resolver for every app, so JUST PLAY, JUST TAG, JUST STREAM and the CLI
/// can never disagree about where their data lives (and so a future macOS build inherits the
/// decision instead of re-making it).
///
/// <para>By default that is <c>%LOCALAPPDATA%</c> — <c>~/Library/Application Support</c> or
/// <c>~/.local/share</c> on the other platforms, whatever the BCL reports. The layout underneath is
/// unchanged: <c>&lt;base&gt;\JustPlay\settings.json</c>, <c>&lt;base&gt;\JUST\library\*.db</c>, and
/// so on.</para>
///
/// <para><b>Setting <c>JUSTPLAY_DATA_DIR</c> moves that whole tree somewhere else, same shape.</b>
/// That is how you start a PRISTINE copy — first-run, no library root, no index — next to a fully
/// configured one, without touching the real settings:</para>
/// <code>
/// $env:JUSTPLAY_DATA_DIR = "C:\temp\just-fresh"
/// dotnet run --project src\JustPlay.App
/// </code>
/// <para>It is also how a "works on my machine" report gets reproduced on a machine that is already
/// set up, and how a UI empty state can be looked at without wrecking the state you actually use.</para>
///
/// <para>⛔ What it deliberately does NOT move: anything that reports on the MACHINE itself
/// (JustCheck.Probe reads the real user profile on purpose — a redirected doctor would diagnose a
/// phantom), and the user's music, which we never write to anyway.</para>
///
/// <para>Resolved ONCE per process. A half-switched process — settings here, index there — would be
/// worse than either choice.</para>
/// </summary>
public static class JustDataPaths
{
    /// <summary>The environment variable that relocates the whole suite data tree.</summary>
    public const string OverrideVariable = "JUSTPLAY_DATA_DIR";

    static JustDataPaths()
    {
        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Base = Resolve(Environment.GetEnvironmentVariable(OverrideVariable), fallback);
        IsOverridden = !string.Equals(Base, fallback, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The suite's data root for this process. Apps append their own folder to it.</summary>
    public static string Base { get; }

    /// <summary>
    /// True when <see cref="OverrideVariable"/> redirected us. Worth surfacing somewhere visible in a
    /// debug build: a pristine instance that LOOKS like the real one is a good way to lose an evening.
    /// </summary>
    public static bool IsOverridden { get; }

    /// <summary>A path under the suite data root, e.g. <c>Combine("JustPlay", "logs")</c>.</summary>
    public static string Combine(params string[] segments) => Path.Combine([Base, .. segments]);

    /// <summary>
    /// The pure half, so the rules are testable without touching the process environment: blank means
    /// "not set", a value is made absolute, and a value we cannot make sense of falls back rather than
    /// taking the app down on a typo'd variable.
    /// </summary>
    internal static string Resolve(string? raw, string fallback)
    {
        // A shell that pastes a quoted path is a normal accident, not a different intent.
        var value = raw?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JUST] ignoring {OverrideVariable}='{raw}': {ex.Message}");
            return fallback;
        }
    }
}
