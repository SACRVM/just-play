using System;
using System.IO;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.App.Settings;

/// <summary>
/// PRE CUE FINDER settings persistence in <c>finder.settings.json</c>, next to the
/// main <c>settings.json</c> (<c>%LOCALAPPDATA%\JustPlay\</c> on Windows). Same
/// atomic-write / forgiving-load behaviour as <see cref="JsonSettingsService"/>, but
/// the JSON goes through the source-generated <see cref="FinderSettingsCodec"/> —
/// no reflection serialisation (trim/AOT-safe).
/// </summary>
public sealed class FinderSettingsService : IFinderSettingsService
{
    private readonly string _settingsPath;
    private FinderSettings _current;

    public FinderSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JustPlay");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "finder.settings.json");

        _current = Load();
    }

    public FinderSettings Current => _current;

    public void Save(FinderSettings settings)
    {
        _current = settings;

        // Atomic write: temp file in the same directory, then File.Move(overwrite).
        // A crash mid-write can never leave finder.settings.json half-empty.
        var tempPath = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, FinderSettingsCodec.Serialize(settings));
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FinderSettings] save failed: {ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
        }
    }

    private FinderSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return FinderSettings.Defaults;

        try
        {
            return FinderSettingsCodec.TryParse(File.ReadAllText(_settingsPath));
        }
        catch (Exception ex)
        {
            // IO failure (codec-level corruption is already handled inside TryParse).
            Console.WriteLine($"[FinderSettings] load failed, using defaults: {ex.GetType().Name}: {ex.Message}");
            return FinderSettings.Defaults;
        }
    }
}
