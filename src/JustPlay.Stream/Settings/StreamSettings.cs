using System.Collections.Generic;
using JustPlay.Core.Models;

namespace JustPlay.Stream.Settings;

/// <summary>
/// Persisted JUST STREAM preferences. Self-contained (NOT JustPlay's UserSettings - this app has
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

    /// <summary>True when the source is a captured APPLICATION (per-process loopback) rather than a device.</summary>
    public bool AppSourceMode { get; set; }

    /// <summary>Executable name of the last-captured app (e.g. "rekordbox"), re-resolved to a live pid at launch.</summary>
    public string? CaptureAppExe { get; set; }

    /// <summary>Which channels of a captured app go on air: 1 = broadcast channels 1/2 (default - on
    /// multi-out DJ gear that's the Master, dropping the Cue on 3/4; lossless for a plain stereo app),
    /// 0 = full stereo mix (all channels combined), 2 = broadcast channels 3/4. Initialised to 1 so a
    /// fresh install defaults to channels 1-2; a legacy value of 3 (the removed "Auto") is treated as 1
    /// on load. See <see cref="JustPlay.Core.Audio.AppCaptureChannels"/>.</summary>
    public int AppMasterChannels { get; set; } = 1;

    // -- Bus DSP rack (linear gains; defaults = transparent) --------------
    public double EqLow { get; set; } = 1.0;
    public double EqMid { get; set; } = 1.0;
    public double EqHigh { get; set; } = 1.0;
    public double AutoTilt { get; set; } = 0.0;
    public double Punch { get; set; } = 0.0;

    /// <summary>Limiter/maximizer drive: "Off" | "Soft" | "Club" | "Loud". See StreamViewModel mapping.</summary>
    public string LimiterDrive { get; set; } = "Soft";

    // -- User-saved Sound presets (additive to the built-in Normal / Hard) --
    /// <summary>User's saved DSP presets (the shared <see cref="DspPreset"/> Core model). The built-in
    /// Normal + Hard are seeded into this list once (see <see cref="SoundPresetsSeeded"/>) as ordinary,
    /// editable/deletable entries. Empty by default; an old settings.json without it deserializes empty.</summary>
    public List<DspPreset> SoundPresets { get; set; } = new();

    /// <summary>True once the built-ins have been seeded into <see cref="SoundPresets"/> (done exactly
    /// once). Persisted so deleting the built-ins does NOT bring them back next launch.</summary>
    public bool SoundPresetsSeeded { get; set; }

    /// <summary>Which built-in preset SET has been seeded (0 = none / pre-versioned). A load below
    /// <see cref="JustPlay.Core.Models.DspPreset.BuiltInSeedVersion"/> tops up any missing built-ins
    /// (by name) once. Mirrors the field on JUST PLAY's UserSettings; supersedes SoundPresetsSeeded.</summary>
    public int SoundPresetsSeedVersion { get; set; }

    // -- Sample rate ------------------------------------------------------
    /// <summary>Capture/mixer sample rate: 44100 (default) or 48000 Hz.</summary>
    public int SampleRate { get; set; } = 44100;

    // -- Levels -----------------------------------------------------------
    public double InputGainDb { get; set; } = 0.0;

    /// <summary>Name of the local monitor OUTPUT device (resolved to a BASS index at runtime).
    /// null / unknown => "No output (stream only)" - no local monitor (the default).</summary>
    public string? MonitorDeviceName { get; set; }
    public double MonitorVolume { get; set; } = 0.8;

    // -- Recording ("record your set") ------------------------------------
    /// <summary>Target folder for set recordings. Null/empty = the default
    /// (Music\JUST STREAM Recordings), resolved at record start.</summary>
    public string? RecordingFolder { get; set; }

    /// <summary>Recording format - a <see cref="JustPlay.Core.Models.RecordingFormat"/> member name
    /// ("SameAsStream" | "Mp3_320" | "Flac" | "Aiff" | "Wav"). Stored as a string like
    /// <see cref="LimiterDrive"/>/<see cref="Theme"/> so the JSON stays readable; an unknown
    /// value falls back to SameAsStream on load.</summary>
    public string RecordingFormat { get; set; } = "SameAsStream";

    /// <summary>Start the recorder with CONNECT and save the file on DISCONNECT - nobody
    /// remembers to hit record at gig start.</summary>
    public bool AutoRecord { get; set; }

    /// <summary>Auto-trim silence (default ON): REC only ARMS the recorder - the file starts
    /// on the first beat and ends on the last, with zero dead air at either end (look-behind
    /// gate; short breaks <=30 s are kept 1:1, longer gaps are cut). Kills the "went on air
    /// 15 minutes before the music" silence, and the tail nobody remembers to stop.</summary>
    public bool TrimSilence { get; set; } = true;

    // -- Stream / privacy -------------------------------------------------
    /// <summary>U7 - send now-playing/station title to the server (default on). Off = privacy mode.</summary>
    public bool SendSongInfo { get; set; } = true;

    /// <summary>Keep the display awake while on air / recording (default ON). DJ software gets
    /// controller input only - the OS sees an idle keyboard and blanks the screen mid-set;
    /// Traktor doesn't suppress that itself (Chloe 2026-07-15). See <see cref="JustPlay.UI.KeepAwake"/>.</summary>
    public bool KeepScreenAwake { get; set; } = true;

    /// <summary>Whether the bottom error-log strip is expanded.</summary>
    public bool LogVisible { get; set; } = false;

    /// <summary>Show the bottom keyboard-hint bar (C = on air / off air, R = record / stop). Default on;
    /// pros switch it off once the keys are muscle memory (Chloe 2026-07-06).</summary>
    public bool ShowKeyHints { get; set; } = true;

    /// <summary>Name of the active theme palette (see <see cref="JustPlay.Core.Theming.Themes"/>). Defaults to Aurora.</summary>
    public string Theme { get; set; } = "Aurora";
}
