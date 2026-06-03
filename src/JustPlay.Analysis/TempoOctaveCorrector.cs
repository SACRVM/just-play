namespace JustPlay.Analysis;

/// <summary>
/// Post-processes a raw BPM estimate from BASS_FX to correct half/double-tempo
/// (octave) errors using an Ellis-style onset-strength autocorrelation combined
/// with a log-Gaussian tempo prior.
///
/// <para><b>Algorithm (Ellis 2007 §3.1 + librosa parameterisation) — hybrid ACF scan:</b></para>
/// <list type="number">
///   <item>
///     <b>Onset-strength envelope:</b> compute a short-time spectral flux envelope
///     over the decoded audio using power-of-two FFT frames at ~8–11 kHz.
///     We use the audio already decoded for energy/key analysis (typically 11 025 Hz)
///     to avoid an extra decode. Each frame produces a half-wave-rectified spectral
///     flux value — the sum of positive magnitude differences across bins.
///   </item>
///   <item>
///     <b>Autocorrelation:</b> compute the biased autocorrelation of the onset
///     envelope up to the maximum lag corresponding to 40 BPM (i.e. 1.5 s period).
///   </item>
///   <item>
///     <b>Log-Gaussian tempo prior:</b> weight the autocorrelation by a Gaussian
///     centred on τ₀ = 0.5 s (120 BPM) on a log-time axis:
///     <c>W(τ) = exp(−½ · (log₂(τ/τ₀) / σ_τ)²)</c>.
///     Parameters: τ₀ = 0.5 s → 120 BPM; σ_τ = 0.9 octaves (Ellis/librosa default).
///   </item>
///   <item>
///     <b>Hybrid candidate set:</b> union of
///     (a) BASS raw octave multiples {raw/4, raw/3, raw/2, raw, raw×2, raw×3, raw×4} and
///     (b) the top ACF-scan peaks from a full sweep over <see cref="MinFundamentalBpm"/>–
///         <see cref="MaxFundamentalBpm"/>. This means a true tempo of 155 BPM that is
///         not a clean multiple of a broken raw (e.g. raw=31 from BASS_FX's out-of-range
///         return) can still win via the ACF scan, independent of the raw value.
///   </item>
///   <item>
///     <b>Conservative correction:</b> if BASS raw is in range AND its ACF score is within
///     <see cref="MinScoreMargin"/> of the winner, prefer staying at raw (don't flip
///     confident-correct tracks). Only override when the winner clearly dominates
///     (≥<see cref="MinScoreMargin"/> better) OR when raw is out of the fundamental range
///     / has near-zero ACF.
///   </item>
/// </list>
///
/// <para>All managed, no NuGet, reflection-free, trim-/AOT-safe. No BASS or Avalonia
/// references — lives entirely in <c>JustPlay.Analysis</c>.</para>
/// </summary>
public sealed class TempoOctaveCorrector
{
    // ---- Onset envelope FFT parameters ----
    private const int OnsetFrameSize = 512;
    private const int OnsetHopSize   = OnsetFrameSize / 4;

    // ---- Prior parameters (Ellis 2007 / librosa parameterisation) ----
    private const double PriorCentreSeconds = 0.5;   // 120 BPM
    private const double PriorSigmaOctaves  = 0.9;

    // ---- Fundamental BPM range for the ACF scan (step 4b) ----
    // The scan sweeps every integer BPM in this range independently of rawBPM.
    // Floor 70 / ceiling 200 covers all standard EDM genres (hard house to tech-house).
    private const double MinFundamentalBpm = 70.0;
    private const double MaxFundamentalBpm = 200.0;

    // ---- Candidate BPM search range (guards for both sources of candidates) ----
    private const double MinCandidateBpm = 40.0;
    private const double MaxCandidateBpm = 240.0;

    // ---- BASS_FX native range — used for the conservative guard ----
    // BASS_FX's BPMDecodeGet returns values only in 45..230 BPM (MinMaxBPM=0 default).
    // A rawBPM outside this window is a gross detector error (e.g. 31 BPM for a 155 BPM
    // track). We do NOT defend gross-error raws with the conservative margin guard — only
    // in-range raws get the "confident correct" protection.
    private const double BassNativeMinBpm = 45.0;
    private const double BassNativeMaxBpm = 230.0;

    // ---- Raw octave multipliers tried (4a candidates) ----
    // Includes /4.../×4 so even a raw=31 "fifth of 155" can reach 124 (×4) and the
    // ACF scan will find 155 directly anyway. The wide set is cheap because we're
    // just evaluating named lags in an already-computed ACF.
    private static readonly double[] RawMultipliers = [0.25, 1.0/3.0, 0.5, 1.0, 2.0, 3.0, 4.0];

    // ---- Conservative-correction threshold ----
    // An alternate must outscore the raw by at least this fractional margin.
    // 0.15 (15%): calibrated so that confident in-range tracks are not flipped by
    // the prior alone, but a dominant ACF peak at a non-multiple tempo can win.
    private const double MinScoreMargin = 0.15;

    // ---- Maximum lag (in seconds) ----
    private const double MaxLagSeconds = 1.5;  // 40 BPM period

    // ---- Number of top ACF-scan peaks to add to the candidate set ----
    // Taking the top 3 independent peaks from the full scan is robust: one of them
    // will typically correspond to the true tempo even when rawBPM is grossly wrong.
    private const int AcfScanTopPeaks = 3;

    /// <summary>
    /// Applies octave correction to a raw BPM estimate.
    /// </summary>
    /// <param name="rawBpm">Raw BPM from BASS_FX. Returned unchanged on failure.</param>
    /// <param name="samples">Decoded mono float samples (any sample rate ≥ 8000 Hz).</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The best candidate BPM from the hybrid set, or <paramref name="rawBpm"/> if
    /// the correction margin is not met or the audio is too short.
    /// </returns>
    public double Correct(double rawBpm, float[] samples, int sampleRate, CancellationToken ct = default)
    {
        if (rawBpm <= 0 || samples is null || samples.Length == 0 || sampleRate <= 0)
            return rawBpm;

        // ---- 1. Build onset-strength envelope ----
        var envelope = BuildOnsetEnvelope(samples, ct);
        if (envelope is null || envelope.Length < 4)
            return rawBpm;

        // ---- 2. Autocorrelate the envelope ----
        var maxLagFrames = (int)Math.Min(
            MaxLagSeconds * sampleRate / OnsetHopSize,
            envelope.Length - 1);
        if (maxLagFrames < 2)
            return rawBpm;

        var acf = Autocorrelate(envelope, maxLagFrames, ct);

        // ---- 3. Build hybrid candidate set ----
        // 3a: raw octave multiples (wide set: /4 … ×4)
        var candidatesBpm = new HashSet<double>(capacity: 32);
        foreach (var mult in RawMultipliers)
        {
            var c = rawBpm * mult;
            if (c >= MinCandidateBpm && c <= MaxCandidateBpm)
                candidatesBpm.Add(c);
        }

        // 3b: ACF scan peaks across the fundamental range (independent of rawBPM).
        // This is the key addition that lets the corrector find 155 BPM even when
        // rawBPM is 31 (BASS_FX out-of-range glitch): the ACF has a dominant peak
        // at lag=155 BPM regardless of what rawBPM was.
        AddAcfScanPeaks(acf, sampleRate, maxLagFrames, candidatesBpm, ct);

        // ---- 4. Score all candidates ----
        var bestBpm   = rawBpm;
        var bestScore = double.MinValue;
        var rawScore  = double.MinValue;
        // Only defend the raw value if it falls within BASS_FX's own native output range
        // (45–230 BPM). Values outside that window are gross detector errors — don't protect them.
        var rawInRange = rawBpm >= BassNativeMinBpm && rawBpm <= BassNativeMaxBpm;

        foreach (var cBpm in candidatesBpm)
        {
            ct.ThrowIfCancellationRequested();

            var periodSec = 60.0 / cBpm;
            var lagFrames = (int)Math.Round(periodSec * sampleRate / OnsetHopSize);
            if (lagFrames < 1 || lagFrames > maxLagFrames) continue;

            var acfVal = acf[lagFrames];
            var weight = LogGaussianWeight(periodSec);
            var score  = acfVal * weight;

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm   = cBpm;
            }

            // Record the raw BPM's own score (from the closest candidate to rawBPM).
            if (Math.Abs(cBpm - rawBpm) < 0.5)
                rawScore = score;
        }

        // ---- 5. Conservative correction guard ----
        // No change if raw already won.
        if (Math.Abs(bestBpm - rawBpm) < 0.5)
            return rawBpm;

        // No change if the best alternate has no positive ACF energy.
        if (bestScore <= 0)
            return rawBpm;

        // If raw is in range and has a positive score, require a clear margin.
        // This prevents the prior from flipping confident-correct tracks.
        // Raw out-of-range (e.g. BASS returned 31 BPM below the 45 BPM floor)
        // means the raw value is unreliable — allow correction without a margin fight.
        if (rawInRange && rawScore > 0 && bestScore <= rawScore * (1.0 + MinScoreMargin))
            return rawBpm;

        return bestBpm;
    }

    // -------------------------------------------------------------------------
    // ACF scan — full sweep over MinFundamentalBpm..MaxFundamentalBpm
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sweeps every integer BPM in [<see cref="MinFundamentalBpm"/>,
    /// <see cref="MaxFundamentalBpm"/>], scores each with ACF × prior, and adds the
    /// top <see cref="AcfScanTopPeaks"/> BPM values to <paramref name="candidates"/>.
    ///
    /// This is what lets the corrector reach 155 BPM from a broken raw=31:
    /// the ACF has a real peak at the 155 BPM lag independent of rawBPM.
    /// </summary>
    private static void AddAcfScanPeaks(
        double[] acf,
        int sampleRate,
        int maxLagFrames,
        HashSet<double> candidates,
        CancellationToken ct)
    {
        // Score every integer BPM in the fundamental range.
        var scored = new List<(double Bpm, double Score)>(
            capacity: (int)(MaxFundamentalBpm - MinFundamentalBpm) + 1);

        for (var bpmI = (int)MinFundamentalBpm; bpmI <= (int)MaxFundamentalBpm; bpmI++)
        {
            ct.ThrowIfCancellationRequested();

            var bpm       = (double)bpmI;
            var periodSec = 60.0 / bpm;
            var lagFrames = (int)Math.Round(periodSec * sampleRate / OnsetHopSize);
            if (lagFrames < 1 || lagFrames > maxLagFrames) continue;

            var acfVal = acf[lagFrames];
            var weight = LogGaussianWeight(periodSec);
            var score  = acfVal * weight;

            scored.Add((bpm, score));
        }

        // Sort descending and add the top N to the candidate set.
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var added = 0;
        foreach (var (bpm, _) in scored)
        {
            if (added >= AcfScanTopPeaks) break;
            candidates.Add(bpm);
            added++;
        }
    }

    // -------------------------------------------------------------------------
    // Onset-strength envelope
    // -------------------------------------------------------------------------

    private static double[]? BuildOnsetEnvelope(float[] samples, CancellationToken ct)
    {
        if (samples.Length < OnsetFrameSize)
            return null;

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
    // Biased autocorrelation
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
    // Log-Gaussian tempo prior
    // -------------------------------------------------------------------------

    private static double LogGaussianWeight(double periodSec)
    {
        if (periodSec <= 0.0) return 0.0;
        var logRatio = Math.Log2(periodSec / PriorCentreSeconds);
        var z = logRatio / PriorSigmaOctaves;
        return Math.Exp(-0.5 * z * z);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
