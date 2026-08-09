using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// The fingerprint of a detection version. Feeds constructed signals through the REAL analysis
/// orchestrator (<see cref="TrackAnalysisService"/>, the same class the app and the CLI resolve
/// from DI) and pins the composite <see cref="AnalysisResult"/> it produces against recorded
/// values.
///
/// <para>
/// <b>Why this exists.</b> <see cref="TrackAnalysisState.CurrentVersion"/> is the contract that
/// says which detection stack produced a stored analysis; the library index stores it per row and
/// <c>StaleRule.DetectionVersionBelow</c> is how rows that need redoing are found. That contract
/// has exactly one failure mode, and it has already happened: commit c687d46 (2026-07-10) changed
/// what the analyzers compute - <c>DecodeMono</c> had been handing interleaved stereo back as
/// "mono", so FLAC/WAV/AIFF were analysed at half speed - and the version was not bumped. "v9
/// before the fix" and "v9 after the fix" are therefore indistinguishable through the one field
/// that survives a library re-sync (an unchanged file is skipped on a cheap size+mtime key, so no
/// timestamp inside the file ever reaches the index). Measured 2026-08-01: 957 files carry numbers
/// no rule can find. The whole point of the tests below is that the NEXT such commit fails here,
/// at commit time, instead of eleven months later.
/// </para>
///
/// <para>
/// <b>The lock is two-way.</b> An analyzer change without a version bump fails the value
/// assertions; a version bump without re-recording fails
/// <see cref="GoldenSet_IsRecordedForTheCurrentDetectionVersion"/>. Neither half can be satisfied
/// by accident, and a failure of either one names the other as the fix.
/// </para>
///
/// <para>
/// <b>What this does NOT cover</b> - stated here rather than discovered later:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The BPM detector itself.</b> <c>BassBpmDetector</c> decodes through BASS from a file path;
/// it needs the native library and real audio, and these tests must run with neither. A fixed raw
/// BPM is injected instead, which means the octave-correction path, <c>AcfSharpness</c> and
/// everything derived from the final BPM ARE pinned on real DSP, but a change to BASS_FX's own
/// tempo estimate is invisible here.
/// </item>
/// <item>
/// <b>The ML key detector.</b> Production resolves <c>IKeyDetector</c> to <c>BestKeyDetector</c>,
/// which prefers <c>MlKeyDetector</c> (ONNX) and falls back to <c>HpcpKeyDetector</c>. The ONNX
/// model is an optional file that may be absent, so pinning the ML branch would make the golden
/// depend on what is installed - a golden that reports a different truth per machine is worse than
/// no golden. The always-available DSP branch is what is pinned.
/// </item>
/// <item>
/// <b>The decoder.</b> <c>BassAudioDecoder</c> cannot run here for the same reason. The mono
/// regression tests below pin the CONTRACT that decoder must satisfy (frames averaged, not
/// interleaved) and prove the analysis outputs move when it is violated - they do not execute
/// BASS's own read loop.
/// </item>
/// <item>
/// <b>Absolute correctness.</b> These are goldens, not ground truth. Two values are cross-checked
/// against constructed truth (the 128 BPM the kick train was built at, and the BS.1770 level of a
/// 1 kHz tone of known amplitude); the rest say only "the stack still computes what it computed
/// when v9 was recorded". That is the question the version field answers, and it is a different
/// question from "is the maths right", which the 247 per-analyzer tests in this project already ask.
/// </item>
/// <item>
/// <b>Values that saturate at a clamp on a given signal</b> are left unpinned THERE, because a pin
/// sitting on a ceiling cannot notice anything smaller than a fall off the ceiling - the same
/// defect as a golden that cannot fail at all. Concretely: <c>Dark</c> and
/// <c>Fingerprint.Danceability</c> are not pinned on the kick train or the stereo signal (they
/// measure exactly 1.0 and exactly 3.0 there - a 60 Hz kick body has no highs, and a metronomic
/// train is at the top of the DFA scale). Both are pinned unsaturated on the chord, so neither
/// value is uncovered overall.
/// </item>
/// <item>
/// <b>Vector shape beyond a peak bin and an L1 sum.</b> The 64-bin scale transform and 24-bin
/// cyclic tempogram are pinned by those two measures rather than by 88 literals, and the peak-bin
/// half of that is coarse - see the comment at the pin.
/// </item>
/// <item>
/// <b>Nothing in this file is a UI or persistence check.</b> Whether the pinned values are written
/// to a tag, read back, or mapped onto an index row is <c>AnalysisStateCodec</c>'s and
/// <c>TrackIndexMapping</c>'s business and is tested in their own projects.
/// </item>
/// </list>
/// </summary>
public sealed class DetectionVersionGoldenTests
{
    // =============================================================================================
    // The version these goldens were recorded against.
    // =============================================================================================

    /// <summary>
    /// The detection version the values below are the fingerprint OF. Must equal
    /// <see cref="TrackAnalysisState.CurrentVersion"/>; see
    /// <see cref="GoldenSet_IsRecordedForTheCurrentDetectionVersion"/>.
    /// </summary>
    private const int RecordedForDetectionVersion = 9;

    private static string VersionAdmonition(string signal) =>
        $"The recorded values below are the fingerprint of detection version " +
        $"{RecordedForDetectionVersion} on the '{signal}' signal." + NL +
        "  - If you changed an analyzer ON PURPOSE: bump TrackAnalysisState.CurrentVersion and" + NL +
        "    re-record this golden from the 'measured now' block printed below. A stored analysis" + NL +
        "    is only findable again through that version number - the library index keeps nothing" + NL +
        "    else that survives a re-sync (see StaleRule.DetectionVersionBelow)." + NL +
        "  - If you did NOT change an analyzer on purpose: you broke one. This is c687d46" + NL +
        "    happening again (that commit changed what every sample-based analyzer computed and" + NL +
        "    left the version at 9, which is why 957 files now carry numbers no rule can find).";

    private static readonly string NL = Environment.NewLine;

    // =============================================================================================
    // Tolerances - one named constant per KIND of value, so a loosened tolerance is a visible edit.
    // =============================================================================================

    /// <summary>
    /// Unit-interval scores (vibe quartet, rhythm features, key confidence, grid confidence).
    /// 1e-4 is ~3 orders of magnitude above float32 accumulation noise in these FFT sums and ~2
    /// orders BELOW the smallest change any real analyzer edit produces - the c687d46 downmix
    /// change moved these by 0.1 and more (see <see cref="MonoDownmix_InterleavedPassthrough_MovesTheFingerprint"/>,
    /// which measures the actual gap). Tight enough to catch a real change, loose enough that a
    /// different vectorisation on another CPU cannot flap it.
    /// </summary>
    private const double UnitScoreTolerance = 1e-4;

    /// <summary>Decibel values (LUFS, ReplayGain). 1e-3 dB is far below audibility and far below
    /// any change a real analyzer edit makes; the arithmetic is double-precision throughout.</summary>
    private const double DecibelTolerance = 1e-3;

    /// <summary>Linear sample peak - a plain max over the buffer, so it is exact bar the float
    /// literal round-trip.</summary>
    private const double PeakTolerance = 1e-6;

    /// <summary>BPM. The corrector's output is the raw value times a power of two or a refined
    /// ACF-derived estimate; 1e-6 pins it without pinning the printf format.</summary>
    private const double BpmTolerance = 1e-6;

    /// <summary>DFA danceability scalar (~0..3) and the L1 sums of the fingerprint vectors.</summary>
    private const double ScalarTolerance = 1e-3;

    // =============================================================================================
    // Test seams: the two production dependencies that need real audio, replaced by constructions
    // whose ground truth we know. Everything else in the pipeline is the real analyzer.
    // =============================================================================================

    /// <summary>
    /// Stands in for <c>BassBpmDetector</c>, which decodes through BASS from a file path. Returning
    /// a fixed value is not a weakening of the golden: <see cref="TrackAnalysisService"/> feeds this
    /// number into the REAL <see cref="TempoOctaveCorrector"/> against the REAL decoded samples, so
    /// the corrected BPM and <c>AcfSharpness</c> that get pinned are genuine DSP output.
    /// </summary>
    private sealed class FixedBpmDetector(double? bpm) : IBpmDetector
    {
        public double? Detect(string filePath, CancellationToken ct = default) => bpm;
    }

    /// <summary>How a <see cref="SyntheticDecoder"/> turns its two channels into the mono buffer
    /// the analyzers see.</summary>
    private enum Downmix
    {
        /// <summary>Average each frame - the contract <c>IAudioDecoder.DecodeMono</c> promises and
        /// what <c>BassAudioDecoder</c> has done since c687d46.</summary>
        FrameAverage,

        /// <summary>Hand the interleaved buffer back untouched - the c687d46 bug. Twice the sample
        /// count at the same declared rate, i.e. half-speed pseudo-audio.</summary>
        InterleavedPassthrough,
    }

    /// <summary>
    /// Stands in for <c>BassAudioDecoder</c>. Synthesises both channels directly at whatever rate
    /// the service asks for (44.1 kHz for key, 11.025 kHz for everything else), so no resampler sits
    /// between the constructed signal and the analyzers - the ground truth stays exact.
    /// </summary>
    private sealed class SyntheticDecoder(
        Func<int, float[]> left, Func<int, float[]> right, Downmix mode) : IAudioDecoder
    {
        public DecodedAudio DecodeMono(string filePath, int targetSampleRate, CancellationToken ct = default)
        {
            var interleaved = SyntheticSignals.InterleaveStereo(left(targetSampleRate), right(targetSampleRate));
            var mono = mode == Downmix.FrameAverage
                ? SyntheticSignals.FrameAverageStereo(interleaved)
                : interleaved;
            return new DecodedAudio(mono, targetSampleRate);
        }
    }

    /// <summary>
    /// Builds the production orchestrator with the real key / energy / loudness detectors and runs
    /// it. <paramref name="right"/> defaults to <paramref name="left"/>, i.e. a mono source.
    /// </summary>
    private static async Task<AnalysisResult> AnalyzeAsync(
        Func<int, float[]> left,
        double? rawBpm,
        Func<int, float[]>? right = null,
        Downmix mode = Downmix.FrameAverage)
    {
        var service = new TrackAnalysisService(
            new FixedBpmDetector(rawBpm),
            new SyntheticDecoder(left, right ?? left, mode),
            new HpcpKeyDetector(),
            new SpectralEnergyDetector(),
            new Bs1770LoudnessDetector());

        // The path is never opened - SyntheticDecoder ignores it and FixedBpmDetector ignores it.
        // Nothing in this file touches the file system.
        return await service.AnalyzeAsync("synthetic://in-memory");
    }

    // =============================================================================================
    // The signals. All closed-form, RNG-free, generated at the rate the service asks for.
    // =============================================================================================

    /// <summary>
    /// 30.0 s = exactly 64 beats of four-on-the-floor at 128 BPM: a 60 Hz kick body decaying with a
    /// 90 ms time constant, peak-normalised to 0.9. Ground truth built into the signal: the beat
    /// period. This is the rhythm/grid/vibe workhorse.
    /// </summary>
    private static float[] Kick128(int rate) =>
        SyntheticSignals.NormalizePeak(
            SyntheticSignals.KickTrain(30.0, rate, bpm: 128.0, toneHz: 60.0, decaySeconds: 0.09, amplitude: 0.8),
            0.9);

    /// <summary>
    /// 20 s A minor triad (A3 220 / C4 261.626 / E4 329.628 Hz), 6 harmonics each at 1/k, peak
    /// normalised to 0.7. Every fundamental clears the detector's 100 Hz high-pass. The tonic
    /// carries twice the amplitude of the other two chord tones on purpose: {A, C, E} is exactly
    /// the pitch-class set of C major as well, so an evenly weighted triad is genuinely ambiguous
    /// between A minor and its relative major and the winner would be decided by whichever key
    /// profile happens to correlate a hair higher - not a truth worth pinning.
    /// </summary>
    private static float[] ChordAMinor(int rate) =>
        SyntheticSignals.NormalizePeak(
            SyntheticSignals.Chord(20.0, rate,
                [(220.0, 0.6), (261.6255653005986, 0.3), (329.6275569128699, 0.3)], harmonics: 6),
            0.7);

    /// <summary>20 s 1 kHz sine at amplitude 0.1 - the loudness reference, see
    /// <see cref="Tone1k_LoudnessMatchesTheBs1770ClosedForm"/>.</summary>
    private static float[] Tone1k(int rate) => SyntheticSignals.Sine(20.0, rate, 1000.0, 0.1);

    /// <summary>
    /// 30 s 440 Hz sine at 0.5 - the second channel of the distinct-channel stereo case. Its
    /// length must match <see cref="Kick128"/>'s: a decoder averages FRAMES, and a frame needs both
    /// channels.
    /// </summary>
    private static float[] SineA440(int rate) => SyntheticSignals.Sine(30.0, rate, 440.0, 0.5);

    /// <summary>10 s of digital black.</summary>
    private static float[] Silence(int rate) => SyntheticSignals.Silence(10.0, rate);

    // =============================================================================================
    // Golden 1 - the kick train. Rhythm, grid confidence, vibe, energy, loudness.
    // =============================================================================================

    /// <summary>
    /// Recorded 2026-08-09 against detection version 9.
    ///
    /// <para><b>Deliberately NOT pinned on this signal</b>, because both values sit ON a clamp
    /// ceiling here and a pin at a ceiling can only ever notice a change big enough to fall off it:
    /// <c>Dark</c> (measures exactly 1.0 - a 60 Hz kick body has no high-frequency content at all,
    /// so the normalised brightness bottoms out) and <c>Fingerprint.Danceability</c> (measures
    /// exactly 3.0, the <c>Math.Clamp(1/alpha, 0, 3)</c> ceiling in
    /// <c>BeatFingerprintExtractor.ComputeDfaDanceability</c> - a metronomic train is as steady as
    /// the DFA scale can express). Both ARE pinned unsaturated on the chord signal, so neither is
    /// uncovered; they are only uncovered HERE.</para>
    ///
    /// <para><b>Two values worth knowing the margin of.</b> <c>HalfTimeFeel</c> lands at 0.507
    /// against a <c>Thresholds.HalftimeWin</c> of 0.55 - a uniform-velocity kick train genuinely
    /// has no way to prefer one beat over the next, so this measures "undecided", 0.043 short of
    /// flipping <c>BeatType</c> to "halftime". It cannot flap (the arithmetic is deterministic to
    /// ~1e-16 here) but it does make the <c>BeatType</c> pin a sensitive one, which is the intent.
    /// <c>Swing</c> and the near-zero rhythm features are structural zeros - a perfectly straight
    /// train has no off-beat events to swing - so they pin "still exactly straight", not a
    /// calibration.</para>
    /// </summary>
    [Fact]
    public async Task Golden_KickTrain128_MatchesRecordedDetectionVersionFingerprint()
    {
        var r = await AnalyzeAsync(Kick128, rawBpm: 128.0);
        var pins = new Pins("kick-train-128");

        pins.Number("Bpm", 128.0, r.Bpm, BpmTolerance);
        pins.Number("LoudnessLufs", -15.125319138133964, r.LoudnessLufs, DecibelTolerance);
        pins.Number("ReplayGainDb", -2.874680861866036, r.ReplayGainDb, DecibelTolerance);
        pins.Number("Peak", 0.8999999761581421, r.Peak, PeakTolerance);
        pins.Number("RawEnergyScore", 0.4269661030796172, r.RawEnergyScore, UnitScoreTolerance);
        pins.Exact("Energy", 5, r.Energy);

        pins.Number("Rhythm.FourOnFloor", 0.984375, r.Rhythm?.FourOnFloor, UnitScoreTolerance);
        pins.Number("Rhythm.OffbeatEnergy", 0.00293671522322319, r.Rhythm?.OffbeatEnergy, UnitScoreTolerance);
        pins.Number("Rhythm.Swing", 0.0, r.Rhythm?.Swing, UnitScoreTolerance);
        pins.Number("Rhythm.Syncopation", 0.0025116956275803657, r.Rhythm?.Syncopation, UnitScoreTolerance);
        pins.Number("Rhythm.HalfTimeFeel", 0.5068971523920677, r.Rhythm?.HalfTimeFeel, UnitScoreTolerance);
        pins.Exact("Rhythm.BeatType", "4x4-driving", r.Rhythm?.BeatType);

        pins.Number("AcfSharpness", 0.9929733824640508, r.AcfSharpness, UnitScoreTolerance);
        pins.Number("GridConfidence", 0.8898604064493251, r.GridConfidence, UnitScoreTolerance);

        pins.Number("SpectralFlatness", 0.0037833527025375843, r.SpectralFlatness, UnitScoreTolerance);
        pins.Number("Harshness", 0.07770840732302374, r.Harshness, UnitScoreTolerance);
        pins.Number("BassPunch", 0.2813824049988722, r.BassPunch, UnitScoreTolerance);
        pins.Number("BassGroove", 0.0012558478137901829, r.BassGroove, UnitScoreTolerance);
        pins.Number("Hypnotic", 0.08966048714114772, r.Hypnotic, UnitScoreTolerance);

        // The two 88-float fingerprint vectors are pinned by shape, not element by element:
        // eighty-eight literals would pin roughly the same information at eighty-eight times the
        // reading cost. Of the two shape measures the L1 sum is the sensitive one - it moves
        // whenever energy is redistributed between bins. ArgMax is deliberately coarse and does
        // NOT resolve small changes (measured: a 0.8% shift of the kick fundamental moved the sum
        // but left both peak bins where they were); it is here to catch a gross reshaping, and it
        // is the sum that does the day-to-day work.
        pins.Exact("Fingerprint.ScaleTransform.ArgMax", 7, ArgMax(r.Fingerprint?.ScaleTransform));
        pins.Number("Fingerprint.ScaleTransform.Sum", 6.792534282431006, Sum(r.Fingerprint?.ScaleTransform), ScalarTolerance);
        pins.Exact("Fingerprint.CyclicTempogram.ArgMax", 1, ArgMax(r.Fingerprint?.CyclicTempogram));
        pins.Number("Fingerprint.CyclicTempogram.Sum", 1.5219568113679998, Sum(r.Fingerprint?.CyclicTempogram), ScalarTolerance);

        pins.AssertAll();
    }

    // =============================================================================================
    // Golden 2 - the chord. Key, and the structural consequence of an unknown BPM.
    // =============================================================================================

    /// <summary>
    /// Recorded 2026-08-09 against detection version 9. <c>Key.Camelot == "8A"</c> is A minor,
    /// which is the key this signal was BUILT as - the one place in the file where the key stack's
    /// answer is checked against construction rather than against a recording.
    ///
    /// <para><b>Read the confidence honestly.</b> 0.091 is a small margin between the top two key
    /// candidates, and the runner-up is almost certainly C major (see <see cref="ChordAMinor"/> on
    /// why a triad is pitch-class-ambiguous with its relative). That does not make the pin flap -
    /// nothing here is stochastic - but it does mean the "8A" pin is SENSITIVE: a modest change to
    /// the key profiles or the HPCP weighting will trip it. That is the correct behaviour for a
    /// version fingerprint and the wrong thing to read as "the detector is confident".</para>
    ///
    /// <para><b>Weak by magnitude:</b> <c>SpectralFlatness</c> measures 3.7e-4, only about 4x the
    /// absolute tolerance, because a stack of sine partials is about as un-flat as a spectrum gets.
    /// It is pinned as a "still essentially tonal" check; the kick train's 3.8e-3 is the one that
    /// carries real resolution.</para>
    /// </summary>
    [Fact]
    public async Task Golden_AMinorChord_MatchesRecordedDetectionVersionFingerprint()
    {
        var r = await AnalyzeAsync(ChordAMinor, rawBpm: null);
        var pins = new Pins("chord-a-minor");

        pins.Exact("Key.Camelot", "8A", r.Key?.Camelot);
        pins.Number("KeyConfidence", 0.09080522503010047, r.KeyConfidence, UnitScoreTolerance);
        pins.Number("LoudnessLufs", -13.278599264760963, r.LoudnessLufs, DecibelTolerance);
        pins.Number("Peak", 0.699999988079071, r.Peak, PeakTolerance);
        pins.Number("RawEnergyScore", 0.21922142553240856, r.RawEnergyScore, UnitScoreTolerance);
        pins.Exact("Energy", 3, r.Energy);
        pins.Number("SpectralFlatness", 0.0003742830485273913, r.SpectralFlatness, UnitScoreTolerance);
        pins.Number("Dark", 0.8846501963280065, r.Dark, UnitScoreTolerance);
        pins.Number("Hypnotic", 0.9488560462061976, r.Hypnotic, UnitScoreTolerance);
        pins.Number("Fingerprint.Danceability", 1.7403004169464111, r.Fingerprint?.Danceability, ScalarTolerance);
        pins.Exact("Fingerprint.ScaleTransform.ArgMax", 7, ArgMax(r.Fingerprint?.ScaleTransform));
        pins.Number("Fingerprint.ScaleTransform.Sum", 6.914758707396686, Sum(r.Fingerprint?.ScaleTransform), ScalarTolerance);

        // Structural, not numeric: with no BPM there is no beat grid, so the rhythm pattern and
        // everything derived from it must be absent rather than defaulted to zero. A future
        // refactor that "helpfully" substitutes a guessed tempo would silently start writing
        // rhythm features for tracks whose tempo is unknown - this is where that shows up.
        pins.Exact("Bpm", (double?)null, r.Bpm);
        pins.Exact("Rhythm", (string?)null, r.Rhythm?.BeatType);
        pins.Exact("AcfSharpness", (double?)null, r.AcfSharpness);
        pins.Exact("GridConfidence", (double?)null, r.GridConfidence);

        pins.AssertAll();
    }

    // =============================================================================================
    // Golden 3 - silence. The floor of every detector.
    // =============================================================================================

    [Fact]
    public async Task Golden_Silence_MatchesRecordedDetectionVersionFingerprint()
    {
        var r = await AnalyzeAsync(Silence, rawBpm: null);
        var pins = new Pins("silence");

        pins.Exact("Key", (string?)null, r.Key?.Camelot);
        pins.Exact("Energy", (int?)null, r.Energy);
        pins.Exact("RawEnergyScore", (double?)null, r.RawEnergyScore);
        pins.Exact("LoudnessLufs", (double?)null, r.LoudnessLufs);
        pins.Exact("ReplayGainDb", (double?)null, r.ReplayGainDb);
        pins.Exact("Peak", (double?)null, r.Peak);
        pins.Exact("SpectralFlatness", (double?)null, r.SpectralFlatness);
        pins.Exact("Harshness", (double?)null, r.Harshness);
        pins.Exact("BassPunch", (double?)null, r.BassPunch);
        pins.Exact("Dark", (double?)null, r.Dark);
        pins.Exact("Hypnotic", (double?)null, r.Hypnotic);

        pins.AssertAll();
    }

    // =============================================================================================
    // Golden 4 - the stereo case c687d46 was about, decoded correctly.
    // =============================================================================================

    /// <summary>
    /// Two channels that genuinely differ - a 128 BPM kick train left, a 440 Hz sine right - so the
    /// frame average and the interleaved passthrough are not merely scaled versions of one another
    /// but different signals. This is the golden for the CORRECT downmix;
    /// <see cref="MonoDownmix_InterleavedPassthrough_MovesTheFingerprint"/> is the regression half.
    ///
    /// <para>Recorded 2026-08-09 against detection version 9. <c>Fingerprint.Danceability</c> is
    /// left unpinned here for the same reason as on the kick train: it saturates at the 3.0 clamp.
    /// <c>Peak</c> is 0.6997, not the 0.7 the arithmetic average of the two channel peaks (0.9 and
    /// 0.5) would suggest, because the kick's peak sample and the sine's peak sample do not fall in
    /// the same frame - which is exactly the kind of detail a hand-computed "expected" value gets
    /// wrong and a recorded one does not.</para>
    /// </summary>
    [Fact]
    public async Task Golden_StereoDistinctChannels_FrameAveraged_MatchesRecordedFingerprint()
    {
        var r = await AnalyzeAsync(Kick128, rawBpm: 128.0, right: SineA440);
        var pins = new Pins("stereo-distinct-frame-averaged");

        pins.Number("Bpm", 128.0, r.Bpm, BpmTolerance);
        pins.Number("LoudnessLufs", -14.545429856903606, r.LoudnessLufs, DecibelTolerance);
        pins.Number("Peak", 0.699687123298645, r.Peak, PeakTolerance);
        pins.Number("RawEnergyScore", 0.25149439402738766, r.RawEnergyScore, UnitScoreTolerance);
        pins.Exact("Energy", 3, r.Energy);
        pins.Number("Rhythm.FourOnFloor", 0.984375, r.Rhythm?.FourOnFloor, UnitScoreTolerance);
        pins.Number("Rhythm.Syncopation", 0.0032933353803652945, r.Rhythm?.Syncopation, UnitScoreTolerance);
        pins.Number("AcfSharpness", 0.9921455594247419, r.AcfSharpness, UnitScoreTolerance);
        pins.Number("GridConfidence", 0.8894335872690744, r.GridConfidence, UnitScoreTolerance);
        pins.Number("Dark", 0.9772651248860218, r.Dark, UnitScoreTolerance);
        pins.Number("Hypnotic", 0.607107242462894, r.Hypnotic, UnitScoreTolerance);
        pins.Number("BassPunch", 0.28195271915230224, r.BassPunch, UnitScoreTolerance);

        pins.AssertAll();
    }

    // =============================================================================================
    // Standing regression - the mono downmix contract (c687d46).
    // =============================================================================================

    /// <summary>
    /// The arithmetic half, pinned on its own so a failure says WHICH thing broke. N stereo frames
    /// must become N mono samples, each the average of its frame - not 2N samples of L,R,L,R.
    /// </summary>
    [Fact]
    public void MonoDownmix_FrameAverage_HalvesTheSampleCountAndAveragesEachFrame()
    {
        float[] left = [1.0f, 0.5f, -1.0f, 0.0f];
        float[] right = [0.0f, -0.5f, 1.0f, 0.25f];

        var interleaved = SyntheticSignals.InterleaveStereo(left, right);
        Assert.Equal([1.0f, 0.0f, 0.5f, -0.5f, -1.0f, 1.0f, 0.0f, 0.25f], interleaved);

        var mono = SyntheticSignals.FrameAverageStereo(interleaved);

        // The count IS the regression signature c687d46's message describes: "exactly 2x samples =
        // half-speed pseudo-audio". If this ever reads 8 again, every sample-based analyzer is
        // seeing a track at double tempo and an octave up.
        Assert.Equal(4, mono.Length);
        Assert.Equal([0.5f, 0.0f, 0.0f, 0.125f], mono);
    }

    /// <summary>
    /// The consequence half. Running the identical stereo source through the pipeline with the
    /// interleaved passthrough must NOT reproduce the frame-averaged golden - that is what makes
    /// <see cref="Golden_StereoDistinctChannels_FrameAveraged_MatchesRecordedFingerprint"/> a net
    /// rather than a decoration. Deliberately asserts a MINIMUM gap, not an exact one: pinning the
    /// buggy values too would be pinning a bug, and the sizes below are the honest measurement of
    /// how far off the analysis was on every lossless file between 2026-06-10 (v9) and 2026-07-10.
    /// </summary>
    [Fact]
    public async Task MonoDownmix_InterleavedPassthrough_MovesTheFingerprint()
    {
        var good = await AnalyzeAsync(Kick128, rawBpm: 128.0, right: SineA440);
        var bug = await AnalyzeAsync(Kick128, rawBpm: 128.0, right: SineA440,
            mode: Downmix.InterleavedPassthrough);

        Assert.NotNull(good.LoudnessLufs);
        Assert.NotNull(bug.LoudnessLufs);

        var moved = new List<string>();
        void Moved(string field, double? a, double? b, double minGap)
        {
            if (a is null || b is null) { moved.Add($"{field}: one side null (good={a}, bug={b})"); return; }
            if (Math.Abs(a.Value - b.Value) < minGap)
                moved.Add($"{field}: only moved {Math.Abs(a.Value - b.Value):G6} " +
                          $"(good={a.Value:G6}, bug={b.Value:G6}), expected at least {minGap:G6}");
        }

        // Minimum gaps are set at roughly half the measured 2026-08-09 separation, so the test
        // states "these are different analyses" without pinning either side's exact number.
        Moved("Dark", good.Dark, bug.Dark, 0.05);
        Moved("Hypnotic", good.Hypnotic, bug.Hypnotic, 0.02);
        Moved("RawEnergyScore", good.RawEnergyScore, bug.RawEnergyScore, 0.02);

        Assert.True(moved.Count == 0,
            "The interleaved passthrough (the c687d46 bug: L,R,L,R handed back as mono, i.e. 2x the" + NL +
            "samples at the declared rate = half-speed pseudo-audio) produced an analysis too close" + NL +
            "to the correctly frame-averaged one, so this golden set would no longer notice that" + NL +
            "regression:" + NL + "  " + string.Join(NL + "  ", moved));
    }

    // =============================================================================================
    // Ground-truth cross-checks - the two values that are checkable against first principles
    // rather than only against a recording.
    // =============================================================================================

    /// <summary>
    /// The kick train was BUILT at 128 BPM. Feed the octave-corrector half that (64, the classic
    /// half-tempo error) and it must recover the constructed truth. Unlike everything else in this
    /// file this is not a recording - if it fails, the corrector is wrong, not merely different.
    /// </summary>
    [Fact]
    public async Task KickTrain_HalfTempoInput_IsCorrectedBackToTheConstructedBpm()
    {
        var r = await AnalyzeAsync(Kick128, rawBpm: 64.0);

        Assert.NotNull(r.Bpm);
        Assert.InRange(r.Bpm!.Value, 127.0, 129.0);
    }

    /// <summary>
    /// BS.1770 integrated loudness of a steady sine is closed-form:
    /// <c>-0.691 + 10*log10(mean_square)</c> plus the K-weighting gain at that frequency. For a
    /// 1 kHz sine of amplitude 0.1 the unweighted term is -0.691 + 10*log10(0.005) = -23.70 LUFS,
    /// and the K-weighting filter's gain at 1 kHz is small but not zero - the tolerance below is
    /// wide enough to absorb it and nothing else. This is the only absolute level anchor in the
    /// file: it catches a scale error (a factor of two, a dropped -0.691, a channel-count divisor)
    /// that a recorded golden would happily preserve forever.
    /// </summary>
    [Fact]
    public async Task Tone1k_LoudnessMatchesTheBs1770ClosedForm()
    {
        var r = await AnalyzeAsync(Tone1k, rawBpm: null);

        Assert.NotNull(r.LoudnessLufs);
        const double amplitude = 0.1;
        var unweighted = -0.691 + 10 * Math.Log10(amplitude * amplitude / 2.0);   // -23.70 LUFS
        Assert.InRange(r.LoudnessLufs!.Value, unweighted - 1.5, unweighted + 1.5);

        // Peak of a sine sampled at a non-integer number of samples per period approaches the
        // amplitude from below; 20 s at 11.025 kHz is ~20 000 periods, so it is there to 1e-4.
        Assert.NotNull(r.Peak);
        Assert.InRange(r.Peak!.Value, 0.0999, 0.1001);
    }

    // =============================================================================================
    // The other half of the two-way lock.
    // =============================================================================================

    /// <summary>
    /// Bumping <see cref="TrackAnalysisState.CurrentVersion"/> without re-recording the goldens
    /// leaves a "fingerprint of version 9" claiming to describe version 10. This is the test that
    /// stops that, and it is the one a deliberate analyzer change is SUPPOSED to trip.
    /// </summary>
    [Fact]
    public void GoldenSet_IsRecordedForTheCurrentDetectionVersion()
    {
        Assert.True(RecordedForDetectionVersion == TrackAnalysisState.CurrentVersion,
            $"TrackAnalysisState.CurrentVersion is {TrackAnalysisState.CurrentVersion} but the goldens in" + NL +
            $"this file were recorded against version {RecordedForDetectionVersion}." + NL +
            "If the bump was intentional, re-record every golden here (run the tests, take the" + NL +
            "'measured now' block out of each failure message) and set RecordedForDetectionVersion" + NL +
            "to match. A golden set that silently describes an older stack is exactly the state" + NL +
            "c687d46 left the library in.");
    }

    // =============================================================================================
    // Assertion collector - reports EVERY mismatch at once plus a paste-ready re-record block,
    // because fixing a version bump one assertion per test run is how a golden set rots.
    // =============================================================================================

    private sealed class Pins(string signal)
    {
        private readonly List<string> _failures = [];
        private readonly List<string> _measured = [];

        public void Number(string field, double? expected, double? actual, double tolerance)
        {
            _measured.Add($"{field} = {Literal(actual)}");
            if (expected is null && actual is null) return;
            if (expected is null || actual is null)
            {
                _failures.Add($"{field}: expected {Show(expected)}, measured {Show(actual)}");
                return;
            }
            var delta = Math.Abs(expected.Value - actual.Value);
            if (delta > tolerance)
                _failures.Add(
                    $"{field}: expected {Literal(expected)}, measured {Literal(actual)} " +
                    $"(off by {delta:G6}, tolerance {tolerance:G6})");
        }

        public void Exact<T>(string field, T expected, T actual)
        {
            _measured.Add($"{field} = {Show(actual)}");
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                _failures.Add($"{field}: expected {Show(expected)}, measured {Show(actual)}");
        }

        public void AssertAll()
        {
            if (_failures.Count == 0) return;
            Assert.Fail(
                VersionAdmonition(signal) + NL + NL +
                $"{_failures.Count} value(s) no longer match:" + NL +
                "  " + string.Join(NL + "  ", _failures) + NL + NL +
                "measured now (paste back to re-record):" + NL +
                "  " + string.Join(NL + "  ", _measured));
        }

        private static string Show(object? v) =>
            v switch
            {
                null => "null",
                string s => $"\"{s}\"",
                double d => Literal(d),
                float f => Literal(f),
                _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "null",
            };

        // "R" round-trips: the printed literal reconstructs the exact double it was measured from,
        // so re-recording cannot quietly lose precision and widen the effective tolerance.
        private static string Literal(double? v) =>
            v is null ? "null" : v.Value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static int ArgMax(float[]? v)
    {
        if (v is null || v.Length == 0) return -1;
        var best = 0;
        for (var i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    private static double? Sum(float[]? v)
    {
        if (v is null) return null;
        double s = 0;
        foreach (var x in v) s += x;
        return s;
    }
}
