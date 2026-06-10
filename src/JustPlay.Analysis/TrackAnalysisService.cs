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
    // The octave corrector reuses this same decode — no extra I/O required.
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
    private readonly ILoudnessDetector _loudness;
    // Stateless — one instance is fine for the process lifetime.
    private readonly TempoOctaveCorrector _octaveCorrector = new();

    public TrackAnalysisService(IBpmDetector bpm, IAudioDecoder decoder, IKeyDetector key, IEnergyDetector energy, ILoudnessDetector loudness)
    {
        _bpm = bpm;
        _decoder = decoder;
        _key = key;
        _energy = energy;
        _loudness = loudness;
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
            // The same 11 kHz buffer is also used for BPM octave correction — no extra I/O.
            ct.ThrowIfCancellationRequested();
            var energyAudio = _decoder.DecodeMono(filePath, EnergySampleRate, ct);

            // BPM octave correction (half/double-tempo fix via onset autocorrelation +
            // log-Gaussian prior). Runs on the 11 kHz decode — cheap, no extra I/O.
            // Reports a corrected BPM back to the progress even if energy detection fails,
            // so the UI always shows the best available BPM before analysis completes.
            if (bpm is { } rawBpm && energyAudio.Samples?.Length > 0)
            {
                var correctedBpm = _octaveCorrector.Correct(
                    rawBpm, energyAudio.Samples, energyAudio.SampleRate, ct);
                if (Math.Abs(correctedBpm - rawBpm) > 0.01)
                {
                    // Correction was applied — update result so the final stored value is corrected.
                    result = result with { Bpm = correctedBpm };
                }
            }

            // RawEnergyScore: extract continuous score [0,1] alongside the 1-10 integer.
            // Stored so the 1-10 mapping can be re-calibrated later without re-analysis.
            double? rawEnergyScore = null;
            if (energyAudio.Samples?.Length > 0 &&
                _energy is SpectralEnergyDetector sed)
            {
                var ef = sed.ExtractFeatures(energyAudio, ct);
                if (ef is { } features)
                {
                    rawEnergyScore = SpectralEnergyDetector.BlendedScore(features);
                    result = result with
                    {
                        Energy = SpectralEnergyDetector.ScoreFeatures(features),
                        RawEnergyScore = rawEnergyScore,
                    };
                    progress?.Report(result);
                }
                else if (result.Bpm != bpm)
                {
                    progress?.Report(result);
                }
            }
            else if (_energy.Detect(energyAudio, ct) is { } e)
            {
                result = result with { Energy = e };
                progress?.Report(result);
            }
            else if (result.Bpm != bpm)
            {
                // Energy failed but BPM was corrected — report the updated BPM.
                progress?.Report(result);
            }

            // Loudness / ReplayGain — reuses the same 11 kHz buffer, no extra I/O.
            ct.ThrowIfCancellationRequested();
            if (_loudness.Detect(energyAudio, ct) is { } loud)
            {
                var gain = ReplayGain.TrackGainDb(loud.IntegratedLufs);
                result = result with { LoudnessLufs = loud.IntegratedLufs, ReplayGainDb = gain, Peak = loud.Peak };
                progress?.Report(result);
            }

            // Beat fingerprint (Scale Transform + Cyclic Tempogram + DFA danceability).
            // Reuses the same 11 kHz decode already in memory — no extra I/O.
            // The fingerprint is NOT reported as a progress intermediate (it does not update
            // any visible UI column) — it is silently attached to the final result so it
            // round-trips through the blob and is available to HarmonicSort.
            if (energyAudio.Samples?.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var fingerprint = BeatFingerprintExtractor.Extract(
                    energyAudio.Samples, energyAudio.SampleRate, ct);
                if (fingerprint is not null)
                    result = result with { Fingerprint = fingerprint };
            }

            // Rhythm pattern (FourOnFloor, OffbeatEnergy, Swing, Syncopation, HalfTimeFeel,
            // BeatType). Reuses the same 11 kHz decode — no extra I/O. Requires a known BPM;
            // skip if BPM was not detected. Also not a visible progress intermediate.
            if (energyAudio.Samples?.Length > 0 && result.Bpm is { } finalBpm)
            {
                ct.ThrowIfCancellationRequested();
                var rhythm = RhythmPatternDetector.Detect(
                    energyAudio.Samples, energyAudio.SampleRate, finalBpm, ct);
                if (rhythm is not null)
                    result = result with { Rhythm = rhythm };
            }

            // Vibe quartet (punch/groove/dark/hypnotic) + noisy fatigue flag (harshness).
            // Requires: samples, loudness (LUFS + peak), raw energy score, rhythm pattern.
            // Reuses all already-computed values — no extra I/O or decode.
            if (energyAudio.Samples?.Length > 0 &&
                result.LoudnessLufs is { } charLufs &&
                result.Peak is { } charPeak &&
                result.RawEnergyScore is { } charRawScore)
            {
                ct.ThrowIfCancellationRequested();
                var cf = CharacterClassifier.Compute(
                    energyAudio.Samples,
                    energyAudio.SampleRate,
                    result.Bpm,
                    charLufs,
                    charPeak,
                    charRawScore,
                    result.Rhythm,
                    ct);
                if (cf is not null)
                {
                    result = result with
                    {
                        SpectralFlatness = cf.SpectralFlatness,
                        Harshness        = cf.Harshness,
                        BassPunch        = cf.BassPunch,
                        BassGroove       = cf.BassGroove,
                        Dark             = cf.Dark,
                        Hypnotic         = cf.Hypnotic,
                    };
                }
            }

            return result;
        }, ct);
    }
}
