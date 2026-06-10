using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Cli.Index;

/// <summary>
/// The sidecar index: all <see cref="TrackIndexEntry"/> records keyed by file path.
/// Serialized as a single JSON file; designed to be safely written atomically
/// (write to .tmp, then rename) so an interrupted analyze run does not corrupt it.
///
/// <para>Resumability: <see cref="AnalyzeCommand"/> skips any file whose path already
/// has an entry with a matching <see cref="TrackIndexEntry.ContentHash"/> and the
/// current <see cref="CurrentDetectionVersion"/>.</para>
/// </summary>
public sealed class TrackIndex
{
    /// <summary>
    /// Version of the detection stack baked into new entries.
    /// Bump this when the DSP/key/energy logic changes so old entries are re-analysed.
    /// </summary>
    public const int CurrentDetectionVersion = 1;

    [JsonPropertyName("entries")]
    public Dictionary<string, TrackIndexEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("lastUpdatedAt")]
    public string LastUpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    // ── Load / Save ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the index from disk, or returns an empty index if the file does not exist.
    /// Throws if the file exists but is malformed.
    /// </summary>
    public static TrackIndex Load(string path)
    {
        if (!File.Exists(path))
            return new TrackIndex();

        var json = File.ReadAllText(path);
        var idx = JsonSerializer.Deserialize(json, CliJsonContext.Default.TrackIndex);
        return idx ?? new TrackIndex();
    }

    /// <summary>
    /// Saves the index atomically: writes to <c><paramref name="path"/>.tmp</c> then renames.
    /// Safe against mid-write interruption.
    /// </summary>
    public void Save(string path)
    {
        LastUpdatedAt = DateTime.UtcNow.ToString("o");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(this, CliJsonContext.Default.TrackIndex);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Returns true if the entry for <paramref name="filePath"/> is up-to-date:
    /// the content hash matches and the detection version is current.
    /// </summary>
    public bool IsUpToDate(string filePath, string contentHash)
    {
        if (!Entries.TryGetValue(filePath, out var entry))
            return false;
        return entry.ContentHash == contentHash
            && entry.DetectionVersion == CurrentDetectionVersion;
    }
}
