using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Estimates a track's perceived "energy" on the Mixed-In-Key-style 1..10 scale -
/// roughly how danceable / intense it feels, NOT its tempo. MIK's exact formula is
/// proprietary; this blends three features that MIR ties to perceived arousal + groove:
///
/// <list type="bullet">
///   <item><b>Loudness (BS.1770 integrated LUFS)</b> - ITU-R BS.1770-4/-5 K-weighted,
///         gated integrated loudness. Replaces the previous median-RMS->dBFS heuristic.
///         Literature shows loudness<->arousal is weak in music (r~0.16-0.18) but it
///         is still a useful term in the blend (Weninger 2013). [energy-detection.md Sec.EBU R128]</item>
///   <item><b>Onset density (spectral flux)</b> - mean positive spectral flux, normalised
///         by frame magnitude (loudness-independent). MIR consensus ties perceived energy
///         AND groove to rhythmic/onset density. [energy-detection.md Sec.Pipeline]</item>
///   <item><b>Brightness (spectral centroid)</b> - magnitude-weighted mean frequency.
///         Arousal correlates with high-frequency content across genres.
///         [energy-detection.md Sec.Pipeline]</item>
///   <item><b>Groove / dynamics variability (RMS-SD)</b> - standard deviation of
///         short-frame RMS over time. Captures rhythmic variability that is provably
///         independent of absolute loudness (Stupacher et al. 2016, Music Perception).
///         Under-weighting this misrates groove-driven tracks. [energy-detection.md Sec.Grounding]</item>
/// </list>
///
/// <para><b>Loudness implementation (BS.1770 / EBU R128):</b> two-stage K-weighting biquad
/// IIR filter (high-shelf then RLB high-pass), 400 ms blocks at 75% overlap, absolute gate
/// -70 LUFS, relative gate -10 LU. [energy-detection.md Sec.BS.1770 algorithm]</para>
///
/// <para><b>Sample rate:</b> the detector runs at 11 025 Hz (set by
/// <c>TrackAnalysisService.EnergySampleRate</c>). The K-weighting biquad coefficients
/// below were recomputed for EXACTLY this rate via the RBJ audio-EQ-cookbook bilinear
/// transform (they are NOT the 48 kHz table values from the standard - see spec warning).
/// The calibration constant (-0.5899 instead of the spec's -0.691) was adjusted so that
/// a 997 Hz 0 dBFS sine reads -3.01 LUFS at 11 025 Hz. [energy-detection.md Sec.44.1 kHz GOTCHA]</para>
///
/// <para><b>1-10 mapping calibration (DEAM 2026-06-03):</b> blend weights and floor/span
/// are calibrated against the DEAM dataset (1802 excerpts, CC BY-NC, cvml.unige.ch/databases/DEAM)
/// via NNLS regression of normalised features to arousal, then linear LS for the 1-10 floor/span.
/// Spearman(energy, arousal) = +0.550 vs +0.210 before. Archetype sanity: ambient -> 2,
/// mid-dance -> 5-6, hard techno -> 9. See <c>ml/calibrate_energy.py</c>.
/// [energy-detection.md Sec.Grounding 1-10 scale]</para>
///
/// <para>All managed, no NuGet, reflection-free, trim-/AOT-safe.</para>
/// </summary>
public sealed class SpectralEnergyDetector : IEnergyDetector
{
    // ---- Spectral feature pipeline constants (unchanged from original) ----
    private const int FrameSize = 2048;        // ~186 ms at 11025 Hz - fine for flux/centroid
    private const int HopSize   = FrameSize / 2;
    private const double SilenceFloor = 1e-7;

    // ---- Feature normalisation ranges (calibrated against DEAM 2026-06-03, ml/calibrate_energy.py) ----
    // Previous MIK-only calibration only validated centring (mean 7.0, near-zero variance) and
    // was completely wrong for this detector's magnitude-normalised flux at 11025 Hz: the shipped
    // FluxLo=0.010/Hi=0.060 clamps ALL real tracks to nFlux=1.0 (the flux values this pipeline
    // produces are in the 0.12-0.47 range). The calibrated ranges cover:
    //   LUFS: -35 (near-silent/fade) .. -5 (brick-wall EDM master, DEAM's loudest)
    //   Flux: 0.10 (below DEAM p5=0.23) .. 0.50 (above DEAM max=0.47, head-room for EDM density)
    //   Centroid: 300 Hz (sub-bass) .. 3000 Hz (above DEAM max=2659 Hz)
    //   RmsSd: 0.005 (near-constant) .. 0.15 (DEAM max=0.16, highly dynamic)
    // Ranges verified to span [~0, ~1] across all 1802 DEAM tracks; EDM extremes reach the tails.
    private const double LufsLo     = -35.0;
    private const double LufsHi     =  -5.0;
    private const double FluxLo     = 0.100;
    private const double FluxHi     = 0.500;
    private const double CentroidLo =  300.0;   // Hz
    private const double CentroidHi = 3000.0;   // Hz
    private const double RmsSdLo    = 0.005;
    private const double RmsSdHi    = 0.150;

    // ---- Blend weights (NNLS fit to DEAM arousal, non-negative, then RMS-SD floored to 0.10) ----
    // Calibrated 2026-06-03 via NNLS regression of normalised features -> DEAM arousal (n=1802).
    // NNLS result: loud=0.294, flux=0.446, bright=0.260, rmssd=0.000. RMS-SD weight is zero in
    // cross-genre DEAM (negatively correlated there: steady loud classical reads high arousal).
    // For DJ energy/groove, RMS-SD is capped at 0.10 minimum and the other three rescaled.
    // Spearman(calibrated, DEAM arousal) = +0.550 vs shipped +0.210.
    // [energy-detection.md Sec.Grounding the 1-10 scale, ml/calibrate_energy.py]
    private const double WLoud    = 0.2642;
    private const double WFlux    = 0.4015;
    private const double WBright  = 0.2343;
    private const double WRmsSd   = 0.1000;  // groove / dynamics variability (floored; DEAM NNLS=0)

    // ---- 1-10 output mapping (fitted linear map: energy = floor + span * blended_score) ----
    // Calibrated 2026-06-03 via linear LS on DEAM training split: maps blended score -> 1-10 energy
    // so ambient/quiet tracks land ~1-3 and peak-time EDM lands ~8-9. Verified archetypes:
    //   ambient (-28 LUFS, sparse flux, dull)  -> 2     hard techno (-8 LUFS, busy, bright) -> 9
    // [energy-detection.md Sec.Grounding the 1-10 scale, ml/calibrate_energy.py]
    private const double EnergyFloor = 0.8677;
    private const double EnergySpan  = 9.3478;

    /// <summary>
    /// Estimates the track's perceived energy on 1..10. Returns <c>null</c> if the audio
    /// is too short to analyse (fewer than one spectral frame). Returns 1 for silent input.
    /// The <paramref name="audio"/> sample rate MUST match
    /// <c>TrackAnalysisService.EnergySampleRate</c> (11 025 Hz); the K-weighting biquad
    /// coefficients are hard-coded for that rate.
    /// </summary>
    public int? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        var features = ExtractFeatures(audio, ct);
        if (features is null) return null;
        return ScoreFeatures(features.Value);
    }

    /// <summary>
    /// Extracts raw and normalised energy features from <paramref name="audio"/> without
    /// producing the final 1-10 integer output. Used by the <c>--dump-energy-features</c>
    /// console harness to calibrate normalisation ranges and blend weights against the DEAM
    /// arousal dataset. Returns <c>null</c> for audio that is too short or silent.
    /// [calibrate_energy.py, energy-detection.md Sec.Grounding the 1-10 scale]
    /// </summary>
    public EnergyFeatures? ExtractFeatures(DecodedAudio audio, CancellationToken ct = default)
    {
        var samples    = audio.Samples;
        var sampleRate = audio.SampleRate;
        if (samples is null || samples.Length < FrameSize || sampleRate <= 0)
            return null;

        var kWeighted      = Bs1770Loudness.ApplyKWeighting(samples);
        var integratedLufs = Bs1770Loudness.IntegratedLoudnessLufs(kWeighted, ct);

        var (meanFlux, meanCentroid, rmsSd) = ComputeSpectralFeatures(samples, sampleRate, ct);

        if (double.IsNegativeInfinity(integratedLufs) || integratedLufs < -80.0)
            return null;  // silent - skip row in the dump

        var nLoud   = Norm(integratedLufs, LufsLo, LufsHi);
        var nFlux   = Norm(meanFlux,       FluxLo,    FluxHi);
        var nBright = Norm(meanCentroid,   CentroidLo, CentroidHi);
        var nRmsSd  = Norm(rmsSd,          RmsSdLo,   RmsSdHi);

        return new EnergyFeatures(
            RawLufs:     integratedLufs,
            RawFlux:     meanFlux,
            RawCentroid: meanCentroid,
            RawRmsSd:    rmsSd,
            NLoud:       nLoud,
            NFlux:       nFlux,
            NBright:     nBright,
            NRmsSd:      nRmsSd);
    }

    /// <summary>
    /// Blends <paramref name="f"/> into the 1-10 integer energy output using the current
    /// calibrated constants (floor/span). Silent input (null features) maps to 1.
    /// </summary>
    public static int ScoreFeatures(EnergyFeatures f)
    {
        var energy = (int)Math.Round(EnergyFloor + EnergySpan * BlendedScore(f));
        return Math.Clamp(energy, 1, 10);
    }

    /// <summary>
    /// Returns the continuous blended energy score in [0, 1] - the raw value before the
    /// 1-10 integer mapping. Store this alongside <see cref="ScoreFeatures"/> so the
    /// 1-10 scale can be re-calibrated to the DJ's ear later WITHOUT re-analysis.
    /// [energy-detection.md Sec.raw score note]
    /// </summary>
    public static double BlendedScore(EnergyFeatures f)
        => WLoud * f.NLoud + WFlux * f.NFlux + WBright * f.NBright + WRmsSd * f.NRmsSd;

    // -------------------------------------------------------------------------
    // Spectral features: flux, centroid, RMS-SD
    // [energy-detection.md Sec.onset/percussive density, Sec.RMS-SD groove feature]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes mean positive spectral flux, mean spectral centroid, and RMS standard
    /// deviation (groove) over 50%-hop Hann-windowed frames of the raw (un-K-weighted)
    /// signal. Flux is normalised by total frame magnitude (loudness-independent).
    /// </summary>
    private static (double MeanFlux, double MeanCentroid, double RmsSd) ComputeSpectralFeatures(
        float[] samples, int sampleRate, CancellationToken ct)
    {
        var hann    = BuildHannWindow(FrameSize);
        var re      = new float[FrameSize];
        var im      = new float[FrameSize];
        var halfBins = FrameSize / 2;

        var mag     = new double[halfBins];
        var prevMag = new double[halfBins];
        var havePrev = false;

        var rmsList      = new List<double>();
        var fluxSum      = 0.0;
        var fluxCount    = 0;
        var centroidSum  = 0.0;
        var centroidCount = 0;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            ct.ThrowIfCancellationRequested();

            // Time-domain RMS of raw (un-windowed) frame - for RMS-SD groove feature.
            var sumSq = 0.0;
            for (var n = 0; n < FrameSize; n++)
            {
                var s = (double)samples[start + n];
                sumSq += s * s;
            }
            var rms = Math.Sqrt(sumSq / FrameSize);
            if (rms > 0) rmsList.Add(rms);

            // Windowed FFT for spectral features.
            for (var n = 0; n < FrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            var magSum     = 0.0;
            var weightedFreq = 0.0;
            for (var k = 0; k < halfBins; k++)
            {
                var m = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                mag[k]   = m;
                magSum   += m;
                weightedFreq += m * (k * (double)sampleRate / FrameSize);
            }

            if (magSum > 0)
            {
                centroidSum += weightedFreq / magSum;
                centroidCount++;

                if (havePrev)
                {
                    var flux = 0.0;
                    for (var k = 0; k < halfBins; k++)
                    {
                        var diff = mag[k] - prevMag[k];
                        if (diff > 0) flux += diff;
                    }
                    fluxSum += flux / magSum;
                    fluxCount++;
                }

                Array.Copy(mag, prevMag, halfBins);
                havePrev = true;
            }
        }

        var meanFlux     = fluxCount    > 0 ? fluxSum    / fluxCount    : 0.0;
        var meanCentroid = centroidCount > 0 ? centroidSum / centroidCount : 0.0;

        // RMS-SD: standard deviation of frame RMS values - captures rhythmic variability.
        // [energy-detection.md Sec.Grounding: add RMS-SD; Stupacher et al. 2016]
        var rmsSd = RmsStdDev(rmsList);

        return (meanFlux, meanCentroid, rmsSd);
    }

    /// <summary>
    /// Standard deviation of a list of RMS values. Returns 0 for fewer than 2 values.
    /// [energy-detection.md Sec.Grounding: RMS-SD feature; Madison et al. 2011]
    /// </summary>
    private static double RmsStdDev(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        var mean = 0.0;
        foreach (var v in values) mean += v;
        mean /= values.Count;
        var variance = 0.0;
        foreach (var v in values) variance += (v - mean) * (v - mean);
        return Math.Sqrt(variance / values.Count);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static double Norm(double v, double lo, double hi)
        => Math.Clamp((v - lo) / (hi - lo), 0.0, 1.0);

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
