namespace JustPlay.Analysis;

/// <summary>
/// Post-processes a raw BPM estimate from BASS_FX to correct half/double-tempo
/// (octave) errors using an Ellis-style onset-strength autocorrelation combined
/// with a log-Gaussian tempo prior.
///
/// <para><b>Algorithm (Ellis 2007 §3.1 + librosa parameterisation):</b></para>
/// <list type="number">
///   <item>
///     <b>Onset-strength envelope:</b> compute a short-time spectral flux envelope
///     over the decoded audio using power-of-two FFT frames at ~8–11 kHz.
///     We use the audio already decoded for energy/key analysis (typically 11 025 Hz)
///     to avoid an extra decode. Each frame produces a half-wave-rectified spectral
///     flux value — the sum of positive magnitude differences across bins. This is
///     the same flux computation used in <see cref="SpectralEnergyDetector"/> but
///     operating at the onset envelope's hop rate.
///   </item>
///   <item>
///     <b>Autocorrelation:</b> compute the biased autocorrelation of the onset
///     envelope up to the maximum lag corresponding to 40 BPM (i.e. 1.5 s period).
///     This reveals the dominant periodicity of the onsets.
///   </item>
///   <item>
///     <b>Log-Gaussian tempo prior:</b> weight the autocorrelation by a Gaussian
///     centred on τ₀ = 0.5 s (120 BPM) on a log-time axis:
///     <c>W(τ) = exp(−½ · (log₂(τ/τ₀) / σ_τ)²)</c>.
///     Parameters chosen:
///     <list type="bullet">
///       <item>τ₀ = 0.5 s → 120 BPM — the Ellis/librosa canonical prior centre.
///         120 BPM sits near the midpoint of the EDM range (110–135) and the
///         perceptual tempo centre from Parncutt 1994. The alternative of 125 BPM
///         (dead centre of House) was considered but 120 is more broadly validated
///         across genres (Ellis MIREX-06, librosa default) and minimises prior-induced
///         bias across the full 60–200 BPM EDM range.</item>
///       <item>σ_τ = 0.9 octaves — the tighter Ellis "duple/triple" width, shown
///         to reach 84 % MIREX-06 agreement vs 77 % at 1.4 octaves. At this width
///         the prior meaningfully discriminates between octave candidates while
///         still reaching the full EDM range (60–200 BPM ≈ ±1 octave of 120 BPM),
///         so it can always express a preference.</item>
///     </list>
///   </item>
///   <item>
///     <b>Octave candidate scoring:</b> evaluate {rawBPM/2, rawBPM, rawBPM×2}
///     (the three musically meaningful octave candidates within the BASS_FX 45–230
///     BPM window). Each candidate's score is <c>autoCorr[lag] × W(lag)</c>.
///     Candidates outside 40–240 BPM are excluded.
///   </item>
///   <item>
///     <b>Conservative correction:</b> only deviate from the raw BASS_FX estimate
///     when the winning alternate octave's score exceeds the raw-octave score by a
///     margin of <c>MinScoreMargin = 0.15</c>. This prevents the prior from
///     overriding confident correct detections — e.g. a strong 140 BPM signal
///     produces a sharp autocorrelation peak that dominates the prior weighting.
///   </item>
/// </list>
///
/// <para>All managed, no NuGet, reflection-free, trim-/AOT-safe. No BASS or Avalonia
/// references — lives entirely in <c>JustPlay.Analysis</c>.</para>
/// </summary>
public sealed class TempoOctaveCorrector
{
    // ---- Onset envelope FFT parameters ----
    // Frame size for the spectral-flux onset envelope.
    // At 11025 Hz: 512 samples ≈ 46 ms (Ellis uses 32 ms / 8 kHz ≈ 256 samples;
    // we match the spirit with 46 ms at 11025 Hz — still sub-50 ms, enough temporal
    // resolution to see individual beats at 200 BPM where a beat = 300 ms).
    private const int OnsetFrameSize = 512;
    // Hop = frame/4 gives ~12 ms hop (≈ 87 onset frames/s) — fine enough resolution
    // for autocorrelation up to 1.5 s without a huge envelope vector.
    private const int OnsetHopSize = OnsetFrameSize / 4;

    // ---- Prior parameters (Ellis 2007 / librosa parameterisation) ----
    // τ₀ = 0.5 s ↔ 120 BPM. See class doc for rationale.
    private const double PriorCentreSeconds = 0.5;
    // σ_τ = 0.9 octaves (tighter Ellis "duple/triple" setting). See class doc.
    private const double PriorSigmaOctaves = 0.9;

    // ---- Candidate BPM search range ----
    // Floor/ceiling for valid candidates — slightly wider than BASS_FX's 45–230
    // to allow rawBPM/2 when rawBPM ≈ 90.
    private const double MinCandidateBpm = 40.0;
    private const double MaxCandidateBpm = 240.0;

    // ---- Conservative-correction threshold ----
    // An alternate octave must outscore the raw estimate by at least this fraction
    // of the raw score before we apply the correction. At 0.15 (15%) a moderately
    // strong preference for the alternate is required — slight prior bias alone is
    // not enough. Calibrated so that confident 140 BPM EDM tracks (sharp
    // autocorrelation peak) are left untouched while genuinely halved 70 BPM
    // estimates are fixed.
    private const double MinScoreMargin = 0.15;

    // ---- Maximum lag to autocorrelate (in seconds) ----
    // 40 BPM → period = 1.5 s. No need to look further.
    private const double MaxLagSeconds = 1.5;

    /// <summary>
    /// Applies octave correction to a raw BPM estimate.
    /// </summary>
    /// <param name="rawBpm">Raw BPM from BASS_FX. Returned unchanged on failure.</param>
    /// <param name="samples">Decoded mono float samples (any sample rate ≥ 8000 Hz).</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The corrected BPM (one of rawBPM/2, rawBPM, or rawBPM×2), or <paramref name="rawBpm"/>
    /// unchanged if the audio is too short or the correction margin is not met.
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

        // ---- 3. Score octave candidates ----
        // Candidates: rawBPM÷2, rawBPM, rawBPM×2.
        // We don't include ÷3/×3: the Ellis recipe only warrants duple/triple (÷2/×2)
        // for the octave tie-breaker; BASS_FX's 45–230 BPM window makes triple errors
        // rare (200 BPM/3 = 66 BPM is within range, 45 BPM×3 = 135 BPM is too low
        // to be confused with 45 BPM in practice).
        double[] candidatesBpm = [rawBpm / 2.0, rawBpm, rawBpm * 2.0];

        var bestBpm   = rawBpm;
        var bestScore = double.MinValue;
        // Sentinel value that distinguishes "raw candidate was in range but scored 0"
        // from "raw candidate was out of range (not evaluated)".
        var rawScore  = double.MinValue;
        var rawInRange = false;

        foreach (var cBpm in candidatesBpm)
        {
            ct.ThrowIfCancellationRequested();

            if (cBpm < MinCandidateBpm || cBpm > MaxCandidateBpm)
                continue;

            // Convert candidate BPM → period in seconds → lag in frames.
            var periodSec   = 60.0 / cBpm;
            var lagFrames   = (int)Math.Round(periodSec * sampleRate / OnsetHopSize);

            if (lagFrames < 1 || lagFrames > maxLagFrames)
                continue;

            // ACF value at this lag (biased, so already normalised by N).
            var acfVal = acf[lagFrames];

            // Log-Gaussian prior: W(τ) = exp(−½ · (log₂(τ/τ₀) / σ_τ)²)
            var weight = LogGaussianWeight(periodSec);

            var score = acfVal * weight;

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm   = cBpm;
            }
            if (Math.Abs(cBpm - rawBpm) < 0.5) // this is the raw-BPM candidate
            {
                rawScore   = score;
                rawInRange = true;
            }
        }

        // ---- 4. Conservative correction guard ----
        // Only correct if the winner is different from raw AND its margin is sufficient.
        if (Math.Abs(bestBpm - rawBpm) < 0.5)
            return rawBpm;  // raw already won

        // If the best alternate has no meaningful ACF energy at all, keep raw.
        if (bestScore <= 0)
            return rawBpm;

        // If the raw lag was evaluated and also has positive score, require a margin.
        // This is the "confident-correct" guard: a genuine N BPM track has a strong
        // ACF peak at its own lag, making the score difference small even with
        // a prior that prefers another octave.
        if (rawInRange && rawScore > 0 && bestScore <= rawScore * (1.0 + MinScoreMargin))
            return rawBpm;  // not convinced enough — keep raw

        // If the raw lag scored 0 (ACF=0 at the raw period) but the best alternate
        // has a positive score, that IS strong evidence for correction — zero ACF at
        // the raw lag means the signal has no periodicity there at all.
        // If rawBpm was out of the candidate range entirely, also allow correction.
        return bestBpm;
    }

    // -------------------------------------------------------------------------
    // Onset-strength envelope
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the half-wave-rectified spectral-flux onset-strength envelope.
    /// Returns an array of non-negative values, one per hop. Returns null if the
    /// signal is too short for even one frame.
    /// </summary>
    private static double[]? BuildOnsetEnvelope(float[] samples, CancellationToken ct)
    {
        if (samples.Length < OnsetFrameSize)
            return null;

        var hann    = BuildHannWindow(OnsetFrameSize);
        var re      = new float[OnsetFrameSize];
        var im      = new float[OnsetFrameSize];
        var halfBins = OnsetFrameSize / 2;

        var prevMag  = new double[halfBins];
        var havePrev = false;

        var frameCount = (samples.Length - OnsetFrameSize) / OnsetHopSize + 1;
        var envelope   = new double[frameCount];
        var envIdx     = 0;

        for (var start = 0; start + OnsetFrameSize <= samples.Length; start += OnsetHopSize)
        {
            ct.ThrowIfCancellationRequested();

            // Hann-windowed FFT frame.
            for (var n = 0; n < OnsetFrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            // Magnitude spectrum (one-sided).
            double flux = 0.0;
            for (var k = 0; k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                if (havePrev)
                {
                    var diff = mag - prevMag[k];
                    if (diff > 0.0) flux += diff;   // half-wave rectify
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

    /// <summary>
    /// Computes the biased autocorrelation of <paramref name="signal"/> for lags
    /// 0 … <paramref name="maxLag"/> inclusive. Returns an array of length
    /// <c>maxLag + 1</c>. Uses an O(N·maxLag) direct summation — fast enough for
    /// envelope lengths of a few thousand and maxLag of a few hundred.
    /// </summary>
    private static double[] Autocorrelate(double[] signal, int maxLag, CancellationToken ct)
    {
        var n   = signal.Length;
        var acf = new double[maxLag + 1];

        for (var lag = 0; lag <= maxLag; lag++)
        {
            ct.ThrowIfCancellationRequested();
            var sum = 0.0;
            var count = n - lag;
            for (var i = 0; i < count; i++)
                sum += signal[i] * signal[i + lag];
            acf[lag] = sum / n; // biased normalisation (by full length, not count)
        }

        return acf;
    }

    // -------------------------------------------------------------------------
    // Log-Gaussian tempo prior
    // -------------------------------------------------------------------------

    /// <summary>
    /// Evaluates the log-Gaussian perceptual prior weight for a given tempo period.
    /// <c>W(τ) = exp(−½ · (log₂(τ / τ₀) / σ_τ)²)</c>
    /// where τ₀ = <see cref="PriorCentreSeconds"/> and
    /// σ_τ = <see cref="PriorSigmaOctaves"/>.
    ///
    /// At τ = τ₀ (120 BPM), W = 1.0 (maximum weight).
    /// At τ = 2·τ₀ (60 BPM) or τ = τ₀/2 (240 BPM), W ≈ exp(−½/0.81) ≈ 0.54.
    /// </summary>
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
