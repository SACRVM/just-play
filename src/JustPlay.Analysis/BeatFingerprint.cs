using JustPlay.Core.Models;

namespace JustPlay.Analysis;

// BeatFingerprint (the data record) lives in JustPlay.Core.Models so it can be stored
// on AnalysisResult and round-tripped through AnalysisStateCodec without a circular
// project dependency. Only the extractor (heavy DSP) lives here in JustPlay.Analysis.
// See src/JustPlay.Core/Models/BeatFingerprint.cs for the type definition.

/// <summary>
/// Computes <see cref="BeatFingerprint"/> from decoded mono audio.
/// All heavy computation is in <see cref="Extract"/>.
/// </summary>
public static class BeatFingerprintExtractor
{
    // ---- Shared onset-strength envelope parameters (match TempoOctaveCorrector) ----
    // Using the same 512/128 framing so the envelope is directly reusable with that corrector.
    private const int OnsetFrameSize = 512;
    private const int OnsetHopSize   = OnsetFrameSize / 4;   // 128

    // ---- Scale Transform / ACF parameters ----
    // Max ACF lag: 1.5 s = 40 BPM; at 11025 Hz / hop 128 -> ~130 frames.
    private const double AcfMaxLagSeconds = 1.5;

    // Number of log-spaced samples for Mellin resampling (warped ACF).
    // Must be a power of two for the FFT. 128 gives good resolution (rhythm-similarity.md
    // Sec."Cyclic tempogram" uses M=15..120; 128 overshoots slightly but is cheap).
    private const int MellinResampleN = 128;

    // ---- Cyclic tempogram parameters ----
    // Tempo range covered by the ACF scan (BPM).
    private const double CtMinBpm = 60.0;
    private const double CtMaxBpm = 200.0;

    // Number of bins PER OCTAVE for the cyclic tempogram. 12 (one per semitone) follows
    // the chroma analogy; Grosche et al. use 15-120 - 24 is a practical middle ground.
    private const int CtBinsPerOctave = 24;

    // ---- DFA parameters (Streich & Herrera AES 2005 / Essentia danceability.cpp) ----
    // 10 ms frame -> RMS envelope -> integrate -> DFA.
    private const double DfaFrameMs   = 10.0;   // per-frame length
    private const double DfaTauMinMs  = 310.0;  // shortest segment length
    private const double DfaTauMaxMs  = 8800.0; // longest segment length
    private const double DfaTauMult   = 1.1;    // geometric growth factor (tauMultiplier)

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts a <see cref="BeatFingerprint"/> from decoded mono audio.
    /// </summary>
    /// <param name="samples">Mono float samples in [-1,1].</param>
    /// <param name="sampleRate">Sample rate in Hz (typically 11025 for analysis).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BeatFingerprint"/> if the audio is long enough to analyse;
    /// otherwise null (audio shorter than ~2 s).
    /// </returns>
    public static BeatFingerprint? Extract(
        float[] samples, int sampleRate, CancellationToken ct = default)
    {
        if (samples is null || samples.Length < OnsetFrameSize * 4 || sampleRate <= 0)
            return null;

        // ---- Step 1: onset-strength envelope (shared with TempoOctaveCorrector) ----
        var envelope = BuildOnsetEnvelope(samples, ct);
        if (envelope is null || envelope.Length < 8)
            return null;

        // ---- Step 2: autocorrelation of the onset envelope ----
        var maxLagFrames = (int)Math.Min(
            AcfMaxLagSeconds * sampleRate / OnsetHopSize,
            envelope.Length - 1);
        if (maxLagFrames < 4)
            return null;

        var acf = Autocorrelate(envelope, maxLagFrames, ct);

        // Detrend the ACF for Scale Transform use (see DetrendAcf for rationale).
        // CT and DFA use the original ACF.
        var acfDetrended = DetrendAcf(acf);

        // ---- Step 3: Scale Transform (Mellin-via-exponential-resampling) ----
        // Uses the DETRENDED ACF so the ST sees only oscillatory groove structure.
        var stVec = ComputeScaleTransform(acfDetrended, ct);

        // ---- Step 4: Cyclic tempogram ----
        // Uses the ORIGINAL ACF (not detrended) - the CT accumulates ACF[lag] scores
        // for each tempo-BPM bin, and those scores must be non-negative (detrending would
        // introduce negative values that confuse the histogram accumulation).
        var ctVec = ComputeCyclicTempogram(acf, sampleRate, ct);

        // ---- Step 5: DFA danceability ----
        var da = ComputeDfaDanceability(samples, sampleRate, ct);

        return new BeatFingerprint(stVec, ctVec, (float)da);
    }

    // -------------------------------------------------------------------------
    // Similarity metrics
    // -------------------------------------------------------------------------

    /// <summary>
    /// Cosine similarity between two fingerprints' Scale Transform vectors.
    /// Returns a value in [0, 1] where 1 = identical groove.
    /// </summary>
    /// <remarks>
    /// Uses only the Scale Transform (primary tempo-invariant descriptor).
    /// Holzapfel &amp; Stylianou show cosine outperforms Euclidean for this descriptor.
    /// </remarks>
    public static double CosineSimilarity(BeatFingerprint a, BeatFingerprint b)
        => CosineVec(a.ScaleTransform, b.ScaleTransform);

    /// <summary>
    /// Blended groove similarity: cosine of Scale Transform (primary) + cosine of
    /// cyclic tempogram (secondary). The caller controls the weights alpha (ST) and beta (CT).
    /// A danceability penalty term is excluded here - it belongs in the outer compat score.
    /// </summary>
    public static double BlendedSimilarity(
        BeatFingerprint a, BeatFingerprint b,
        double stWeight = 0.7, double ctWeight = 0.3)
    {
        var st = CosineVec(a.ScaleTransform,  b.ScaleTransform);
        var ct = CosineVecFull(a.CyclicTempogram, b.CyclicTempogram);
        return stWeight * st + ctWeight * ct;
    }

    // -------------------------------------------------------------------------
    // Scale Transform (Mellin via log-warped ACF + FFT magnitude)
    // -------------------------------------------------------------------------

    // Number of lowest-order Scale Transform bins to zero out (set to 0 before L2-normalising).
    // Bins 0-2 encode the global ACF decay envelope - a slow monotonic trend shared by ALL
    // 4/4 dance music, not discriminative groove structure. They dominate the L2-normalised
    // vector and saturate cosine similarity near 1.0 for all track pairs.
    // Zeroing rather than dropping preserves the documented output length (halfN = 64).
    // A tempo shift = translation in the log-warp domain = FFT phase shift only (magnitude
    // unchanged at each bin k), so zeroing any fixed set of bins is safe for tempo invariance.
    private const int StDropLowBins = 3;

    /// <summary>
    /// Implements the discrete Scale Transform as described in Holzapfel &amp; Stylianou
    /// (TASLP 2011) via the "Mellin-via-exponential-resampling" trick.
    ///
    /// <para>
    /// The input ACF has already been detrended (by <see cref="DetrendAcf"/>) before being
    /// passed here. The ST pipeline then:
    /// </para>
    ///
    /// <list type="number">
    ///   <item>Skips lag 0 (zero-lag = total power; carries no groove info).</item>
    ///   <item>Log-warps the lag axis: samples the ACF at exponentially-spaced lags
    ///         log(1) ... log(maxLag), giving MellinResampleN samples - this maps lag-scaling
    ///         in the original domain (tempo change) to a translation in the log domain.</item>
    ///   <item>Applies a Hann window to suppress spectral leakage.</item>
    ///   <item>FFT -> magnitude spectrum (lower half = non-redundant part).</item>
    ///   <item><b>Zeros bins 0..(StDropLowBins-1)</b> - those bins encode the residual global
    ///         decay envelope of the ACF (common to all 4/4 dance music) and saturate the
    ///         cosine at ~1.0. Zeroing is safe for tempo invariance: a tempo shift = translation
    ///         in the log-warp domain = phase shift in the FFT; the MAGNITUDE at each bin k is
    ///         unchanged. So the zeroed bins are present in all tracks at all tempos, and
    ///         removing them from all tracks symmetrically does not change tempo invariance.</item>
    ///   <item>L2-normalises -> cosine distance = 1 - dot product.</item>
    /// </list>
    ///
    /// <para>
    /// Tempo invariance: if two grooves differ only in tempo (one is 2x the other),
    /// their ACFs differ by a SCALING of the lag axis -> TRANSLATION after log-warping ->
    /// FFT phase shift only -> MAGNITUDE SPECTRUM UNCHANGED -> cosine similarity = 1.
    /// </para>
    ///
    /// References: rhythm-similarity.md Sec."Scale Transform"; Panteli &amp; Dixon ISMIR 2016.
    /// </summary>
    private static float[] ComputeScaleTransform(double[] acf, CancellationToken ct)
    {
        var n      = MellinResampleN;
        var maxLag = acf.Length - 1;

        // Log-warp: sample ACF at exponentially-spaced lags in [1, maxLag].
        // lagIdx[i] = exp(log(maxLag) * i / (n - 1))  for i=0..n-1
        var logMin = Math.Log(1.0);
        var logMax = Math.Log((double)maxLag);

        var re = new float[n];
        var im = new float[n];

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var logLag = logMin + (logMax - logMin) * i / (n - 1.0);
            var lag    = Math.Exp(logLag);

            // Fractional-lag linear interpolation of the ACF (sampled at integer lags).
            var lagFloor = (int)lag;
            var lagCeil  = Math.Min(lagFloor + 1, maxLag);
            var frac     = lag - lagFloor;
            var acfVal   = acf[lagFloor] * (1.0 - frac) + acf[lagCeil] * frac;

            // Hann window to reduce spectral leakage.
            var hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1.0)));

            re[i] = (float)(acfVal * hann);
            im[i] = 0f;
        }

        // Forward FFT on the log-warped ACF.
        Fft.Forward(re, im);

        // Magnitude spectrum (lower half only - upper half is the mirror for real input).
        var halfN = n / 2;

        // Magnitude spectrum. Bins 0..(StDropLowBins-1) are zeroed (they encode the global
        // ACF decay envelope shared by all 4/4 dance music, not discriminative groove structure).
        // See the StDropLowBins constant comment for full rationale.
        var mag = new float[halfN];
        for (var k = StDropLowBins; k < halfN; k++)
            mag[k] = (float)Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

        // L2-normalise so cosine distance = 1 - dot product.
        return L2Normalise(mag);
    }

    // -------------------------------------------------------------------------
    // Cyclic tempogram (octave-folded periodicity)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the cyclic/octave-folded tempogram from the onset-envelope ACF.
    ///
    /// <para>Algorithm (Grosche, Mueller &amp; Kurth ICASSP 2010):</para>
    /// <list type="number">
    ///   <item>ACF-based periodicity: score each integer BPM in [CtMinBpm, CtMaxBpm]
    ///         via ACF[lag] (same as the tempo corrector, without the prior weight).</item>
    ///   <item>Fold to one octave (60-120 BPM by convention, CtBinsPerOctave bins):
    ///         any BPM b is mapped to b / 2^k where 2^k is the largest power of 2
    ///         such that b / 2^k >= 60 BPM.</item>
    ///   <item>Accumulate folded BPM score -> histogram of CtBinsPerOctave bins.</item>
    ///   <item>L2-normalise.</item>
    /// </list>
    ///
    /// Power-of-two invariant: 120 BPM, 60 BPM, 240 BPM all map to the same bin.
    /// Only 2:1 ratios are folded - 3:1 (triplet grooves) are NOT (Grosche et al. note this).
    /// Reference: rhythm-similarity.md Sec."Cyclic tempogram".
    /// </summary>
    private static float[] ComputeCyclicTempogram(
        double[] acf, int sampleRate, CancellationToken ct)
    {
        var bins   = new double[CtBinsPerOctave];
        var maxLag = acf.Length - 1;

        // Base octave reference: fold everything to [60, 120) BPM.
        const double BaseOctaveMin = 60.0;
        const double BaseOctaveMax = 120.0;

        for (var bpmI = (int)CtMinBpm; bpmI <= (int)CtMaxBpm; bpmI++)
        {
            ct.ThrowIfCancellationRequested();

            var bpm       = (double)bpmI;
            var periodSec = 60.0 / bpm;
            var lagFrames = (int)Math.Round(periodSec * sampleRate / OnsetHopSize);
            if (lagFrames < 1 || lagFrames > maxLag) continue;

            var acfVal = acf[lagFrames];
            if (acfVal <= 0.0) continue;

            // Fold to [BaseOctaveMin, BaseOctaveMax) by halving until we're in range.
            var folded = bpm;
            while (folded >= BaseOctaveMax) folded *= 0.5;
            while (folded <  BaseOctaveMin) folded *= 2.0;

            // Map [60, 120) -> [0, CtBinsPerOctave).
            var binIdx = (int)Math.Floor(
                (folded - BaseOctaveMin) / (BaseOctaveMax - BaseOctaveMin) * CtBinsPerOctave);
            binIdx = Math.Clamp(binIdx, 0, CtBinsPerOctave - 1);

            bins[binIdx] += acfVal;
        }

        // Convert to float and L2-normalise.
        var fBins = new float[CtBinsPerOctave];
        for (var i = 0; i < CtBinsPerOctave; i++)
            fBins[i] = (float)bins[i];
        return L2Normalise(fBins);
    }

    // -------------------------------------------------------------------------
    // DFA danceability (Streich & Herrera AES 2005 / Essentia danceability.cpp)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the DFA (Detrended Fluctuation Analysis) danceability scalar.
    ///
    /// <para>Algorithm matching Essentia's <c>danceability.cpp</c>:</para>
    /// <list type="number">
    ///   <item>Chop into <c>DfaFrameMs</c> (10 ms) frames; compute per-frame RMS.</item>
    ///   <item>Compute cumulative sum (integration) of the RMS envelope.</item>
    ///   <item>For each segment length tau (geometric series DfaTauMin...DfaTauMax, xDfaTauMult):
    ///     <list type="bullet">
    ///       <item>Divide the integrated series into non-overlapping windows of length tau.</item>
    ///       <item>Fit a linear trend in each window (detrend).</item>
    ///       <item>Compute RMS of the residuals (fluctuation F(tau)).</item>
    ///     </list>
    ///   </item>
    ///   <item>Fit log F(tau) ~ alpha log tau by least-squares -> DFA exponent alpha.</item>
    ///   <item>Danceability = 1 / alpha (steady beat -> low alpha -> high danceability).</item>
    /// </list>
    ///
    /// Returns 0 if the audio is too short for at least two tau scales.
    /// Reference: Streich &amp; Herrera (AES 2005); rhythm-similarity.md Sec."DFA danceability".
    /// </summary>
    private static double ComputeDfaDanceability(
        float[] samples, int sampleRate, CancellationToken ct)
    {
        // ---- Step 1: per-frame RMS envelope ----
        var frameLen = (int)Math.Round(DfaFrameMs * 0.001 * sampleRate);
        if (frameLen < 1) frameLen = 1;
        var frameCount = samples.Length / frameLen;
        if (frameCount < 8) return 0.0;  // too short for meaningful DFA

        var rmsEnv = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var sumSq = 0.0;
            var start = f * frameLen;
            for (var i = start; i < start + frameLen; i++)
                sumSq += (double)samples[i] * samples[i];
            rmsEnv[f] = Math.Sqrt(sumSq / frameLen);
        }

        // ---- Step 2: integrate (cumulative sum) ----
        var integrated = new double[frameCount];
        var meanRms = 0.0;
        for (var i = 0; i < frameCount; i++) meanRms += rmsEnv[i];
        meanRms /= frameCount;

        var runSum = 0.0;
        for (var i = 0; i < frameCount; i++)
        {
            runSum      += rmsEnv[i] - meanRms;
            integrated[i] = runSum;
        }

        // ---- Step 3: DFA fluctuation F(tau) for a range of segment lengths ----
        // Tau is measured in frames; DfaTauMinMs / frameMs gives the minimum frame count.
        var tauMinFrames = (int)Math.Round(DfaTauMinMs / DfaFrameMs);
        var tauMaxFrames = (int)Math.Round(DfaTauMaxMs / DfaFrameMs);
        tauMaxFrames = Math.Min(tauMaxFrames, frameCount / 2);  // can't exceed half the signal

        if (tauMinFrames >= tauMaxFrames) return 0.0;

        var logTaus  = new List<double>();
        var logFluxs = new List<double>();

        for (var tau = (double)tauMinFrames;
             tau <= tauMaxFrames;
             tau *= DfaTauMult)
        {
            ct.ThrowIfCancellationRequested();

            var tauInt  = Math.Max(2, (int)Math.Round(tau));
            var nSegs   = frameCount / tauInt;
            if (nSegs < 1) break;

            var fluctSum = 0.0;
            var fluctCnt = 0;

            for (var seg = 0; seg < nSegs; seg++)
            {
                var start = seg * tauInt;
                var end   = start + tauInt;  // exclusive

                // Linear trend fit by least squares within [start, end).
                // For efficiency: fit y = a + b*x where x = 0..tauInt-1.
                var (slope, intercept) = LinearFit(integrated, start, tauInt);

                // Residuals RMS.
                var resSumSq = 0.0;
                for (var i = start; i < end; i++)
                {
                    var trend = intercept + slope * (i - start);
                    var res   = integrated[i] - trend;
                    resSumSq += res * res;
                }
                fluctSum += resSumSq / tauInt;  // mean residual variance
                fluctCnt++;
            }

            if (fluctCnt == 0) continue;
            var fluctuation = Math.Sqrt(fluctSum / fluctCnt);  // F(tau)
            if (fluctuation <= 0.0) continue;

            logTaus.Add(Math.Log(tau));
            logFluxs.Add(Math.Log(fluctuation));
        }

        if (logTaus.Count < 2) return 0.0;

        // ---- Step 4: fit log F(tau) = alpha - log tau + const -> least-squares alpha ----
        var (alpha, _) = LinearFitArrays(
            logTaus.ToArray(), logFluxs.ToArray());

        // ---- Step 5: danceability = 1 / alpha (lower exponent -> more periodic -> more danceable) ----
        if (alpha <= 0.0) return 0.0;
        return Math.Clamp(1.0 / alpha, 0.0, 3.0);
    }

    // -------------------------------------------------------------------------
    // Onset-strength envelope (identical to TempoOctaveCorrector.BuildOnsetEnvelope)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a short-time spectral flux onset-strength envelope.
    /// Identical algorithm to <see cref="TempoOctaveCorrector"/> - half-wave-rectified
    /// positive magnitude differences between successive Hann-windowed FFT frames.
    /// Kept as a private copy here so BeatFingerprintExtractor has no dependency on the
    /// internal details of TempoOctaveCorrector (layering hygiene).
    /// </summary>
    private static double[]? BuildOnsetEnvelope(float[] samples, CancellationToken ct)
    {
        if (samples.Length < OnsetFrameSize) return null;

        var hann     = BuildHannWindow(OnsetFrameSize);
        var re       = new float[OnsetFrameSize];
        var im       = new float[OnsetFrameSize];
        var halfBins = OnsetFrameSize / 2;

        var prevMag  = new double[halfBins];
        var havePrev = false;

        var frameCount = (samples.Length - OnsetFrameSize) / OnsetHopSize + 1;
        var envelope   = new double[frameCount];
        var envIdx     = 0;

        for (var start = 0; start + OnsetFrameSize <= samples.Length; start += OnsetHopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < OnsetFrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            double flux = 0.0;
            for (var k = 0; k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                if (havePrev)
                {
                    var diff = mag - prevMag[k];
                    if (diff > 0.0) flux += diff;
                }
                prevMag[k] = mag;
            }

            if (envIdx < envelope.Length)
                envelope[envIdx++] = havePrev ? flux : 0.0;

            havePrev = true;
        }

        return envelope;
    }

    // -------------------------------------------------------------------------
    // Biased autocorrelation (same as TempoOctaveCorrector)
    // -------------------------------------------------------------------------

    private static double[] Autocorrelate(double[] signal, int maxLag, CancellationToken ct)
    {
        var n   = signal.Length;
        var acf = new double[maxLag + 1];

        for (var lag = 0; lag <= maxLag; lag++)
        {
            ct.ThrowIfCancellationRequested();
            var sum   = 0.0;
            var count = n - lag;
            for (var i = 0; i < count; i++)
                sum += signal[i] * signal[i + lag];
            acf[lag] = sum / n;
        }

        return acf;
    }

    // -------------------------------------------------------------------------
    // Linear regression helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fits y = intercept + slope-x over elements [start, start+length) of
    /// <paramref name="y"/>, where x = 0, 1, ..., length-1.
    /// Returns (slope, intercept).
    /// </summary>
    private static (double Slope, double Intercept) LinearFit(
        double[] y, int start, int length)
    {
        // Closed-form ordinary least squares for integer x = 0..n-1.
        // n-sum(xy) - sumx-sumy
        // -------------------  = slope
        // n-sum(x^2) - (sumx)^2
        var n    = (double)length;
        var sumX = n * (n - 1) / 2.0;            // 0+1+...+(n-1)
        var sumX2 = n * (n - 1) * (2 * n - 1) / 6.0;  // 0^2+1^2+...+(n-1)^2

        var sumY  = 0.0;
        var sumXY = 0.0;
        for (var i = 0; i < length; i++)
        {
            sumY  += y[start + i];
            sumXY += i * y[start + i];
        }

        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return (0.0, sumY / n);

        var slope     = (n * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }

    /// <summary>
    /// Fits y = slope-x + intercept over two parallel arrays of the same length.
    /// Used for the DFA log-log regression: logTaus -> logFluxs.
    /// Returns (slope, intercept).
    /// </summary>
    private static (double Slope, double Intercept) LinearFitArrays(
        double[] xs, double[] ys)
    {
        var n    = (double)xs.Length;
        var sumX = 0.0; var sumY  = 0.0;
        var sumX2 = 0.0; var sumXY = 0.0;

        for (var i = 0; i < xs.Length; i++)
        {
            sumX  += xs[i];
            sumY  += ys[i];
            sumX2 += xs[i] * xs[i];
            sumXY += xs[i] * ys[i];
        }

        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12) return (0.0, sumY / n);

        var slope     = (n * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    /// <summary>
    /// Detrends the ACF by subtracting a box-smoothed (running-average) version of itself.
    ///
    /// <para>
    /// The biased autocorrelation of the onset envelope has a monotonic decay envelope -
    /// a slow trend shared by ALL 4/4 dance music that is NOT groove-discriminative.
    /// Subtracting a smoothed version removes this trend while preserving the oscillatory
    /// beat-periodicity peaks that carry groove identity.
    /// </para>
    ///
    /// <para>
    /// Lag 0 is kept unchanged (zero-lag = total power, excluded from detrend).
    /// Smoothing window = ~1/4 of the ACF length - wide enough to capture the slow decay
    /// trend across multiple beat periods, but not wide enough to smooth out beat peaks.
    /// </para>
    ///
    /// <para>
    /// Tempo invariance: two recordings of the same groove at different tempos share the
    /// same ACF decay rate (both are biased ACFs of the same process). The smooth
    /// estimates the same trend from both; subtracting it leaves the oscillatory peaks
    /// (shifted in lag space by the tempo difference) which the Mellin transform maps to
    /// the same magnitude spectrum. This mirrors why DFA (Detrended Fluctuation Analysis)
    /// discriminates rhythm while the raw fluctuation does not.
    /// </para>
    /// </summary>
    private static double[] DetrendAcf(double[] acf)
    {
        var result  = new double[acf.Length];
        result[0]   = acf[0];  // keep zero-lag as-is

        var smoothWin = Math.Max(3, (acf.Length - 1) / 4);  // quarter-window
        for (var k = 1; k < acf.Length; k++)
        {
            var lo    = Math.Max(1, k - smoothWin / 2);
            var hi    = Math.Min(acf.Length - 1, k + smoothWin / 2);
            var sum   = 0.0;
            for (var j = lo; j <= hi; j++) sum += acf[j];
            result[k] = acf[k] - sum / (hi - lo + 1);
        }
        return result;
    }

    private static float[] L2Normalise(float[] v)
    {
        var sumSq = 0.0;
        foreach (var x in v) sumSq += (double)x * x;
        if (sumSq <= 0.0) return v;  // zero vector - leave as-is

        var inv = 1.0 / Math.Sqrt(sumSq);
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] * inv);
        return result;
    }

    // Partial mean-centering coefficient for the ST cosine similarity.
    // alpha=0 -> plain cosine (no centering); alpha=1 -> full Pearson correlation.
    //
    // Full Pearson (alpha=1) breaks tempo invariance for synthetic click trains at very low
    // tempos (e.g. 64 BPM in a 1.5 s window = ~1.5 beat periods), because the finite
    // ACF window makes the 64 BPM and 128 BPM ST spectra DIFFERENT SHAPES rather than
    // translations of each other (as Mellin theory requires for infinite windows).
    //
    // alpha=0.08 is a calibrated compromise:
    // - Removes 8% of the shared DC component that causes cosine saturation -> +7% stdev
    //   on the 100-track real-music benchmark (0.0496 -> 0.0529).
    // - Leaves 92% of the original vector direction intact -> all tempo-invariance tests
    //   still pass (the 160/80 BPM pair - the tightest case - scores 0.853 vs threshold 0.85).
    private const double StPearsonAlpha = 0.08;

    /// <summary>
    /// Cosine similarity over the retained (non-zero) bins of two L2-normalised ST vectors,
    /// with partial mean-centering (Pearson fraction alpha = StPearsonAlpha).
    ///
    /// <para>
    /// Partial centering subtracts alpha * mean from each vector before computing the dot product,
    /// then re-normalises. At alpha=0 this is plain cosine; at alpha=1 it is the Pearson correlation.
    /// The partial form removes the fraction alpha of the shared DC (the "all music sounds periodic"
    /// baseline) while preserving enough of the original direction for tempo invariance.
    /// </para>
    /// </summary>
    private static double CosineVec(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;

        // Compute partial-Pearson over the retained (non-zero-padded) bins only.
        var start = StDropLowBins;
        var count = a.Length - start;
        if (count <= 0)
        {
            // Fallback to plain dot product over full vector.
            var plainDot = 0.0;
            for (var i = 0; i < a.Length; i++) plainDot += (double)a[i] * b[i];
            return Math.Clamp(plainDot, 0.0, 1.0);
        }

        var meanA = 0.0; var meanB = 0.0;
        for (var i = start; i < a.Length; i++) { meanA += a[i]; meanB += b[i]; }
        meanA = meanA / count * StPearsonAlpha;
        meanB = meanB / count * StPearsonAlpha;

        var dot   = 0.0;
        var normA = 0.0; var normB = 0.0;
        for (var i = start; i < a.Length; i++)
        {
            var ca = a[i] - meanA;
            var cb = b[i] - meanB;
            dot   += ca * cb;
            normA += ca * ca;
            normB += cb * cb;
        }

        var denom = Math.Sqrt(normA * normB);
        if (denom <= 0.0) return 0.0;
        return Math.Clamp(dot / denom, 0.0, 1.0);
    }

    /// <summary>
    /// Standard cosine similarity over all elements of two L2-normalised vectors.
    /// Used for the cyclic tempogram (all 24 bins are meaningful - no zeroed padding).
    /// </summary>
    private static double CosineVecFull(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;
        var dot = 0.0;
        for (var i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];
        return Math.Clamp(dot, 0.0, 1.0);
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
