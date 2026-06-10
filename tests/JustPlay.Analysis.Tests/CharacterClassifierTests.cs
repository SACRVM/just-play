using JustPlay.Analysis;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="CharacterClassifier"/> (vibe quartet: punch / groove / dark / hypnotic).
///
/// Coverage:
/// 1. Guard cases: null/too-short input → null (no throw).
/// 2. All returned scalars are in [0, 1].
/// 3. Dark: bright HF-rich signal (white noise) → dark≈0; low-passed dull sine → dark≈1.
/// 4. Hypnotic: a highly repetitive loop (constant spectrum) → hypnotic≈1;
///             an evolving signal (centroid sweeps) → hypnotic&lt;hypnotic_of_repetitive.
/// 5. Harshness elevated for white noise (noisy fatigue flag).
/// 6. SpectralFlatness white noise > kick train.
/// 7. BassPunch: kick train > silence.
/// 8. BassGroove: groovy RhythmPattern > straight.
/// 9. Low FoF → punch and groove clamped down.
/// 10. SpectralEnergyDetector.BlendedScore in [0,1].
/// </summary>
public class CharacterClassifierTests
{
    private const int SampleRate = 11025;
    private const double DurationSeconds = 20.0;

    // =========================================================================
    // 1. Guard cases
    // =========================================================================

    [Fact]
    public void Compute_NullSamples_ReturnsNull()
    {
        var cf = CharacterClassifier.Compute(
            null!, SampleRate, 128.0, -12.0, 0.9, 0.5, null);
        Assert.Null(cf);
    }

    [Fact]
    public void Compute_EmptySamples_ReturnsNull()
    {
        var cf = CharacterClassifier.Compute(
            [], SampleRate, 128.0, -12.0, 0.9, 0.5, null);
        Assert.Null(cf);
    }

    [Fact]
    public void Compute_TooShortSamples_ReturnsNull()
    {
        var cf = CharacterClassifier.Compute(
            new float[8], SampleRate, 128.0, -12.0, 0.9, 0.5, null);
        Assert.Null(cf);
    }

    // =========================================================================
    // 2. All scalars in [0, 1] for a generic kick signal
    // =========================================================================

    [Fact]
    public void Compute_AllScalars_InUnitInterval()
    {
        var samples = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var cf = CharacterClassifier.Compute(
            samples, SampleRate, 128.0,
            integratedLufs: -14.0,
            peak: 0.9,
            rawEnergyScore: 0.55,
            rhythm: null);

        Assert.NotNull(cf);
        Assert.InRange(cf!.SpectralFlatness, 0.0, 1.0);
        Assert.InRange(cf.Harshness,          0.0, 1.0);
        Assert.InRange(cf.BassPunch,          0.0, 1.0);
        Assert.InRange(cf.BassGroove,         0.0, 1.0);
        Assert.InRange(cf.Dark,               0.0, 1.0);
        Assert.InRange(cf.Hypnotic,           0.0, 1.0);
    }

    // =========================================================================
    // 3. Dark: bright signal → dark≈0; dull/bass-only signal → dark≈1
    // =========================================================================

    [Fact]
    public void BrightSignal_WhiteNoise_Dark_IsLow()
    {
        // White noise has energy spread across all frequencies including high — bright → dark≈0.
        var samples = WhiteNoiseSamples(DurationSeconds, SampleRate);
        var cf = CharacterClassifier.Compute(
            samples, SampleRate, 128.0,
            integratedLufs: -12.0, peak: 0.9, rawEnergyScore: 0.7, rhythm: null);

        Assert.NotNull(cf);
        // White noise centroid should be well above the CentroidLo=300 Hz threshold,
        // giving a high nBright → dark should be clearly below 0.5.
        Assert.True(cf!.Dark < 0.5,
            $"White noise (broadband) should give dark < 0.5, got {cf.Dark:F3}");
    }

    [Fact]
    public void DullLowFreqSignal_Dark_IsHigherThanBrightSignal()
    {
        // A low-frequency sine (100 Hz) has all energy in the bass region — low centroid → dark≈1.
        // A white-noise signal has broadband content — high centroid → dark≈0.
        var dullSamples   = QuietSine(100.0, DurationSeconds, SampleRate, amplitude: 0.5f);
        var brightSamples = WhiteNoiseSamples(DurationSeconds, SampleRate);

        var dullCf   = CharacterClassifier.Compute(dullSamples,   SampleRate, 128.0, -10.0, 0.5, 0.5, null);
        var brightCf = CharacterClassifier.Compute(brightSamples, SampleRate, 128.0, -12.0, 0.9, 0.7, null);

        Assert.NotNull(dullCf);
        Assert.NotNull(brightCf);
        Assert.True(dullCf!.Dark > brightCf!.Dark,
            $"100 Hz sine (dark={dullCf.Dark:F3}) should be darker than white noise (dark={brightCf.Dark:F3})");
    }

    [Fact]
    public void HighFreqSine_Dark_IsLow()
    {
        // A high-frequency sine (4000 Hz → above analysis range, but a 2000 Hz sine still gives
        // a high centroid) → should score low dark.
        // Use 2000 Hz which is near CentroidHi = 3000 Hz → nBright close to 1 → dark close to 0.
        var brightSine = QuietSine(2000.0, DurationSeconds, SampleRate, amplitude: 0.5f);
        var darkSine   = QuietSine(100.0,  DurationSeconds, SampleRate, amplitude: 0.5f);

        var brightCf = CharacterClassifier.Compute(brightSine, SampleRate, 128.0, -10.0, 0.5, 0.5, null);
        var darkCf   = CharacterClassifier.Compute(darkSine,   SampleRate, 128.0, -10.0, 0.5, 0.5, null);

        Assert.NotNull(brightCf);
        Assert.NotNull(darkCf);
        Assert.True(brightCf!.Dark < darkCf!.Dark,
            $"2 kHz sine (dark={brightCf.Dark:F3}) should be brighter than 100 Hz sine (dark={darkCf.Dark:F3})");
    }

    // =========================================================================
    // 4. Hypnotic: highly repetitive → higher than evolving signal
    // =========================================================================

    [Fact]
    public void RepetitiveLoop_Hypnotic_HigherThan_EvolvingSignal()
    {
        // A pure sine wave has a constant spectrum (same centroid every frame) → low centroid variance
        // → Hypnotic≈1.
        // An AM-modulated signal whose carrier sweeps in frequency = evolving → high centroid variance
        // → Hypnotic low.
        var repetitiveSamples = QuietSine(440.0, DurationSeconds, SampleRate, amplitude: 0.5f);

        // Evolving: a linear-chirp from 200 Hz to 4000 Hz over the track duration.
        var evolvingSamples = LinearChirp(200.0, 4000.0, DurationSeconds, SampleRate, amplitude: 0.5f);

        var repCf  = CharacterClassifier.Compute(repetitiveSamples, SampleRate, null, -10.0, 0.5, 0.4, null);
        var evoCf  = CharacterClassifier.Compute(evolvingSamples,   SampleRate, null, -10.0, 0.5, 0.4, null);

        Assert.NotNull(repCf);
        Assert.NotNull(evoCf);
        Assert.True(repCf!.Hypnotic > evoCf!.Hypnotic,
            $"Repetitive sine (hypnotic={repCf.Hypnotic:F3}) should exceed evolving chirp (hypnotic={evoCf.Hypnotic:F3})");
    }

    [Fact]
    public void PureSine_Hypnotic_IsHigh()
    {
        // A pure sine at constant frequency: all frames have the same spectral centroid.
        // Centroid std-dev ≈ 0 → Hypnotic should be close to 1.
        var samples = QuietSine(440.0, DurationSeconds, SampleRate, amplitude: 0.5f);
        var cf = CharacterClassifier.Compute(
            samples, SampleRate, null,
            integratedLufs: -10.0, peak: 0.5, rawEnergyScore: 0.4, rhythm: null);

        Assert.NotNull(cf);
        // Hypnotic > 0.7 for a perfectly repetitive signal.
        Assert.True(cf!.Hypnotic > 0.7,
            $"Pure sine (constant spectrum) should give Hypnotic > 0.7, got {cf.Hypnotic:F3}");
    }

    // =========================================================================
    // 5. Harshness elevated for white noise
    // =========================================================================

    [Fact]
    public void WhiteNoise_HighHarshness()
    {
        var samples = WhiteNoiseSamples(DurationSeconds, SampleRate);
        var cf = CharacterClassifier.Compute(
            samples, SampleRate, 128.0,
            integratedLufs: -8.0,
            peak: 0.9,
            rawEnergyScore: 0.80,
            rhythm: null);

        Assert.NotNull(cf);
        Assert.True(cf!.Harshness >= 0.30,
            $"White noise harshness ({cf.Harshness:F3}) should be elevated (≥ 0.30)");
    }

    // =========================================================================
    // 6. SpectralFlatness: white noise > kick train
    // =========================================================================

    [Fact]
    public void WhiteNoise_SpectralFlatness_IsHigherThan_KickTrain()
    {
        var noiseSamples = WhiteNoiseSamples(DurationSeconds, SampleRate);
        var kickSamples  = KickClickTrain(128.0, DurationSeconds, SampleRate);

        var noiseCf = CharacterClassifier.Compute(noiseSamples, SampleRate, 128.0, -12.0, 0.9, 0.7, null);
        var kickCf  = CharacterClassifier.Compute(kickSamples,  SampleRate, 128.0, -14.0, 0.9, 0.55, null);

        Assert.NotNull(noiseCf);
        Assert.NotNull(kickCf);
        Assert.True(noiseCf!.SpectralFlatness > kickCf!.SpectralFlatness,
            $"White noise flatness ({noiseCf.SpectralFlatness:F3}) should exceed kick train ({kickCf.SpectralFlatness:F3})");
    }

    // =========================================================================
    // 7. BassPunch: kick train > silence
    // =========================================================================

    [Fact]
    public void KickTrain_BassPunch_HigherThan_Silence()
    {
        var kickSamples    = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var silenceSamples = new float[(int)(DurationSeconds * SampleRate)];

        var kickCf    = CharacterClassifier.Compute(kickSamples,    SampleRate, 128.0, -14.0, 0.9, 0.55, null);
        var silenceCf = CharacterClassifier.Compute(silenceSamples, SampleRate, 128.0, -60.0, 0.0, 0.01, null);

        Assert.NotNull(kickCf);
        Assert.True(kickCf!.BassPunch > 0.0,
            $"Kick train BassPunch ({kickCf.BassPunch:F3}) should be > 0");
        if (silenceCf is not null)
        {
            Assert.True(kickCf.BassPunch > silenceCf.BassPunch,
                $"Kick train BassPunch ({kickCf.BassPunch:F3}) should exceed silence ({silenceCf.BassPunch:F3})");
        }
    }

    // =========================================================================
    // 8. BassGroove: groovy RhythmPattern > straight
    // =========================================================================

    [Fact]
    public void GroovyPattern_BassGroove_HigherThan_Straight()
    {
        var kickSamples = KickClickTrain(128.0, DurationSeconds, SampleRate);

        var straightRhythm = new RhythmPattern
        {
            FourOnFloor = 0.90, OffbeatEnergy = 0.10, Swing = 0.05,
            Syncopation = 0.05, HalfTimeFeel = 0.10, BeatType = "4x4-driving",
        };
        var groovyRhythm = new RhythmPattern
        {
            FourOnFloor = 0.85, OffbeatEnergy = 0.55, Swing = 0.45,
            Syncopation = 0.35, HalfTimeFeel = 0.10, BeatType = "4x4-groovy",
        };

        var straightCf = CharacterClassifier.Compute(kickSamples, SampleRate, 128.0, -14.0, 0.9, 0.55, straightRhythm);
        var groovyCf   = CharacterClassifier.Compute(kickSamples, SampleRate, 128.0, -14.0, 0.9, 0.55, groovyRhythm);

        Assert.NotNull(straightCf);
        Assert.NotNull(groovyCf);
        Assert.True(groovyCf!.BassGroove > straightCf!.BassGroove,
            $"Groovy BassGroove ({groovyCf.BassGroove:F3}) should exceed straight ({straightCf.BassGroove:F3})");
    }

    // =========================================================================
    // 9. Low FoF → punch and groove clamped down
    // =========================================================================

    [Fact]
    public void LowFoF_SetsLowConfidence_And_ClampsGrooveAndPunch()
    {
        var kickSamples = KickClickTrain(128.0, DurationSeconds, SampleRate);

        var highFofRhythm = new RhythmPattern
        {
            FourOnFloor = 0.90, OffbeatEnergy = 0.55, Swing = 0.60,
            Syncopation = 0.50, HalfTimeFeel = 0.10, BeatType = "4x4-groovy",
        };
        var lowFofRhythm = new RhythmPattern
        {
            FourOnFloor = CharacterClassifier.FofConfidenceMin - 0.01,
            OffbeatEnergy = 0.55, Swing = 0.60, Syncopation = 0.50,
            HalfTimeFeel = 0.10, BeatType = "4x4-groovy",
        };

        var highFofCf = CharacterClassifier.Compute(kickSamples, SampleRate, 128.0, -14.0, 0.9, 0.55, highFofRhythm);
        var lowFofCf  = CharacterClassifier.Compute(kickSamples, SampleRate, 128.0, -14.0, 0.9, 0.55, lowFofRhythm);

        Assert.NotNull(highFofCf);
        Assert.NotNull(lowFofCf);
        Assert.False(highFofCf!.LowConfidence);
        Assert.True(lowFofCf!.LowConfidence, "Low FourOnFloor should set LowConfidence");
        // Low-FoF version should have lower (clamped) groove than high-FoF version.
        Assert.True(lowFofCf.BassGroove < highFofCf.BassGroove,
            $"Low FoF groove ({lowFofCf.BassGroove:F3}) should be clamped below high FoF ({highFofCf.BassGroove:F3})");
        Assert.True(lowFofCf.BassPunch < highFofCf.BassPunch,
            $"Low FoF punch ({lowFofCf.BassPunch:F3}) should be clamped below high FoF ({highFofCf.BassPunch:F3})");
    }

    // =========================================================================
    // 10. SpectralEnergyDetector.BlendedScore in [0,1]
    // =========================================================================

    [Fact]
    public void BlendedScore_InUnitInterval_ForKickTrain()
    {
        var detector = new SpectralEnergyDetector();
        var samples  = KickClickTrain(128.0, DurationSeconds, SampleRate);
        var audio    = new JustPlay.Core.Models.DecodedAudio(samples, SampleRate);
        var features = detector.ExtractFeatures(audio);

        Assert.NotNull(features);
        var score = SpectralEnergyDetector.BlendedScore(features!.Value);
        Assert.InRange(score, 0.0, 1.0);
    }

    // =========================================================================
    // Synthetic signal generators
    // =========================================================================

    /// <summary>Pure four-on-the-floor kick train at 60 Hz (low band).</summary>
    private static float[] KickClickTrain(double bpm, double durationSeconds, int sampleRate)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        var beatPeriodSamples = sampleRate * 60.0 / bpm;
        var sigma = sampleRate * 0.003;
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

    /// <summary>White noise — maximum spectral flatness, broadband (bright).</summary>
    private static float[] WhiteNoiseSamples(double durationSeconds, int sampleRate)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        var rng = new Random(42);
        for (var i = 0; i < n; i++)
            samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.5f;
        return samples;
    }

    /// <summary>
    /// Quiet single-frequency sine — maximally tonal, constant spectrum
    /// (low centroid variance → hypnotic≈1 for low freq; dark≈1 for low freq).
    /// </summary>
    private static float[] QuietSine(double freqHz, double durationSeconds, int sampleRate, float amplitude)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = amplitude * (float)Math.Sin(2.0 * Math.PI * freqHz * i / sampleRate);
        return samples;
    }

    /// <summary>
    /// Linear chirp: frequency sweeps linearly from <paramref name="startHz"/> to
    /// <paramref name="endHz"/> over the track duration. Produces high centroid variance
    /// → hypnotic≈0 (evolving/progressive).
    /// </summary>
    private static float[] LinearChirp(
        double startHz, double endHz, double durationSeconds, int sampleRate, float amplitude)
    {
        var n = (int)(durationSeconds * sampleRate);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t    = (double)i / sampleRate;
            var freq = startHz + (endHz - startHz) * t / durationSeconds;
            var phase = 2.0 * Math.PI * freq * t;
            samples[i] = amplitude * (float)Math.Sin(phase);
        }
        return samples;
    }
}
