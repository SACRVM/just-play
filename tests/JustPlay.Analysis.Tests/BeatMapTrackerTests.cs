using JustPlay.Analysis;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="BeatMapTracker"/>.
///
/// All signals are synthetic click tracks with KNOWN ground-truth beat times, so the
/// tests measure real timing error in milliseconds — the metric the beatbed renderer
/// lives or dies by:
/// <list type="number">
///   <item><b>Steady tempo</b> — every truth beat found within tolerance; the mean
///     SIGNED error calibrates <c>TransientFrameFraction</c> (reported in the assert
///     message so re-tuning is one-shot).</item>
///   <item><b>Drifting tempo</b> (115→125 BPM) — THE reason this module exists: a rigid
///     grid accumulates seconds of error here; the tracker must follow the drift and
///     report the range via MinBpm/MaxBpm.</item>
///   <item><b>Human jitter</b> — gaussian ±6 ms per beat stays locked.</item>
///   <item><b>Breakdown gap</b> — 8 s of silence mid-track is bridged: beats keep
///     coming at the right instants, Coverage drops below 1.</item>
///   <item><b>Seed robustness</b> — a slightly-off seed BPM (detector error) still locks.</item>
///   <item>Guards: null / silence / too short / bad BPM → null (no throw).</item>
/// </list>
/// </summary>
public class BeatMapTrackerTests
{
    private const int SampleRate = 11025;

    // =========================================================================
    // Synthetic signal helpers
    // =========================================================================

    /// <summary>Adds a click (3 kHz burst, instant attack, 2 ms exponential decay) at
    /// <paramref name="timeSec"/> — a clean transient for spectral flux.</summary>
    private static void AddClick(float[] buf, double timeSec, float amp = 0.9f)
    {
        var start = (int)Math.Round(timeSec * SampleRate);
        var len = (int)(0.006 * SampleRate);
        for (var i = 0; i < len; i++)
        {
            var idx = start + i;
            if (idx < 0 || idx >= buf.Length) continue;
            var t = i / (double)SampleRate;
            buf[idx] += (float)(amp * Math.Exp(-t / 0.002) * Math.Sin(2.0 * Math.PI * 3000.0 * t));
        }
    }

    /// <summary>
    /// Builds a click track whose instantaneous tempo is <paramref name="bpmAt"/>(t).
    /// Returns the samples and the ground-truth beat times. Beats inside
    /// [<paramref name="muteFrom"/>, <paramref name="muteTo"/>) are still counted as
    /// truth (the drummer keeps time silently) but receive no click.
    /// </summary>
    private static (float[] Samples, List<double> Truth) ClickTrack(
        Func<double, double> bpmAt, double durationSec,
        double firstBeatSec = 0.2, double muteFrom = -1.0, double muteTo = -1.0,
        Func<double, double>? jitter = null)
    {
        var samples = new float[(int)(durationSec * SampleRate)];
        var truth = new List<double>();
        var t = firstBeatSec;
        while (t < durationSec - 0.05)
        {
            var beatTime = t + (jitter?.Invoke(t) ?? 0.0);
            truth.Add(beatTime);
            if (beatTime < muteFrom || beatTime >= muteTo)
                AddClick(samples, beatTime);
            t += 60.0 / bpmAt(t);
        }
        return (samples, truth);
    }

    /// <summary>
    /// Matches truth beats (skipping <paramref name="skipEnds"/> at each end) to the
    /// nearest detected beat and returns error stats in milliseconds.
    /// </summary>
    private static (double MeanAbsMs, double MeanSignedMs, double MaxAbsMs, int Missed) MatchTruth(
        IReadOnlyList<double> truth, double[] detected, double tolMs, int skipEnds = 2)
    {
        double sumAbs = 0, sumSigned = 0, maxAbs = 0;
        var matched = 0;
        var missed = 0;

        for (var k = skipEnds; k < truth.Count - skipEnds; k++)
        {
            var best = double.MaxValue;
            foreach (var d in detected)
            {
                var e = d - truth[k];
                if (Math.Abs(e) < Math.Abs(best)) best = e;
            }
            var absMs = Math.Abs(best) * 1000.0;
            if (absMs > tolMs) { missed++; continue; }
            matched++;
            sumAbs    += absMs;
            sumSigned += best * 1000.0;
            if (absMs > maxAbs) maxAbs = absMs;
        }

        return matched == 0
            ? (double.MaxValue, double.MaxValue, double.MaxValue, missed)
            : (sumAbs / matched, sumSigned / matched, maxAbs, missed);
    }

    // =========================================================================
    // Guards
    // =========================================================================

    [Fact]
    public void NullSamples_ReturnsNull()
        => Assert.Null(BeatMapTracker.Track(null!, SampleRate, 120.0));

    [Fact]
    public void TooShort_ReturnsNull()
        => Assert.Null(BeatMapTracker.Track(new float[SampleRate], SampleRate, 120.0));

    [Fact]
    public void ZeroBpm_ReturnsNull()
    {
        var (samples, _) = ClickTrack(_ => 120.0, 30.0);
        Assert.Null(BeatMapTracker.Track(samples, SampleRate, 0.0));
    }

    [Fact]
    public void Silence_ReturnsNull()
        => Assert.Null(BeatMapTracker.Track(new float[SampleRate * 30], SampleRate, 120.0));

    // =========================================================================
    // Steady tempo — beat positions + TransientFrameFraction calibration
    // =========================================================================

    [Fact]
    public void SteadyTempo_FindsEveryBeat_WithinTolerance()
    {
        var (samples, truth) = ClickTrack(_ => 120.0, 60.0);
        var map = BeatMapTracker.Track(samples, SampleRate, 120.0);

        Assert.NotNull(map);
        Assert.InRange(map!.Count, truth.Count - 3, truth.Count + 3);

        var (meanAbs, meanSigned, maxAbs, missed) = MatchTruth(truth, map.BeatTimes, tolMs: 25.0);
        Assert.True(missed == 0,
            $"{missed} truth beats have no detected beat within 25 ms");
        // meanSigned calibrates TransientFrameFraction: it must sit near 0 ms.
        Assert.True(Math.Abs(meanSigned) <= 8.0,
            $"systematic offset {meanSigned:F2} ms — re-tune BeatMapTracker.TransientFrameFraction " +
            $"(meanAbs={meanAbs:F2} ms, maxAbs={maxAbs:F2} ms)");
        Assert.True(meanAbs <= 12.0, $"meanAbs={meanAbs:F2} ms exceeds 12 ms");

        Assert.InRange(map.MedianBpm, 119.0, 121.0);
        Assert.True(map.Coverage > 0.9, $"coverage={map.Coverage:F2}");
    }

    // =========================================================================
    // Drifting tempo — the reason this module exists
    // =========================================================================

    [Fact]
    public void DriftingTempo_FollowsTheDrift()
    {
        const double duration = 90.0;
        // 115 → 125 BPM linearly over the track (a "breathing" 1979 drummer, exaggerated).
        var (samples, truth) = ClickTrack(t => 115.0 + 10.0 * (t / duration), duration);

        // Seed = the single global BPM a scalar detector would report.
        var map = BeatMapTracker.Track(samples, SampleRate, 120.0);

        Assert.NotNull(map);
        var (meanAbs, _, maxAbs, missed) = MatchTruth(truth, map!.BeatTimes, tolMs: 30.0);
        Assert.True(missed == 0,
            $"{missed} truth beats missed >30 ms — a rigid 120 grid would fail this by seconds " +
            $"(meanAbs={meanAbs:F2} ms, maxAbs={maxAbs:F2} ms)");
        Assert.True(meanAbs <= 12.0, $"meanAbs={meanAbs:F2} ms exceeds 12 ms");

        // The drift range must be visible in the stats.
        Assert.InRange(map.MedianBpm, 117.0, 123.0);
        Assert.True(map.MinBpm < 118.0 && map.MinBpm > 110.0, $"MinBpm={map.MinBpm:F1}");
        Assert.True(map.MaxBpm > 122.0 && map.MaxBpm < 130.0, $"MaxBpm={map.MaxBpm:F1}");
    }

    // =========================================================================
    // Human jitter
    // =========================================================================

    [Fact]
    public void JitteredBeats_StayLocked()
    {
        // Gaussian ±6 ms per beat (deterministic Box-Muller).
        var rng = new Random(42);
        double Gauss(double _)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return 0.006 * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        var (samples, truth) = ClickTrack(_ => 122.0, 60.0, jitter: Gauss);
        var map = BeatMapTracker.Track(samples, SampleRate, 122.0);

        Assert.NotNull(map);
        var (meanAbs, _, _, missed) = MatchTruth(truth, map!.BeatTimes, tolMs: 30.0);
        Assert.True(missed <= 1, $"{missed} jittered beats missed >30 ms");
        Assert.True(meanAbs <= 14.0, $"meanAbs={meanAbs:F2} ms exceeds 14 ms");
    }

    // =========================================================================
    // Breakdown gap — the carrier must keep pulsing through the quiet bridge
    // =========================================================================

    [Fact]
    public void BreakdownGap_IsBridgedOnTime()
    {
        var (samples, truth) = ClickTrack(_ => 120.0, 60.0, muteFrom: 25.0, muteTo: 33.0);
        var map = BeatMapTracker.Track(samples, SampleRate, 120.0);

        Assert.NotNull(map);

        // Every truth beat INSIDE the silent gap must still get a detected beat close by.
        var gapTruth = truth.Where(t => t >= 25.0 && t < 33.0).ToList();
        Assert.True(gapTruth.Count > 10, "test setup: gap should contain beats");
        foreach (var g in gapTruth)
        {
            var nearest = map!.BeatTimes.Min(d => Math.Abs(d - g)) * 1000.0;
            Assert.True(nearest <= 45.0,
                $"gap beat at {g:F2}s has nearest detected {nearest:F1} ms away");
        }

        // Coverage must reflect the bridged (unsupported) beats.
        Assert.True(map!.Coverage < 0.95 && map.Coverage > 0.6,
            $"coverage={map.Coverage:F2} — expected ~0.87 for an 8 s gap in 60 s");
    }

    // =========================================================================
    // Seed robustness — a slightly wrong global BPM still locks
    // =========================================================================

    [Fact]
    public void SlightlyOffSeed_StillLocksOntoTrueTempo()
    {
        var (samples, truth) = ClickTrack(_ => 120.0, 60.0);
        // Scalar detector was 2.5% off — well inside the ±15% search spread.
        var map = BeatMapTracker.Track(samples, SampleRate, 117.0);

        Assert.NotNull(map);
        var (meanAbs, _, _, missed) = MatchTruth(truth, map!.BeatTimes, tolMs: 25.0);
        Assert.True(missed == 0, $"{missed} beats missed with off seed");
        Assert.True(meanAbs <= 12.0, $"meanAbs={meanAbs:F2} ms");
        Assert.InRange(map.MedianBpm, 119.0, 121.0);
    }
}
