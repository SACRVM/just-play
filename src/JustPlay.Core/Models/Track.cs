namespace JustPlay.Core.Models;

/// <summary>
/// A track the user has thrown into the current session. Lives only in memory —
/// JustPlay keeps no library and remembers nothing between runs.
/// Metadata and analysis are filled in asynchronously after the file is added.
/// </summary>
public sealed class Track
{
    public Track(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

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
