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
    // Energy runs at 11025 Hz (cheap, and its calibration is fixed to this rate).
    private const int EnergySampleRate = 11025;
    // Key (HpcpKeyDetector) runs at 44100 Hz: its HPCP harmonic summation needs the upper
    // harmonics that an 11 kHz Nyquist (~5.5 kHz) would cut off. Measured on the GiantSteps
    // ground-truth set, the 44.1 kHz peak-picked HPCP scores MIREX 0.629 / 52% exact vs the
    // old 11 kHz braw chromagram's 0.562 / 41%. The extra decode is worth it for background
    // analysis. See the dj-audio-analysis skill.
    private const int KeySampleRate = 44100;

    private readonly IBpmDetector _bpm;
    private readonly IAudioDecoder _decoder;
    private readonly IKeyDetector _key;
    private readonly IEnergyDetector _energy;

    public TrackAnalysisService(IBpmDetector bpm, IAudioDecoder decoder, IKeyDetector key, IEnergyDetector energy)
    {
        _bpm = bpm;
        _decoder = decoder;
        _key = key;
        _energy = energy;
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

            // Key: decode at 44.1 kHz (HPCP needs the harmonic headroom).
            ct.ThrowIfCancellationRequested();
            var keyAudio = _decoder.DecodeMono(filePath, KeySampleRate, ct);
            if (_key.Detect(keyAudio, ct) is { } k)
            {
                result = result with { Key = k.Key, KeyConfidence = k.Confidence };
                progress?.Report(result);
            }

            // Energy: separate decode at 11.025 kHz (its calibration is fixed to this rate).
            ct.ThrowIfCancellationRequested();
            var energyAudio = _decoder.DecodeMono(filePath, EnergySampleRate, ct);
            if (_energy.Detect(energyAudio, ct) is { } e)
            {
                result = result with { Energy = e };
                progress?.Report(result);
            }

            return result;
        }, ct);
    }
}
