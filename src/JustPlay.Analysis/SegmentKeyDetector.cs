using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// EXPERIMENTAL - N19 segment-aware key detection study (2026-06-24).
///
/// <para>Motivation: whole-song chroma averaging smears tonally-ambiguous sections
/// (pure-beat intros, amodal drops) with the tonal breakdowns where the key is actually
/// audible. MixInKey's suspected edge over global detectors is per-segment voting on
/// STABLE, TONAL sections. This class measures that hypothesis on GiantSteps.</para>
///
/// <para><b>Three experimental variants, all sharing the shipped HpcpKeyDetector's
/// HPCP feature extractor:</b></para>
/// <list type="number">
///   <item><b>Global (V1 baseline):</b> identical to shipped <see cref="HpcpKeyDetector"/> -
///     the baseline to beat, reproduced here for a clean apples-to-apples comparison.</item>
///   <item><b>Per-tonal-segment vote (V2):</b> use <see cref="StructureDetector"/> boundaries
///     to slice the audio into sections, compute a 12-bin chroma per section, gate on
///     tonalness (spectral flatness - tonal ~ low flatness), then take a weighted majority
///     vote. Each tonal segment's vote is weighted by length x (1-flatness). A minimum-
///     tonalness threshold avoids drum-only intro windows contributing noise.
///     Falls back to global when fewer than 2 tonal segments are found.</item>
///   <item><b>HPSS flatness-gated chroma (V3):</b> uses <see cref="HpssDrumSuppressor"/>
///     to build a per-frame-flatness-WEIGHTED chroma over the whole track. High-flatness
///     (percussive/noise) frames contribute less energy, sharpening the harmonic profile.
///     This is an approximation to true HPSS (Fitzgerald 2010) that avoids the O(T^2F)
///     cost of the full time-median approach.</item>
/// </list>
///
/// <para><b>Agreed corrections from the N19 brief (baked in):</b></para>
/// <list type="bullet">
///   <item>Drums HURT key - suppression via flatness gating, not "focus on bass/drums".</item>
///   <item>Bass alone gives the tonic but NOT the mode - use the full 100-3500 Hz band.</item>
///   <item>"First 30s" is often beat-only/atonal - gate by TONALNESS, not a fixed window.</item>
/// </list>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass. Hand-rolled FFT only. No new NuGet.
/// Trim/AOT-safe. NOT the shipped path - experiment only.</para>
/// </summary>
public sealed class SegmentKeyDetector
{
    // Use the same HPCP feature chain as the shipped HpcpKeyDetector (44.1 kHz).
    private readonly HpcpKeyDetector _hpcp = new();

    // Structure detector: coarse boundaries (15 s minimum = section-level, not beat-level).
    private readonly StructureDetector _structure = new() { MinBoundaryGapSeconds = 15.0 };

    /// <summary>
    /// Minimum tonal weight for a segment to participate in the vote.
    /// Spectral flatness  in  [0,1]; 0=pure tone, 1=white noise.
    /// Tonalness = 1 - flatness. Segments with tonalness below this are excluded.
    /// Default 0.05 = only exclude segments that are almost entirely percussive.
    /// </summary>
    public double MinTonalWeight { get; init; } = 0.05;

    // -- Variant 1: global baseline ----------------------------------------------------

    /// <summary>
    /// BASELINE (V1): the shipped HpcpKeyDetector.Detect() - same result, same feature,
    /// reproduced here so all variants share the same decode-and-run call site.
    /// </summary>
    public (MusicalKey Key, double Confidence)? DetectGlobal(DecodedAudio audio, CancellationToken ct = default)
        => _hpcp.Detect(audio, ct);

    // -- Variant 2: per-tonal-segment vote --------------------------------------------

    /// <summary>
    /// PER-TONAL-SEGMENT VOTE (V2): slices by structural boundaries, computes chroma per
    /// tonal segment, and majority-votes weighted by segment-length x tonalness.
    /// Returns a <see cref="SegmentVoteDetails"/> alongside the key result.
    /// Falls back to the global result when fewer than 2 tonal segments are found.
    /// </summary>
    public (MusicalKey Key, double Confidence, SegmentVoteDetails Details)? DetectSegmented(
        DecodedAudio audio, double? bpm = null, CancellationToken ct = default)
    {
        if (audio.Samples is null || audio.SampleRate <= 0) return null;

        // 1. Structural segmentation (StructureDetector needs ~11 kHz signal).
        var structAudio = DownsampleBy4(audio);
        var boundaries  = _structure.Detect(structAudio, bpm, ct);

        // 2. Build segment time-ranges from boundaries.
        var segments = BuildSegments(boundaries, audio.SampleRate, audio.Samples.Length);

        // 3. Compute chroma + tonalness per segment, collecting tonal ones.
        var votes = new List<SegmentVote>(segments.Count);
        foreach (var (startSample, endSample) in segments)
        {
            ct.ThrowIfCancellationRequested();
            var segAudio = SliceAudio(audio, startSample, endSample);
            var chroma   = _hpcp.BuildChroma12(segAudio, ct);
            if (chroma is null) continue;

            var tonalness  = ComputeTonalness(segAudio, ct);
            var durationS  = (double)(endSample - startSample) / audio.SampleRate;
            var weight     = durationS * tonalness;

            if (tonalness >= MinTonalWeight)
                votes.Add(new SegmentVote(chroma, weight,
                    startSample / (double)audio.SampleRate,
                    endSample   / (double)audio.SampleRate,
                    tonalness));
        }

        var details = new SegmentVoteDetails(votes, segments.Count);

        // Fallback: if too few tonal segments, use the global result.
        if (votes.Count < 2)
        {
            var fallback = DetectGlobal(audio, ct);
            return fallback is null ? null
                : (fallback.Value.Key, fallback.Value.Confidence, details with { UsedFallback = true });
        }

        // 4. Weighted vote: accumulate weight to each of 24 possible keys.
        var keyScores = new double[24];  // [pitch*2 + (minor?1:0)]
        var totalWeight = 0.0;
        foreach (var v in votes)
        {
            var d = ChromagramKeyDetector.Classify(v.Chroma, 0.0);
            if (d is null) continue;
            var idx = d.Value.Key.PitchClass * 2 + (d.Value.Key.Mode == KeyMode.Minor ? 1 : 0);
            keyScores[idx] += v.Weight;
            totalWeight    += v.Weight;
        }

        if (totalWeight <= 0) return null;

        // Pick winner (highest weight) and runner-up (for confidence margin).
        var best   = 0;
        for (var i = 1; i < 24; i++) if (keyScores[i] > keyScores[best]) best = i;
        var second = -1;
        for (var i = 0; i < 24; i++)
        {
            if (i == best) continue;
            if (second < 0 || keyScores[i] > keyScores[second]) second = i;
        }

        var confidence  = (second < 0 || keyScores[best] <= 0) ? 0.0
            : Math.Clamp((keyScores[best] - keyScores[second]) / keyScores[best], 0.0, 1.0);
        var winnerKey   = new MusicalKey(best / 2, best % 2 == 1 ? KeyMode.Minor : KeyMode.Major);
        return (winnerKey, confidence, details);
    }

    // -- Variant 3: HPSS flatness-gated chroma ----------------------------------------

    /// <summary>
    /// HPSS FLATNESS-GATED (V3): uses <see cref="HpssDrumSuppressor.BuildGatedChroma12"/>
    /// which accumulates HPCP energy weighted by per-frame tonalness (1-flatness), so
    /// heavily percussive frames contribute little to the final chroma.
    ///
    /// <para>Simpler than full median-filter HPSS (Fitzgerald 2010) but O(TxF) vs O(TxFxL)
    /// and measurable in a night session. If it helps, the full HPSS can be implemented
    /// as a follow-up.</para>
    /// </summary>
    public (MusicalKey Key, double Confidence)? DetectHpssGated(
        DecodedAudio audio, CancellationToken ct = default)
    {
        var chroma = HpssDrumSuppressor.BuildGatedChroma12(audio, ct);
        return chroma is null ? null : ChromagramKeyDetector.Classify(chroma, 0.0);
    }

    // -- Helpers -----------------------------------------------------------------------

    /// <summary>
    /// Compute tonalness = 1 - spectral_flatness for a segment.
    /// Spectral flatness = geometric_mean / arithmetic_mean of |X| in [100-3500 Hz].
    /// Pure tones -> near 0 (very flat distribution -> low flatness), noise -> near 1.
    /// We average across frames (non-overlapping 2048-sample frames for speed).
    /// </summary>
    private static double ComputeTonalness(DecodedAudio audio, CancellationToken ct)
    {
        const int FrameSz = 2048;
        const double MinFlatHz = 100.0;
        const double MaxFlatHz = 3500.0;
        const double eps = 1e-30;

        var samples = audio.Samples;
        if (samples is null || samples.Length < FrameSz) return 0.0;

        var hann    = BuildHannWindow(FrameSz);
        var re      = new float[FrameSz];
        var im      = new float[FrameSz];
        var halfBins = FrameSz / 2;
        var binHz   = (double)audio.SampleRate / FrameSz;
        var kMin    = Math.Max(1, (int)(MinFlatHz / binHz));
        var kMax    = Math.Min(halfBins - 1, (int)(MaxFlatHz / binHz));
        var bandN   = kMax - kMin + 1;
        if (bandN <= 0) return 0.0;

        var flatnessSum = 0.0;
        var frameCount  = 0;

        for (var start = 0; start + FrameSz <= samples.Length; start += FrameSz)
        {
            ct.ThrowIfCancellationRequested();
            for (var n = 0; n < FrameSz; n++) { re[n] = samples[start + n] * hann[n]; im[n] = 0f; }
            Fft.Forward(re, im);

            double logSum = 0.0, linSum = 0.0;
            for (var k = kMin; k <= kMax; k++)
            {
                var m = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]) + eps;
                logSum += Math.Log(m);
                linSum += m;
            }
            var geom      = Math.Exp(logSum / bandN);
            var arith     = linSum / bandN;
            var flatness  = arith > eps ? Math.Clamp(geom / arith, 0.0, 1.0) : 1.0;
            flatnessSum  += flatness;
            frameCount++;
        }

        if (frameCount == 0) return 0.0;
        var avgFlatness = flatnessSum / frameCount;
        return 1.0 - avgFlatness;
    }

    private static DecodedAudio SliceAudio(DecodedAudio audio, int startSample, int endSample)
    {
        endSample = Math.Min(endSample, audio.Samples!.Length);
        var len   = endSample - startSample;
        if (len <= 0) return new DecodedAudio([], audio.SampleRate);
        var slice = new float[len];
        Array.Copy(audio.Samples!, startSample, slice, 0, len);
        return new DecodedAudio(slice, audio.SampleRate);
    }

    /// <summary>Crude 4x downsample (no anti-aliasing filter - StructureDetector is robust).</summary>
    private static DecodedAudio DownsampleBy4(DecodedAudio audio)
    {
        var samples = audio.Samples!;
        var newLen  = samples.Length / 4;
        var dst     = new float[newLen];
        for (var i = 0; i < newLen; i++) dst[i] = samples[i * 4];
        return new DecodedAudio(dst, audio.SampleRate / 4);
    }

    private static List<(int Start, int End)> BuildSegments(
        IReadOnlyList<StructureBoundary> boundaries, int sampleRate, int totalSamples)
    {
        var times = new List<double> { 0.0 };
        foreach (var b in boundaries) times.Add(b.TimeSeconds);
        times.Add(totalSamples / (double)sampleRate);

        var segments = new List<(int, int)>(times.Count - 1);
        for (var i = 0; i < times.Count - 1; i++)
        {
            var s = (int)(times[i]     * sampleRate);
            var e = (int)(times[i + 1] * sampleRate);
            if (e > s + sampleRate)  // at least 1 second
                segments.Add((s, e));
        }
        return segments;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}

/// <summary>One segment's key vote in the segmented detector (V2).</summary>
public readonly record struct SegmentVote(
    double[] Chroma,
    double Weight,
    double StartSeconds,
    double EndSeconds,
    double Tonalness);

/// <summary>Diagnostics from the segmented-vote run (surfaced in the GiantSteps report).</summary>
public record SegmentVoteDetails(
    IReadOnlyList<SegmentVote> TonalSegments,
    int TotalSegments)
{
    public bool UsedFallback { get; init; } = false;
    public int TonalSegmentCount => TonalSegments.Count;
}
