using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// A user-saved Sound-tab bus preset - a name plus the full set of tone-shaping bus values the
/// Sound tab exposes after the 2026-06-17 "blendware cull". Saving captures the CURRENT bus state;
/// applying pushes every field back onto the engine + persistence (mirroring the built-in
/// Hard / Neutral presets in <c>MainWindowViewModel.ApplyHardPreset</c> /
/// <c>ApplyNeutralPreset</c>). [[own-limiter-no-vst]]
///
/// The six captured fields ARE the whole post-cull Sound chain:
///   - <see cref="EqLowGain"/> / <see cref="EqMidGain"/> / <see cref="EqHighGain"/> - 3-band EQ (linear, 1.0 = flat)
///   - <see cref="AutoTiltStrength"/> - adaptive spectral tilt (0..1, 0 = off)
///   - <see cref="TransientPunch"/> - transient designer (0..1, 0 = off)
///   - <see cref="LimiterMode"/> - master limiter mode ("Off"/"Soft"/"Club"/"Loud")
///
/// Plain data model - no behaviour, no IO. A <c>record</c> with init-only (NON-positional)
/// properties so it has a public parameterless ctor: reflection-serializable (the established
/// UserSettings path) AND source-gen-serializable (<see cref="DspPresetJsonContext"/>), trim/AOT-safe.
/// </summary>
public sealed record DspPreset
{
    /// <summary>Human-readable display name, e.g. "Warm club" or "My hardcore". User-supplied.</summary>
    public string Name { get; init; } = "Preset";

    /// <summary>Low-band EQ gain (linear): 1.0 = flat, 0.0 = kill, 2.0 = +6 dB.</summary>
    public double EqLowGain { get; init; } = 1.0;

    /// <inheritdoc cref="EqLowGain"/>
    public double EqMidGain { get; init; } = 1.0;

    /// <inheritdoc cref="EqLowGain"/>
    public double EqHighGain { get; init; } = 1.0;

    /// <summary>Adaptive spectral tilt strength, 0..1 (0 = off / bypass).</summary>
    public double AutoTiltStrength { get; init; } = 0.0;

    /// <summary>Transient-designer punch, 0..1 (0 = off / bypass).</summary>
    public double TransientPunch { get; init; } = 0.0;

    /// <summary>Master true-peak limiter mode: "Off" / "Soft" / "Club" / "Loud".</summary>
    public string LimiterMode { get; init; } = "Off";

    // -- Built-in starting points (shared across the JUST suite) --------------
    // Genre-oriented presets seeded ONCE per app into the user's list, then fully editable/deletable.
    // The TONAL identity (EQ / AutoTilt / Punch) is IDENTICAL in every app - same recognisable sound,
    // the suite "Wiedererkennungswert". Only the LIMITER differs by app: a live STREAM maximises
    // loudness (a DJ can't gain-stage a broadcast and must stay competitive), local PLAYBACK stays
    // transparent for monitoring. Hence the two seed sets PlaybackDefaults / StreamDefaults below.

    /// <summary>Bumped whenever the built-in seed set changes. A settings file whose
    /// <c>SoundPresetsSeedVersion</c> is lower TOPS UP any missing built-ins (by name) exactly once,
    /// so existing installs gain new starting points without duplicating or resurrecting user presets.</summary>
    public const int BuiltInSeedVersion = 1;

    /// <summary>"Electronic" - EDM / House / Techno: already-loud produced masters. A hair of low-end
    /// weight (+0.5 dB) + a gentle golden-curve tilt so the top doesn't fatigue over a long set, plus
    /// light transient definition through the codec. Limiter is set per app in the seed sets.</summary>
    private static DspPreset Electronic => new()
    {
        Name = "Electronic", EqLowGain = 1.06, AutoTiltStrength = 0.25, TransientPunch = 0.15,
    };

    /// <summary>"Hard" - the validated correction for structurally-bright hard genres (hard techno /
    /// hardstyle): a static High -3 dB shelf + golden-curve AutoTilt 0.65 tame the hot 2-16 kHz, and
    /// Limiter <b>Loud</b> pushes it loud/dense so it holds up in a club/stream (Soft = quiet -> you get
    /// out-loudened). Measured 2026-06-17 on 35 tracks from a real library. [[hard-dance-headphone-mode]]</summary>
    public static DspPreset Hard => new()
    {
        Name = "Hard", EqHighGain = 0.72, AutoTiltStrength = 0.65, LimiterMode = "Loud",
    };

    /// <summary>"Rock" - rock / live / organic / vocal: dynamic music that lives on its transients.
    /// Warmth over brightness (+0.5 dB low, -0.5 dB high to ease sibilance), a very gentle tilt, and
    /// more punch for snare/kick life. The limiter stays conservative so the dynamics survive.</summary>
    private static DspPreset Rock => new()
    {
        Name = "Rock", EqLowGain = 1.06, EqHighGain = 0.94, AutoTiltStrength = 0.15, TransientPunch = 0.25,
    };

    /// <summary>"Neutral" - the whole bus chain flat / off (bit-transparent). Identical to the type
    /// defaults; named so it seeds as a recognisable reset starting point.</summary>
    public static DspPreset Neutral => new() { Name = "Neutral" };

    /// <summary>The genre starting points seeded into JUST PLAY (local playback / DJ monitoring): the
    /// limiter stays TRANSPARENT (loudness maximisation is a broadcast concern, not a monitoring one).
    /// Picker order; Neutral is the transparent reset.</summary>
    public static IReadOnlyList<DspPreset> PlaybackDefaults =>
    [
        Electronic with { LimiterMode = "Soft" },
        Hard,                                   // validated Loud
        Rock with { LimiterMode = "Soft" },
        Neutral,
    ];

    /// <summary>The SAME starting points seeded into JUST STREAM (broadcast): identical tonal identity
    /// to <see cref="PlaybackDefaults"/>, but the limiter is one notch LOUDER because a live stream must
    /// stay competitively loud ([[hard-dance-headphone-mode]] / [[roadmap-just-stream]]).</summary>
    public static IReadOnlyList<DspPreset> StreamDefaults =>
    [
        Electronic with { LimiterMode = "Club" },
        Hard,                                   // Loud (validated)
        Rock with { LimiterMode = "Club" },
        Neutral,
    ];
}

/// <summary>
/// Source-generated JSON context for <see cref="DspPreset"/> - reflection-free / trim-AOT-safe,
/// mirroring <see cref="StreamingJsonContext"/>. Provided for parity and for any future code that
/// must (de)serialize presets without reflection (NativeAOT, tests). The main settings file
/// (<c>JsonSettingsService</c>) still uses its own reflection-based options for the whole
/// <see cref="UserSettings"/> graph - the established pattern - and <see cref="DspPreset"/> rides
/// along that path exactly like <see cref="StreamServerProfile"/> does.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(DspPreset))]
[JsonSerializable(typeof(List<DspPreset>))]
public sealed partial class DspPresetJsonContext : JsonSerializerContext;
