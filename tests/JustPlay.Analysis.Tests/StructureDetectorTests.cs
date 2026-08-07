using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="StructureDetector"/>.
///
/// All signals are synthetically generated - no real audio files are read.
/// The core design principle for each test: **if the detector cannot find a
/// clear boundary in a signal that was deliberately constructed to have one,
/// it is broken.**
///
/// Test categories:
/// 1. Edge-case / contract (too short, silence, empty).
/// 2. Two-section boundary detection: low-energy -> high-energy transition.
///    This is the minimum viable test for drop detection in EDM.
/// 3. Three-section signal: two transitions -> two boundaries detected.
/// 4. No-boundary signal: uniform signal -> no false boundaries.
/// 5. Beatgrid snap: with BPM supplied, boundary moves to nearest phrase.
/// 6. Output contracts: timestamps ordered, NoveltyScore  in  (0,1].
///
/// [structure-detection.md Sec.Foote SSM; Sec.EDM structure is ENERGY/TIMBRE/RHYTHM]
/// </summary>
public class StructureDetectorTests
{
    // The analysis pipeline runs at 11 025 Hz (same as SpectralEnergyDetector).
    private const int SampleRate = 11025;

    // =========================================================================
    // 1. Edge cases - contract guarantees
    // =========================================================================

    [Fact]
    public void TooShort_ReturnsEmpty()
    {
        var det    = new StructureDetector();
        var audio  = new DecodedAudio(new float[100], SampleRate);
        var result = det.Detect(audio);

        Assert.Empty(result);
    }

    [Fact]
    public void Silence_ReturnsEmpty()
    {
        // 30 s of zeros - no spectral content, no gradients, no peaks.
        var det   = new StructureDetector();
        var audio = new DecodedAudio(new float[SampleRate * 30], SampleRate);
        var result = det.Detect(audio);

        Assert.Empty(result);
    }

    [Fact]
    public void EmptySamples_ReturnsEmpty()
    {
        var det   = new StructureDetector();
        var audio = new DecodedAudio(Array.Empty<float>(), SampleRate);
        Assert.Empty(det.Detect(audio));
    }

    // =========================================================================
    // 2. Two-section signal: low-energy -> high-energy
    //    (minimum viable "drop" test)
    // =========================================================================

    /// <summary>
    /// A signal with a clear energy jump at its midpoint should produce at least
    /// one boundary near that transition.
    ///
    /// Signal design:
    ///   - First half:  quiet 200 Hz sine at amplitude 0.02 ("breakdown").
    ///   - Second half: loud broadband noise + 100 Hz bass pulse at amplitude 0.9 ("drop").
    ///
    /// The two halves are maximally different across all 7 feature dimensions
    /// (sub-bass energy, bass energy, mid energy, hi energy, centroid,
    /// flatness, flux), so the novelty peak should be unmissable.
    ///
    /// Detection tolerance: the boundary must land within +/-10% of total duration
    /// from the true midpoint (i.e. within [0.40T, 0.60T]).
    /// </summary>
    [Fact]
    public void TwoSectionSignal_BoundaryNearMidpoint()
    {
        const double totalSeconds = 60.0;  // long enough for SSM to be meaningful
        var samples  = TwoSectionSignal(totalSeconds, SampleRate);
        var audio    = new DecodedAudio(samples, SampleRate);

        var det    = new StructureDetector { ThresholdSigmas = 0.5, MinBoundaryGapSeconds = 5.0 };
        var result = det.Detect(audio);

        Assert.NotEmpty(result);

        // At least one boundary must fall within +/-10% of the midpoint.
        var midpoint = totalSeconds / 2.0;
        var tolerance = totalSeconds * 0.10;
        var foundNearMid = result.Any(b =>
            Math.Abs(b.TimeSeconds - midpoint) <= tolerance);

        Assert.True(foundNearMid,
            $"Expected a boundary near midpoint {midpoint:F1}s (+/-{tolerance:F1}s), " +
            $"but got boundaries at: [{string.Join(", ", result.Select(b => $"{b.TimeSeconds:F1}s"))}]");
    }

    /// <summary>
    /// Same two-section signal: verify that all returned boundary timestamps are
    /// within the track duration and novelty scores are positive.
    /// </summary>
    [Fact]
    public void TwoSectionSignal_OutputContractsHold()
    {
        const double totalSeconds = 60.0;
        var audio  = new DecodedAudio(TwoSectionSignal(totalSeconds, SampleRate), SampleRate);
        var det    = new StructureDetector { ThresholdSigmas = 0.5, MinBoundaryGapSeconds = 5.0 };
        var result = det.Detect(audio);

        Assert.NotEmpty(result);

        foreach (var b in result)
        {
            Assert.True(b.TimeSeconds >= 0.0,
                $"Boundary timestamp must be non-negative, got {b.TimeSeconds}");
            Assert.True(b.TimeSeconds <= totalSeconds + 1.0,
                $"Boundary timestamp {b.TimeSeconds:F1}s exceeds track length {totalSeconds}s");
            Assert.True(b.NoveltyScore > 0.0 && b.NoveltyScore <= 1.0,
                $"NoveltyScore must be in (0,1], got {b.NoveltyScore:F3}");
        }
    }

    // =========================================================================
    // 3. Three-section signal: two transitions
    // =========================================================================

    /// <summary>
    /// A three-section signal (A->B->A pattern: quiet->loud->quiet) should produce
    /// at least two boundaries, one near each transition.
    ///
    ///   Section A : 0   - T/3  : quiet 200 Hz sine (0.02 amp)
    ///   Section B : T/3 - 2T/3 : loud broadband burst (0.9 amp)
    ///   Section A': 2T/3 - T  : quiet 200 Hz sine again (0.02 amp)
    ///
    /// With a 90-second total track, the two transitions are at 30 s and 60 s.
    /// Both should be detected within +/-10 s.
    /// </summary>
    [Fact]
    public void ThreeSectionSignal_TwoBoundariesDetected()
    {
        const double totalSeconds = 90.0;
        var samples = ThreeSectionSignal(totalSeconds, SampleRate);
        var audio   = new DecodedAudio(samples, SampleRate);

        // Wider threshold because three-section signals produce two competing peaks;
        // the detector must keep both.
        var det = new StructureDetector { ThresholdSigmas = 0.5, MinBoundaryGapSeconds = 10.0 };
        var result = det.Detect(audio);

        Assert.True(result.Count >= 2,
            $"Expected at least 2 boundaries for a three-section signal, got {result.Count}: " +
            $"[{string.Join(", ", result.Select(b => $"{b.TimeSeconds:F1}s"))}]");

        const double t1 = totalSeconds / 3.0;  // ~30 s
        const double t2 = totalSeconds * 2.0 / 3.0;  // ~60 s
        const double tol = 10.0;

        var foundT1 = result.Any(b => Math.Abs(b.TimeSeconds - t1) <= tol);
        var foundT2 = result.Any(b => Math.Abs(b.TimeSeconds - t2) <= tol);

        Assert.True(foundT1,
            $"Expected boundary near {t1:F1}s (+/-{tol}s). " +
            $"Got: [{string.Join(", ", result.Select(b => $"{b.TimeSeconds:F1}s"))}]");
        Assert.True(foundT2,
            $"Expected boundary near {t2:F1}s (+/-{tol}s). " +
            $"Got: [{string.Join(", ", result.Select(b => $"{b.TimeSeconds:F1}s"))}]");
    }

    // =========================================================================
    // 4. Uniform signal - no false boundaries
    // =========================================================================

    /// <summary>
    /// A signal that is homogeneous throughout (steady broadband noise, constant
    /// amplitude) should produce NO boundaries.  This guards against false positives
    /// from boundary effects of the kernel at the SSM edges.
    ///
    /// We use a high threshold (2sigma) to make sure only genuine novelty peaks are
    /// counted.
    /// </summary>
    [Fact]
    public void UniformSignal_NoBoundaries()
    {
        const double totalSeconds = 60.0;
        var audio  = new DecodedAudio(UniformNoise(totalSeconds, SampleRate, amplitude: 0.3f), SampleRate);
        var det    = new StructureDetector { ThresholdSigmas = 2.0, MinBoundaryGapSeconds = 5.0 };
        var result = det.Detect(audio);

        Assert.Empty(result);
    }

    // =========================================================================
    // 5. Beatgrid snap - boundary moves to phrase boundary
    // =========================================================================

    /// <summary>
    /// When BPM is provided, the detected boundary should snap to the nearest
    /// 4-bar phrase boundary.  We engineer the signal so the raw detection lands
    /// slightly off the grid, then verify it has been snapped.
    ///
    /// 128 BPM -> 4-bar phrase = 4 x 4 x (60/128) ~ 7.5 s.
    ///
    /// Signal: two sections, transition at exactly T = 30.0 s.
    /// Nearest phrase boundary to 30.0 s = round(30.0 / 7.5) x 7.5 = 4 x 7.5 = 30.0 s.
    /// (30 s is exactly on a phrase boundary at 128 BPM -> snap has no effect, but
    /// the boundary must still be present and close to 30 s.)
    ///
    /// Also verifies that the snapped timestamp is within +/-7.5 s of the raw transition
    /// (i.e. within +/-1 phrase).
    /// </summary>
    [Fact]
    public void BeatgridSnap_BoundaryIsNearPhraseGrid()
    {
        const double totalSeconds = 60.0;
        const double bpm          = 128.0;

        var samples = TwoSectionSignal(totalSeconds, SampleRate);
        var audio   = new DecodedAudio(samples, SampleRate);

        var det    = new StructureDetector { ThresholdSigmas = 0.5, MinBoundaryGapSeconds = 5.0 };
        var result = det.Detect(audio, bpm: bpm);

        Assert.NotEmpty(result);

        var phraseDuration = 4.0 * 4.0 * 60.0 / bpm;  // ~ 7.5 s
        var midpoint       = totalSeconds / 2.0;        // 30.0 s

        // All boundaries must lie on the phrase grid (within floating-point of a multiple).
        foreach (var b in result)
        {
            var nearestPhrase = Math.Round(b.TimeSeconds / phraseDuration) * phraseDuration;
            var error = Math.Abs(b.TimeSeconds - nearestPhrase);
            Assert.True(error < 0.01,
                $"After snap, boundary at {b.TimeSeconds:F3}s should be on phrase grid " +
                $"(nearest = {nearestPhrase:F3}s, error = {error:F3}s)");
        }

        // And at least one must be near the actual midpoint.
        Assert.True(result.Any(b => Math.Abs(b.TimeSeconds - midpoint) <= phraseDuration * 1.5),
            $"No boundary within 1.5 phrases of midpoint {midpoint:F1}s.");
    }

    // =========================================================================
    // 6. Output ordering
    // =========================================================================

    [Fact]
    public void Boundaries_AreOrderedByTime()
    {
        const double totalSeconds = 90.0;
        var audio  = new DecodedAudio(ThreeSectionSignal(totalSeconds, SampleRate), SampleRate);
        var det    = new StructureDetector { ThresholdSigmas = 0.5, MinBoundaryGapSeconds = 8.0 };
        var result = det.Detect(audio);

        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i].TimeSeconds >= result[i - 1].TimeSeconds,
                $"Boundaries must be sorted by time: {result[i - 1].TimeSeconds:F1}s > {result[i].TimeSeconds:F1}s");
    }

    // =========================================================================
    // 7. CancellationToken is respected
    // =========================================================================

    [Fact]
    public void CancelledToken_ThrowsOperationCancelled()
    {
        var audio = new DecodedAudio(TwoSectionSignal(60.0, SampleRate), SampleRate);
        var det   = new StructureDetector();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => det.Detect(audio, ct: cts.Token));
    }

    // =========================================================================
    // Signal generators
    // =========================================================================

    /// <summary>
    /// Two-section signal:
    ///   Section 1 (first half)  : quiet 200 Hz sine, amplitude 0.02.
    ///   Section 2 (second half) : loud 80 Hz sub-bass + broadband noise, amplitude 0.9.
    ///
    /// The two halves are maximally dissimilar across all feature dimensions so
    /// the novelty peak at the midpoint is unambiguous.
    /// </summary>
    private static float[] TwoSectionSignal(double totalSeconds, int sampleRate)
    {
        var n    = (int)(totalSeconds * sampleRate);
        var half = n / 2;
        var x    = new float[n];
        var rng  = new Random(42);

        // Section 1: quiet low sine (dark, low energy, low centroid, low flatness).
        for (var i = 0; i < half; i++)
        {
            var t = i / (double)sampleRate;
            x[i] = (float)(0.02 * Math.Sin(2.0 * Math.PI * 200.0 * t));
        }

        // Section 2: loud broadband noise + 80 Hz sub-bass kick pulse every 0.5 s.
        for (var i = half; i < n; i++)
        {
            var t     = (i - half) / (double)sampleRate;
            // Broadband white noise (bright, flat spectrum, high centroid).
            var noise = (rng.NextDouble() * 2.0 - 1.0) * 0.9;
            // Sub-bass tone (80 Hz): high sub-bass energy, low centroid.
            var bass  = 0.9 * Math.Sin(2.0 * Math.PI * 80.0 * t);
            // Combined - high total energy, very different from section 1.
            x[i] = (float)Math.Clamp((noise + bass) * 0.5, -1.0, 1.0);
        }

        return x;
    }

    /// <summary>
    /// Three-section A-B-A signal: quiet -> loud -> quiet.
    /// </summary>
    private static float[] ThreeSectionSignal(double totalSeconds, int sampleRate)
    {
        var n    = (int)(totalSeconds * sampleRate);
        var t1   = n / 3;
        var t2   = 2 * n / 3;
        var x    = new float[n];
        var rng  = new Random(99);

        // Section A (0..T/3): quiet 200 Hz sine.
        for (var i = 0; i < t1; i++)
        {
            var t = i / (double)sampleRate;
            x[i] = (float)(0.02 * Math.Sin(2.0 * Math.PI * 200.0 * t));
        }

        // Section B (T/3..2T/3): loud broadband noise + 80 Hz sub-bass.
        for (var i = t1; i < t2; i++)
        {
            var t     = (i - t1) / (double)sampleRate;
            var noise = (rng.NextDouble() * 2.0 - 1.0) * 0.9;
            var bass  = 0.9 * Math.Sin(2.0 * Math.PI * 80.0 * t);
            x[i] = (float)Math.Clamp((noise + bass) * 0.5, -1.0, 1.0);
        }

        // Section A' (2T/3..T): quiet 200 Hz sine again.
        for (var i = t2; i < n; i++)
        {
            var t = (i - t2) / (double)sampleRate;
            x[i] = (float)(0.02 * Math.Sin(2.0 * Math.PI * 200.0 * t));
        }

        return x;
    }

    /// <summary>
    /// Uniform white noise at constant amplitude - no structural changes.
    /// </summary>
    private static float[] UniformNoise(double totalSeconds, int sampleRate, float amplitude)
    {
        var n   = (int)(totalSeconds * sampleRate);
        var x   = new float[n];
        var rng = new Random(7);
        for (var i = 0; i < n; i++)
            x[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * amplitude);
        return x;
    }
}
