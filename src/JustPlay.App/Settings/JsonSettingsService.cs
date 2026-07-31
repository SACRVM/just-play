using System;
using System.IO;
using System.Text.Json;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Storage;

namespace JustPlay.App.Settings;

/// <summary>
/// User-settings persistence in <c>settings.json</c> under the platform's
/// per-user, app-private data directory:
///
///   Windows: <c>%LOCALAPPDATA%\JustPlay\settings.json</c>
///   macOS:   <c>~/Library/Application Support/JustPlay/settings.json</c>
///   Linux:   <c>~/.local/share/JustPlay/settings.json</c>
///
/// Same path on each OS that the BCL would pick for any other reasonable
/// app — no need for OS-specific code. The file is JSON so the user can
/// poke it manually if a tweak gets stuck (it shouldn't, but the option
/// matters for debugging).
///
/// First-run / missing file / corrupt JSON all silently fall back to
/// <see cref="UserSettings.Defaults"/> — preferences are convenience, not
/// data integrity; a parsing crash that prevents the app starting would
/// be a much worse user experience than losing one customisation.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,                   // human-pokeable
        PropertyNameCaseInsensitive = true,     // tolerate hand-edits
    };

    private readonly string _settingsPath;
    private UserSettings _current;

    public JsonSettingsService()
    {
        var dir = JustDataPaths.Combine("JustPlay");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");

        _current = Load();
    }

    public UserSettings Current => _current;

    public void Save(UserSettings settings)
    {
        _current = settings;

        // Atomic write: serialise to a temp file in the same directory, then
        // File.Move(overwrite: true). Avoids the failure mode where a crash
        // mid-write leaves settings.json half-empty and the next startup
        // crashes trying to parse it.
        var tempPath = _settingsPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] save failed: {ex.GetType().Name}: {ex.Message}");
            // Best-effort cleanup so we don't leave .tmp turds behind.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
        }
    }

    private UserSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return UserSettings.Defaults;

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json, Options)
                   ?? UserSettings.Defaults;
        }
        catch (Exception ex)
        {
            // Corrupted file (user hand-edited badly, disk truncated mid-write
            // before atomic rename landed, etc.). Log + fall back to defaults
            // rather than crash on startup. The next Save() overwrites the
            // bad file with valid JSON.
            Console.WriteLine($"[Settings] load failed, using defaults: {ex.GetType().Name}: {ex.Message}");
            return UserSettings.Defaults;
        }
    }
}
