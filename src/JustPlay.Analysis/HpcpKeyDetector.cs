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
    // Finer than edmkey's 4096 (a deliberate improvement): at 44.1 kHz, 8192 = 5.4 Hz/bin
    // vs edmkey's 10.8 — better bass-octave peak localisation (a semitone at 100 Hz is ~6 Hz,
    // unresolvable at 4096). 50% overlap (hop = size/2) gives ~2× frames for a steadier sum.
    // Measured sweet spot: 8192 → MIREX 0.686; 16384 over-smooths across chord changes
    // (0.37 s window) and regressed to 0.667; 4096 was 0.629.
    private const int FrameSize = 8192;
    private const int HopSize = 4096;
    private const double C0Hz = 16.3515978312874;
    private const double MinHz = 25.0;                // edmkey MIN_HZ
    private const double MaxHz = 3500.0;              // edmkey MAX_HZ
    private const double HighpassHz = 200.0;          // edmkey HIGHPASS_CUTOFF, applied ×3
    private const int MaxPeaks = 60;                  // edmkey SPECTRAL_PEAKS_MAX
    private const double PcpGate = 0.2;               // edmkey PCP_THRESHOLD
    // (PeakThreshold removed — we now keep local maxima and take the strongest MaxPeaks.)
    private const int Harmonics = 4;                  // edmkey HPCP_HARMONICS
    private const double HarmonicDecay = 0.6;
    private const int BinsPerSemitone = 3;
    private const int ChromaBins = 12 * BinsPerSemitone; // 36 (fine, for tuning)
    private const double SilenceFloor = 1e-9;

    // bgate profiles (Faraldo — braw with the 4 least-relevant elements zeroed; Essentia
    // default). A full profile×metric×bias sweep over the 8192/whitened chroma found
    // bgate + cosine + minor-bias 0.05 best (MIREX 0.695 vs braw+cosine+0's 0.686). The
    // small minor bias breaks relative ties toward minor (EDM is overwhelmingly minor) and
    // now helps because the sharper whitened chroma separates the modes more cleanly.
    private static readonly double[] BgateMajor =
        [1.00, 0.00, 0.42, 0.00, 0.53, 0.37, 0.00, 0.77, 0.00, 0.38, 0.21, 0.30];
    private static readonly double[] BgateMinor =
        [1.00, 0.00, 0.36, 0.39, 0.00, 0.38, 0.00, 0.74, 0.27, 0.00, 0.42, 0.23];
    private const double MinorBias = 0.05;

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        var chroma = BuildChroma12(audio, ct);
        return chroma is null ? null : ChromagramKeyDetector.Classify(chroma, MinorBias, BgateMajor, BgateMinor);
    }

    /// <summary>
    /// Builds the final, gated, normalised 12-bin chroma (the expensive 44.1 kHz + FFT
    /// stage, through edmkey's peak-normalise + 0.2 gate). Exposed so a tuning harness can
    /// decode + build ONCE and then re-score under different profiles/metrics cheaply.
    /// </summary>
    public double[]? BuildChroma12(DecodedAudio audio, CancellationToken ct = default)
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
        return chroma;
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

        var fineChroma = new double[ChromaBins];   // SUM across frames (edmkey: no per-frame norm)
        var anyEnergy = false;

        var kMin = Math.Max(1, (int)(MinHz / binHz));
        var kMax = Math.Min(halfBins - 2, (int)(maxHz / binHz));

        var peaks = new List<(double Freq, double Mag)>(256);
        var whitened = new double[MaxPeaks];

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

            // Peak-pick local maxima of the RAW spectrum in [25, 3500] Hz, parabolic-
            // interpolated to sub-bin frequency + magnitude (Essentia SpectralPeaks).
            peaks.Clear();
            for (var k = kMin; k <= kMax; k++)
            {
                if (mag[k] <= mag[k - 1] || mag[k] <= mag[k + 1]) continue;
                var denom = mag[k - 1] - 2 * mag[k] + mag[k + 1];
                var delta = denom != 0 ? 0.5 * (mag[k - 1] - mag[k + 1]) / denom : 0.0;
                var freq = (k + delta) * binHz;
                if (freq < MinHz || freq > maxHz) continue;
                var pmag = mag[k] - 0.25 * (mag[k - 1] - mag[k + 1]) * delta;
                peaks.Add((freq, pmag));
            }
            if (peaks.Count == 0)
                continue;

            // Keep the strongest MaxPeaks (edmkey SPECTRAL_PEAKS_MAX = 60).
            if (peaks.Count > MaxPeaks)
                peaks.Sort((p, q) => q.Mag.CompareTo(p.Mag));
            var take = Math.Min(MaxPeaks, peaks.Count);

            // Faithful Essentia SpectralWhitening of the kept peaks (dB BPF envelope).
            WhitenPeaks(mag, halfBins, sampleRate, peaks, take, whitened);

            for (var i = 0; i < take; i++)
            {
                // Harmonic contribution: the peak may be the n-th harmonic of a fundamental
                // at freq/n; credit each candidate with decaying weight (edmkey harmonics=4).
                var hw = 1.0;
                for (var n = 1; n <= Harmonics; n++, hw *= HarmonicDecay)
                {
                    var fund = peaks[i].Freq / n;
                    if (fund < MinHz) break;
                    AddToChroma(fineChroma, fund, whitened[i] * hw);
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

    /// <summary>
    /// Faithful port of Essentia <c>SpectralWhitening</c> (`spectralwhitening.cpp`): builds a
    /// dB noise envelope ("true-envelope"-style BPF, 100 Hz steps, energy-weighted triangular
    /// windows) from the full magnitude spectrum, then expresses each peak relative to it —
    /// peaks at/above the envelope → 0 dB, peaks below → attenuated, far below → killed, plus
    /// a high-frequency de-emphasis tilt. Flattens timbre so harmony peaks contribute
    /// comparably regardless of instrument. Writes linear whitened magnitudes to
    /// <paramref name="outWhite"/>[0..count).
    /// </summary>
    private static void WhitenPeaks(double[] mag, int halfBins, int sampleRate,
        List<(double Freq, double Mag)> peaks, int count, double[] outWhite)
    {
        var spectralRange = sampleRate / 2.0;
        var maxFreqW = MaxHz * 1.2;          // Essentia: maxFrequency * 1.2
        const double incr = 100.0;           // bpfResolution

        // Build the BPF envelope points (freq, power), then convert to amplitude dB.
        var bpfF = new List<double>(64);
        var bpfDb = new List<double>(64);
        for (var freq = 0.0; freq <= maxFreqW && freq <= spectralRange; freq += incr)
        {
            var bf = freq - Math.Max(50.0, freq * 0.34);
            var ef = freq + Math.Max(50.0, freq * 0.58);
            var b = (int)(bf / spectralRange * (halfBins - 1) + 0.5);
            var e = (int)(ef / spectralRange * (halfBins - 1) + 0.5);
            b = Math.Clamp(b, 0, halfBins - 1);
            e = Math.Max(e, b + 1);
            e = Math.Min(halfBins, e);
            var c = (b + e) / 2.0;
            var hw = e - c;
            var n = 0.0;
            var wavg = 0.0;
            for (var i = b; i < e; i++)
            {
                var w = 1.0 - Math.Abs(i - c) / hw;
                w *= w; w *= w;              // triangular window ^4
                var eng = mag[i] * mag[i];
                w *= eng;
                wavg += eng * w;
                n += w;
            }
            if (n != 0.0) wavg /= n;
            bpfF.Add(freq);
            bpfDb.Add(wavg);                 // power for now
        }
        if (bpfF.Count == 0)
        {
            for (var i = 0; i < count; i++) outWhite[i] = peaks[i].Mag;
            return;
        }
        bpfDb[^1] = bpfDb.Count >= 2 ? bpfDb[^2] : bpfDb[^1];
        for (var i = 0; i < bpfDb.Count; i++)
            bpfDb[i] = 20.0 * Math.Log10(Math.Sqrt(bpfDb[i]) + 1e-30);

        for (var idx = 0; idx < count; idx++)
        {
            var freq = peaks[idx].Freq;
            var ampDb = 20.0 * Math.Log10(peaks[idx].Mag + 1e-30);
            double whiteDb;
            if (freq > maxFreqW - incr)
            {
                whiteDb = ampDb;
            }
            else
            {
                var envDb = InterpBpf(bpfF, bpfDb, freq);
                if (ampDb > envDb) whiteDb = 0.0;
                else if (ampDb > envDb - 30.0) whiteDb = ampDb - envDb;
                else whiteDb = -200.0;
                whiteDb -= 20.0 * freq / 4000.0;   // high-frequency de-emphasis tilt
            }
            outWhite[idx] = Math.Pow(10.0, whiteDb / 20.0);
        }
    }

    /// <summary>Piecewise-linear interpolation of the BPF envelope (clamped at the ends).</summary>
    private static double InterpBpf(List<double> xs, List<double> ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        // points are evenly spaced by `incr`, but binary-search to stay correct if not.
        var lo = 0;
        var hi = xs.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x) lo = mid; else hi = mid;
        }
        var t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        return ys[lo] + t * (ys[hi] - ys[lo]);
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
