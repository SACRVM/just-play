using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Computes <see cref="RhythmPattern"/> from decoded mono audio given a known BPM.
///
/// <para>All five features are tempo-aware: they align against the beat grid implied by
/// <paramref name="bpm"/> and the known sample rate. The features are computed from the
/// same 11 kHz onset-strength envelope that BeatFingerprintExtractor and
/// TempoOctaveCorrector use — no second decode is needed.</para>
///
/// <para>Algorithm references:</para>
/// <list type="bullet">
///   <item>FourOnFloor / Syncopation — Hockman et al. "Computational Analysis of
///     Perceived Metrical Strength in EDM" (ISMIR 2012); beat-grid onset energy ratio.</item>
///   <item>OffbeatEnergy / Swing — Butterfield "Why Do Jazz Musicians Swing Their
///     Eighth Notes?" (Music Perception 2011) + Räsänen et al. "Automated Methods for
///     the Transcription and Analysis of Rhythm" (Computer Music Journal 2015).</item>
///   <item>HalfTimeFeel — autocorrelation at half-tempo lag (2× beat period).</item>
/// </list>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, reflection-free, trim-/AOT-safe.
/// No new NuGet packages — reuses Fft.cs already in this project.</para>
/// </summary>
public static class RhythmPatternDetector
{
    // =========================================================================
    // Thresholds (named constants — tune these on Chloe's ear, not a corpus).
    // The BeatType label is entirely determined by these thresholds. Raise or
    // lower them in this one block; nothing else needs to change.
    // =========================================================================

    /// <summary>Thresholds used by <see cref="ClassifyBeatType"/> to derive the label.</summary>
    public static class Thresholds
    {
        // --- HalfTimeFeel wins first (strongest over-riding signal) ---

        /// <summary>
        /// HalfTimeFeel above this → "halftime" regardless of other features.
        /// Dubstep / trap: dominant pulse at half the detected tempo.
        /// </summary>
        public const double HalftimeWin = 0.55;

        // --- Breaks detection ---

        /// <summary>
        /// FourOnFloor below this threshold → kick is NOT consistently on every beat → breaks / syncopated.
        /// </summary>
        public const double FofBreaksMax = 0.45;

        /// <summary>
        /// Syncopation above this threshold → confirmed breaks character.
        /// </summary>
        public const double SynBreaksMin = 0.35;

        // --- 4x4-groovy vs 4x4-driving ---

        /// <summary>
        /// OffbeatEnergy above this → house feel (hi-hat / open-hat emphasis on the "and").
        /// </summary>
        public const double OffbeatGroovyMin = 0.38;

        /// <summary>
        /// Swing above this → house shuffle feel.
        /// </summary>
        public const double SwingGroovyMin = 0.30;

        // --- offbeat-bass: four-on-floor + strong off-beat but no swing ---

        /// <summary>
        /// OffbeatEnergy above this even when swing is low → "offbeat-bass" pattern (stabs, synth hooks).
        /// </summary>
        public const double OffbeatBassMin = 0.50;
    }

    // =========================================================================
    // Onset envelope parameters (must MATCH BeatFingerprintExtractor + TempoOctaveCorrector)
    // =========================================================================

    private const int OnsetFrameSize = 512;
    private const int OnsetHopSize   = OnsetFrameSize / 4;  // 128 samples @ 11025 Hz ≈ 11.6 ms

    // =========================================================================
    // Band splitting for FourOnFloor vs OffbeatEnergy
    // At 11025 Hz the FFT has OnsetFrameSize/2 = 256 bins.
    // Bin spacing = 11025 / 512 ≈ 21.5 Hz.
    // Kick drum fundamental: 40–120 Hz → bins 2..5
    // Low band (kick body + sub): 0–250 Hz → bins 0..11
    // Mid band (snare/hat, offbeat emphasis): 250–3500 Hz → bins 12..162
    // =========================================================================

    private const int LowBandMaxBin  = 11;   // ≤ ~237 Hz (kick body)
    private const int MidBandMaxBin  = 162;  // ≤ ~3483 Hz (snare / hi-hat range)

    // Minimum number of onset frames — tracks shorter than this yield null.
    private const int MinFrames = 32;

    // When scoring beat-grid alignment we look at a neighbourhood of ±HalfWindowFrames
    // around each nominal grid position to absorb small timing jitter (quantisation).
    // At hop=128/sr=11025 → 1 frame ≈ 11.6 ms; ±1 frame window covers ±23 ms (typical
    // kick transient width). ±2 frames cover ±46 ms for looser grooves.
    private const int HalfWindowFrames = 2;

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Computes a <see cref="RhythmPattern"/> from the given mono audio samples and BPM.
    /// Returns <c>null</c> when audio is too short or BPM is not positive.
    /// </summary>
    /// <param name="samples">Mono float samples in [-1,1] at <paramref name="sampleRate"/> Hz.</param>
    /// <param name="sampleRate">Sample rate in Hz (typically 11025 for analysis).</param>
    /// <param name="bpm">Detected (corrected) BPM of the track. Must be positive.</param>
    /// <param name="ct">Cancellation token.</param>
    public static RhythmPattern? Detect(
        float[] samples,
        int sampleRate,
        double bpm,
        CancellationToken ct = default)
    {
        if (samples is null || samples.Length < OnsetFrameSize * 4 || sampleRate <= 0 || bpm <= 0)
            return null;

        // ---- Step 1: band-split onset envelopes (low = kick, mid = hat/snare) ----
        BuildBandOnsets(samples, sampleRate, ct,
            out var lowEnvelope,   // kick-range flux
            out var midEnvelope);  // hi-hat/snare-range flux

        if (lowEnvelope.Length < MinFrames)
            return null;

        // Combined (full-band) envelope for syncopation and general beat-grid work.
        // We compute it from the band arrays rather than a third FFT pass.
        var fullEnvelope = CombineEnvelopes(lowEnvelope, midEnvelope);

        // ---- Step 2: beat grid ----
        // Beat period in frames (float, fractional).
        var beatPeriodFrames = (sampleRate / (double)OnsetHopSize) * (60.0 / bpm);

        // ---- Step 3: FourOnFloor — low-band onset energy on every beat ----
        var fof = ComputeFourOnFloor(lowEnvelope, beatPeriodFrames, ct);

        // ---- Step 4: OffbeatEnergy — mid-band onset at the half-beat ("and") ----
        var offbeat = ComputeOffbeatEnergy(midEnvelope, beatPeriodFrames, ct);

        // ---- Step 5: Swing — timing deviation of off-beat events ----
        var swing = ComputeSwing(midEnvelope, beatPeriodFrames, ct);

        // ---- Step 6: Syncopation — full-band onset energy off the beat grid ----
        var syncopation = ComputeSyncopation(fullEnvelope, beatPeriodFrames, ct);

        // ---- Step 7: HalfTimeFeel — ACF energy at the double-beat period ----
        var halfTime = ComputeHalfTimeFeel(fullEnvelope, beatPeriodFrames, sampleRate, ct);

        // ---- Step 8: Rule-based label ----
        var beatType = ClassifyBeatType(fof, offbeat, swing, syncopation, halfTime);

        return new RhythmPattern
        {
            FourOnFloor   = fof,
            OffbeatEnergy = offbeat,
            Swing         = swing,
            Syncopation   = syncopation,
            HalfTimeFeel  = halfTime,
            BeatType      = beatType,
        };
    }

    // =========================================================================
    // Rule-based label derivation
    // =========================================================================

    /// <summary>
    /// Derives a <see cref="RhythmPattern.BeatType"/> label from the five feature scalars
    /// using the named thresholds in <see cref="Thresholds"/>. Evaluated top-to-bottom;
    /// first match wins.
    ///
    /// Rule precedence:
    /// 1. halftime — dominant half-time ACF peak.
    /// 2. breaks — low four-on-floor + high syncopation.
    /// 3. offbeat-bass — very high offbeat energy but LOW swing (stabs/bass hooks, not house).
    /// 4. 4x4-groovy — four-on-floor + moderate offbeat OR swing (house feel).
    /// 5. 4x4-driving — default: straight four-on-floor.
    /// </summary>
    public static string ClassifyBeatType(
        double fof, double offbeat, double swing, double syncopation, double halfTime)
    {
        // 1. Half-time feel dominates (dubstep / trap).
        if (halfTime >= Thresholds.HalftimeWin)
            return "halftime";

        // 2. Breaks: kick not consistently on every beat AND high syncopation.
        if (fof < Thresholds.FofBreaksMax && syncopation >= Thresholds.SynBreaksMin)
            return "breaks";

        // 3. offbeat-bass: VERY high offbeat energy but no swing → stab chords, bass hooks.
        //    Must check BEFORE 4x4-groovy because OffbeatBassMin > OffbeatGroovyMin,
        //    and we want to reserve "4x4-groovy" for cases with genuine swing/shuffle.
        if (offbeat >= Thresholds.OffbeatBassMin && swing < Thresholds.SwingGroovyMin)
            return "offbeat-bass";

        // 4. 4x4-groovy: four-on-floor + moderate offbeat emphasis OR swing (house feel).
        if (offbeat >= Thresholds.OffbeatGroovyMin || swing >= Thresholds.SwingGroovyMin)
            return "4x4-groovy";

        // 5. Default: straight driving four-on-floor.
        return "4x4-driving";
    }

    // =========================================================================
    // Feature computation
    // =========================================================================

    /// <summary>
    /// FourOnFloor: fraction of beat positions that have a significant low-band onset.
    ///
    /// For each nominal beat position we take the PEAK low-band onset within a
    /// ±HalfWindowFrames neighbourhood (to absorb timing jitter). A beat position is
    /// "active" if its peak exceeds an adaptive threshold (mean envelope level). The
    /// fraction of active positions is the FourOnFloor score.
    ///
    /// Using a hit-fraction (not an energy ratio) avoids the window-overlap and
    /// energy-doublecounting pitfalls of a sum-based approach, and makes the score
    /// interpretable: "fraction of beats that have a kick".
    /// </summary>
    private static double ComputeFourOnFloor(
        double[] lowEnv, double beatPeriodFrames, CancellationToken ct)
    {
        var nFrames = lowEnv.Length;

        // Adaptive threshold: a beat position is "active" if its peak exceeds
        // the mean envelope level (avoids silent / very quiet tracks scoring 1.0).
        var meanLevel = SumEnvelope(lowEnv) / Math.Max(1, lowEnv.Length);
        // Use a lower floor: mean * 0.5 catches real kicks that may sit just above the noise.
        var threshold = meanLevel * 0.5;

        var activeBeats = 0;
        var totalBeats  = 0;

        // Walk every beat position.
        for (var beat = 0; beat * beatPeriodFrames < nFrames + beatPeriodFrames * 0.5; beat++)
        {
            ct.ThrowIfCancellationRequested();
            var centre = (int)Math.Round(beat * beatPeriodFrames);
            if (centre >= nFrames) break;
            var lo = Math.Max(0, centre - HalfWindowFrames);
            var hi = Math.Min(nFrames - 1, centre + HalfWindowFrames);

            // Peak within the neighbourhood window.
            var peak = 0.0;
            for (var f = lo; f <= hi; f++)
                if (lowEnv[f] > peak) peak = lowEnv[f];

            totalBeats++;
            if (peak > threshold) activeBeats++;
        }

        if (totalBeats == 0) return 0.0;
        return Math.Clamp((double)activeBeats / totalBeats, 0.0, 1.0);
    }

    /// <summary>
    /// OffbeatEnergy: fraction of the mid-band (hi-hat/snare) onset energy that falls in the
    /// OFF-beat REGION — the second quarter of each beat period (between the beat and the
    /// three-quarter mark).
    ///
    /// Unlike a narrow ±window around the exact half-beat, this measures the broad "and" region
    /// [beat + ¼, beat + ¾ × period], which captures both straight and swung off-beat events.
    /// A track with hi-hats anywhere in the off-beat region scores high; a track with only
    /// kick (on-beat) scores near 0.
    ///
    /// Formula:
    ///   offbeatEnergy = sum of midEnv frames where offset ∈ (¼, ¾) of the beat period
    ///   totalEnergy   = sum of all midEnv frames
    ///   score = offbeatEnergy / totalEnergy → [0, 1]
    /// </summary>
    private static double ComputeOffbeatEnergy(
        double[] midEnv, double beatPeriodFrames, CancellationToken ct)
    {
        var totalEnergy = SumEnvelope(midEnv);
        if (totalEnergy <= 0.0) return 0.0;

        var nFrames = midEnv.Length;
        var offbeatEnergy = 0.0;

        // Walk every beat and accumulate mid-band energy in the off-beat region.
        for (var beat = 0; (int)Math.Round(beat * beatPeriodFrames) < nFrames; beat++)
        {
            ct.ThrowIfCancellationRequested();

            // Off-beat region: [beat + ¼*period, beat + ¾*period).
            var regionLo = (int)Math.Round(beat * beatPeriodFrames + beatPeriodFrames * 0.25);
            var regionHi = (int)Math.Round(beat * beatPeriodFrames + beatPeriodFrames * 0.75);
            regionLo = Math.Max(0, regionLo);
            regionHi = Math.Min(nFrames - 1, regionHi);

            for (var f = regionLo; f <= regionHi; f++)
                offbeatEnergy += midEnv[f];
        }

        return Math.Clamp(offbeatEnergy / totalEnergy, 0.0, 1.0);
    }

    /// <summary>
    /// Swing: how far from the exact half-beat position (0.5 × period) the dominant
    /// off-beat mid-band event falls, averaged over all beat intervals.
    ///
    /// For each inter-beat interval:
    ///   1. Compute the centroid (energy-weighted mean frame) of mid-band energy in the
    ///      off-beat region [¼, ¾] × period. This is more robust than the peak because
    ///      hat transients are short and may span only 1–2 frames.
    ///   2. Only include intervals where the off-beat region has more energy than the
    ///      on-beat region (prevents kick-dominant beats from contributing false swing).
    ///   3. Measure deviation of the centroid from the exact half-beat (¼ period away
    ///      from centre of the ¼–¾ window), normalised to [0, 1].
    ///
    /// Straight groove: centroid near 0.5 × period → low deviation → Swing ≈ 0.
    /// Swung groove: centroid near 0.65 × period → deviation ≈ 0.3 → Swing elevated.
    /// </summary>
    private static double ComputeSwing(
        double[] midEnv, double beatPeriodFrames, CancellationToken ct)
    {
        var nFrames = midEnv.Length;

        var swingSum   = 0.0;
        var swingCount = 0;

        for (var beat = 0; (beat + 1) * beatPeriodFrames <= nFrames; beat++)
        {
            ct.ThrowIfCancellationRequested();

            var beatOrigin = beat * beatPeriodFrames;
            var beatEnd    = (beat + 1) * beatPeriodFrames;

            // Off-beat region: [beat + ¼, beat + ¾).
            var obLo = (int)Math.Round(beatOrigin + beatPeriodFrames * 0.25);
            var obHi = (int)Math.Round(beatOrigin + beatPeriodFrames * 0.75) - 1;
            obLo = Math.Clamp(obLo, 0, nFrames - 1);
            obHi = Math.Clamp(obHi, 0, nFrames - 1);
            if (obHi <= obLo) continue;

            // On-beat region: [beat, beat + ¼) ∪ [beat + ¾, beat+1).
            var onBeatEnergy  = 0.0;
            var offBeatEnergy = 0.0;
            var offBeatWtSum  = 0.0;  // weighted sum for centroid
            var offBeatWt     = 0.0;  // total weight for centroid

            var beatStart = (int)Math.Round(beatOrigin);
            var beatStop  = (int)Math.Round(beatEnd) - 1;
            beatStart = Math.Clamp(beatStart, 0, nFrames - 1);
            beatStop  = Math.Clamp(beatStop,  0, nFrames - 1);

            for (var f = beatStart; f <= beatStop; f++)
            {
                if (f >= obLo && f <= obHi)
                {
                    offBeatEnergy += midEnv[f];
                    offBeatWtSum  += f * midEnv[f];
                    offBeatWt     += midEnv[f];
                }
                else
                {
                    onBeatEnergy += midEnv[f];
                }
            }

            // Only count this beat interval if off-beat energy dominates.
            // (If the kick bleed in mid-band is louder than the hat, skip this interval.)
            if (offBeatEnergy <= onBeatEnergy || offBeatWt <= 0.0) continue;

            // Energy-weighted centroid of the off-beat region.
            var centroid = offBeatWtSum / offBeatWt;

            // Exact half-beat position (relative to beat origin).
            var exactHalf = beatOrigin + beatPeriodFrames * 0.5;

            // Deviation from the half-beat, normalised so that ¼ period deviation = 1.0.
            // The off-beat region spans ¼–¾, so the max possible deviation from the
            // exact half is ¼ of the period. Divide by (period/4) to normalise to [0, 1].
            var deviation = Math.Abs(centroid - exactHalf) / (beatPeriodFrames * 0.25);
            swingSum += Math.Min(1.0, deviation);
            swingCount++;
        }

        return swingCount == 0 ? 0.0 : Math.Clamp(swingSum / swingCount, 0.0, 1.0);
    }

    /// <summary>
    /// Syncopation: fraction of the full-band onset energy that falls AWAY from the beat grid.
    ///
    /// We split the beat grid into four sub-beat positions per beat:
    ///   position 0 (on-beat), ¼ (e), ½ (and/off-beat), ¾ (a).
    /// On-beat energy = window around positions 0 and ½ of each period.
    /// Off-grid energy = windows around ¼ and ¾ positions.
    /// Syncopation = off-grid energy / total energy.
    ///
    /// A pure 4/4 kick has all energy at position 0 → low syncopation.
    /// A breakbeat spreads energy across all four sub-beats → high syncopation.
    /// </summary>
    private static double ComputeSyncopation(
        double[] env, double beatPeriodFrames, CancellationToken ct)
    {
        var totalEnergy = SumEnvelope(env);
        if (totalEnergy <= 0.0) return 0.0;

        var offGridEnergy = 0.0;
        var nFrames = env.Length;
        var quarterPeriod = beatPeriodFrames * 0.25;

        // We test the ¼ and ¾ offsets (off-grid) vs. 0 and ½ (on-grid).
        // Walk beats; for each beat test the ¼ and ¾ sub-positions.
        for (var beat = 0; beat * beatPeriodFrames < nFrames + beatPeriodFrames * 0.5; beat++)
        {
            ct.ThrowIfCancellationRequested();
            var beatOrigin = beat * beatPeriodFrames;

            // ¼ sub-position.
            var quarter = (int)Math.Round(beatOrigin + quarterPeriod);
            var lo1 = Math.Max(0, quarter - HalfWindowFrames);
            var hi1 = Math.Min(nFrames - 1, quarter + HalfWindowFrames);
            for (var f = lo1; f <= hi1; f++)
                offGridEnergy += env[f];

            // ¾ sub-position.
            var threeQuarter = (int)Math.Round(beatOrigin + 3.0 * quarterPeriod);
            var lo2 = Math.Max(0, threeQuarter - HalfWindowFrames);
            var hi2 = Math.Min(nFrames - 1, threeQuarter + HalfWindowFrames);
            for (var f = lo2; f <= hi2; f++)
                offGridEnergy += env[f];
        }

        return Math.Clamp(offGridEnergy / totalEnergy, 0.0, 1.0);
    }

    /// <summary>
    /// HalfTimeFeel: ratio of the ACF value at the double-beat period (2× beatPeriod) to
    /// the ACF value at the beat period.
    ///
    /// If the dominant pulse is at the beat period the ratio is &lt; 1 (normal 4/4).
    /// If the dominant pulse is at 2× the beat period (snare every 2 beats → "two and four"
    /// feels like a half-time snare) the ratio exceeds 1. We normalise to [0, 1] by
    /// dividing through by the sum of both ACF values.
    ///
    /// This captures the half-time feel of dubstep / trap where BASS_FX may detect the
    /// "two" BPM but the real perceived pulse is at the "one" level.
    /// </summary>
    private static double ComputeHalfTimeFeel(
        double[] env,
        double beatPeriodFrames,
        int sampleRate,
        CancellationToken ct)
    {
        // We need at least 4 beat periods in the ACF window to see a reliable half-time peak.
        var halfTimePeriodFrames = beatPeriodFrames * 2.0;
        var maxLagNeeded = (int)Math.Ceiling(halfTimePeriodFrames) + 2;

        if (env.Length <= maxLagNeeded)
            return 0.0;

        // Build a biased ACF up to halfTimePeriodFrames + a small margin.
        var maxLag = Math.Min(maxLagNeeded + 4, env.Length - 1);
        var acf = AutocorrelateShort(env, maxLag, ct);

        // ACF at beat period.
        var lagBeat = (int)Math.Round(beatPeriodFrames);
        if (lagBeat < 1 || lagBeat >= acf.Length) return 0.0;

        // ACF at double-beat period.
        var lagHalf = (int)Math.Round(halfTimePeriodFrames);
        if (lagHalf < 1 || lagHalf >= acf.Length) return 0.0;

        var acfBeat  = Math.Max(0.0, acf[lagBeat]);
        var acfHalf  = Math.Max(0.0, acf[lagHalf]);
        var denom    = acfBeat + acfHalf;

        if (denom <= 0.0) return 0.0;

        // HalfTimeFeel = how dominant is the half-time peak relative to the beat peak.
        // acfHalf/(acfBeat+acfHalf): 0 = pure 4/4, 1 = pure half-time.
        return Math.Clamp(acfHalf / denom, 0.0, 1.0);
    }

    // =========================================================================
    // Band-split onset envelope builder
    // =========================================================================

    /// <summary>
    /// Builds two onset-strength envelopes from the same spectral-flux computation:
    /// <list type="bullet">
    ///   <item><paramref name="lowEnv"/> — positive magnitude differences summed over bins 0..LowBandMaxBin
    ///     (kick body + sub, ~0–237 Hz @ 11 kHz).</item>
    ///   <item><paramref name="midEnv"/> — same over bins LowBandMaxBin+1..MidBandMaxBin
    ///     (snare / hi-hat range, ~237–3483 Hz).</item>
    /// </list>
    /// One FFT pass, two partial sums — no extra decode.
    /// </summary>
    private static void BuildBandOnsets(
        float[] samples,
        int sampleRate,
        CancellationToken ct,
        out double[] lowEnv,
        out double[] midEnv)
    {
        _ = sampleRate;  // not needed once the frame size and hop are fixed

        var hann    = BuildHannWindow(OnsetFrameSize);
        var re      = new float[OnsetFrameSize];
        var im      = new float[OnsetFrameSize];
        var halfBins = OnsetFrameSize / 2;

        var prevMagLow  = new double[LowBandMaxBin + 1];
        var prevMagMid  = new double[MidBandMaxBin - LowBandMaxBin];
        var havePrev    = false;

        var frameCount = (samples.Length - OnsetFrameSize) / OnsetHopSize + 1;
        lowEnv = new double[frameCount];
        midEnv = new double[frameCount];
        var envIdx = 0;

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
            double fluxMid = 0.0;

            for (var k = 0; k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

                if (havePrev)
                {
                    if (k <= LowBandMaxBin)
                    {
                        var diff = mag - prevMagLow[k];
                        if (diff > 0.0) fluxLow += diff;
                        prevMagLow[k] = mag;
                    }
                    else if (k <= MidBandMaxBin)
                    {
                        var idx = k - LowBandMaxBin - 1;
                        var diff = mag - prevMagMid[idx];
                        if (diff > 0.0) fluxMid += diff;
                        prevMagMid[idx] = mag;
                    }
                }
                else
                {
                    if (k <= LowBandMaxBin)
                        prevMagLow[k] = mag;
                    else if (k <= MidBandMaxBin)
                        prevMagMid[k - LowBandMaxBin - 1] = mag;
                }
            }

            if (envIdx < frameCount)
            {
                lowEnv[envIdx] = havePrev ? fluxLow : 0.0;
                midEnv[envIdx] = havePrev ? fluxMid : 0.0;
                envIdx++;
            }

            havePrev = true;
        }
    }

    // =========================================================================
    // Short ACF (only up to maxLag — cheaper than the full BeatFingerprint ACF)
    // =========================================================================

    private static double[] AutocorrelateShort(double[] signal, int maxLag, CancellationToken ct)
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
            acf[lag] = count > 0 ? sum / n : 0.0;
        }

        return acf;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static double[] CombineEnvelopes(double[] low, double[] mid)
    {
        var len    = Math.Min(low.Length, mid.Length);
        var result = new double[len];
        for (var i = 0; i < len; i++)
            result[i] = low[i] + mid[i];
        return result;
    }

    private static double SumEnvelope(double[] env)
    {
        var s = 0.0;
        foreach (var v in env) s += v;
        return s;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
