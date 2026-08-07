namespace JustPlay.Core.Models;

/// <summary>
/// A track the user has thrown into the current session. Lives only in memory -
/// JustPlay keeps no library and remembers nothing between runs.
/// Metadata and analysis are filled in asynchronously after the file is added.
/// </summary>
public sealed class Track
{
    public Track(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; private set; }

    /// <summary>
    /// Point this track at the same audio under a new path, after the user renamed the file in the
    /// tag editor. The identity of a track is the FILE, not the string - a rename must move the
    /// existing row rather than leave it pointing at something that is no longer there (standing
    /// rule: never leave a song behind). Analysis and metadata carry over untouched; nothing about
    /// the audio changed.
    /// </summary>
    public void Relocate(string newFilePath) => FilePath = newFilePath;

    /// <summary>
    /// When the analysis in <see cref="Analysis"/> was actually MEASURED - set to now when our DSP
    /// runs, and carried over from the file's blob when the values were imported instead of computed.
    /// Null when neither is known.
    /// <para>Without this, a write stamps the blob with the moment of WRITING, which is why every
    /// imported track used to look "analysed" at import time and no staleness rule could find the
    /// FLAC mono-decode debt (measured 2026-08-01). The distinction only holds if it is carried from
    /// where the DSP ran, not invented where the tag is written.</para>
    /// </summary>
    public DateTime? AnalysedAtUtc { get; set; }

    /// <summary>Tag information; null until read.</summary>
    public TrackMetadata? Metadata { get; set; }

    /// <summary>Our DSP analysis (BPM/key/energy); null until computed.</summary>
    public AnalysisResult? Analysis { get; set; }

    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.Pending;
}

public enum AnalysisStatus
{
    Pending,
    Running,
    Done,
    Failed
}
