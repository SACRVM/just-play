using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Computes the continuous per-track <b>vibe quartet</b> (punch / groove / dark / hypnotic,
/// each in [0, 1]) plus the <b>noisy</b> fatigue flag (Harshness, also in [0, 1]).
///
/// <para><b>Design intent (revised 2026-06-09, vibe quartet):</b>
/// The previous discrete four-way label (punchy/groovy/noisy/dreamy) was replaced by four
/// independent continuous axes that the DJ uses for mix-compatibility and set-building:
/// <list type="bullet">
///   <item><b>punch</b> = BassPunch (already built — see below).</item>
///   <item><b>groove</b> = BassGroove (already built — see below).</item>
///   <item><b>dark</b> = 1 − normalizedBrightness. Low HF/centroid → dark→1; bright → dark→0.</item>
///   <item><b>hypnotic</b> = 1 − normalizedCentroidVariance. Minimal/looping → 1; evolving → 0.</item>
/// </list>
/// "noisy" (Harshness) is kept SEPARATE from the quartet — it is a quality/fatigue flag,
/// not a mix-character axis. "dreamy" was retired; low-energy tracks are now identified
/// directly by <see cref="AnalysisResult.RawEnergyScore"/>.
/// </para>
///
/// <para><b>Features computed here (all from the same 512-sample FFT pass, no extra decode):</b></para>
/// <list type="bullet">
///   <item><b>SpectralFlatness</b> — geometric-mean / arithmetic-mean of the magnitude
///     spectrum per frame, averaged. 0 = tonal, 1 = noise-like/saturated.
///     The primary "noisy" signal.</item>
///   <item><b>Harshness</b> (noisy fatigue flag) — blends flatness + crest factor + HF-energy ratio.
///     Crest = peakDb − integratedLufs (BOTH already computed — reused here, no extra pass).</item>
///   <item><b>Dark</b> — 1 − normalizedBrightness, where brightness = spectral centroid of each
///     frame, averaged, normalised to [CentroidLo=300 Hz, CentroidHi=3000 Hz].
///     Reuses the per-frame centroid already computed in the flatness/HF pass.</item>
///   <item><b>Hypnotic</b> — 1 − normalizedCentroidVariance. The temporal standard deviation
///     of per-frame spectral centroid is normalised by [CentroidVarLo=0, CentroidVarHi=200 000 Hz²].
///     Low variance → repetitive/looping → Hypnotic≈1. High variance → evolving → Hypnotic≈0.
///     Reuses the centroid values already computed in the same pass — zero extra FFT cost.</item>
///   <item><b>BassPunch</b> — transient sharpness / attack steepness of low-band onsets.
///     Computed from the low-band onset envelope built here (separate low-band FFT pass,
///     reusing the same 11 kHz buffer).</item>
///   <item><b>BassGroove</b> — swing / syncopation derived from existing
///     <see cref="RhythmPattern"/> fields so no second onset pass is needed.</item>
/// </list>
///
/// <para><b>Low-FoF confidence gate:</b>
/// When FourOnFloor is very low (~14% of real tracks have FoF &lt; 0.20, beat grid unreliable),
/// BassGroove (and BassPunch) are clamped toward 0 so confident punch/groove are not emitted
/// when the beat grid was unreliable.
/// </para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.
/// No new NuGet packages — reuses Fft.cs already in this project.</para>
/// </summary>
public static class CharacterClassifier
{
    // =========================================================================
    // Confidence gate for beat-grid-dependent features
    // =========================================================================

    /// <summary>
    /// When FourOnFloor drops below this threshold (~14% of real tracks), the beat grid
    /// is probably not found reliably. BassPunch and BassGroove are clamped toward 0.
    /// FIRST-GUESS.
    /// </summary>
    public const double FofConfidenceMin = 0.20;

    // =========================================================================
    // Normalisation ranges (documented in AnalysisResult XML docs)
    // =========================================================================

    // Dark: centroid is in Hz; normalise over [CentroidLo, CentroidHi] from SpectralEnergyDetector.
    // These MUST stay in sync with SpectralEnergyDetector.CentroidLo / CentroidHi (300/3000 Hz).
    private const double CentroidLo = 300.0;   // Hz (sub-bass end)
    private const double CentroidHi = 3000.0;  // Hz (above DEAM max)

    // Hypnotic: temporal standard-deviation of per-frame centroid in Hz.
    // First-guess calibration: variance = (std_dev)^2.
    //   Very repetitive loop (same spectrum every frame) → std_dev ≈ 0 Hz → Hypnotic = 1.
    //   Highly evolving track (centroid sweeps 300–3000 Hz) → std_dev ≈ 500 Hz → Hypnotic ≈ 0.
    // CentroidStdDevHi = 450 Hz → Hypnotic=0 at this point and above; first-guess, tune via stats.
    private const double CentroidStdDevHi = 450.0;  // Hz

    // =========================================================================
    // Onset parameters (must match RhythmPatternDetector for low-band reuse)
    // =========================================================================

    private const int OnsetFrameSize = 512;
    private const int OnsetHopSize   = OnsetFrameSize / 4;  // 128 samples @ 11025 Hz
    // Low-band ceiling (same as RhythmPatternDetector.LowBandMaxBin).
    // At 11025 Hz: bin spacing = 11025/512 ≈ 21.5 Hz → bin 11 ≈ 237 Hz (kick body).
    private const int LowBandMaxBin = 11;
    // HF band for harshness ratio: above 3500 Hz → bin 163+
    // At 11025 Hz: 3500 Hz / (11025/512) ≈ bin 162
    private const int HfBandMinBin  = 163;
    // Minimum frames for analysis.
    private const int MinFrames     = 32;

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Compute all vibe-quartet scalars (punch, groove, dark, hypnotic) plus the noisy
    /// fatigue flag (harshness) and supporting spectral features (flatness).
    ///
    /// <para><b>Dark computation:</b> 1 − normalizedBrightness where brightness = mean spectral
    /// centroid normalised over [300 Hz, 3000 Hz]. Reuses centroids from the same flatness pass
    /// — no extra FFT.</para>
    ///
    /// <para><b>Hypnotic computation:</b> 1 − normalizedCentroidStdDev where centroid std-dev is
    /// measured across frames and normalised by CentroidStdDevHi = 450 Hz (first-guess). Low
    /// temporal centroid variance = repetitive/looping → Hypnotic≈1. Reuses per-frame centroid
    /// values already computed for Dark — no extra FFT pass at all.</para>
    ///
    /// Returns null when audio is too short, or loudness/peak are missing.
    /// </summary>
    /// <param name="samples">Mono float samples in [-1,1] at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate in Hz (typically 11025 for analysis).</param>
    /// <param name="bpm">Detected (corrected) BPM of the track.</param>
    /// <param name="integratedLufs">BS.1770 integrated LUFS from the energy pass (reused, no redecode).</param>
    /// <param name="peak">Sample-domain peak (0..~1) from the loudness pass (reused).</param>
    /// <param name="rawEnergyScore">The continuous blended energy score [0,1] from SpectralEnergyDetector.</param>
    /// <param name="rhythm">Already-computed RhythmPattern, used to derive BassGroove. May be null.</param>
    /// <param name="ct">Cancellation token.</param>
    public static CharacterFeatures? Compute(
        float[] samples,
        int sampleRate,
        double? bpm,
        double integratedLufs,
        double peak,
        double rawEnergyScore,
        RhythmPattern? rhythm,
        CancellationToken ct = default)
    {
        if (samples is null || samples.Length < OnsetFrameSize * 4 || sampleRate <= 0)
            return null;

        // ── Spectral flatness, HF ratio, centroid (mean + std-dev) — one FFT pass ──
        var (flatness, hfRatio, meanCentroid, centroidStdDev) =
            ComputeSpectralFeaturesAndCentroid(samples, sampleRate, ct);

        // ── Crest factor: peak_dBFS − integrated_LUFS ─────────────────────────
        var peakDbFs = peak > 0.0 ? 20.0 * Math.Log10(peak) : -96.0;
        var rawCrest  = peakDbFs - integratedLufs;
        var nCrest    = Norm(rawCrest, CrestLo, CrestHi);

        // ── Harshness (noisy fatigue flag) = flatness + (1-crest) + hfRatio ───
        var harshness = HarshWFlat * flatness + HarshWCrest * (1.0 - nCrest) + HarshWHf * hfRatio;
        harshness = Math.Clamp(harshness, 0.0, 1.0);

        // ── Dark = 1 − normalizedBrightness ──────────────────────────────────
        // meanCentroid is in Hz; normalise over [CentroidLo, CentroidHi].
        // dark=1 → dark/dull (low centroid/HF); dark=0 → bright (high centroid/HF).
        var nBright = Norm(meanCentroid, CentroidLo, CentroidHi);
        var dark = Math.Clamp(1.0 - nBright, 0.0, 1.0);

        // ── Hypnotic = 1 − normalizedCentroidStdDev ──────────────────────────
        // centroidStdDev in Hz; normalise by CentroidStdDevHi (first-guess 450 Hz).
        // Low variance → same spectrum every frame → repetitive/looping → Hypnotic≈1.
        // High variance → evolving/progressive → Hypnotic≈0.
        var nCentroidVar = Norm(centroidStdDev, 0.0, CentroidStdDevHi);
        var hypnotic = Math.Clamp(1.0 - nCentroidVar, 0.0, 1.0);

        // ── BassPunch: transient sharpness of low-band onsets ─────────────────
        var bpm_ = bpm ?? 0.0;
        var beatPeriodFrames = bpm_ > 0.0
            ? sampleRate / (double)OnsetHopSize * (60.0 / bpm_)
            : 0.0;

        double bassPunch = 0.0;
        if (bpm_ > 0.0 && beatPeriodFrames > 0)
        {
            var lowEnv = BuildLowBandOnsets(samples, ct);
            if (lowEnv.Length >= MinFrames)
                bassPunch = ComputeBassPunch(lowEnv, beatPeriodFrames);
        }

        // ── BassGroove: derived from existing RhythmPattern low-band fields ───
        // BassGroove = how off-grid the LOW-BAND events are.
        // We use Syncopation (off-grid energy) and the Swing measure from the full-band
        // RhythmPattern as a proxy — the low-band groove is not separately re-detected
        // to avoid a second onset pass. If rhythm is null, default to 0.
        //
        // Note on ~14% kick-failure cases: when FourOnFloor is very low (beat grid was
        // not reliably found), BassGroove is unreliable. Clamp toward 0 in that case.
        double bassGroove = 0.0;
        bool lowConfidence = false;
        if (rhythm is not null)
        {
            bassGroove = Math.Clamp(
                0.5 * rhythm.Syncopation + 0.5 * rhythm.Swing,
                0.0, 1.0);

            if (rhythm.FourOnFloor < FofConfidenceMin)
            {
                lowConfidence = true;
                // Clamp groove and punch toward 0 when beat grid is unreliable.
                bassGroove = bassGroove * 0.3;
                bassPunch  = bassPunch  * 0.3;
            }
        }

        return new CharacterFeatures(
            SpectralFlatness: flatness,
            Harshness:        harshness,
            BassPunch:        bassPunch,
            BassGroove:       bassGroove,
            Dark:             dark,
            Hypnotic:         hypnotic,
            LowConfidence:    lowConfidence);
    }

    // =========================================================================
    // Feature computation
    // =========================================================================

    // Harshness blend weights — the three spectral signals each contribute.
    private const double HarshWFlat  = 0.45;  // spectral flatness  (noise-like content)
    private const double HarshWCrest = 0.30;  // (1 - normalised crest) = squashed loudness
    private const double HarshWHf    = 0.25;  // HF energy ratio          (harshness brightness)

    // Crest factor normalisation: raw LU range for the (1-nCrest) term.
    // Typical EDM range: 3 LU (maximum squash) … 18 LU (ambient / wide dynamic range).
    private const double CrestLo = 3.0;
    private const double CrestHi = 18.0;

    /// <summary>
    /// Computes per-frame spectral flatness (Wiener entropy), the HF energy ratio,
    /// the mean spectral centroid in Hz, and the temporal std-dev of centroid in Hz.
    ///
    /// One FFT pass over the samples — no extra decode. The centroid values are collected
    /// per-frame, then mean and std-dev are computed over all frames. Dark and Hypnotic
    /// are both derived from these centroid statistics after this call.
    ///
    /// [CharacterClassifier: Dark = 1−nBright; Hypnotic = 1−nCentroidStdDev]
    /// </summary>
    private static (double Flatness, double HfRatio, double MeanCentroid, double CentroidStdDev)
        ComputeSpectralFeaturesAndCentroid(float[] samples, int sampleRate, CancellationToken ct)
    {
        var hann    = BuildHannWindow(OnsetFrameSize);
        var re      = new float[OnsetFrameSize];
        var im      = new float[OnsetFrameSize];
        var halfBins = OnsetFrameSize / 2;

        var flatnessSum    = 0.0;
        var hfEnergySum    = 0.0;
        var totalEnergySum = 0.0;
        var validFrames    = 0;

        // Collect per-frame centroid values for std-dev computation.
        // Pre-allocate enough space.
        var maxFrames   = (samples.Length - OnsetFrameSize) / OnsetHopSize + 1;
        var centroids   = new double[maxFrames];
        var centroidCount = 0;

        for (var start = 0; start + OnsetFrameSize <= samples.Length; start += OnsetHopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < OnsetFrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            var magSum      = 0.0;
            var logMagSum   = 0.0;
            var hfMagSum    = 0.0;
            var weightedFreq = 0.0;
            var nonZeroBins = 0;
            var binHz = (double)sampleRate / OnsetFrameSize;

            for (var k = 1; k < halfBins; k++)  // skip DC (k=0)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                magSum += mag;
                weightedFreq += mag * (k * binHz);

                if (k >= HfBandMinBin)
                    hfMagSum += mag;

                if (mag > 1e-12)
                {
                    logMagSum += Math.Log(mag);
                    nonZeroBins++;
                }
            }

            if (magSum <= 0.0 || nonZeroBins < 2) continue;

            // Spectral flatness: gm / am.
            var geoMean   = Math.Exp(logMagSum / nonZeroBins);
            var arithMean = magSum / nonZeroBins;
            var flatness  = arithMean > 0.0 ? geoMean / arithMean : 0.0;
            flatnessSum += Math.Clamp(flatness, 0.0, 1.0);

            hfEnergySum    += hfMagSum;
            totalEnergySum += magSum;

            // Spectral centroid in Hz for this frame.
            var frameCentroid = weightedFreq / magSum;
            if (centroidCount < maxFrames)
                centroids[centroidCount++] = frameCentroid;

            validFrames++;
        }

        if (validFrames == 0) return (0.0, 0.0, CentroidLo, 0.0);

        var avgFlatness = flatnessSum / validFrames;
        var avgHfRatio  = totalEnergySum > 0.0
            ? Math.Clamp(hfEnergySum / totalEnergySum, 0.0, 1.0)
            : 0.0;

        // Mean centroid and std-dev from the per-frame values.
        var meanC = 0.0;
        for (var i = 0; i < centroidCount; i++) meanC += centroids[i];
        meanC /= centroidCount;

        var varC = 0.0;
        for (var i = 0; i < centroidCount; i++)
        {
            var diff = centroids[i] - meanC;
            varC += diff * diff;
        }
        var stdDevC = centroidCount > 1 ? Math.Sqrt(varC / centroidCount) : 0.0;

        return (avgFlatness, avgHfRatio, meanC, stdDevC);
    }

    /// <summary>
    /// Builds the low-band onset (spectral flux) envelope from the samples —
    /// same algorithm as <c>RhythmPatternDetector.BuildBandOnsets</c> but only
    /// the low band, returned as a flat array. No extra decode.
    /// </summary>
    private static double[] BuildLowBandOnsets(float[] samples, CancellationToken ct)
    {
        var hann    = BuildHannWindow(OnsetFrameSize);
        var re      = new float[OnsetFrameSize];
        var im      = new float[OnsetFrameSize];
        var halfBins = OnsetFrameSize / 2;

        var frameCount = (samples.Length - OnsetFrameSize) / OnsetHopSize + 1;
        var lowEnv    = new double[frameCount];
        var prevMag   = new double[LowBandMaxBin + 1];
        var havePrev  = false;
        var envIdx    = 0;

        for (var start = 0; start + OnsetFrameSize <= samples.Length; start += OnsetHopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < OnsetFrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            double fluxLow = 0.0;
            for (var k = 0; k <= LowBandMaxBin && k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                if (havePrev)
                {
                    var diff = mag - prevMag[k];
                    if (diff > 0.0) fluxLow += diff;
                }
                prevMag[k] = mag;
            }

            if (envIdx < frameCount)
                lowEnv[envIdx++] = havePrev ? fluxLow : 0.0;

            havePrev = true;
        }

        return lowEnv;
    }

    /// <summary>
    /// BassPunch: measures how sharply the low-band onset envelope rises at each beat.
    /// A punchy kick has a very fast attack — the onset value at the peak frame is
    /// much larger than the preceding frame. This is the local "crest" of the low-band
    /// onset envelope around each beat position.
    ///
    /// Algorithm:
    ///   For each beat position, find the peak low-band onset within ±HalfWindowFrames.
    ///   Measure the ratio of that peak to the preceding frame (attack steepness).
    ///   Average across beats. Normalise to [0, 1] via calibrated range.
    /// </summary>
    private static double ComputeBassPunch(double[] lowEnv, double beatPeriodFrames)
    {
        if (lowEnv.Length < 4) return 0.0;

        const int halfWindow = 2;
        var nFrames = lowEnv.Length;
        var ratioSum = 0.0;
        var count = 0;

        var meanLevel = 0.0;
        foreach (var v in lowEnv) meanLevel += v;
        meanLevel /= lowEnv.Length;
        if (meanLevel < 1e-12) return 0.0;

        for (var beat = 0; beat * beatPeriodFrames < nFrames + beatPeriodFrames * 0.5; beat++)
        {
            var centre = (int)Math.Round(beat * beatPeriodFrames);
            if (centre >= nFrames) break;

            var lo = Math.Max(1, centre - halfWindow);
            var hi = Math.Min(nFrames - 1, centre + halfWindow);

            var peakVal = 0.0;
            var peakIdx = lo;
            for (var f = lo; f <= hi; f++)
            {
                if (lowEnv[f] > peakVal)
                {
                    peakVal = lowEnv[f];
                    peakIdx = f;
                }
            }

            if (peakVal < meanLevel * 0.3) continue;

            var prevVal = lowEnv[peakIdx - 1];
            var ratio   = prevVal > 1e-12 ? peakVal / prevVal : peakVal / meanLevel * 5.0;

            ratioSum += Norm(ratio, 1.0, PunchRatioHi);
            count++;
        }

        return count > 0 ? Math.Clamp(ratioSum / count, 0.0, 1.0) : 0.0;
    }

    // Calibrated upper bound for the attack ratio.
    private const double PunchRatioHi = 15.0;

    // =========================================================================
    // Helpers
    // =========================================================================

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

/// <summary>
/// Output of <see cref="CharacterClassifier.Compute"/>: the continuous vibe-quartet scalars
/// (punch, groove, dark, hypnotic) plus the noisy fatigue flag (harshness) and the supporting
/// spectral flatness. Platform-agnostic, reflection-free, trim-/AOT-safe.
/// </summary>
public sealed record CharacterFeatures(
    double SpectralFlatness,
    double Harshness,
    double BassPunch,
    double BassGroove,
    double Dark,
    double Hypnotic,
    bool   LowConfidence
);
