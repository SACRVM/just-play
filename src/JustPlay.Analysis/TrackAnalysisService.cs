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
    // Sample rate for the sample-based detectors. 11025 Hz is plenty for key
    // (highest pitch class we care about is well under its Nyquist) and keeps the
    // decode + FFT cheap.
    private const int AnalysisSampleRate = 11025;

    private readonly IBpmDetector _bpm;
    private readonly IAudioDecoder _decoder;
    private readonly IKeyDetector _key;

    public TrackAnalysisService(IBpmDetector bpm, IAudioDecoder decoder, IKeyDetector key)
    {
        _bpm = bpm;
        _decoder = decoder;
        _key = key;
    }

    public Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisResult>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // BPM first (decodes internally via BASS_FX) and report it immediately
            // so the BPM cell fills while the key analysis runs.
            var bpm = _bpm.Detect(filePath, ct);
            var result = AnalysisResult.Empty with { Bpm = bpm };
            progress?.Report(result);

            // Sample-based detectors share one mono decode.
            ct.ThrowIfCancellationRequested();
            var audio = _decoder.DecodeMono(filePath, AnalysisSampleRate, ct);

            if (_key.Detect(audio, ct) is { } k)
            {
                result = result with { Key = k.Key, KeyConfidence = k.Confidence };
                progress?.Report(result);
            }

            // Future: energy detector runs here on the same decoded audio.
            return result;
        }, ct);
    }
}
