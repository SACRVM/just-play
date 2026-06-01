using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// SHIPPED key detector (since 2026-06-01) — <b>GiantSteps ground truth: MIREX 0.629, 52%
/// exact, 74% harmonically ok</b>, beating the previous braw+cosine chromagram (0.562 / 41%).
/// Ports the portable parts of Faraldo's <c>edmkey</c> reference chain (anxefaraldo/edmkey,
/// branch AES2017) — the implementation that DERIVED the braw/edma profiles on the GiantSteps
/// set and reports ~0.74 MIREX. Reached by copying edmkey's fine-tuning rather than guessing
/// (the first naive HPCP scored 0.465); further headroom to ~0.74 likely remains in the
/// still-approximated bits noted below.
///
/// <para>edmkey's documented chain (edmkey.py):
/// MonoLoader@44.1k → HighPass(200 Hz)×3 → per frame: FrameCutter(4096/4096) → Hann →
/// Spectrum → SpectralPeaks(25–3500 Hz, thr 1e-4, ≤60) → SpectralWhitening → HPCP(size 12,
/// harmonics 4, cosine weight, 1-semitone window, ref 440) → SUM frames → peak-normalise →
/// gate (&lt;0.2 → 0) → detuning shift → Pearson template match.</para>
///
/// <para>Faithfully ported here: 200 Hz triple high-pass, 4096/4096 framing, top-60
/// peak-picking in 25–3500 Hz with parabolic interpolation, a (simplified) spectral
/// whitening, SUM aggregation (no per-frame normalisation), peak-normalise + 0.2 gate, and
/// tuning-corrected fold. Approximated/deferred: Essentia's exact SpectralWhitening BPF
/// envelope and HPCP harmonic weighting are simplified. Classification reuses the shipped
/// braw + cosine (<see cref="ChromagramKeyDetector.Classify"/>); edmkey uses Pearson, but
/// our own A/B found cosine better, so we keep cosine.</para>
/// </summary>
public sealed class HpcpKeyDetector : IKeyDetector
{
    private const int FrameSize = 4096;               // edmkey WINDOW_SIZE
    private const int HopSize = 4096;                 // edmkey HOP_SIZE (no overlap)
    private const double C0Hz = 16.3515978312874;
    private const double MinHz = 25.0;                // edmkey MIN_HZ
    private const double MaxHz = 3500.0;              // edmkey MAX_HZ
    private const double HighpassHz = 200.0;          // edmkey HIGHPASS_CUTOFF, applied ×3
    private const int MaxPeaks = 60;                  // edmkey SPECTRAL_PEAKS_MAX
    private const double PcpGate = 0.2;               // edmkey PCP_THRESHOLD
    private const int Harmonics = 4;                  // edmkey HPCP_HARMONICS
    private const double HarmonicDecay = 0.6;
    private const int BinsPerSemitone = 3;
    private const int ChromaBins = 12 * BinsPerSemitone; // 36 (fine, for tuning)
    private const double SilenceFloor = 1e-9;

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        var samples = audio.Samples;
        var sampleRate = audio.SampleRate;
        if (samples is null || samples.Length < FrameSize || sampleRate <= 0)
            return null;

        var filtered = HighPassCubed(samples, sampleRate, HighpassHz);
        var fine = BuildHpcp(filtered, sampleRate, ct);
        if (fine is null)
            return null;

        var chroma = FoldToTwelve(fine);

        // edmkey: peak-normalise then gate (zero everything below PcpGate × max).
        var max = 0.0;
        for (var i = 0; i < 12; i++) max = Math.Max(max, chroma[i]);
        if (max <= SilenceFloor) return null;
        var sum = 0.0;
        for (var i = 0; i < 12; i++)
        {
            chroma[i] /= max;
            if (chroma[i] < PcpGate) chroma[i] = 0.0;
            sum += chroma[i];
        }
        if (sum <= SilenceFloor) return null;
        for (var i = 0; i < 12; i++) chroma[i] /= sum;

        return ChromagramKeyDetector.Classify(chroma, 0.0);
    }

    /// <summary>First-order high-pass applied three times (cascaded), matching edmkey's
    /// <c>hpf(hpf(hpf(audio)))</c> — removes the tuned kick / sub-bass below ~200 Hz.</summary>
    private static float[] HighPassCubed(float[] x, int sampleRate, double cutoffHz)
    {
        var rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        var dt = 1.0 / sampleRate;
        var a = rc / (rc + dt);
        var y = (float[])x.Clone();
        for (var pass = 0; pass < 3; pass++)
        {
            var prevX = 0.0;
            var prevY = 0.0;
            for (var n = 0; n < y.Length; n++)
            {
                var cur = y[n];
                var outv = a * (prevY + cur - prevX);
                prevX = cur;
                prevY = outv;
                y[n] = (float)outv;
            }
        }
        return y;
    }

    private static double[]? BuildHpcp(float[] samples, int sampleRate, CancellationToken ct)
    {
        var nyquist = sampleRate / 2.0;
        var maxHz = Math.Min(MaxHz, nyquist);
        var hann = BuildHannWindow(FrameSize);
        var halfBins = FrameSize / 2;
        var binHz = (double)sampleRate / FrameSize;

        var re = new float[FrameSize];
        var im = new float[FrameSize];
        var mag = new double[halfBins];
        var env = new double[halfBins];

        var fineChroma = new double[ChromaBins];   // SUM across frames (edmkey: no per-frame norm)
        var anyEnergy = false;

        var kMin = Math.Max(1, (int)(MinHz / binHz));
        var kMax = Math.Min(halfBins - 2, (int)(maxHz / binHz));
        // Whitening envelope half-window in bins (~150 Hz each side).
        var envHalf = Math.Max(4, (int)(150.0 / binHz));

        var peaks = new List<(double Freq, double Mag)>(128);

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < FrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            for (var k = 0; k < halfBins; k++)
                mag[k] = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

            // Simplified spectral whitening: divide each bin by a smoothed local-magnitude
            // envelope (moving average over ±envHalf bins), flattening timbre so loud
            // broadband regions don't dominate. (Essentia uses a dB BPF envelope; this is a
            // linear-domain approximation of the same idea.)
            for (var k = 0; k < halfBins; k++)
            {
                var lo = Math.Max(0, k - envHalf);
                var hi = Math.Min(halfBins - 1, k + envHalf);
                var s = 0.0;
                for (var j = lo; j <= hi; j++) s += mag[j];
                env[k] = s / (hi - lo + 1);
            }

            // Peak-pick local maxima of the WHITENED spectrum in [25, 3500] Hz.
            peaks.Clear();
            for (var k = kMin; k <= kMax; k++)
            {
                var w = mag[k] / (env[k] + 1e-12);
                var wl = mag[k - 1] / (env[k - 1] + 1e-12);
                var wr = mag[k + 1] / (env[k + 1] + 1e-12);
                if (w <= wl || w <= wr) continue;

                // Parabolic interpolation (whitened magnitude domain) for sub-bin frequency.
                var denom = wl - 2 * w + wr;
                var delta = denom != 0 ? 0.5 * (wl - wr) / denom : 0.0;
                var freq = (k + delta) * binHz;
                if (freq < MinHz || freq > maxHz) continue;
                peaks.Add((freq, w));
            }
            if (peaks.Count == 0)
                continue;

            // Keep the strongest MaxPeaks (edmkey SPECTRAL_PEAKS_MAX = 60).
            if (peaks.Count > MaxPeaks)
                peaks.Sort((p, q) => q.Mag.CompareTo(p.Mag));
            var take = Math.Min(MaxPeaks, peaks.Count);
            for (var i = 0; i < take; i++)
            {
                // Harmonic contribution: the peak may be the n-th harmonic of a fundamental
                // at freq/n; credit each candidate with decaying weight (edmkey harmonics=4).
                var hw = 1.0;
                for (var n = 1; n <= Harmonics; n++, hw *= HarmonicDecay)
                {
                    var fund = peaks[i].Freq / n;
                    if (fund < MinHz) break;
                    AddToChroma(fineChroma, fund, peaks[i].Mag * hw);
                }
            }

            anyEnergy = true;
        }

        if (!anyEnergy)
            return null;
        var total = 0.0;
        for (var b = 0; b < ChromaBins; b++) total += fineChroma[b];
        return total <= SilenceFloor ? null : fineChroma;
    }

    /// <summary>Cosine-windowed contribution of a frequency to the 36-bin fine chroma
    /// (±2 fine bins ≈ Gómez 4/3-semitone window).</summary>
    private static void AddToChroma(double[] fine, double freq, double weight)
    {
        var pos = ChromaBins * Math.Log2(freq / C0Hz);
        var center = (int)Math.Round(pos);
        for (var d = -2; d <= 2; d++)
        {
            var dist = Math.Abs(pos - (center + d));
            if (dist > 2.0) continue;
            var wnd = 0.5 * (1.0 + Math.Cos(Math.PI * dist / 2.0));
            fine[Mod(center + d, ChromaBins)] += weight * wnd;
        }
    }

    private static double[] FoldToTwelve(double[] fine)
    {
        var subEnergy = new double[BinsPerSemitone];
        for (var b = 0; b < ChromaBins; b++)
            subEnergy[b % BinsPerSemitone] += fine[b];
        var bestSub = 0;
        for (var s = 1; s < BinsPerSemitone; s++)
            if (subEnergy[s] > subEnergy[bestSub]) bestSub = s;

        var chroma = new double[12];
        for (var pc = 0; pc < 12; pc++)
        {
            var c = pc * BinsPerSemitone + bestSub;
            chroma[pc] += fine[Mod(c, ChromaBins)];
            chroma[pc] += 0.5 * fine[Mod(c - 1, ChromaBins)];
            chroma[pc] += 0.5 * fine[Mod(c + 1, ChromaBins)];
        }
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
