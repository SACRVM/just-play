using System.Collections.Generic;

namespace JustPlay.Core.Models;

/// <summary>
/// Persisted user preferences. Every field is nullable / defaultable so the
/// settings file forward-compatibly survives new fields being added — a
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
    /// Use the trained "AI key" model (ONNX, MIREX ~0.75) when it's available, instead of the
    /// DSP template detector (~0.71). Falls back to DSP automatically if the model/runtime
    /// aren't present, so turning this off just forces the lightweight path.
    /// </summary>
    public bool UseAiKeyDetection { get; init; } = true;

    /// <summary>
    /// Run BPM/key/energy analysis automatically when tracks are added. Off by default — JustPlay
    /// is "just play": dropping a track plays it instantly; analysis is an explicit choice (right-
    /// click → Analyze) so adding a big folder never pegs the CPU unasked.
    /// </summary>
    public bool AutoAnalyze { get; init; } = false;

    /// <summary>How many tracks to analyse at once (bounded concurrency). Default 4. Each worker
    /// pegs a core for the duration of a track's decode + DSP, so this trades CPU for throughput.</summary>
    public int AnalysisThreads { get; init; } = 4;

    /// <summary>
    /// After analysing a track, immediately write the detected BPM/key/energy into its tags — our
    /// values become the file's truth, so no "differs from the tag" flags ever appear. Off by
    /// default: writing files is otherwise always an explicit, consent-gated action.
    /// </summary>
    public bool AutoWriteOnAnalyze { get; init; } = false;

    /// <summary>
    /// When writing tags, also prepend a "DJ Software compatible" segment to the file's comment
    /// field in the format understood by Serato, rekordbox, Traktor, and VirtualDJ —
    /// e.g. <c>8A - Energy 7</c>. The user's existing comment text is preserved after a
    /// <c>" | "</c> separator. Off by default (opt-in, non-destructive).
    /// </summary>
    public bool WriteDjComment { get; init; } = false;

    // ── Audio output device ───────────────────────────────────────────────────────────

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

    // ── JUST STREAM — Icecast broadcast profiles (S1 config foundation) ──────────────
    // These fields are populated by the future JUST STREAM streaming UI (S2+).
    // Loading an old settings.json that lacks these fields is safe — the JSON deserializer
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

    public static readonly UserSettings Defaults = new();
}
