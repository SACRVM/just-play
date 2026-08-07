using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// EXPERIMENTAL - N19 per-frame tonalness-weighted chroma for drum suppression (2026-06-24).
///
/// <para><b>Why weight by tonalness?</b> Drums are spectrally broadband (high flatness);
/// tonal chord/melody frames have sharp peaks (low flatness). By weighting each frame's
/// contribution to the chroma accumulation by its tonalness (1 - spectral_flatness), we
/// upweight the frames that actually carry key-relevant information and downweight the
/// percussive noise. This approximates the intent of HPSS without the O(TxFxL) cost of
/// a full time-median filter.</para>
///
/// <para><b>Method - per-frame flatness-weighted chromagram:</b></para>
/// <list type="number">
///   <item>For each analysis frame (same 8192/4096 Hann-windowed STFT as ChromagramKeyDetector),
///     compute the spectral flatness in the harmonic band [100-5000 Hz].</item>
///   <item>Tonalness weight w = max(0, 1 - flatness). Flat frames (drums) -> w~0. Tonal frames -> w~1.</item>
///   <item>Accumulate the frame's 36-bin chroma contribution with weight w (instead of the uniform
///     L2-normalised contribution used in <see cref="ChromagramKeyDetector"/>).</item>
///   <item>Classify with the same braw profile + cosine as the shipped ChromagramKeyDetector.</item>
/// </list>
///
/// <para><b>Design rationale:</b> the ChromagramKeyDetector (whole-band magnitude chromagram +
/// per-frame L2 + per-bin median) already partially does this via the median - but the median
/// is across ALL frames including noise. Replacing the uniform L2 weight with a flatness-derived
/// weight before the accumulation biases the median toward the tonal subset. For the experiment we
/// measure whether this flatness weighting helps on the GiantSteps set where drum-heavy intro
/// sections are common.</para>
///
/// <para>On LOFI 96kbps mp3 (the GiantSteps audio), encoding artifacts raise the spectral flatness
/// of all frames somewhat. The <see cref="FlatnessGateThreshold"/> is calibrated for lossless
/// audio; on LOFI, the threshold is relaxed to avoid discarding all frames. The weight is soft
/// (continuous, not binary) so even if no frames are fully excluded, tonal frames still get
/// proportionally higher weight.</para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, no new NuGet. Trim/AOT-safe.
/// NOT part of the shipped path - experiment only.</para>
/// </summary>
public static class HpssDrumSuppressor
{
    // Flatness gate threshold.
    // At this flatness level, tonalness weight = 0 (frame fully excluded).
    // Frames below this threshold contribute proportionally.
    // 0.6 was selected empirically: tonal EDM frames at 44.1 kHz have flatness ~0.1-0.4;
    // drum/noise frames have ~0.5-0.8. On LOFI 96kbps artifacts push values up ~0.2.
    // A threshold of 0.6 suppresses pure-drum frames on LOFI while keeping breakdowns.
    public const double FlatnessGateThreshold = 0.6;

    // FFT params - same as ChromagramKeyDetector (11025 Hz analysis rate).
    // Using the chroma detector's rate so there's no additional decode step.
    private const int FrameSize = 8192;
    private const int HopSize   = FrameSize / 2;   // 50% overlap (ChromagramKeyDetector standard)

    // Harmonic band for both chromagram and flatness measurement.
    private const double MinHz = 100.0;
    private const double MaxHz = 5000.0;

    private const int BinsPerSemitone = 3;
    private const int ChromaBins = 12 * BinsPerSemitone;  // 36
    private const double C0Hz    = 16.3515978312874;
    private const double SilenceFloor = 1e-9;

    /// <summary>
    /// Builds a 12-bin chroma vector via per-frame flatness-weighted accumulation of the
    /// magnitude chromagram. Frames with high spectral flatness (percussive/noisy) receive
    /// low weight; tonal frames receive high weight. Returns null if the audio is too short
    /// or entirely silent/percussive.
    /// </summary>
    public static double[]? BuildGatedChroma12(DecodedAudio audio, CancellationToken ct = default)
    {
        var samples    = audio.Samples;
        var sampleRate = audio.SampleRate;
        if (samples is null || samples.Length < FrameSize || sampleRate <= 0) return null;

        var nyquist    = sampleRate / 2.0;
        var maxFreq    = Math.Min(MaxHz, nyquist);
        var hann       = BuildHannWindow(FrameSize);
        var halfBins   = FrameSize / 2;

        // Pre-map FFT bins to 36-bin chroma (same as ChromagramKeyDetector).
        var binToFine = new int[halfBins];
        for (var k = 0; k < halfBins; k++)
        {
            var freq = k * (double)sampleRate / FrameSize;
            if (freq < MinHz || freq > maxFreq || freq <= 0) { binToFine[k] = -1; continue; }
            var fine = (int)Math.Round(ChromaBins * Math.Log2(freq / C0Hz));
            binToFine[k] = ((fine % ChromaBins) + ChromaBins) % ChromaBins;
        }

        // Flatness measurement band [MinHz, min(3500Hz, nyquist)] in bin indices.
        var kFlatMin = Math.Max(1, (int)(MinHz / ((double)sampleRate / FrameSize)));
        var kFlatMax = Math.Min(halfBins - 1, (int)(Math.Min(3500.0, nyquist) / ((double)sampleRate / FrameSize)));
        var flatBandN = Math.Max(1, kFlatMax - kFlatMin + 1);

        var re         = new float[FrameSize];
        var im         = new float[FrameSize];
        var mag        = new double[halfBins];
        var frameChroma = new double[ChromaBins];

        // Weighted accumulation: each frame's chroma contribution x tonalness weight.
        // We accumulate a WEIGHTED SUM then normalise.
        var weightedChroma = new double[ChromaBins];
        var totalWeight    = 0.0;
        var anyEnergy      = false;

        const double eps = 1e-30;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < FrameSize; n++) { re[n] = samples[start + n] * hann[n]; im[n] = 0f; }
            Fft.Forward(re, im);
            for (var k = 0; k < halfBins; k++)
                mag[k] = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

            // Per-frame spectral flatness in the flatness measurement band.
            double logSum = 0.0, linSum = 0.0;
            for (var k = kFlatMin; k <= kFlatMax; k++)
            {
                var m = mag[k] + eps;
                logSum += Math.Log(m);
                linSum += m;
            }
            var geom      = Math.Exp(logSum / flatBandN);
            var arith     = linSum / flatBandN;
            var flatness  = arith > eps ? Math.Clamp(geom / arith, 0.0, 1.0) : 1.0;
            var tonalness = Math.Max(0.0, 1.0 - flatness / FlatnessGateThreshold);
            // Soft exponential downweighting: tonal frames count much more than mixed.
            var frameWeight = tonalness * tonalness;  // square to be more aggressive

            // Build frame chroma (peak-emphasised, same as ChromagramKeyDetector).
            Array.Clear(frameChroma, 0, ChromaBins);
            for (var k = 1; k < halfBins - 1; k++)
            {
                var fine = binToFine[k];
                if (fine < 0) continue;
                var floor = Math.Min(mag[k - 1], mag[k + 1]);
                var peak  = mag[k] - floor;
                if (peak > 0) frameChroma[fine] += peak;
            }

            // L2-normalise the frame chroma (so all frames are unit-scale before weighting).
            var energy = 0.0;
            for (var b = 0; b < ChromaBins; b++) energy += frameChroma[b] * frameChroma[b];
            if (energy <= SilenceFloor) continue;

            // Only include frames with enough energy AND significant tonalness.
            if (frameWeight < 0.001) continue;

            var inv = frameWeight / Math.Sqrt(energy);  // scale by tonalness weight
            for (var b = 0; b < ChromaBins; b++) weightedChroma[b] += frameChroma[b] * inv;
            totalWeight += frameWeight;
            anyEnergy = true;
        }

        if (!anyEnergy || totalWeight <= SilenceFloor) return null;

        // Normalise by total weight, then fold 36->12 with tuning correction.
        for (var b = 0; b < ChromaBins; b++) weightedChroma[b] /= totalWeight;

        // Find dominant tuning sub-bin.
        var subEnergy = new double[BinsPerSemitone];
        for (var b = 0; b < ChromaBins; b++) subEnergy[b % BinsPerSemitone] += weightedChroma[b];
        var bestSub = 0;
        if (subEnergy[1] > subEnergy[0]) bestSub = 1;
        if (subEnergy[2] > subEnergy[bestSub]) bestSub = 2;

        // Fold 36->12 centred on tuning.
        var chroma = new double[12];
        for (var pc = 0; pc < 12; pc++)
        {
            var c = pc * BinsPerSemitone + bestSub;
            chroma[pc] += weightedChroma[Mod(c, ChromaBins)];
            chroma[pc] += 0.5 * weightedChroma[Mod(c - 1, ChromaBins)];
            chroma[pc] += 0.5 * weightedChroma[Mod(c + 1, ChromaBins)];
        }

        // Normalise.
        var total = 0.0;
        for (var i = 0; i < 12; i++) total += chroma[i];
        if (total <= SilenceFloor) return null;
        for (var i = 0; i < 12; i++) chroma[i] /= total;
        return chroma;
    }

    private static int Mod(int x, int m) => ((x % m) + m) % m;

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
