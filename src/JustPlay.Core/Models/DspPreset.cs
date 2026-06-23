using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// A user-saved Sound-tab bus preset — a name plus the full set of tone-shaping bus values the
/// Sound tab exposes after the 2026-06-17 "blendware cull". Saving captures the CURRENT bus state;
/// applying pushes every field back onto the engine + persistence (mirroring the built-in
/// Hard / Neutral presets in <c>MainWindowViewModel.ApplyHardPreset</c> /
/// <c>ApplyNeutralPreset</c>). [[own-limiter-no-vst]]
///
/// The six captured fields ARE the whole post-cull Sound chain:
///   • <see cref="EqLowGain"/> / <see cref="EqMidGain"/> / <see cref="EqHighGain"/> — 3-band EQ (linear, 1.0 = flat)
///   • <see cref="AutoTiltStrength"/> — adaptive spectral tilt (0..1, 0 = off)
///   • <see cref="TransientPunch"/> — transient designer (0..1, 0 = off)
///   • <see cref="LimiterMode"/> — master limiter mode ("Off"/"Soft"/"Club"/"Loud")
///
/// Plain data model — no behaviour, no IO. A <c>record</c> with init-only (NON-positional)
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

    // ── Built-in starting points ─────────────────────────────────────────────
    // Seeded ONCE into the user's preset list on a fresh install (see
    // MainWindowViewModel — SoundPresetsSeeded), then fully editable / deletable like any other.

    /// <summary>"Hard" — the validated correction for structurally-bright hard genres (hard techno /
    /// hardstyle): a static High −3 dB shelf + golden-curve AutoTilt 0.65 tame the hot 2–16 kHz, and
    /// Limiter <b>Loud</b> pushes it loud/dense so it holds up in a club/stream (Soft = quiet → you get
    /// out-loudened). Measured 2026-06-17 on 35 of Chloe's tracks. [[hard-dance-headphone-mode]]</summary>
    public static DspPreset Hard => new()
    {
        Name = "Hard", EqHighGain = 0.72, AutoTiltStrength = 0.65, LimiterMode = "Loud",
    };

    /// <summary>"Neutral" — the whole bus chain flat / off (bit-transparent). Identical to the type
    /// defaults; named so it seeds as a recognisable reset starting point.</summary>
    public static DspPreset Neutral => new() { Name = "Neutral" };
}

/// <summary>
/// Source-generated JSON context for <see cref="DspPreset"/> — reflection-free / trim-AOT-safe,
/// mirroring <see cref="StreamingJsonContext"/>. Provided for parity and for any future code that
/// must (de)serialize presets without reflection (NativeAOT, tests). The main settings file
/// (<c>JsonSettingsService</c>) still uses its own reflection-based options for the whole
/// <see cref="UserSettings"/> graph — the established pattern — and <see cref="DspPreset"/> rides
/// along that path exactly like <see cref="StreamServerProfile"/> does.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(DspPreset))]
[JsonSerializable(typeof(List<DspPreset>))]
public sealed partial class DspPresetJsonContext : JsonSerializerContext;
