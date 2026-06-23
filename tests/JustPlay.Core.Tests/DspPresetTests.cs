using System.Collections.Generic;
using System.Text.Json;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Round-trip and backward-compat tests for <see cref="DspPreset"/> and the user-savable
/// <see cref="UserSettings.SoundPresets"/> list. Mirrors <see cref="StreamServerProfileTests"/>:
/// the source-gen context (<see cref="DspPresetJsonContext"/>) is the trim/AOT-safe path; the
/// reflection-based <see cref="JsonSerializerOptions"/> matches exactly what <c>JsonSettingsService</c>
/// uses for the whole <see cref="UserSettings"/> graph (the established settings persistence pattern).
/// </summary>
public class DspPresetTests
{
    // Match exactly what JsonSettingsService uses so the tests exercise the real persistence path.
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void DefaultValues_AreFlatBypass()
    {
        var p = new DspPreset();
        Assert.Equal("Preset", p.Name);
        Assert.Equal(1.0, p.EqLowGain);
        Assert.Equal(1.0, p.EqMidGain);
        Assert.Equal(1.0, p.EqHighGain);
        Assert.Equal(0.0, p.AutoTiltStrength);
        Assert.Equal(0.0, p.TransientPunch);
        Assert.Equal("Off", p.LimiterMode);
    }

    [Fact]
    public void SinglePreset_RoundTrips_ViaSourceGenContext()
    {
        var p = new DspPreset
        {
            Name = "My hardcore",
            EqLowGain = 1.1,
            EqMidGain = 0.95,
            EqHighGain = 0.72,
            AutoTiltStrength = 0.65,
            TransientPunch = 0.3,
            LimiterMode = "Loud",
        };

        var json = JsonSerializer.Serialize(p, DspPresetJsonContext.Default.DspPreset);
        var restored = JsonSerializer.Deserialize(json, DspPresetJsonContext.Default.DspPreset);

        Assert.NotNull(restored);
        Assert.Equal("My hardcore", restored!.Name);
        Assert.Equal(1.1, restored.EqLowGain);
        Assert.Equal(0.95, restored.EqMidGain);
        Assert.Equal(0.72, restored.EqHighGain);
        Assert.Equal(0.65, restored.AutoTiltStrength);
        Assert.Equal(0.3, restored.TransientPunch);
        Assert.Equal("Loud", restored.LimiterMode);
    }

    [Fact]
    public void UserSettings_WithSoundPresets_RoundTrips_ViaReflectionOptions()
    {
        var settings = new UserSettings
        {
            Theme = "Aurora",
            SoundPresets =
            [
                new DspPreset { Name = "Warm", EqLowGain = 1.2, EqHighGain = 0.85, AutoTiltStrength = 0.4, LimiterMode = "Club" },
                new DspPreset { Name = "Flat plus punch", TransientPunch = 0.5 },
            ],
        };

        var json = JsonSerializer.Serialize(settings, SettingsOptions);
        var restored = JsonSerializer.Deserialize<UserSettings>(json, SettingsOptions);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.SoundPresets.Count);

        Assert.Equal("Warm", restored.SoundPresets[0].Name);
        Assert.Equal(1.2, restored.SoundPresets[0].EqLowGain);
        Assert.Equal(0.85, restored.SoundPresets[0].EqHighGain);
        Assert.Equal(0.4, restored.SoundPresets[0].AutoTiltStrength);
        Assert.Equal("Club", restored.SoundPresets[0].LimiterMode);

        Assert.Equal("Flat plus punch", restored.SoundPresets[1].Name);
        Assert.Equal(0.5, restored.SoundPresets[1].TransientPunch);
        Assert.Equal(1.0, restored.SoundPresets[1].EqLowGain); // default preserved
        Assert.Equal("Off", restored.SoundPresets[1].LimiterMode);
    }

    [Fact]
    public void OldSettingsJson_WithoutSoundPresets_LoadsEmptyList_NoException()
    {
        // A settings.json written before U5 has no SoundPresets field — must default to an empty
        // list (not null), with no exception. Built-in presets are unaffected (they live in code).
        const string oldJson = """
            {
                "Theme": "Midnight",
                "LimiterMode": "Soft",
                "EqHighGain": 0.72,
                "AutoTiltStrength": 0.65
            }
            """;

        var settings = JsonSerializer.Deserialize<UserSettings>(oldJson, SettingsOptions);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.SoundPresets);
        Assert.Empty(settings.SoundPresets);
        // Existing Sound fields still load.
        Assert.Equal("Soft", settings.LimiterMode);
        Assert.Equal(0.72, settings.EqHighGain);
        Assert.Equal(0.65, settings.AutoTiltStrength);
    }

    [Fact]
    public void MultiplePresets_RoundTrip_ViaSourceGenList()
    {
        var presets = new List<DspPreset>
        {
            new() { Name = "A", LimiterMode = "Soft" },
            new() { Name = "B", LimiterMode = "Loud", EqHighGain = 0.7 },
        };

        var json = JsonSerializer.Serialize(presets, DspPresetJsonContext.Default.ListDspPreset);
        var restored = JsonSerializer.Deserialize(json, DspPresetJsonContext.Default.ListDspPreset);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("A", restored[0].Name);
        Assert.Equal("Soft", restored[0].LimiterMode);
        Assert.Equal("B", restored[1].Name);
        Assert.Equal(0.7, restored[1].EqHighGain);
    }
}
