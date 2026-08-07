namespace JustPlay.Analysis;

/// <summary>
/// Shared BS.1770 / EBU R128 K-weighted gated integrated loudness implementation.
/// Used by both <see cref="SpectralEnergyDetector"/> (as part of the energy blend)
/// and <see cref="Bs1770LoudnessDetector"/> (as a standalone loudness measurement).
///
/// <para><b>Sample rate:</b> calibrated for EXACTLY 11 025 Hz
/// (<c>TrackAnalysisService.EnergySampleRate</c>). The K-weighting biquad coefficients
/// were recomputed via the RBJ Audio EQ Cookbook bilinear transform for this rate - do NOT
/// substitute the 48 kHz table values from the BS.1770-5 standard.</para>
///
/// <para><b>Calibration:</b> a full-scale 997 Hz sine at 11 025 Hz reads -3.01 LUFS
/// (the BS.1770 spec-mandated calibration point). The offset constant <c>LufsOffset = -0.5899</c>
/// was chosen to satisfy this; 48 kHz uses -0.691. [energy-detection.md Sec.44.1 kHz GOTCHA]</para>
///
/// <para>Platform-agnostic, reflection-free, trim/AOT-safe.</para>
/// </summary>
internal static class Bs1770Loudness
{
    // ---- BS.1770 loudness block parameters [energy-detection.md Sec.gated integration] ----
    // At 11025 Hz: 400 ms = 4410 samples, 75% overlap -> 100 ms hop = 1102 samples.
    private const int LoudnessBlockSamples = 4410;   // 400 ms @ 11025 Hz
    private const int LoudnessHopSamples   = 1102;   // 100 ms hop (75% overlap)

    // ---- K-weighting biquad coefficients, computed for 11025 Hz ----
    // Stage 1: high-shelf +4 dB, Q = 1/sqrt(2), fc = 1500 Hz [BS.1770-5 Table 1, recalculated]
    // Stage 2: RLB 2nd-order high-pass, fc = 38.13507 Hz      [BS.1770-5 Table 2, recalculated]
    //
    // Bilinear-transform source: RBJ Audio EQ Cookbook (high-shelf / high-pass formulas).
    // Verified: 997 Hz 0 dBFS sine -> mean-square ~ 0.5728, LUFS = -3.01 at this rate.
    //
    // DO NOT substitute the 48 kHz table values from the standard spec - they give a
    // rate-dependent error (~0.1 LU difference at 11025 Hz). [energy-detection.md Sec.44.1 kHz GOTCHA]
    private const double KS1B0 =  1.389132829264160;   // Stage 1 b0
    private const double KS1B1 = -1.334347523892191;   // Stage 1 b1
    private const double KS1B2 =  0.471855655650174;   // Stage 1 b2
    private const double KS1A1 = -0.744739880908181;   // Stage 1 a1
    private const double KS1A2 =  0.271380841930324;   // Stage 1 a2

    private const double KS2B0 =  0.984749705922840;   // Stage 2 b0
    private const double KS2B1 = -1.969499411845681;   // Stage 2 b1
    private const double KS2B2 =  0.984749705922840;   // Stage 2 b2
    private const double KS2A1 = -1.969266826852296;   // Stage 2 a1
    private const double KS2A2 =  0.969731996839065;   // Stage 2 a2

    // ---- BS.1770 LUFS formula: L_K = Offset + 10-log10(mean-square) ----
    // The standard uses -0.691 at 48 kHz. At 11025 Hz the K-weight gain at 997 Hz
    // differs slightly (~0.59 dB vs 0.66 dB), so we use a rate-adjusted constant that
    // keeps the 997 Hz calibration point at exactly -3.01 LUFS. [energy-detection.md Sec.44.1 kHz GOTCHA]
    private const double LufsOffset = -0.5899;

    // ---- Gating thresholds [energy-detection.md Sec.gated integration] ----
    private const double AbsoluteGateLufs  = -70.0;   // ITU-R BS.1770 absolute gate
    private const double RelativeGateDelta = -10.0;   // relative gate: 10 LU below ungated mean

    /// <summary>
    /// Computes the BS.1770 K-weighted gated integrated loudness (LUFS) for the given
    /// mono float samples decoded at 11 025 Hz.
    /// Returns <c>double.NegativeInfinity</c> for silent or below-gate input.
    /// </summary>
    /// <param name="samples">Mono float samples at 11 025 Hz, [-1, 1].</param>
    /// <param name="ct">Cancellation token checked on each gating block.</param>
    public static double IntegratedLufs(float[] samples, CancellationToken ct)
    {
        var kWeighted = ApplyKWeighting(samples);
        return IntegratedLoudnessLufs(kWeighted, ct);
    }

    // -------------------------------------------------------------------------
    // K-weighting - two cascaded biquad IIR filters (direct form II transposed)
    // [energy-detection.md Sec.K-weighting pre-filter, Sec.BS.1770 EXACT biquad coefficients]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies the BS.1770 K-weighting filter chain to <paramref name="input"/> and
    /// returns the filtered signal as doubles. Uses direct-form II transposed IIR to
    /// minimise accumulated floating-point error. The two biquad stages are cascaded
    /// in one pass to avoid allocating an intermediate buffer.
    /// </summary>
    internal static double[] ApplyKWeighting(float[] input)
    {
        var n    = input.Length;
        var out_ = new double[n];

        // Stage 1 state (direct-form II transposed)
        double s1w1 = 0.0, s1w2 = 0.0;
        // Stage 2 state
        double s2w1 = 0.0, s2w2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            var x = (double)input[i];

            // ---- Stage 1: high-shelf (+4 dB, Q=1/sqrt2, fc=1500 Hz) ----
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
    // [energy-detection.md Sec.gated integration]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes BS.1770 integrated loudness in LUFS from a K-weighted mono signal.
    /// Uses 400 ms blocks with 75% overlap (100 ms hop), absolute gate -70 LUFS, and
    /// relative gate -10 LU. Returns <c>double.NegativeInfinity</c> for silent input.
    /// </summary>
    internal static double IntegratedLoudnessLufs(double[] kWeighted, CancellationToken ct)
    {
        var total = kWeighted.Length;
        if (total < LoudnessBlockSamples)
        {
            // Too short for even one block - compute single-block loudness without gating.
            var ms0 = MeanSquare(kWeighted, 0, total);
            return ms0 <= 0 ? double.NegativeInfinity : LufsOffset + 10.0 * Math.Log10(ms0);
        }

        // ---- Pass 1: collect block mean-squares, apply absolute gate ----
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
        // Ungated mean -> relative threshold -> keep blocks above threshold -> average.
        var ungatedMean = 0.0;
        foreach (var m in blockMs) ungatedMean += m;
        ungatedMean /= blockMs.Count;

        var ungatedLufs  = LufsOffset + 10.0 * Math.Log10(ungatedMean);
        var relThreshold = ungatedLufs + RelativeGateDelta;

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
            return ungatedLufs;  // all blocks gated out - return ungated mean as fallback

        var gatedMean = gatedSum / gatedCount;
        return LufsOffset + 10.0 * Math.Log10(gatedMean);
    }

    /// <summary>Mean of squared samples in [<paramref name="start"/>, start + <paramref name="length"/>).</summary>
    internal static double MeanSquare(double[] buf, int start, int length)
    {
        var sum = 0.0;
        var end = start + length;
        for (var i = start; i < end; i++)
            sum += buf[i] * buf[i];
        return sum / length;
    }
}
