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

    /// <summary>The folder the browser was last in, so a session picks up where it stopped. Null on
    /// a first run, and silently ignored when the path no longer exists (a NAS that is not mounted
    /// yet must not turn into an error on startup).</summary>
    public string? LastFolder { get; set; }

    /// <summary>Visible file-table columns, as <see cref="JustPlay.UI.ViewModels.TrackColumns"/> ids.
    /// Null = "never chosen", which is what makes a first run land on the tagging default instead of on
    /// an empty table (an empty ARRAY is a legitimate choice and is kept).</summary>
    public string[]? Columns { get; set; }

    /// <summary>The sorted column id, or null for the folder's own order.</summary>
    public string? SortColumn { get; set; }

    public bool SortDescending { get; set; }
}

/// <summary>Source-generated JSON context — trim/AOT-safe (no reflection serialization), per the repo's
/// reflection-free goal.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TagSettings))]
internal sealed partial class TagSettingsJsonContext : JsonSerializerContext;
