using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="RhythmPatternDetector"/>.
///
/// All signals are synthetic - no real audio files needed. Three canonical patterns:
/// <list type="number">
///   <item><b>Pure 4/4 kick</b>: Gaussian click on every beat -> high FourOnFloor, low Syncopation -> "4x4-driving".</item>
///   <item><b>Swung off-beat hat</b>: kick on every beat + hi-hat clicks between beats, offset late (swing) ->
///     high FourOnFloor, high OffbeatEnergy, high Swing -> "4x4-groovy".</item>
///   <item><b>Breakbeat</b>: irregular kick pattern (beat 1 and 3 only, no beat 2 and 4) + snare syncopation ->
///     low FourOnFloor, high Syncopation -> "breaks".</item>
///   <item><b>Half-time feel</b>: kick every two beats (half-tempo rate) -> high HalfTimeFeel -> "halftime".</item>
/// </list>
///
/// Tests confirm:
/// - Feature scalars are in [0, 1].
/// - Synthetic pure 4/4 -> FourOnFloor &gt; Syncopation.
/// - Synthetic breakbeat -> Syncopation &gt; FourOnFloor.
/// - Swung hat -> OffbeatEnergy and Swing elevated vs pure 4/4.
/// - Half-time -> HalfTimeFeel elevated vs straight 4/4.
/// - BeatType labels match the expected taxonomy for each synthetic signal.
/// - Guard: null / too-short / zero BPM -> null (no throw).
/// - ClassifyBeatType is directly testable from known scalar combos.
/// </summary>
public class RhythmPatternDetectorTests
{
    private const int SampleRate = 11025;
    private const double DurationSeconds = 20.0;  // long enough for >=10 beat periods at 120 BPM

    // =========================================================================
    // Guard / edge cases
    // =========================================================================

    [Fact]
    public void NullSamples_ReturnsNull()
    {
        var rp = RhythmPatternDetector.Detect(null!, SampleRate, 120.0);
        Assert.Null(rp);
    }

    [Fact]
    public void EmptySamples_ReturnsNull()
    {
        var rp = RhythmPatternDetector.Detect([], SampleRate, 120.0);
        Assert.Null(rp);
    }

    [Fact]
    public void TooShortSamples_ReturnsNull()
    {
        // 8 samples - too short for even one onset frame.
        var rp = RhythmPatternDetector.Detect(new float[8], SampleRate, 120.0);
        Assert.Null(rp);
    }

    [Fact]
    public void ZeroBpm_ReturnsNull()
    {
        var samples = KickClickTrain(120.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 0.0);
        Assert.Null(rp);
    }

    [Fact]
    public void NegativeBpm_ReturnsNull()
    {
        var samples = KickClickTrain(120.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, -128.0);
        Assert.Null(rp);
    }

    // =========================================================================
    // Feature range validation: all scalars must be in [0, 1]
    // =========================================================================

    [Theory]
    [InlineData(120.0)]
    [InlineData(128.0)]
    [InlineData(140.0)]
    public void AllFeatures_InUnitInterval(double bpm)
    {
        var samples = KickClickTrain(bpm, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, bpm);

        Assert.NotNull(rp);
        Assert.InRange(rp!.FourOnFloor,   0.0, 1.0);
        Assert.InRange(rp.OffbeatEnergy,  0.0, 1.0);
        Assert.InRange(rp.Swing,          0.0, 1.0);
        Assert.InRange(rp.Syncopation,    0.0, 1.0);
        Assert.InRange(rp.HalfTimeFeel,   0.0, 1.0);
    }

    // =========================================================================
    // BeatType string is non-empty
    // =========================================================================

    [Fact]
    public void BeatType_IsNonEmpty()
    {
        var samples = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 128.0);

        Assert.NotNull(rp);
        Assert.False(string.IsNullOrEmpty(rp!.BeatType),
            "BeatType must not be null or empty.");
    }

    // =========================================================================
    // Pure 4/4 kick -> high FourOnFloor, lower Syncopation, "4x4-driving"
    // =========================================================================

    [Fact]
    public void PureFourOnFloor_FourOnFloor_ExceedsSyncopation()
    {
        // A click on every beat at exactly 128 BPM - maximally regular four-on-the-floor.
        var samples = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 128.0);

        Assert.NotNull(rp);
        Assert.True(rp!.FourOnFloor > rp.Syncopation,
            $"Pure 4/4: FourOnFloor ({rp.FourOnFloor:0.000}) should exceed Syncopation ({rp.Syncopation:0.000})");
    }

    [Fact]
    public void PureFourOnFloor_FourOnFloor_IsHigh()
    {
        // Four-on-the-floor: every beat has a kick -> score should be high (>= 0.70).
        // With the hit-fraction approach (FourOnFloor = fraction of beat positions that
        // have a low-band onset above the mean), a perfect 4/4 kick train should approach 1.0.
        var samples = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 128.0);

        Assert.NotNull(rp);
        Assert.True(rp!.FourOnFloor >= 0.70,
            $"Pure 4/4 FourOnFloor expected >= 0.70, got {rp.FourOnFloor:0.000}");
    }

    [Fact]
    public void PureFourOnFloor_BeatType_IsDriving()
    {
        // A plain kick train with no off-beat emphasis -> "4x4-driving".
        var samples = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 128.0);

        Assert.NotNull(rp);
        Assert.Equal("4x4-driving", rp!.BeatType);
    }

    // =========================================================================
    // Swung-hat pattern -> elevated OffbeatEnergy and Swing, "4x4-groovy"
    // =========================================================================

    [Fact]
    public void SwungHat_OffbeatEnergy_HigherThanPureKick()
    {
        // Kick on every beat + swung hi-hat clicks between beats.
        var kickOnly  = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var swungHat  = KickPlusSwungHat(128.0, DurationSeconds, SampleRate, swingFactor: 0.65);

        var rpKick    = RhythmPatternDetector.Detect(kickOnly, SampleRate, 128.0);
        var rpGroovy  = RhythmPatternDetector.Detect(swungHat, SampleRate, 128.0);

        Assert.NotNull(rpKick);
        Assert.NotNull(rpGroovy);

        Assert.True(rpGroovy!.OffbeatEnergy > rpKick!.OffbeatEnergy,
            $"Swung-hat OffbeatEnergy ({rpGroovy.OffbeatEnergy:0.000}) should exceed pure kick ({rpKick.OffbeatEnergy:0.000})");
    }

    [Fact]
    public void SwungHat_BeatType_IsGroovy()
    {
        // With strong off-beat hits and swing -> "4x4-groovy".
        var samples = KickPlusSwungHat(128.0, DurationSeconds, SampleRate, swingFactor: 0.65);
        var rp = RhythmPatternDetector.Detect(samples, SampleRate, 128.0);

        Assert.NotNull(rp);
        Assert.Equal("4x4-groovy", rp!.BeatType);
    }

    // =========================================================================
    // Breakbeat pattern -> low FourOnFloor, high Syncopation, "breaks"
    // =========================================================================

    [Fact]
    public void Breakbeat_FourOnFloor_LowerThanPureKick()
    {
        // Broken kick: kicks at 1/4 sub-beat positions (NOT on the beat grid), plus
        // syncopated snare hits. The FourOnFloor score should be lower than a pure 4/4 kick
        // because the low-band energy does NOT land consistently on beat positions.
        var kickOnly  = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var breakbeat = BrokenKickWithSyncopation(128.0, DurationSeconds, SampleRate);

        var rpKick  = RhythmPatternDetector.Detect(kickOnly,  SampleRate, 128.0);
        var rpBreak = RhythmPatternDetector.Detect(breakbeat, SampleRate, 128.0);

        Assert.NotNull(rpKick);
        Assert.NotNull(rpBreak);

        Assert.True(rpBreak!.FourOnFloor < rpKick!.FourOnFloor,
            $"Breakbeat FourOnFloor ({rpBreak.FourOnFloor:0.000}) should be lower than pure kick ({rpKick.FourOnFloor:0.000})");
    }

    [Fact]
    public void Breakbeat_Syncopation_HigherThanPureKick()
    {
        var kickOnly  = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var breakbeat = BrokenKickWithSyncopation(128.0, DurationSeconds, SampleRate);

        var rpKick  = RhythmPatternDetector.Detect(kickOnly,  SampleRate, 128.0);
        var rpBreak = RhythmPatternDetector.Detect(breakbeat, SampleRate, 128.0);

        Assert.NotNull(rpKick);
        Assert.NotNull(rpBreak);

        Assert.True(rpBreak!.Syncopation > rpKick!.Syncopation,
            $"Breakbeat Syncopation ({rpBreak.Syncopation:0.000}) should exceed pure kick ({rpKick.Syncopation:0.000})");
    }

    // =========================================================================
    // Half-time feel -> HalfTimeFeel elevated vs straight 4/4
    // =========================================================================

    [Fact]
    public void HalfTimePulse_HalfTimeFeel_HigherThanStraight()
    {
        // Kick every two beats (half-tempo) vs every beat.
        var straight  = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var halfTime  = KickClickTrain(64.0,  DurationSeconds, SampleRate); // same click at half tempo

        // Tell the detector the BPM is 128 for both - the half-time track
        // will show up as a half-time feel at 128 BPM.
        var rpStraight = RhythmPatternDetector.Detect(straight, SampleRate, 128.0);
        var rpHalfTime = RhythmPatternDetector.Detect(halfTime, SampleRate, 128.0);

        Assert.NotNull(rpStraight);
        Assert.NotNull(rpHalfTime);

        Assert.True(rpHalfTime!.HalfTimeFeel > rpStraight!.HalfTimeFeel,
            $"Half-time HalfTimeFeel ({rpHalfTime.HalfTimeFeel:0.000}) should exceed straight ({rpStraight.HalfTimeFeel:0.000})");
    }

    // =========================================================================
    // ClassifyBeatType - direct unit tests of the rule logic
    // =========================================================================

    [Fact]
    public void Classify_Halftime_WinsWhenHighHalfTimeFeel()
    {
        // HalfTimeFeel dominates regardless of other features.
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: 0.80, offbeat: 0.50, swing: 0.40, syncopation: 0.10,
            halfTime: RhythmPatternDetector.Thresholds.HalftimeWin + 0.01);
        Assert.Equal("halftime", bt);
    }

    [Fact]
    public void Classify_Breaks_WhenLowFofAndHighSyncopation()
    {
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: RhythmPatternDetector.Thresholds.FofBreaksMax - 0.01,
            offbeat: 0.20,
            swing: 0.10,
            syncopation: RhythmPatternDetector.Thresholds.SynBreaksMin + 0.01,
            halfTime: 0.10);
        Assert.Equal("breaks", bt);
    }

    [Fact]
    public void Classify_FourOnFloorGroovy_WhenHighOffbeat()
    {
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: 0.75,
            offbeat: RhythmPatternDetector.Thresholds.OffbeatGroovyMin + 0.01,
            swing: 0.10,
            syncopation: 0.10,
            halfTime: 0.10);
        Assert.Equal("4x4-groovy", bt);
    }

    [Fact]
    public void Classify_FourOnFloorGroovy_WhenHighSwing()
    {
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: 0.75,
            offbeat: 0.20,
            swing: RhythmPatternDetector.Thresholds.SwingGroovyMin + 0.01,
            syncopation: 0.10,
            halfTime: 0.10);
        Assert.Equal("4x4-groovy", bt);
    }

    [Fact]
    public void Classify_OffbeatBass_WhenVeryHighOffbeatButLowSwing()
    {
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: 0.75,
            offbeat: RhythmPatternDetector.Thresholds.OffbeatBassMin + 0.01,
            swing: RhythmPatternDetector.Thresholds.SwingGroovyMin - 0.05,
            syncopation: 0.10,
            halfTime: 0.10);
        Assert.Equal("offbeat-bass", bt);
    }

    [Fact]
    public void Classify_FourOnFloorDriving_WhenLowEverything()
    {
        var bt = RhythmPatternDetector.ClassifyBeatType(
            fof: 0.80,
            offbeat: 0.10,
            swing: 0.05,
            syncopation: 0.05,
            halfTime: 0.10);
        Assert.Equal("4x4-driving", bt);
    }

    // =========================================================================
    // Synthetic signal generators
    // =========================================================================

    /// <summary>
    /// Generates a pure four-on-the-floor kick train (Gaussian pulses at every beat).
    /// Uses the low-band frequency range (carrier = 60 Hz) to ensure the energy lands
    /// in the kick band of the detector.
    /// </summary>
    private static float[] KickClickTrain(double bpm, double durationSeconds, int sampleRate)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        var beatPeriodSamples = sampleRate * 60.0 / bpm;
        var sigma = sampleRate * 0.003;  // ~3 ms pulse

        // Use a 60 Hz carrier so the energy lands in the kick/low band.
        var kickFreq = 60.0;

        for (var beat = 0; beat * beatPeriodSamples < n; beat++)
        {
            var beatCentre = beat * beatPeriodSamples;
            var lo = (int)Math.Max(0,     Math.Round(beatCentre - 5 * sigma));
            var hi = (int)Math.Min(n - 1, Math.Round(beatCentre + 5 * sigma));
            for (var i = lo; i <= hi; i++)
            {
                var diff  = (i - beatCentre) / sigma;
                var env   = 0.9 * Math.Exp(-0.5 * diff * diff);
                var phase = 2.0 * Math.PI * kickFreq * i / sampleRate;
                samples[i] += (float)(env * Math.Sin(phase));
            }
        }

        return samples;
    }

    /// <summary>
    /// Generates a kick on every beat PLUS a swung hi-hat click between each beat.
    /// <paramref name="swingFactor"/> controls where the hat lands:
    ///   0.5 = exactly on the half-beat (straight),
    ///   0.67 = typical jazz swing (2/3 of the beat),
    ///   0.75 = heavy swing.
    /// Uses a high-frequency carrier (2000 Hz) for the hat energy.
    /// </summary>
    private static float[] KickPlusSwungHat(
        double bpm, double durationSeconds, int sampleRate, double swingFactor)
    {
        var samples = KickClickTrain(bpm, durationSeconds, sampleRate);
        var n = samples.Length;
        var beatPeriodSamples = sampleRate * 60.0 / bpm;
        var sigma = sampleRate * 0.002;  // ~2 ms sharp hat pulse
        var hatFreq = 2000.0;            // hi-hat carrier -> lands in mid band

        for (var beat = 0; beat * beatPeriodSamples < n; beat++)
        {
            // Place hat at swingFactor x beatPeriod after the beat.
            var hatCentre = beat * beatPeriodSamples + swingFactor * beatPeriodSamples;
            var lo = (int)Math.Max(0,     Math.Round(hatCentre - 4 * sigma));
            var hi = (int)Math.Min(n - 1, Math.Round(hatCentre + 4 * sigma));
            for (var i = lo; i <= hi; i++)
            {
                var diff  = (i - hatCentre) / sigma;
                var env   = 0.6 * Math.Exp(-0.5 * diff * diff);
                var phase = 2.0 * Math.PI * hatFreq * i / sampleRate;
                samples[i] += (float)(env * Math.Sin(phase));
            }
        }

        return samples;
    }

    /// <summary>
    /// Generates a broken kick pattern plus off-beat syncopation hits.
    /// Kick on beats 1 and 3 (low band), plus syncopation clicks at 1/4 and 3/4 (mid band).
    /// </summary>
    private static float[] BrokenKickWithSyncopation(
        double bpm, double durationSeconds, int sampleRate)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        var beatPeriodSamples = sampleRate * 60.0 / bpm;
        var sigma = sampleRate * 0.003;

        var kickFreq = 60.0;    // low band
        var snapFreq = 1500.0;  // mid band (snare-like)

        for (var beat = 0; beat * beatPeriodSamples < n; beat++)
        {
            var beatCentre = beat * beatPeriodSamples;

            // Kick only on even beats (beat 0, 2, 4, ...).
            if (beat % 2 == 0)
            {
                var lo = (int)Math.Max(0,     Math.Round(beatCentre - 5 * sigma));
                var hi = (int)Math.Min(n - 1, Math.Round(beatCentre + 5 * sigma));
                for (var i = lo; i <= hi; i++)
                {
                    var diff  = (i - beatCentre) / sigma;
                    var env   = 0.9 * Math.Exp(-0.5 * diff * diff);
                    var phase = 2.0 * Math.PI * kickFreq * i / sampleRate;
                    samples[i] += (float)(env * Math.Sin(phase));
                }
            }

            // Syncopation hit at 1/4 of the beat period (off-grid).
            var snapCentre = beatCentre + beatPeriodSamples * 0.25;
            {
                var lo = (int)Math.Max(0,     Math.Round(snapCentre - 3 * sigma));
                var hi = (int)Math.Min(n - 1, Math.Round(snapCentre + 3 * sigma));
                for (var i = lo; i <= hi; i++)
                {
                    var diff  = (i - snapCentre) / sigma;
                    var env   = 0.7 * Math.Exp(-0.5 * diff * diff);
                    var phase = 2.0 * Math.PI * snapFreq * i / sampleRate;
                    samples[i] += (float)(env * Math.Sin(phase));
                }
            }
        }

        return samples;
    }
}
