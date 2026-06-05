using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Detects musical key from decoded audio. The headline DJ feature — implementations
/// are free to evolve (chromagram + key profiles today, ML later) behind this contract.
/// </summary>
public interface IKeyDetector
{
    /// <summary>Returns the key plus a 0..1 confidence, or null if undetectable.</summary>
    (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default);
}

/// <summary>Detects tempo (BPM).</summary>
public interface IBpmDetector
{
    /// <summary>BPM from a file path (some backends decode internally), or null.</summary>
    double? Detect(string filePath, CancellationToken ct = default);
}

/// <summary>Estimates perceived energy on a 1..10 scale.</summary>
public interface IEnergyDetector
{
    int? Detect(DecodedAudio audio, CancellationToken ct = default);
}

/// <summary>
/// Measures BS.1770 / EBU R128 integrated loudness and linear sample peak.
/// Used to compute the ReplayGain 2.0 track gain written to <c>REPLAYGAIN_TRACK_GAIN</c>.
/// </summary>
public interface ILoudnessDetector
{
    /// <summary>
    /// Returns the integrated loudness and linear peak for the decoded audio,
    /// or <c>null</c> if the audio is too short, empty, or silent.
    /// </summary>
    LoudnessResult? Detect(DecodedAudio audio, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the full analysis of one track: decode once, fan out to the detectors,
/// and report progress as partial results land.
/// </summary>
public interface ITrackAnalysisService
{
    Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisResult>? progress = null,
        CancellationToken ct = default);
}
