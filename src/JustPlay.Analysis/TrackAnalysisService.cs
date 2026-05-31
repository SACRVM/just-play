using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Fans out one file to the registered detectors and assembles an
/// <see cref="AnalysisResult"/>. Today only the BPM detector is wired up; the
/// key and energy detectors will plug into this same orchestrator once the
/// DSP implementations land.
///
/// Runs entirely off the UI thread (callers <c>await</c> it from a background
/// continuation). Reports partial progress so the UI can show the BPM cell
/// the moment it lands instead of waiting for the rest of the analysis.
/// </summary>
public sealed class TrackAnalysisService : ITrackAnalysisService
{
    private readonly IBpmDetector _bpm;

    public TrackAnalysisService(IBpmDetector bpm)
    {
        _bpm = bpm;
    }

    public Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisResult>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var bpm = _bpm.Detect(filePath, ct);
            var partial = AnalysisResult.Empty with { Bpm = bpm };
            progress?.Report(partial);

            // Future: key + energy detectors run here, each progress-reporting
            // their increment so the queue cells update one by one. The final
            // returned result is the union of all detector outputs.
            return partial;
        }, ct);
    }
}
