using System;
using System.IO;
using System.Text.Json;

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

    public StreamSettings Current { get; private set; }

    public JsonStreamSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JustStream");
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
            Console.WriteLine($"[Settings] Save failed: {ex.Message}");
        }
    }
}
