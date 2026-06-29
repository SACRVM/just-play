using System.Collections.Generic;
using JustPlay.Core.Models;

namespace JustPlay.Stream.Settings;

/// <summary>
/// Persisted JUST STREAM preferences. Self-contained (NOT JustPlay's UserSettings — this app has
/// no playlist/player state). Serialized to <c>%LOCALAPPDATA%\JustStream\settings.json</c> by
/// <see cref="JsonStreamSettingsService"/>.
///
/// Server profiles reuse the shared <see cref="StreamServerProfile"/> Core model, so a profile is
/// portable between JustPlay's in-app streaming panel and JUST STREAM.
/// </summary>
public sealed class StreamSettings
{
    /// <summary>Configured Icecast/Shoutcast server profiles.</summary>
    public List<StreamServerProfile> Servers { get; set; } = new();

    /// <summary>Id of the currently selected server profile (<see cref="StreamServerProfile.Id"/>), or null.</summary>
    public string? SelectedServerId { get; set; }

    /// <summary>Name of the last-used input device (resolved to a BASS index at runtime; names are stable, indices are not).</summary>
    public string? InputDeviceName { get; set; }

    // ── Bus DSP rack (linear gains; defaults = transparent) ──────────────
    public double EqLow { get; set; } = 1.0;
    public double EqMid { get; set; } = 1.0;
    public double EqHigh { get; set; } = 1.0;
    public double AutoTilt { get; set; } = 0.0;
    public double Punch { get; set; } = 0.0;

    /// <summary>Limiter/maximizer drive: "Off" | "Soft" | "Club" | "Loud". See StreamViewModel mapping.</summary>
    public string LimiterDrive { get; set; } = "Soft";

    // ── User-saved Sound presets (additive to the built-in Normal / Hard) ──
    /// <summary>User's saved DSP presets (the shared <see cref="DspPreset"/> Core model). The built-in
    /// Normal + Hard are seeded into this list once (see <see cref="SoundPresetsSeeded"/>) as ordinary,
    /// editable/deletable entries. Empty by default; an old settings.json without it deserializes empty.</summary>
    public List<DspPreset> SoundPresets { get; set; } = new();

    /// <summary>True once Normal + Hard have been seeded into <see cref="SoundPresets"/> (done exactly
    /// once). Persisted so deleting the built-ins does NOT bring them back next launch.</summary>
    public bool SoundPresetsSeeded { get; set; }

    // ── Sample rate ──────────────────────────────────────────────────────
    /// <summary>Capture/mixer sample rate: 44100 (default) or 48000 Hz.</summary>
    public int SampleRate { get; set; } = 44100;

    // ── Levels ───────────────────────────────────────────────────────────
    public double InputGainDb { get; set; } = 0.0;

    /// <summary>Name of the local monitor OUTPUT device (resolved to a BASS index at runtime).
    /// null / unknown ⇒ "No output (stream only)" — no local monitor (the default).</summary>
    public string? MonitorDeviceName { get; set; }
    public double MonitorVolume { get; set; } = 0.8;

    // ── Stream / privacy ─────────────────────────────────────────────────
    /// <summary>U7 — send now-playing/station title to the server (default on). Off = privacy mode.</summary>
    public bool SendSongInfo { get; set; } = true;

    /// <summary>Whether the bottom error-log strip is expanded.</summary>
    public bool LogVisible { get; set; } = false;

    /// <summary>Name of the active theme palette (see <see cref="JustPlay.Core.Theming.Themes"/>). Defaults to Aurora.</summary>
    public string Theme { get; set; } = "Aurora";
}
