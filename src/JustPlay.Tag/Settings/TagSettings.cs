using System.Text.Json.Serialization;

namespace JustPlay.Tag.Settings;

/// <summary>
/// Persisted JUST TAG preferences (self-contained; serialized to
/// <c>%LOCALAPPDATA%\JustTag\settings.json</c> by <see cref="TagSettingsService"/>). Kept tiny on
/// purpose — JUST TAG holds no library/playlist state, just look + write behaviour.
/// </summary>
public sealed class TagSettings
{
    /// <summary>Active theme palette name (see <see cref="JustPlay.Core.Theming.Themes"/>). Default Aurora.</summary>
    public string Theme { get; set; } = "Aurora";

    /// <summary>ID3v2 write mode — the <see cref="JustPlay.Core.Models.Id3WriteFormat"/> enum NAME.
    /// Default = KeepFileVersion: converting is a thing you choose, not something Save does to you.</summary>
    public string WriteFormat { get; set; } = "KeepFileVersion";
}

/// <summary>Source-generated JSON context — trim/AOT-safe (no reflection serialization), per the repo's
/// reflection-free goal.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TagSettings))]
internal sealed partial class TagSettingsJsonContext : JsonSerializerContext;
