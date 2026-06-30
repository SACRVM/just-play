using System;
using System.IO;
using System.Text.Json;
using JustPlay.Core.Models;

namespace JustPlay.Tag.Settings;

/// <summary>
/// Loads / saves JUST TAG preferences to <c>%LOCALAPPDATA%\JustTag\settings.json</c> (mirrors JUST
/// STREAM's <c>JsonStreamSettingsService</c> shape: a <see cref="Current"/> instance + <see cref="Save"/>).
/// Best-effort: a missing / corrupt / locked file falls back to defaults rather than throwing.
/// </summary>
public sealed class TagSettingsService
{
    private readonly string _path;

    /// <summary>The live settings instance — mutate then call <see cref="Save"/>.</summary>
    public TagSettings Current { get; }

    public TagSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustTag");
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    /// <summary>The persisted write mode parsed to the enum (falls back to the safe default).</summary>
    public Id3WriteFormat WriteFormat =>
        Enum.TryParse<Id3WriteFormat>(Current.WriteFormat, out var f) ? f : Id3WriteFormat.Id3v23Utf16;

    private TagSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), TagSettingsJsonContext.Default.TagSettings)
                       ?? new TagSettings();
        }
        catch { /* corrupt / locked → defaults */ }
        return new TagSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, TagSettingsJsonContext.Default.TagSettings));
        }
        catch { /* best-effort — never crash on a settings write */ }
    }
}
