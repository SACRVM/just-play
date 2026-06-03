using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Estimates a track's perceived "energy" on the Mixed-In-Key-style 1..10 scale —
/// roughly how danceable / intense it feels, NOT its tempo. MIK's exact formula is
/// proprietary; this blends three features that MIR ties to perceived arousal + groove:
///
/// <list type="bullet">
///   <item><b>Loudness (BS.1770 integrated LUFS)</b> — ITU-R BS.1770-4/-5 K-weighted,
///         gated integrated loudness. Replaces the previous median-RMS→dBFS heuristic.
///         Literature shows loudness↔arousal is weak in music (r≈0.16–0.18) but it
///         is still a useful term in the blend (Weninger 2013). [energy-detection.md §EBU R128]</item>
///   <item><b>Onset density (spectral flux)</b> — mean positive spectral flux, normalised
///         by frame magnitude (loudness-independent). MIR consensus ties perceived energy
///         AND groove to rhythmic/onset density. [energy-detection.md §Pipeline]</item>
///   <item><b>Brightness (spectral centroid)</b> — magnitude-weighted mean frequency.
///         Arousal correlates with high-frequency content across genres.
///         [energy-detection.md §Pipeline]</item>
///   <item><b>Groove / dynamics variability (RMS-SD)</b> — standard deviation of
///         short-frame RMS over time. Captures rhythmic variability that is provably
///         independent of absolute loudness (Stupacher et al. 2016, Music Perception).
///         Under-weighting this misrates groove-driven tracks. [energy-detection.md §Grounding]</item>
/// </list>
///
/// <para><b>Loudness implementation (BS.1770 / EBU R128):</b> two-stage K-weighting biquad
/// IIR filter (high-shelf then RLB high-pass), 400 ms blocks at 75% overlap, absolute gate
/// −70 LUFS, relative gate −10 LU. [energy-detection.md §BS.1770 algorithm]</para>
///
/// <para><b>Sample rate:</b> the detector runs at 11 025 Hz (set by
/// <c>TrackAnalysisService.EnergySampleRate</c>). The K-weighting biquad coefficients
/// below were recomputed for EXACTLY this rate via the RBJ audio-EQ-cookbook bilinear
/// transform (they are NOT the 48 kHz table values from the standard — see spec warning).
/// The calibration constant (−0.5899 instead of the spec's −0.691) was adjusted so that
/// a 997 Hz 0 dBFS sine reads −3.01 LUFS at 11 025 Hz. [energy-detection.md §44.1 kHz GOTCHA]</para>
///
/// <para><b>1–10 mapping calibration caveat:</b> the arousal/groove literature confirms our
/// feature family (loud + flux + centroid + RMS-SD) is correct (Griffiths 2021, JNMR;
/// Madison et al. 2011). The BLEND WEIGHTS and the floor/span mapping below are
/// heuristic; they were calibrated against a single-genre MIK reference crate that rated
/// mostly 6–7 (mean 7.0, near-zero variance — validates centring, not spread). Final
/// numeric calibration against a reference set spanning ambient→peak-time (e.g. DEAM:
/// cvml.unige.ch/databases/DEAM) is a follow-up. [energy-detection.md §Grounding 1–10 scale]</para>
///
/// <para>All managed, no NuGet, reflection-free, trim-/AOT-safe.</para>
/// </summary>
public sealed class SpectralEnergyDetector : IEnergyDetector
{
    // ---- Spectral feature pipeline constants (unchanged from original) ----
    private const int FrameSize = 2048;        // ~186 ms at 11025 Hz — fine for flux/centroid
    private const int HopSize   = FrameSize / 2;
    private const double SilenceFloor = 1e-7;

    // ---- BS.1770 / EBU R128 loudness block parameters [energy-detection.md §gated integration] ----
    // At 11025 Hz: 400 ms = 4410 samples, 75% overlap → 100 ms hop = 1102 samples.
    private const int   LoudnessBlockSamples = 4410;   // 400 ms @ 11025 Hz
    private const int   LoudnessHopSamples   = 1102;   // 100 ms hop (75% overlap)

    // ---- K-weighting biquad coefficients, computed for 11025 Hz ----
    // Stage 1: high-shelf +4 dB, Q = 1/sqrt(2), fc = 1500 Hz [BS.1770-5 Table 1, recalculated]
    // Stage 2: RLB 2nd-order high-pass, fc = 38.13507 Hz      [BS.1770-5 Table 2, recalculated]
    //
    // Bilinear-transform source: RBJ Audio EQ Cookbook (high-shelf / high-pass formulas).
    // Verified: 997 Hz 0 dBFS sine → mean-square ≈ 0.5728, LUFS = −3.01 at this rate.
    //
    // DO NOT substitute the 48 kHz table values from the standard spec — they give a
    // rate-dependent error (~0.1 LU difference at 11025 Hz). [energy-detection.md §44.1 kHz GOTCHA]
    private const double KS1B0 =  1.389132829264160;   // Stage 1 b0
    private const double KS1B1 = -1.334347523892191;   // Stage 1 b1
    private const double KS1B2 =  0.471855655650174;   // Stage 1 b2
    private const double KS1A1 = -0.744739880908181;   // Stage 1 a1 (sign convention: y = b0 x + b1 x1 + b2 x2 - a1 y1 - a2 y2)
    private const double KS1A2 =  0.271380841930324;   // Stage 1 a2

    private const double KS2B0 =  0.984749705922840;   // Stage 2 b0
    private const double KS2B1 = -1.969499411845681;   // Stage 2 b1
    private const double KS2B2 =  0.984749705922840;   // Stage 2 b2
    private const double KS2A1 = -1.969266826852296;   // Stage 2 a1
    private const double KS2A2 =  0.969731996839065;   // Stage 2 a2

    // ---- BS.1770 LUFS formula: L_K = Offset + 10·log10(mean-square) ----
    // The standard uses −0.691 at 48 kHz. At 11025 Hz the K-weight gain at 997 Hz
    // differs slightly (~0.59 dB vs 0.66 dB), so we use a rate-adjusted constant that
    // keeps the 997 Hz calibration point at exactly −3.01 LUFS. [energy-detection.md §44.1 kHz GOTCHA]
    private const double LufsOffset = -0.5899;

    // ---- Gating thresholds [energy-detection.md §gated integration] ----
    private const double AbsoluteGateLufs  = -70.0;   // ITU-R BS.1770 absolute gate
    private const double RelativeGateDelta = -10.0;   // relative gate: 10 LU below ungated mean

    // ---- Feature normalisation ranges (loudness range recalibrated to LUFS) ----
    // Loudness: typical integrated LUFS range for music. −23 LUFS = EBU R128 target
    // (broadcast norm, quite soft); −6 LUFS = heavily brick-wall limited dance master.
    // CALIBRATION CAVEAT: see class doc. Until a spread-varied reference set exists,
    // the floor/span mapping below centres the output on ~7 for typical dance material.
    private const double LufsLo    = -23.0;
    private const double LufsHi    =  -6.0;
    private const double FluxLo    = 0.010;
    private const double FluxHi    = 0.060;
    private const double CentroidLo = 800.0;    // Hz
    private const double CentroidHi = 3000.0;   // Hz
    // RMS-SD (groove): normalised over typical range. 0 = perfectly steady signal;
    // ~0.25 = highly dynamic material with strong rhythmic accent. [energy-detection.md §Grounding]
    private const double RmsSdLo    = 0.00;
    private const double RmsSdHi    = 0.25;

    // ---- Blend weights [energy-detection.md §defensible energy algorithm] ----
    // Literature guidance: loudness↔music-arousal is weak (r≈0.16); flux and RMS-SD are
    // stronger groove predictors (Madison et al. 2011). Centroid is a lighter tint.
    // CALIBRATION NOTE: these weights are a defensible heuristic pending DEAM calibration.
    private const double WLoud    = 0.25;
    private const double WFlux    = 0.40;
    private const double WBright  = 0.15;
    private const double WRmsSd   = 0.20;  // groove / dynamics variability

    // ---- 1–10 output mapping ----
    // Dance full-mixes saturate the features (typical blended score ≈ 0.85+), so
    // a plain 1 + 9·score biases high. floor 2.5 / span 5.0 centres typical material
    // near 7 and preserves headroom toward 10 for the genuinely hot and 1 for silence.
    private const double EnergyFloor = 2.5;
    private const double EnergySpan  = 5.0;

    /// <summary>
    /// Estimates the track's perceived energy on 1..10. Returns <c>null</c> if the audio
    /// is too short to analyse (fewer than one spectral frame). Returns 1 for silent input.
    /// The <paramref name="audio"/> sample rate MUST match
    /// <c>TrackAnalysisService.EnergySampleRate</c> (11 025 Hz); the K-weighting biquad
    /// coefficients are hard-coded for that rate.
    /// </summary>
    public int? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        var samples   = audio.Samples;
        var sampleRate = audio.SampleRate;
        if (samples is null || samples.Length < FrameSize || sampleRate <= 0)
            return null;

        // ---- 1. K-weight the whole signal (two cascaded biquads) ----
        // [energy-detection.md §K-weighting pre-filter]
        var kWeighted = ApplyKWeighting(samples);

        // ---- 2. BS.1770 gated integrated loudness ----
        // [energy-detection.md §gated integration]
        var integratedLufs = IntegratedLoudnessLufs(kWeighted, ct);

        // ---- 3. Spectral features (flux, centroid) + RMS-SD on raw samples ----
        // [energy-detection.md §onset/percussive density, §RMS-SD groove feature]
        var (meanFlux, meanCentroid, rmsSd) = ComputeSpectralFeatures(samples, sampleRate, ct);

        // ---- 4. Silence guard ----
        if (double.IsNegativeInfinity(integratedLufs) || integratedLufs < -80.0)
            return 1;

        // ---- 5. Blend into 1..10 ----
        // [energy-detection.md §1–10 energy mapping]
        var nLoud   = Norm(integratedLufs, LufsLo, LufsHi);
        var nFlux   = Norm(meanFlux,       FluxLo,    FluxHi);
        var nBright = Norm(meanCentroid,   CentroidLo, CentroidHi);
        var nRmsSd  = Norm(rmsSd,          RmsSdLo,   RmsSdHi);

        var score  = WLoud * nLoud + WFlux * nFlux + WBright * nBright + WRmsSd * nRmsSd;
        var energy = (int)Math.Round(EnergyFloor + EnergySpan * score);
        return Math.Clamp(energy, 1, 10);
    }

    // -------------------------------------------------------------------------
    // K-weighting — two cascaded biquad IIR filters (direct form II transposed)
    // [energy-detection.md §K-weighting pre-filter, §BS.1770 EXACT biquad coefficients]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies the BS.1770 K-weighting filter chain to <paramref name="input"/> and
    /// returns the filtered signal. Uses direct-form II transposed IIR to minimise
    /// accumulated floating-point error. The two biquad stages are cascaded in one pass
    /// to avoid allocating an intermediate buffer.
    /// </summary>
    private static double[] ApplyKWeighting(float[] input)
    {
        var n   = input.Length;
        var out_ = new double[n];

        // Stage 1 state (direct-form II transposed: w1 = feedforward delay 1, w2 = delay 2)
        double s1w1 = 0.0, s1w2 = 0.0;
        // Stage 2 state
        double s2w1 = 0.0, s2w2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            var x = (double)input[i];

            // ---- Stage 1: high-shelf (+4 dB, Q=1/√2, fc=1500 Hz) ----
            var y1 = KS1B0 * x + s1w1;
            s1w1 = KS1B1 * x - KS1A1 * y1 + s1w2;
            s1w2 = KS1B2 * x - KS1A2 * y1;

            // ---- Stage 2: RLB high-pass (fc=38.13507 Hz) ----
            var y2 = KS2B0 * y1 + s2w1;
            s2w1 = KS2B1 * y1 - KS2A1 * y2 + s2w2;
            s2w2 = KS2B2 * y1 - KS2A2 * y2;

            out_[i] = y2;
        }

        return out_;
    }

    // -------------------------------------------------------------------------
    // Gated integrated loudness (BS.1770 / EBU R128)
    // [energy-detection.md §gated integration]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the BS.1770 integrated loudness in LUFS from a K-weighted mono signal.
    /// Uses 400 ms blocks with 75% overlap (100 ms hop), absolute gate −70 LUFS, and
    /// relative gate −10 LU. Returns <c>double.NegativeInfinity</c> for silent input.
    /// </summary>
    private static double IntegratedLoudnessLufs(double[] kWeighted, CancellationToken ct)
    {
        var total = kWeighted.Length;
        if (total < LoudnessBlockSamples)
        {
            // Too short for even one block — compute single-block loudness without gating.
            var ms0 = MeanSquare(kWeighted, 0, total);
            return ms0 <= 0 ? double.NegativeInfinity : LufsOffset + 10.0 * Math.Log10(ms0);
        }

        // --- Pass 1: collect block mean-squares, apply absolute gate ----
        var blockMs = new List<double>();
        for (var start = 0; start + LoudnessBlockSamples <= total; start += LoudnessHopSamples)
        {
            ct.ThrowIfCancellationRequested();
            var ms = MeanSquare(kWeighted, start, LoudnessBlockSamples);
            if (ms <= 0) continue;
            var lk = LufsOffset + 10.0 * Math.Log10(ms);
            if (lk >= AbsoluteGateLufs)
                blockMs.Add(ms);
        }

        if (blockMs.Count == 0)
            return double.NegativeInfinity;

        // ---- Pass 2: relative gate ----
        // Ungated mean → relative threshold → keep blocks above threshold → average.
        var ungatedMean = 0.0;
        foreach (var m in blockMs) ungatedMean += m;
        ungatedMean /= blockMs.Count;

        var ungatedLufs = LufsOffset + 10.0 * Math.Log10(ungatedMean);
        var relThreshold = ungatedLufs + RelativeGateDelta;      // e.g. −20 LU for a −10 LUFS track

        var gatedSum   = 0.0;
        var gatedCount = 0;
        foreach (var m in blockMs)
        {
            var lk = LufsOffset + 10.0 * Math.Log10(m);
            if (lk >= relThreshold)
            {
                gatedSum += m;
                gatedCount++;
            }
        }

        if (gatedCount == 0)
            return ungatedLufs;  // all blocks gated out — return ungated mean as fallback

        var gatedMean = gatedSum / gatedCount;
        return LufsOffset + 10.0 * Math.Log10(gatedMean);
    }

    /// <summary>Mean of squared samples in [<paramref name="start"/>, start + <paramref name="length"/>).</summary>
    private static double MeanSquare(double[] buf, int start, int length)
    {
        var sum = 0.0;
        var end = start + length;
        for (var i = start; i < end; i++)
            sum += buf[i] * buf[i];
        return sum / length;
    }

    // -------------------------------------------------------------------------
    // Spectral features: flux, centroid, RMS-SD
    // [energy-detection.md §onset/percussive density, §RMS-SD groove feature]
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

            // Time-domain RMS of raw (un-windowed) frame — for RMS-SD groove feature.
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

        // RMS-SD: standard deviation of frame RMS values — captures rhythmic variability.
        // [energy-detection.md §Grounding: add RMS-SD; Stupacher et al. 2016]
        var rmsSd = RmsStdDev(rmsList);

        return (meanFlux, meanCentroid, rmsSd);
    }

    /// <summary>
    /// Standard deviation of a list of RMS values. Returns 0 for fewer than 2 values.
    /// [energy-detection.md §Grounding: RMS-SD feature; Madison et al. 2011]
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
