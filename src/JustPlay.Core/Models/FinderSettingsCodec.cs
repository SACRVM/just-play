using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// (De)serialises <see cref="FinderSettings"/> for <c>finder.settings.json</c>.
/// Source-generated (trim/AOT-safe - the repo stays reflection-free; do NOT copy the
/// reflection-based serialisation from <c>JsonSettingsService</c> here). Parsing is
/// forgiving like the main settings file: absent/corrupt input falls back to
/// <see cref="FinderSettings.Defaults"/> because losing a preference beats crashing
/// at startup.
/// </summary>
public static class FinderSettingsCodec
{
    public static string Serialize(FinderSettings settings) =>
        JsonSerializer.Serialize(settings, FinderSettingsJsonContext.Default.FinderSettings);

    /// <summary>Parse, or fall back to defaults; the seek step is snapped to an allowed value.</summary>
    public static FinderSettings TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return FinderSettings.Defaults;

        try
        {
            var parsed = JsonSerializer.Deserialize(json, FinderSettingsJsonContext.Default.FinderSettings);
            return (parsed ?? FinderSettings.Defaults).Normalized();
        }
        catch (JsonException)
        {
            return FinderSettings.Defaults;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FinderSettings))]
internal sealed partial class FinderSettingsJsonContext : JsonSerializerContext;
