using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Library;

/// <summary>
/// The library index: all <see cref="TrackIndexEntry"/> records keyed by file path.
/// Serialized as a single JSON file; written atomically (write to .tmp, then rename) so an
/// interrupted analyze run does not corrupt it.
///
/// <para>0.6 (THE LIBRARY): moved here from <c>JustPlay.Cli.Index</c> with the schema UNCHANGED.
/// That is deliberate - every index already on disk keeps loading, and the CLI keeps
/// reading and writing the same format the app does. One format, or the divergence is back.</para>
///
/// <para>Resumability: a scan skips any file whose entry still matches on the cheap key
/// (<see cref="TrackIndexEntry.LooksUnchanged"/>) or, failing that, on
/// <see cref="TrackIndexEntry.ContentHash"/> - and which no <see cref="StaleRule"/> rejects.</para>
/// </summary>
public sealed class TrackIndex
{
    /// <summary>
    /// Version of the detection stack baked into new entries - the SAME number the app stamps into
    /// a file's JUSTPLAY blob, so an entry's provenance is one value no matter which producer wrote it.
    ///
    /// <para>History worth keeping: this used to be a private counter stuck at <c>1</c> while the
    /// real version discipline lived in FILENAMES ("sets.v9.index.json"). Measured 2026-07-30, all
    /// 6,561 entries of the "v9" index carried <c>detectionVersion: 1</c>. Entries that old
    /// therefore say 1 and mean "unknown".</para>
    ///
    /// <para>(!) Still do NOT treat a version bump as the re-analysis trigger - it would mark the
    /// whole library stale and re-run thousands of healthy MP3s. Express what actually needs
    /// redoing as a <see cref="StaleRule"/> (e.g. FLAC-only for the mono-decode bug).</para>
    /// </summary>
    public const int CurrentDetectionVersion = Core.Models.TrackAnalysisState.CurrentVersion;

    [JsonPropertyName("entries")]
    public Dictionary<string, TrackIndexEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("lastUpdatedAt")]
    public string LastUpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    // -- Load / Save ----------------------------------------------------------

    /// <summary>
    /// Loads the index from disk, or returns an empty index if the file does not exist.
    /// Throws if the file exists but is malformed.
    /// </summary>
    public static TrackIndex Load(string path)
    {
        if (!File.Exists(path))
            return new TrackIndex();

        var json = File.ReadAllText(path);
        var idx = JsonSerializer.Deserialize(json, LibraryJsonContext.Default.TrackIndex);
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
        var json = JsonSerializer.Serialize(this, LibraryJsonContext.Default.TrackIndex);
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
