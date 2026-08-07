using System;
using System.IO;
using System.Text.Json;
using JustPlay.Core.Storage;

namespace JustPlay.Stream.Settings;

/// <summary>
/// Loads/saves <see cref="StreamSettings"/> as JSON in <c>%LOCALAPPDATA%\JustStream\settings.json</c>.
/// Mirrors JustPlay's JsonSettingsService pattern: read once on construction, expose
/// <see cref="Current"/>, persist on <see cref="Save"/>. Failures are swallowed (a corrupt or
/// missing file falls back to defaults) so the app always starts.
/// </summary>
public sealed class JsonStreamSettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private bool _reportedSaveFailure;

    public StreamSettings Current { get; private set; }

    /// <summary>
    /// Invoked ONCE with a human message when a settings save fails (disk full, denied path) - so it
    /// surfaces in the log WINDOW instead of dying silently. Same "storage never crashes; worst case
    /// log-window only" rule as <see cref="Logging.SessionLog"/> (Chloe 2026-07-05).
    /// </summary>
    public Action<string>? OnSaveFailed { get; set; }

    public JsonStreamSettingsService()
    {
        var dir = JustDataPaths.Combine("JustStream");
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    private StreamSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<StreamSettings>(json, Options);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Load failed, using defaults: {ex.Message}");
        }
        return new StreamSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            // Never crash on a failed save - the change stays live in memory for this session.
            // Surface the first failure to the log window (once - a full disk would spam otherwise).
            Console.WriteLine($"[Settings] Save failed: {ex.Message}");
            if (!_reportedSaveFailure)
            {
                _reportedSaveFailure = true;
                try { OnSaveFailed?.Invoke($"Settings could not be saved ({ex.Message}) - your changes stay active for this session but may not persist."); }
                catch { /* the error reporter must never become the error */ }
            }
        }
    }
}
