using System;
using System.Collections.Generic;
using System.Linq;
using JustPlay.Core.Models;

namespace JustPlay.Core.Audio;

/// <summary>
/// A running process as seen by the OS, reduced to the bits the app-capture picker needs. The OS
/// enumeration (System.Diagnostics on Windows) lives in the platform adapter; this record is the
/// platform-agnostic INPUT to <see cref="CaptureAppFilter"/>, so the picker's whole brain is
/// unit-testable in Core with no OS calls.
/// </summary>
/// <param name="ProcessId">OS process id.</param>
/// <param name="ExecutableName">Executable name WITHOUT path or extension (e.g. "rekordbox").
/// Case-insensitive; the filter lower-cases it.</param>
/// <param name="HasMainWindow">True if the process owns a visible main window — a cheap proxy for
/// "a user-facing app" vs a background service.</param>
public readonly record struct RunningProcess(int ProcessId, string ExecutableName, bool HasMainWindow);

/// <summary>
/// Turns the raw running-process list into the ordered set of apps worth offering in the
/// "capture a specific APP" picker. Pure logic (no OS, no audio) so it's fully unit-tested.
///
/// Rules: recognised DJ apps always qualify and sort FIRST (the streaming use case); recognised
/// media players/browsers qualify next; any OTHER process qualifies only if it owns a main window
/// (drops background services / helpers). Duplicates by executable name collapse to the lowest pid
/// (the likely main instance, not a helper/renderer child). Everything else is dropped as noise.
/// </summary>
public static class CaptureAppFilter
{
    // Recognised DJ applications: a distinctive token to look for → the friendly display name.
    // Matched by CONTAINS on the letters-only, lower-cased exe name (see Normalize) so versioned /
    // spaced names work too — "Traktor Pro 4" → "traktorpro" contains "traktor"; "Serato DJ Pro" →
    // "seratodjpro" contains "serato". Order = most specific first (djaypro before djay).
    private static readonly (string Token, string Name)[] DjApps =
    {
        ("rekordbox", "rekordbox"),
        ("traktor",   "Traktor Pro"),
        ("serato",    "Serato DJ"),
        ("virtualdj", "VirtualDJ"),
        ("enginedj",  "Engine DJ"),
        ("djuced",    "DJUCED"),
        ("mixxx",     "Mixxx"),
        ("djaypro",   "djay Pro"),
        ("djay",      "djay"),
    };

    // Recognised general media sources (players + browsers) — useful but ranked below DJ apps.
    private static readonly IReadOnlyDictionary<string, string> MediaApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Spotify",
        ["vlc"] = "VLC",
        ["foobar2000"] = "foobar2000",
        ["aimp"] = "AIMP",
        ["musicbee"] = "MusicBee",
        ["itunes"] = "iTunes",
        ["applemusic"] = "Apple Music",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["tidal"] = "TIDAL",
        ["deezer"] = "Deezer",
    };

    /// <summary>The default friendly name for an unrecognised windowed app: Title-Case the exe name.</summary>
    private static string PrettyName(string exe) =>
        exe.Length == 0 ? exe : char.ToUpperInvariant(exe[0]) + exe[1..];

    /// <summary>Letters-only, lower-cased form of an exe name — so DJ matching ignores version
    /// numbers, spaces and separators ("Traktor Pro 4" → "traktorpro", "Serato_DJ" → "seratodj").</summary>
    private static string Normalize(string exe)
    {
        var chars = new List<char>(exe.Length);
        foreach (var c in exe)
            if (char.IsLetter(c)) chars.Add(char.ToLowerInvariant(c));
        return new string(chars.ToArray());
    }

    /// <summary>Match a DJ app by distinctive token (contains — version/space tolerant).</summary>
    private static bool TryMatchDj(string exe, out string friendly)
    {
        var norm = Normalize(exe);
        if (norm.Length != 0)
            foreach (var (token, name) in DjApps)
                if (norm.Contains(token)) { friendly = name; return true; }
        friendly = string.Empty;
        return false;
    }

    /// <summary>
    /// Project running processes to the ordered picker list. DJ apps first (each in first-seen order),
    /// then media apps, then other windowed apps; de-duplicated by executable (lowest pid wins).
    /// </summary>
    public static IReadOnlyList<CaptureApp> ToCaptureApps(IEnumerable<RunningProcess> processes)
    {
        // Collapse duplicates by exe name → keep the lowest pid (the main instance, not a child helper).
        var byExe = new Dictionary<string, RunningProcess>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            if (string.IsNullOrWhiteSpace(p.ExecutableName)) continue;
            var exe = p.ExecutableName.Trim();
            if (!byExe.TryGetValue(exe, out var existing) || p.ProcessId < existing.ProcessId)
                byExe[exe] = p with { ExecutableName = exe };
        }

        var dj = new List<CaptureApp>();
        var media = new List<CaptureApp>();
        var other = new List<CaptureApp>();

        foreach (var p in byExe.Values)
        {
            var exe = p.ExecutableName;
            if (TryMatchDj(exe, out var djName))
                dj.Add(new CaptureApp(p.ProcessId, djName, exe.ToLowerInvariant(), IsDjApp: true));
            else if (MediaApps.TryGetValue(exe, out var mediaName))
                media.Add(new CaptureApp(p.ProcessId, mediaName, exe.ToLowerInvariant(), IsDjApp: false));
            else if (p.HasMainWindow)
                other.Add(new CaptureApp(p.ProcessId, PrettyName(exe), exe.ToLowerInvariant(), IsDjApp: false));
            // else: background service / helper with no window and not a known audio app → drop.
        }

        // Stable, readable ordering inside each tier: DJ apps + media by display name; keep it deterministic.
        media = media.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        other = other.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        return dj.Concat(media).Concat(other).ToList();
    }

    /// <summary>True if the executable name is a recognised DJ application (version/space tolerant).</summary>
    public static bool IsDjExecutable(string executableName) =>
        !string.IsNullOrWhiteSpace(executableName) && TryMatchDj(executableName.Trim(), out _);
}
