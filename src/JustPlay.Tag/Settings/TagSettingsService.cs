using System;
using System.IO;
using System.Text.Json;
using JustPlay.Core.Models;
using JustPlay.Core.Storage;

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
        var dir = JustDataPaths.Combine("JustTag");
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    /// <summary>
    /// The persisted write mode parsed to the enum. An unreadable / unknown value falls back to
    /// <see cref="Id3WriteFormat.KeepFileVersion"/> — a settings file we can't understand must never
    /// be the reason a file gets converted.
    /// </summary>
    public Id3WriteFormat WriteFormat =>
        Enum.TryParse<Id3WriteFormat>(Current.WriteFormat, out var f) ? f : Id3WriteFormat.KeepFileVersion;

    /// <summary>
    /// Raised after <see cref="SetWriteFormat"/> persists a new mode. The editor listens so its
    /// "saving converts this file to ID3v2.x" notice can't go stale while the Settings window is
    /// open — a stale notice is worse than none.
    /// </summary>
    public event EventHandler? WriteFormatChanged;

    /// <summary>Persist a new ID3 write mode and notify listeners.</summary>
    public void SetWriteFormat(Id3WriteFormat format)
    {
        Current.WriteFormat = format.ToString();
        Save();
        WriteFormatChanged?.Invoke(this, EventArgs.Empty);
    }

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
