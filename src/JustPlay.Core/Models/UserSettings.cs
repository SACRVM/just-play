using System.Collections.Generic;

namespace JustPlay.Core.Models;

/// <summary>
/// Persisted user preferences. Every field is nullable / defaultable so the
/// settings file forward-compatibly survives new fields being added - a
/// settings.json written by today's build, then loaded by tomorrow's build
/// with a new property, just gets the default for that property.
///
/// JustPlay's "stateless DJ player" promise applies to the TRACK QUEUE
/// (close and the queue is empty), not to UI preferences. Theme choice and
/// the tweak toggles are persisted because expecting the user to re-pick
/// "Sunset" every time they open the app would be hostile.
/// </summary>
public sealed record UserSettings
{
    /// <summary>The <see cref="Theming.Theme.Name"/> currently active.</summary>
    public string Theme { get; init; } = "Aurora";

    /// <summary>Whether the vinyl spins during playback (Tweaks toggle).</summary>
    public bool VinylSpinEnabled { get; init; } = true;

    /// <summary>Whether the FFT-driven waveform header animates (Tweaks toggle).</summary>
    public bool WaveformEnabled { get; init; } = true;

    /// <summary>
    /// Run BPM/key/energy analysis automatically when tracks are added. Off by default - JustPlay
    /// is "just play": dropping a track plays it instantly; analysis is an explicit choice (right-
    /// click -> Analyze) so adding a big folder never pegs the CPU unasked.
    /// </summary>
    public bool AutoAnalyze { get; init; } = false;

    /// <summary>How many tracks to analyse at once (bounded concurrency). Default 4. Each worker
    /// pegs a core for the duration of a track's decode + DSP, so this trades CPU for throughput.</summary>
    public int AnalysisThreads { get; init; } = 4;

    /// <summary>
    /// After analysing a track, immediately write the detected BPM/key/energy into its tags - our
    /// values become the file's truth, so no "differs from the tag" flags ever appear. Off by
    /// default: writing files is otherwise always an explicit, consent-gated action.
    /// </summary>
    public bool AutoWriteOnAnalyze { get; init; } = false;

    /// <summary>
    /// When writing tags, also prepend a "DJ Software compatible" segment to the file's comment
    /// field in the format understood by Serato, rekordbox, Traktor, and VirtualDJ -
    /// e.g. <c>8A - Energy 7</c>. The user's existing comment text is preserved after a
    /// <c>" | "</c> separator. Off by default (opt-in, non-destructive).
    /// </summary>
    public bool WriteDjComment { get; init; } = false;

    /// <summary>
    /// Apply each track's measured ReplayGain on playback so the whole queue plays at an even
    /// loudness (non-destructively, via output volume - the file is never altered). Clipping is
    /// prevented using the measured peak. Off by default: only analysed tracks carry a gain, and
    /// raw playback is the least-surprising default; flip it on in Tweaks. [[future-gain-loudness-feature]]
    /// </summary>
    public bool PlaybackNormalization { get; init; } = false;

    /// <summary>
    /// Loudness target for playback normalization, streaming-player style: "Quiet" (-19 LUFS),
    /// "Normal" (-14, default), or "Loud" (-11). Higher = louder + closer to modern masters (less
    /// gain reduction); the RG 2.0 reference (-18) is intentionally quiet/broadcast and was too
    /// soft as a player default. Unknown values fall back to Normal. See [[future-gain-loudness-feature]].
    /// </summary>
    public string NormalizationLevel { get; init; } = "Normal";

    /// <summary>
    /// Crossfade length between tracks on auto-advance, in seconds. 0 = off (hard track boundaries).
    /// The UI offers Off/2/4/8.
    /// </summary>
    public int CrossfadeSeconds { get; init; } = 0;

    /// <summary>
    /// Master true-peak limiter / maximizer mode on the output bus. The UI offers
    /// "Off" / "Soft" / "Club" / "Loud":
    ///   - Off  - bypassed (no DSP on the mixer).
    ///   - Soft - transparent -1 dBTP safety limiter, 0 dB drive (peaks only).
    ///   - Club - -1 dBTP + 3 dB maximizer drive.
    ///   - Loud - -1 dBTP + 6 dB maximizer drive.
    /// Off by default - raw output is the least-surprising default; flip it on in Tweaks. The
    /// ceiling stays broadcast-safe (-1 dBTP) in every non-Off mode. Shapes both local playback
    /// AND the Icecast stream (the encoder taps the mixer after the limiter). [[own-limiter-no-vst]]
    /// </summary>
    public string LimiterMode { get; init; } = "Off";

    /// <summary>
    /// 3-band DJ EQ / isolator band gains (LINEAR): 1.0 = unity/flat, 0.0 = full kill, 2.0 = +6 dB.
    /// All default to 1.0 (flat -> the EQ bypasses entirely). Crossovers fixed at 200 Hz / 4 kHz.
    /// Low/Mid/High shape both local playback and the stream. [[own-limiter-no-vst]]
    /// </summary>
    public double EqLowGain  { get; init; } = 1.0;
    /// <inheritdoc cref="EqLowGain"/>
    public double EqMidGain  { get; init; } = 1.0;
    /// <inheritdoc cref="EqLowGain"/>
    public double EqHighGain { get; init; } = 1.0;

    /// <summary>
    /// "Revive" rack - anti-flat enhancement on the output bus, for tracks that sound dull/lifeless.
    /// All neutral by default (each block bypasses at its zero value), so the bus stays bit-transparent
    /// until the user dials something in. They shape both local playback and the stream. [[own-limiter-no-vst]]
    /// </summary>
    /// <remarks>Each block bypasses at 0; tune by ear.</remarks>
    public double TransientPunch  { get; init; } = 0.0;
    /// <inheritdoc cref="TransientPunch"/>
    public double AutoTiltStrength { get; init; } = 0.0;

    /// <summary>
    /// User-saved Sound-tab bus presets (name + the six tone fields above). Additive on top of the
    /// built-in Hard / Neutral presets - the user saves the current bus state under a name, recalls
    /// it with one click, and deletes it. Empty by default. Forward-compatible: an old settings.json
    /// without this field deserializes to an empty list (no migration needed). [[own-limiter-no-vst]]
    /// </summary>
    public List<DspPreset> SoundPresets { get; init; } = [];

    /// <summary>True once the built-in Hard / Neutral starting points have been seeded into
    /// <see cref="SoundPresets"/> (done exactly once on a fresh install). Persisted so deleting both
    /// built-ins does NOT bring them back on the next launch. Defaults to false -> a pre-U5 settings.json
    /// seeds the two defaults on first load, then flips this true.</summary>
    public bool SoundPresetsSeeded { get; init; }

    /// <summary>Which built-in preset SET has been seeded (0 = none / pre-versioned). A load with a
    /// value below <see cref="DspPreset.BuiltInSeedVersion"/> tops up any missing built-in starting
    /// points (by name) exactly once, so existing installs gain new presets without duplicating or
    /// resurrecting the user's own. Supersedes <see cref="SoundPresetsSeeded"/>; an old settings.json
    /// without this key deserializes to 0 -> tops up on first load. [[own-limiter-no-vst]]</summary>
    public int SoundPresetsSeedVersion { get; init; }

    // -- Audio output device -----------------------------------------------------------

    /// <summary>
    /// The <see cref="AudioOutputDevice.Name"/> of the preferred audio output device,
    /// as returned by <c>Bass.GetDeviceInfo</c>. Null means "use the system default".
    ///
    /// Persisted by NAME (not by BASS index) because the index can change across
    /// reboots when devices are added or removed; the name is stable for a given
    /// piece of hardware. On startup, the engine matches by name and falls back to
    /// the system default if the saved device is no longer present (e.g. unplugged).
    /// </summary>
    public string? OutputDeviceName { get; init; } = null;

    /// <summary>
    /// The <see cref="AudioOutputDevice.Name"/> of the preferred headphone device for
    /// pre-listen (PFL) cue monitoring. Null means no headphone device is selected -
    /// the pre-listen engine produces no audio until a device is chosen.
    ///
    /// Persisted by name (same stability reason as <see cref="OutputDeviceName"/>).
    /// On startup the engine resolves the name to a BASS device index; if the device
    /// is no longer present (e.g. headphones unplugged) the selection stays null and
    /// the pre-listen panel shows the "choose a device" hint. [[roadmap-precue-headphone]]
    /// </summary>
    public string? HeadphoneDeviceName { get; init; } = null;

    /// <summary>
    /// Volume for the pre-listen (PFL) headphone output, 0..1. Persisted separately
    /// from the main output volume so switching back to monitoring restores the last
    /// headphone level. Defaults to 0.8 (slightly under full to protect ears on
    /// headphone plug-in). [[roadmap-precue-headphone]]
    /// </summary>
    public double PreCueVolume { get; init; } = 0.8;

    // -- JUST STREAM - Icecast broadcast profiles (S1 config foundation) --------------
    // These fields are populated by the future JUST STREAM streaming UI (S2+).
    // Loading an old settings.json that lacks these fields is safe - the JSON deserializer
    // leaves them at their default values (empty list / null). No migration is needed.

    /// <summary>
    /// Named Icecast server profiles. Each profile holds the host, port, mount, credentials,
    /// codec, and bitrate for one broadcast destination. Empty by default. The JUST STREAM
    /// UI (S2+) will provide add/edit/remove. Multiple profiles support e.g. a test server
    /// and a live club server as separate one-click options.
    /// </summary>
    public List<StreamServerProfile> StreamServers { get; init; } = [];

    /// <summary>
    /// The <see cref="StreamServerProfile.Id"/> of the profile that is currently selected
    /// in the streaming panel. Null when no profile is selected or the profile list is empty.
    /// </summary>
    public string? SelectedStreamServerId { get; init; } = null;

    // -- Column views (A | B | C) -----------------------------------------------------

    /// <summary>
    /// Visible toggleable-column ids for view A (default: Mix - harmonic info).
    /// Forward-compatible: unknown ids are ignored at runtime so adding new columns
    /// in a later build does not break existing settings files.
    /// </summary>
    public List<string> ColumnViewA { get; init; } = ["genre", "bpm", "key", "nrg", "duration"];

    /// <summary>Visible toggleable-column ids for view B (default: Levels - loudness/gain info).</summary>
    public List<string> ColumnViewB { get; init; } = ["bpm", "gain", "lufs", "duration"];

    /// <summary>Visible toggleable-column ids for view C (default: Minimal - BPM + time only).</summary>
    public List<string> ColumnViewC { get; init; } = ["bpm", "duration"];

    /// <summary>
    /// The active column-view letter ("A", "B", or "C"). Defaults to "A".
    /// Unknown values are treated as "A" at runtime.
    /// </summary>
    public string ActiveColumnView { get; init; } = "A";

    // -- Auto-update (v0.2) -----------------------------------------------------------

    /// <summary>
    /// Poll GitHub Releases on startup + on an interval and surface the title-bar update
    /// badge when a newer build lands. On by default - a music player that silently rots on
    /// an old version is a worse default than one that offers the update (the user still
    /// chooses to install). Set false to opt out of all network checks.
    /// </summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// A release version the user explicitly chose to skip ("Ignore this version"). The badge
    /// stays hidden for this version and anything older; a NEWER release still shows. Null means
    /// nothing is ignored. Stored as a 3-part "Major.Minor.Build" string.
    /// </summary>
    public string? IgnoredUpdateVersion { get; init; } = null;

    public static readonly UserSettings Defaults = new();
}
