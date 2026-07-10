using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Tracks the position of EVERY beat in a track — following local tempo drift — using
/// dynamic-programming beat tracking (Ellis 2007 "Beat Tracking by Dynamic Programming",
/// J. New Music Research; librosa <c>beat_track</c> parameterisation) extended with a
/// time-varying target period.
///
/// <para><b>Why not the beat grid we already have?</b> Every existing rhythm feature in
/// this project assumes a RIGID grid (bpm → fixed period). Pre-click-era recordings
/// breathe: a 1979 drummer drifts several BPM within one track, so a rigid grid
/// accumulates seconds of error by the last chorus. This tracker returns the measured
/// beat instants instead — the input the beatbed renderer needs to lay a synthesized
/// carrier UNDER such a recording without the two drifting apart.</para>
///
/// <para><b>Algorithm:</b></para>
/// <list type="number">
///   <item><b>Onset envelope</b> — shared <see cref="OnsetEnvelope"/> spectral flux.</item>
///   <item><b>Local tempo curve τ(frame)</b> — windowed ACF (8 s windows, 2 s hop),
///     scanned only within ±15 % of the seed period (guards against octave jumps),
///     parabolic peak interpolation for fractional lag; invalid windows (no signal)
///     filled by interpolation and the curve median-3 smoothed. This is what lets the
///     DP follow drift instead of fighting it.</item>
///   <item><b>DP beat chain</b> — cum[i] = local[i] + max(0, max_j { cum[j] −
///     tightness · ln²(Δ/τ(i)) }) over Δ ∈ [τ/2, 2τ]; backtrace from the best score near
///     the track end. The 0 floor lets chains start anywhere; at Δ ≈ τ the penalty is
///     ≈ 0, so an established chain continues essentially for free through breakdowns —
///     deliberate: the carrier must keep pulsing through the classic's quiet bridge.</item>
///   <item><b>Refinement</b> — each beat snapped to the local raw-envelope peak with
///     parabolic (sub-frame) interpolation; beats without onset support (bridged gaps)
///     keep their DP position.</item>
/// </list>
///
/// <para><b>Precondition:</b> <c>bpm</c> must already be octave-corrected
/// (<see cref="TempoOctaveCorrector"/>) — the ±15 % tempo search window cannot recover
/// from a half/double-tempo seed.</para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, no NuGet, reflection-free,
/// trim-/AOT-safe. Reuses <see cref="Fft"/> via <see cref="OnsetEnvelope"/>.</para>
/// </summary>
public static class BeatMapTracker
{
    // =========================================================================
    // Tunables
    // =========================================================================

    // ---- Local tempo curve ----
    private const double TempoWindowSeconds = 8.0;   // ACF window; ≥ 4 beat periods even at 60 BPM
    private const double TempoHopSeconds    = 2.0;   // window hop
    private const double TempoSearchSpread  = 0.15;  // ± fraction around the seed period

    // ---- DP ----
    // librosa default; higher = stiffer tempo (fights syncopation, ignores drift),
    // lower = looser (follows spurious onsets). 100 balances both for the onset
    // envelope normalised to unit standard deviation.
    private const double Tightness = 100.0;

    // ---- Refinement ----
    private const int RefineHalfWindow = 2;          // ± envelope frames searched for the raw peak

    // Where within the flux frame the physical transient sits. Flux(f) compares frame f
    // against f−1, so the peak-flux frame is the one where the Hann window has just
    // swallowed the attack — EMPIRICALLY ~0.62 of the frame length after the frame start
    // (not the ¼ a naive rising-slope argument predicts). Calibrated against synthetic
    // click trains: BeatMapTrackerTests.SteadyTempo reports the mean SIGNED error in its
    // assert message; at 0.25 it measured −17.2 ms @ 11 025 Hz → +190 samples → 318/512.
    // Fraction-of-frame is the right parameterisation: the offset scales with window
    // duration, so it stays correct at other sample rates.
    private const double TransientFrameFraction = 0.62;

    // ---- Sanity floors ----
    private const int    MinBeats   = 8;    // fewer beats → not a usable map
    private const double MinSeconds = 8.0;  // shorter audio → not a usable map

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Tracks all beats in the given mono audio. Returns null when the audio is too
    /// short, carries no onset signal, or the chain degenerates (fewer than
    /// <see cref="MinBeats"/> beats).
    /// </summary>
    /// <param name="samples">Mono float samples in [-1,1] at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate in Hz (typically 11025 for analysis).</param>
    /// <param name="bpm">Octave-corrected global BPM estimate (seed for the tempo search).</param>
    /// <param name="ct">Cancellation token.</param>
    public static BeatMap? Track(
        float[] samples, int sampleRate, double bpm, CancellationToken ct = default)
    {
        if (samples is null || sampleRate <= 0 || bpm <= 0)
            return null;
        if (samples.Length < MinSeconds * sampleRate)
            return null;

        var env = OnsetEnvelope.Build(samples, ct);
        if (env is null || env.Length < 32)
            return null;

        var framesPerSecond = (double)sampleRate / OnsetEnvelope.HopSize;
        var seedPeriod = framesPerSecond * 60.0 / bpm;   // envelope frames per beat
        if (seedPeriod < 4.0 || seedPeriod * MinBeats > env.Length)
            return null;

        // The envelope must carry signal (digital silence → all-zero flux).
        var envMean = 0.0;
        for (var i = 0; i < env.Length; i++) envMean += env[i];
        envMean /= env.Length;
        if (envMean <= 0.0)
            return null;

        // ---- Stage 1: local tempo curve τ(frame) ----
        var tau = BuildTempoCurve(env, seedPeriod, framesPerSecond, ct);

        // ---- Stage 2: DP beat chain ----
        var beatFrames = RunDynamicProgramming(env, tau, ct);
        if (beatFrames.Count < MinBeats)
            return null;

        // ---- Stage 3: sub-frame refinement, strengths, stats ----
        return Finalize(env, beatFrames, envMean, sampleRate, ct);
    }

    // =========================================================================
    // Stage 1 — local tempo curve
    // =========================================================================

    /// <summary>
    /// Estimates the beat period (in envelope frames) as a function of time via windowed
    /// autocorrelation restricted to ±<see cref="TempoSearchSpread"/> around the seed.
    /// Returns one period value per envelope frame (piecewise-linear between window centres).
    /// </summary>
    private static double[] BuildTempoCurve(
        double[] env, double seedPeriod, double framesPerSecond, CancellationToken ct)
    {
        var n = env.Length;
        var tau = new double[n];

        var w   = Math.Min(n, (int)Math.Round(TempoWindowSeconds * framesPerSecond));
        var hop = Math.Max(1, (int)Math.Round(TempoHopSeconds * framesPerSecond));

        var lagMin = Math.Max(2, (int)Math.Floor(seedPeriod * (1.0 - TempoSearchSpread)));
        var lagMax = (int)Math.Ceiling(seedPeriod * (1.0 + TempoSearchSpread));

        // Degenerate: window can't contain the largest lag with room to correlate.
        if (lagMax >= w - 1)
        {
            Array.Fill(tau, seedPeriod);
            return tau;
        }

        // ---- Per-window fractional beat period ----
        var centers = new List<double>();
        var periods = new List<double>();   // NaN = window had no signal

        // Extend the scanned lag range by 1 on each side so the best in-range lag
        // always has neighbours for parabolic interpolation.
        var lagLo = Math.Max(1, lagMin - 1);
        var lagHi = Math.Min(w - 2, lagMax + 1);
        var r = new double[lagHi + 1];       // r[lag] valid for lag in [lagLo, lagHi]

        for (var start = 0; start == 0 || start + w <= n; start += hop)
        {
            ct.ThrowIfCancellationRequested();
            if (start + w > n) break;

            for (var lag = lagLo; lag <= lagHi; lag++)
            {
                var sum = 0.0;
                var end = start + w - lag;
                for (var i = start; i < end; i++)
                    sum += env[i] * env[i + lag];
                r[lag] = sum / (w - lag);    // count-normalised: comparable across lags
            }

            // Best lag within the ACTUAL search range.
            var bestLag = lagMin;
            var bestVal = double.MinValue;
            for (var lag = lagMin; lag <= Math.Min(lagMax, lagHi); lag++)
            {
                if (r[lag] > bestVal) { bestVal = r[lag]; bestLag = lag; }
            }

            double period;
            if (bestVal <= 0.0)
            {
                period = double.NaN;         // silent/aperiodic window → fill later
            }
            else
            {
                // Parabolic (sub-lag) interpolation around the peak.
                period = bestLag;
                if (bestLag - 1 >= lagLo && bestLag + 1 <= lagHi)
                {
                    var y1 = r[bestLag - 1];
                    var y2 = r[bestLag];
                    var y3 = r[bestLag + 1];
                    var denom = y1 - 2.0 * y2 + y3;
                    if (Math.Abs(denom) > 1e-12)
                    {
                        var d = 0.5 * (y1 - y3) / denom;
                        if (d > -0.5 && d < 0.5) period = bestLag + d;
                    }
                }
            }

            centers.Add(start + w * 0.5);
            periods.Add(period);
        }

        if (centers.Count == 0)
        {
            Array.Fill(tau, seedPeriod);
            return tau;
        }

        FillInvalidPeriods(periods, seedPeriod);
        MedianSmooth3(periods);

        // ---- Interpolate window periods to a per-frame curve ----
        if (centers.Count == 1)
        {
            Array.Fill(tau, periods[0]);
            return tau;
        }

        var seg = 0;
        for (var i = 0; i < n; i++)
        {
            if (i <= centers[0]) { tau[i] = periods[0]; continue; }
            if (i >= centers[^1]) { tau[i] = periods[^1]; continue; }
            while (seg < centers.Count - 2 && centers[seg + 1] < i) seg++;
            var t0 = centers[seg];
            var t1 = centers[seg + 1];
            var f  = (i - t0) / (t1 - t0);
            tau[i] = periods[seg] + f * (periods[seg + 1] - periods[seg]);
        }
        return tau;
    }

    /// <summary>Replaces NaN periods: interior by linear interpolation between valid
    /// neighbours, leading/trailing by the nearest valid value; all-NaN → seed.</summary>
    private static void FillInvalidPeriods(List<double> periods, double seedPeriod)
    {
        var firstValid = -1;
        for (var k = 0; k < periods.Count; k++)
            if (!double.IsNaN(periods[k])) { firstValid = k; break; }

        if (firstValid < 0)
        {
            for (var k = 0; k < periods.Count; k++) periods[k] = seedPeriod;
            return;
        }

        // Leading run.
        for (var k = 0; k < firstValid; k++) periods[k] = periods[firstValid];

        var lastValid = firstValid;
        for (var k = firstValid + 1; k < periods.Count; k++)
        {
            if (!double.IsNaN(periods[k])) { lastValid = k; continue; }

            // Find the next valid entry (if any).
            var next = -1;
            for (var m = k + 1; m < periods.Count; m++)
                if (!double.IsNaN(periods[m])) { next = m; break; }

            if (next < 0)
            {
                // Trailing run.
                for (var m = k; m < periods.Count; m++) periods[m] = periods[lastValid];
                return;
            }

            // Interior gap [k, next): linear interpolation lastValid → next.
            for (var m = k; m < next; m++)
            {
                var f = (double)(m - lastValid) / (next - lastValid);
                periods[m] = periods[lastValid] + f * (periods[next] - periods[lastValid]);
            }
        }
    }

    /// <summary>In-place 3-point median smoothing (ends untouched).</summary>
    private static void MedianSmooth3(List<double> v)
    {
        if (v.Count < 3) return;
        var prev = v[0];
        for (var k = 1; k < v.Count - 1; k++)
        {
            var a = prev; var b = v[k]; var c = v[k + 1];
            prev = v[k];
            // median of three
            var lo = Math.Min(a, Math.Min(b, c));
            var hi = Math.Max(a, Math.Max(b, c));
            v[k] = a + b + c - lo - hi;
        }
    }

    // =========================================================================
    // Stage 2 — dynamic programming
    // =========================================================================

    /// <summary>
    /// Ellis-style DP over the envelope with a per-frame target period. Returns the
    /// backtraced chain of beat frames (ascending), possibly empty.
    /// </summary>
    private static List<int> RunDynamicProgramming(
        double[] env, double[] tau, CancellationToken ct)
    {
        var n = env.Length;

        // ---- Local score: envelope normalised to unit std, Gaussian-smoothed ----
        var mean = 0.0;
        for (var i = 0; i < n; i++) mean += env[i];
        mean /= n;
        var variance = 0.0;
        for (var i = 0; i < n; i++) { var d = env[i] - mean; variance += d * d; }
        var sd = Math.Sqrt(variance / n);
        if (sd <= 0.0) return [];

        var meanTau = 0.0;
        for (var i = 0; i < n; i++) meanTau += tau[i];
        meanTau /= n;

        var local = GaussianSmoothNormalized(env, sd, sigma: Math.Max(1.0, meanTau / 32.0), ct);

        // ---- DP sweep ----
        var cum      = new double[n];
        var backlink = new int[n];
        Array.Fill(backlink, -1);

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();

            var t   = tau[i];
            var jHi = i - (int)Math.Round(0.5 * t);
            var best  = 0.0;   // fresh-start floor: chains only persist while profitable
            var bestJ = -1;

            if (jHi >= 0)
            {
                var jLo = Math.Max(0, i - (int)Math.Round(2.0 * t));
                for (var j = jLo; j <= jHi; j++)
                {
                    var logRatio = Math.Log((i - j) / t);
                    var score = cum[j] - Tightness * logRatio * logRatio;
                    if (score > best) { best = score; bestJ = j; }
                }
            }

            cum[i]      = local[i] + best;
            backlink[i] = bestJ;
        }

        // ---- Backtrace from the best score within the final two periods ----
        var tailStart = Math.Max(0, n - (int)Math.Round(2.0 * tau[n - 1]));
        var head = tailStart;
        for (var i = tailStart + 1; i < n; i++)
            if (cum[i] > cum[head]) head = i;

        var beats = new List<int>();
        for (var i = head; i >= 0; i = backlink[i])
            beats.Add(i);
        beats.Reverse();
        return beats;
    }

    /// <summary>env/sd convolved with a Gaussian kernel (clamped at the edges).</summary>
    private static double[] GaussianSmoothNormalized(
        double[] env, double sd, double sigma, CancellationToken ct)
    {
        var n = env.Length;
        var half = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
        var kernel = new double[2 * half + 1];
        var kSum = 0.0;
        for (var k = -half; k <= half; k++)
        {
            var v = Math.Exp(-0.5 * (k / sigma) * (k / sigma));
            kernel[k + half] = v;
            kSum += v;
        }
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= kSum;

        var outv = new double[n];
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var acc = 0.0;
            var wSum = 0.0;
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n - 1, i + half);
            for (var j = lo; j <= hi; j++)
            {
                var kv = kernel[j - i + half];
                acc  += kv * env[j];
                wSum += kv;
            }
            outv[i] = acc / (wSum * sd);
        }
        return outv;
    }

    // =========================================================================
    // Stage 3 — refinement, strengths, stats
    // =========================================================================

    private static BeatMap? Finalize(
        double[] env, List<int> beatFrames, double envMean, int sampleRate, CancellationToken ct)
    {
        var n   = env.Length;
        var hop = OnsetEnvelope.HopSize;

        // Same "has a real hit" convention as RhythmPatternDetector.ComputeFourOnFloor.
        var supportThreshold = envMean * 0.5;

        var times = new List<double>(beatFrames.Count);
        var peaks = new List<double>(beatFrames.Count);

        foreach (var b in beatFrames)
        {
            ct.ThrowIfCancellationRequested();

            // Raw-envelope peak in the refinement neighbourhood.
            var lo = Math.Max(0, b - RefineHalfWindow);
            var hi = Math.Min(n - 1, b + RefineHalfWindow);
            var p  = b;
            var pv = env[b];
            for (var f = lo; f <= hi; f++)
                if (env[f] > pv) { pv = env[f]; p = f; }

            // Beats without onset support (bridged breakdowns) keep the DP position —
            // snapping them to noise would jitter the interpolated pulse.
            double frac = b;
            if (pv > supportThreshold)
            {
                frac = p;
                if (p > 0 && p < n - 1)
                {
                    var y1 = env[p - 1];
                    var y2 = env[p];
                    var y3 = env[p + 1];
                    var denom = y1 - 2.0 * y2 + y3;
                    if (Math.Abs(denom) > 1e-12)
                    {
                        var d = 0.5 * (y1 - y3) / denom;
                        if (d > -0.5 && d < 0.5) frac = p + d;
                    }
                }
            }

            times.Add((frac * hop + OnsetEnvelope.FrameSize * TransientFrameFraction) / sampleRate);
            peaks.Add(pv);
        }

        // Refinement can theoretically reorder ultra-close beats — enforce monotonicity.
        var keptTimes = new List<double>(times.Count);
        var keptPeaks = new List<double>(peaks.Count);
        for (var i = 0; i < times.Count; i++)
        {
            if (keptTimes.Count > 0 && times[i] <= keptTimes[^1] + 1e-3) continue;
            keptTimes.Add(times[i]);
            keptPeaks.Add(peaks[i]);
        }
        if (keptTimes.Count < MinBeats)
            return null;

        // ---- Strengths: p95-normalised ----
        var sorted = keptPeaks.ToArray();
        Array.Sort(sorted);
        var p95 = sorted[(int)(0.95 * (sorted.Length - 1))];
        if (p95 <= 0.0)
            return null;

        var strengths = new float[keptPeaks.Count];
        var supported = 0;
        for (var i = 0; i < keptPeaks.Count; i++)
        {
            strengths[i] = (float)Math.Clamp(keptPeaks[i] / p95, 0.0, 1.0);
            if (keptPeaks[i] > supportThreshold) supported++;
        }
        var coverage = (double)supported / keptPeaks.Count;

        // ---- Local BPM stats (median / p5 / p95 of interval tempi) ----
        var bpms = new double[keptTimes.Count - 1];
        for (var i = 1; i < keptTimes.Count; i++)
            bpms[i - 1] = 60.0 / (keptTimes[i] - keptTimes[i - 1]);
        Array.Sort(bpms);
        var median = bpms[bpms.Length / 2];
        var minBpm = bpms[(int)(0.05 * (bpms.Length - 1))];
        var maxBpm = bpms[(int)(0.95 * (bpms.Length - 1))];

        return new BeatMap(keptTimes.ToArray(), strengths, median, minBpm, maxBpm, coverage);
    }
}
