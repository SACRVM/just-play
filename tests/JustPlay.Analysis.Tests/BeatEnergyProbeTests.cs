using System.Text;
using JustPlay.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// (*) Origin: Chloe's challenge (2026-07-16), "let's see if you can get past 6" - run JUST BEAT's
/// mastered hard-techno loop through JUST PLAY's own energy analyser and see where it lands on the
/// 1..10 scale (ambient -> 2, mid-dance -> 5-6, hard techno -> 9).
///
/// <para>
/// <b>What is synthetic vs real-audio-only here.</b> <see cref="SpectralEnergyDetector"/>'s own
/// math (loudness/flux/centroid/RMS-SD blending, silence handling, the 1..10 mapping) is already
/// pinned exhaustively on synthetic fixtures in <c>SpectralEnergyDetectorTests</c>, which builds
/// <see cref="DecodedAudio"/> directly in memory. This file's own contribution is different: it
/// is the only place that exercises the WAV-file-reading and box-decimation glue
/// (<see cref="ReadWavMono"/>, <see cref="DecimateBy4"/>, <see cref="Peak"/> below) that the
/// original probe used to turn a real 44.1 kHz render into 11 025 Hz <see cref="DecodedAudio"/>.
/// Those methods are THIS TEST FILE's own minimal reimplementation for building/reading
/// fixtures - not <c>src/JustPlay.Analysis</c> product code - so pinning them proves the numbers
/// this probe reports are trustworthy, not that <c>src/</c> contains a WAV reader (it does not
/// need one; ManagedBass decodes real playback). The SIGNALS they are fed (sine, impulse train,
/// silence) come from <see cref="SyntheticSignals"/>, shared with
/// <see cref="DetectionVersionGoldenTests"/> so a fixture both files call "a click train at 120
/// BPM" cannot quietly become two different signals.
/// </para>
///
/// <para>
/// <b>Visible skip, not a silent one.</b> The two tests below that read
/// <c>D:\repos\just-edit\demos\*.wav</c> (a sibling repo's real rendered audio, gitignored,
/// outside this repo) are marked <c>[RequiresJustEditDemosFact]</c> instead of plain
/// <c>[Fact]</c> - the same nested-<see cref="FactAttribute"/>-with-a-constructor-set-<c>Skip</c>
/// trick <c>RawTagReaderTests.RequiresCorpusFactAttribute</c> uses (see that file's doc comment
/// for why: xunit 2.9.x has no dynamic <c>Assert.Skip</c>, that is v3 only, and
/// <c>FactAttribute.Skip</c> is a plain settable property that xunit reads off a real,
/// constructor-run attribute instance at discovery time). Before this fix, a missing fixture made
/// these tests <c>WriteLine("SKIP...")</c> then <c>return</c>, which xunit reports as Passed -
/// green on every machine that does not have the sibling repo checked out, including CI and the
/// planned macOS port. Now a missing fixture reports Skipped, its own bucket in the run summary.
/// </para>
///
/// <para>
/// Every reusable claim these real-audio tests make (WAV parsing correctness, decimation
/// correctness, "soft-clip saturation raises measured brightness") is also pinned synthetically
/// below, so coverage does not disappear when the sibling repo is absent. What is genuinely
/// real-audio-only, and stays that way, is the exact "does energy not drop across a specific real
/// mastered mix's drive sweep" finding - a claim about one particular render, not reproducible
/// from a fixture whose spectral content we invented.
/// </para>
/// </summary>
public sealed class BeatEnergyProbeTests : IDisposable
{
    private const string DemosDir = @"D:\repos\just-edit\demos";

    private static readonly (string FileName, string Label)[] DemoFiles =
    [
        ("mastered.wav",              "HardTechno rack, ClubDefault master (drive 3)"),
        ("unmastered.wav",            "HardTechno rack, RAW (-6 dBFS, no bus)"),
        ("after-hardtechno-rack.wav", "HardTechno rack (before/after demo)"),
    ];

    /// <summary>
    /// A [Fact] that needs JustEdit's real rendered demo WAVs. See the class doc comment for why
    /// a dynamically-set <see cref="FactAttribute.Skip"/> is the right way to make this skip show
    /// up as Skipped rather than Passed. Nested (not top-level) so it can read
    /// <see cref="DemosDir"/> directly, mirroring <c>RawTagReaderTests.RequiresCorpusFactAttribute</c>.
    /// </summary>
    private sealed class RequiresJustEditDemosFactAttribute : FactAttribute
    {
        public RequiresJustEditDemosFactAttribute()
        {
            if (!Directory.Exists(DemosDir))
                Skip = $"JustEdit's rendered demo WAVs not found at '{DemosDir}' (a sibling repo, " +
                       "gitignored there, expected absent on CI/other machines/the macOS port). " +
                       "This is an extra cross-check against a real mastered mix - every reusable " +
                       "behaviour it exercises (WAV parsing, decimation, saturation-driven " +
                       "brightness) is also pinned on synthetic fixtures elsewhere in this file.";
        }
    }

    private readonly ITestOutputHelper _out;
    private readonly List<string> _tempFiles = [];

    public BeatEnergyProbeTests(ITestOutputHelper o) => _out = o;

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists)) File.Delete(f);
    }

    // ===========================================================================================
    // Real-audio cross-check - corpus-only, visibly skipped when the sibling repo is absent.
    // ===========================================================================================

    [RequiresJustEditDemosFact]
    public void Probe_Energy_OnRealJustEditDemos()
    {
        foreach (var (fileName, label) in DemoFiles)
        {
            var wav = Path.Combine(DemosDir, fileName);
            if (!File.Exists(wav)) { _out.WriteLine($"SKIP (missing from demos): {fileName}"); continue; }

            var (mono44, rate) = ReadWavMono(wav);
            var mono = DecimateBy4(mono44);                 // 44100 -> 11025 (box-averaged)
            var audio = new DecodedAudio(mono, rate / 4);

            var det = new SpectralEnergyDetector();
            var feats = det.ExtractFeatures(audio);
            var energy = det.Detect(audio);

            _out.WriteLine($"=== {label} ===");
            _out.WriteLine($"  ENERGY (1-10): {energy}");
            if (feats is { } f)
            {
                _out.WriteLine($"  raw:  LUFS={f.RawLufs:F1}  Flux={f.RawFlux:F3}  Centroid={f.RawCentroid:F0} Hz  RmsSd={f.RawRmsSd:F3}");
                _out.WriteLine($"  norm: loud={f.NLoud:F2} flux={f.NFlux:F2} bright={f.NBright:F2} rmssd={f.NRmsSd:F2}  -> blended={SpectralEnergyDetector.BlendedScore(f):F3}");
            }
            Assert.NotNull(energy);
            Assert.InRange(energy!.Value, 1, 10);
        }
    }

    /// <summary>(*) The point: the research's #1 slam lever is a clipper/saturator - it adds upper
    /// harmonics (brightness) AND raises RMS (loudness), the two terms holding our energy at 6. Apply
    /// a soft clip (tanh) at increasing drive to the ALREADY-mastered loop, renormalise to the same
    /// peak (so we measure TONE, not just level), and watch the energy climb over 6. tanh at 44100 ->
    /// box-decimate to 11025 partially anti-aliases (honest-ish; the real engine would oversample).
    ///
    /// <para>The exact "clears 6" number is specific to this one real render and cannot be
    /// reproduced from a synthetic tone (it depends on the loop's full mix). The general,
    /// portable half of this claim - soft-clipping raises measured brightness on ANY tone - is
    /// pinned synthetically in <see cref="SaturationSweep_RaisesMeasuredBrightness_OnSyntheticLowTone"/>.
    /// What THIS test now actually asserts, where the original had no assertion at all (only
    /// WriteLine output), is the weaker but real claim: driving the mix harder does not lower its
    /// measured energy.</para>
    /// </summary>
    [RequiresJustEditDemosFact]
    public void Probe_Saturation_Sweep_ClearsSix()
    {
        var wav = Path.Combine(DemosDir, "mastered.wav");
        if (!File.Exists(wav)) { _out.WriteLine("SKIP (missing from demos): mastered.wav"); return; }
        var (mono44, rate) = ReadWavMono(wav);
        var det = new SpectralEnergyDetector();

        _out.WriteLine("Soft-clip (tanh) drive sweep on the mastered loop, peak-matched:");
        int? first = null, last = null;
        foreach (var k in new[] { 1.0, 1.5, 2.0, 3.0, 4.0, 6.0 })
        {
            var sat = new float[mono44.Length];
            var peakIn = Peak(mono44);
            for (var i = 0; i < sat.Length; i++) sat[i] = (float)Math.Tanh(k * mono44[i]);
            var g = Peak(sat) is var po && po > 1e-6f ? peakIn / po : 1f;
            for (var i = 0; i < sat.Length; i++) sat[i] *= g;

            var audio = new DecodedAudio(DecimateBy4(sat), rate / 4);
            var e = det.Detect(audio);
            first ??= e;
            last = e;
            if (det.ExtractFeatures(audio) is { } f)
                _out.WriteLine($"  drive k={k,3}:  ENERGY={e}  centroid={f.RawCentroid,5:F0} Hz  nBright={f.NBright:F2}  LUFS={f.RawLufs:F1}  nLoud={f.NLoud:F2}  blended={SpectralEnergyDetector.BlendedScore(f):F3}");
        }

        Assert.NotNull(first);
        Assert.NotNull(last);
        Assert.True(last!.Value >= first!.Value,
            $"driving the mastered loop harder (k=6, energy={last}) should not score lower than the least-driven pass (k=1, energy={first})");
    }

    // ===========================================================================================
    // Synthetic: the WAV-reading and decimation glue this probe depends on. ReadWavMono,
    // DecimateBy4 and Peak are this test file's OWN fixture helpers (not src/ product code) - the
    // point of pinning them is that the corpus tests above are only trustworthy if this glue is
    // correct, and every real .wav on Chloe's disk happens to be mono-or-stereo PCM16 with no
    // extra chunks, so none of these branches were ever exercised by the real files either.
    // ===========================================================================================

    [Fact]
    public void ReadWavMono_MonoPcm16_DecodesKnownSineWithinQuantizationTolerance()
    {
        const int rate = 44100;
        const double freq = 440.0;
        const double amp = 0.5;
        const int frames = rate; // 1 second

        var interleaved = new short[frames];
        for (var i = 0; i < frames; i++)
            interleaved[i] = (short)Math.Round(amp * short.MaxValue * Math.Sin(2 * Math.PI * freq * i / rate));

        var path = WriteWav(TempWavPath("sine_mono"), channels: 1, sampleRate: rate, interleaved);
        var (mono, decodedRate) = ReadWavMono(path);

        Assert.Equal(rate, decodedRate);
        Assert.Equal(frames, mono.Length);

        // RMS of a sine at amplitude A is A/sqrt(2); PCM16 quantization error (<= 1/32768 per
        // sample) is negligible against a 0.5-amplitude tone. This pins the /32768.0 scale factor -
        // a doubled or halved scale, or a channel-count-off-by-one, would fail this immediately.
        var expectedRms = amp / Math.Sqrt(2);
        var actualRms = Math.Sqrt(mono.Select(s => (double)s * s).Average());
        Assert.InRange(actualRms, expectedRms - 0.01, expectedRms + 0.01);

        var actualPeak = mono.Select(s => (double)Math.Abs(s)).Max();
        Assert.InRange(actualPeak, amp - 0.001, amp + 0.001);
    }

    [Fact]
    public void ReadWavMono_StereoPcm16_AveragesChannelsToMono()
    {
        const int rate = 8000;
        const int frames = 10;
        const short left = 16000;   // ~ 0.4883
        const short right = -8000;  // ~ -0.2441

        var interleaved = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[i * 2] = left;
            interleaved[i * 2 + 1] = right;
        }

        var path = WriteWav(TempWavPath("stereo_const"), channels: 2, sampleRate: rate, interleaved);
        var (mono, decodedRate) = ReadWavMono(path);

        Assert.Equal(rate, decodedRate);
        Assert.Equal(frames, mono.Length);

        var expected = (left / 32768.0 + right / 32768.0) / 2.0;
        foreach (var s in mono)
            Assert.Equal(expected, (double)s, precision: 5);
    }

    [Fact]
    public void ReadWavMono_SkipsUnknownChunkBeforeData_IncludingOddSizePadByte()
    {
        const int rate = 22050;
        var interleaved = new short[] { 1000, -1000, 2000, -2000 };
        // A 5-byte payload is odd-length, forcing the RIFF pad-byte branch on the SKIPPED chunk
        // (not just the data chunk) - a real-world WAV writer (e.g. a "LIST"/"INFO" metadata
        // block) can produce exactly this shape and none of the real demo files happen to.
        var path = WriteWav(TempWavPath("junk_chunk"), channels: 1, sampleRate: rate, interleaved,
            extraChunk: ("LIST", [1, 2, 3, 4, 5]));

        var (mono, decodedRate) = ReadWavMono(path);

        Assert.Equal(rate, decodedRate);
        Assert.Equal(interleaved.Length, mono.Length);
        for (var i = 0; i < interleaved.Length; i++)
            Assert.Equal(interleaved[i] / 32768.0, (double)mono[i], precision: 6);
    }

    [Fact]
    public void ReadWavMono_ClickTrain_PreservesOnsetSpacingAtKnownBpm()
    {
        const int rate = 44100;
        const double bpm = 120.0;
        const int beats = 8;
        var samplesPerBeat = (int)Math.Round(rate * 60.0 / bpm); // 22050 samples/beat at 120 BPM
        var totalFrames = samplesPerBeat * beats;

        var clicks = SyntheticSignals.ImpulseTrain(beats * 60.0 / bpm, rate, bpm, amplitude: 1.0);
        var interleaved = new short[totalFrames];
        for (var i = 0; i < totalFrames; i++)
            interleaved[i] = (short)Math.Round(clicks[i] * short.MaxValue);

        // Ground truth stated independently of the generator, on purpose: if both the fixture and
        // the expectation came from the same loop, a drifting generator would agree with itself.
        var expectedOnsets = new List<int>();
        for (var b = 0; b < beats; b++) expectedOnsets.Add(b * samplesPerBeat);

        var path = WriteWav(TempWavPath("click_train"), channels: 1, sampleRate: rate, interleaved);
        var (mono, decodedRate) = ReadWavMono(path);

        Assert.Equal(rate, decodedRate);
        Assert.Equal(totalFrames, mono.Length);

        var foundOnsets = new List<int>();
        for (var i = 0; i < mono.Length; i++)
            if (mono[i] > 0.9f) foundOnsets.Add(i);

        // The onset positions ARE the ground truth this fixture was built from - this proves
        // ReadWavMono introduces no sample drift (no off-by-one framing, no chunk mis-parse
        // eating bytes before "data"). It does NOT prove anything about beat/BPM DETECTION -
        // SpectralEnergyDetector has no such output; this is purely about read-back fidelity.
        Assert.Equal(expectedOnsets, foundOnsets);
        for (var i = 1; i < foundOnsets.Count; i++)
            Assert.Equal(samplesPerBeat, foundOnsets[i] - foundOnsets[i - 1]);
    }

    [Fact]
    public void ReadWavMono_SilentBuffer_DecodesAllZeroSamples()
    {
        const int rate = 11025;
        const int frames = 500;
        var interleaved = new short[frames]; // all zero

        var path = WriteWav(TempWavPath("silence"), channels: 1, sampleRate: rate, interleaved);
        var (mono, decodedRate) = ReadWavMono(path);

        Assert.Equal(rate, decodedRate);
        Assert.Equal(frames, mono.Length);
        Assert.All(mono, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void FullPipeline_SilentWav_DecimatedThroughDetector_ReturnsFloorEnergy()
    {
        const int rate = 44100;
        var interleaved = new short[rate * 2]; // 2 s of silence at the real demos' render rate

        var path = WriteWav(TempWavPath("silence_pipeline"), channels: 1, sampleRate: rate, interleaved);
        var (mono44, wavRate) = ReadWavMono(path);
        var mono = DecimateBy4(mono44);
        var audio = new DecodedAudio(mono, wavRate / 4);

        var energy = new SpectralEnergyDetector().Detect(audio);

        // Matches SpectralEnergyDetectorTests.Silence_ReturnsLowestOrNull at the DecodedAudio
        // level - this test's own contribution is proving the WAV-read + decimate glue (unique to
        // this file) does not inject noise that would push a silent file's score above the floor.
        Assert.True(energy is null or 1);
    }

    [Fact]
    public void DecimateBy4_BoxAveragesFourConsecutiveSamples()
    {
        var input = new float[] { 0f, 4f, 8f, 12f, 16f, 20f, 24f, 28f };
        var result = DecimateBy4(input);

        Assert.Equal([6f, 22f], result);
    }

    [Fact]
    public void DecimateBy4_TruncatesRemainder_DoesNotRoundUpOrPad()
    {
        // 7 samples -> integer division floors the output to 1 sample; the trailing 3 samples
        // (values 100/100/100) are silently dropped, not padded or folded into a partial average.
        // Harmless for a WAV whose length happens to be a multiple of 4, but worth pinning so a
        // future refactor of this test's own decimator does not change that silently.
        var input = new float[] { 4f, 4f, 4f, 4f, 100f, 100f, 100f };
        var result = DecimateBy4(input);

        Assert.Equal([4f], result);
    }

    [Fact]
    public void Peak_ReturnsMaxAbsoluteMagnitude_NotMaxSignedValue()
    {
        var samples = new float[] { -0.9f, 0.5f, -0.2f, 0.1f };
        Assert.Equal(0.9f, Peak(samples));
    }

    [Fact]
    public void Peak_EmptyArray_ReturnsZero()
    {
        Assert.Equal(0f, Peak([]));
    }

    // ===========================================================================================
    // Synthetic: the general, reproducible half of the saturation-sweep claim.
    // ===========================================================================================

    /// <summary>
    /// Soft-clipping (tanh) ANY tone generates odd harmonics, and harmonics are higher-frequency
    /// content than the fundamental, so the measured spectral centroid (brightness) must rise
    /// with drive - this is provable from a single low sine tone we constructed, independent of
    /// any real track's mix. It does NOT prove the specific 1..10 energy score reaches any
    /// particular number on real material (that depends on the whole mix's loudness/flux/RMS-SD
    /// too, not just brightness) - only that the brightness lever
    /// <see cref="Probe_Saturation_Sweep_ClearsSix"/> exploits behaves as claimed, everywhere,
    /// not just on Chloe's one render.
    /// </summary>
    [Fact]
    public void SaturationSweep_RaisesMeasuredBrightness_OnSyntheticLowTone()
    {
        const int rate = 44100;
        var mono44 = SineWave(seconds: 3.0, rate: rate, frequencyHz: 110.0, amplitude: 0.6);
        var det = new SpectralEnergyDetector();

        double? baselineCentroid = null;
        double? drivenCentroid = null;
        foreach (var k in new[] { 1.0, 6.0 })
        {
            var sat = new float[mono44.Length];
            var peakIn = Peak(mono44);
            for (var i = 0; i < sat.Length; i++) sat[i] = (float)Math.Tanh(k * mono44[i]);
            var g = Peak(sat) is var po && po > 1e-6f ? peakIn / po : 1f;
            for (var i = 0; i < sat.Length; i++) sat[i] *= g;

            var audio = new DecodedAudio(DecimateBy4(sat), rate / 4);
            var f = det.ExtractFeatures(audio);
            Assert.NotNull(f);
            if (k == 1.0) baselineCentroid = f!.Value.RawCentroid; else drivenCentroid = f!.Value.RawCentroid;
        }

        Assert.NotNull(baselineCentroid);
        Assert.NotNull(drivenCentroid);
        Assert.True(drivenCentroid!.Value > baselineCentroid!.Value,
            $"heavily-driven tone centroid ({drivenCentroid} Hz) should be brighter than the lightly-driven one ({baselineCentroid} Hz)");
    }

    // -------------------------------------------------------------------------
    // Signal / fixture generators
    // -------------------------------------------------------------------------

    // Sine / impulse-train / silence generation and Peak live in SyntheticSignals - one copy for
    // the whole test project. Aliased here so the call sites below stay short.
    private static float[] SineWave(double seconds, int rate, double frequencyHz, double amplitude)
        => SyntheticSignals.Sine(seconds, rate, frequencyHz, amplitude);

    private static float Peak(float[] x) => SyntheticSignals.Peak(x);

    private static float[] DecimateBy4(float[] x)
    {
        var y = new float[x.Length / 4];
        for (var i = 0; i < y.Length; i++)
            y[i] = 0.25f * (x[i * 4] + x[i * 4 + 1] + x[i * 4 + 2] + x[i * 4 + 3]);
        return y;
    }

    /// <summary>Minimal RIFF/WAVE PCM16 reader -> mono float[-1,1]. Enough for our own WavWriter output.</summary>
    private static (float[] Mono, int Rate) ReadWavMono(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        br.ReadBytes(12);   // "RIFF" <size> "WAVE"
        int channels = 1, rate = 44100, bits = 16;
        float[] mono = [];

        while (fs.Position + 8 <= fs.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            var size = br.ReadInt32();
            if (id == "fmt ")
            {
                var fmt = br.ReadBytes(size);
                channels = BitConverter.ToInt16(fmt, 2);
                rate = BitConverter.ToInt32(fmt, 4);
                bits = BitConverter.ToInt16(fmt, 14);
            }
            else if (id == "data")
            {
                var data = br.ReadBytes(size);
                var bps = bits / 8;
                var frames = data.Length / (bps * channels);
                mono = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    double sum = 0;
                    for (var c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(data, (i * channels + c) * bps) / 32768.0;
                    mono[i] = (float)(sum / channels);
                }
            }
            else
            {
                br.ReadBytes(size);   // skip unknown chunk
            }
            if ((size & 1) == 1 && fs.Position < fs.Length) br.ReadByte();   // RIFF pad byte
        }
        return (mono, rate);
    }

    // -------------------------------------------------------------------------
    // Fixture builder - synthesised in-process into a temp directory, cleaned up in Dispose.
    // Nothing here is a src/ product component; TagLib# / ManagedBass never touch this file, it
    // exists purely so ReadWavMono above has real bytes with known ground truth to parse.
    // -------------------------------------------------------------------------

    /// <summary>Writes a minimal RIFF/WAVE PCM16 file with an optional extra chunk inserted
    /// between "fmt " and "data" (to exercise the unknown-chunk-skip branch).</summary>
    private static string WriteWav(
        string path, int channels, int sampleRate, short[] interleaved,
        (string Id, byte[] Payload)? extraChunk = null)
    {
        const int bytesPerSample = 2;
        var blockAlign = channels * bytesPerSample;
        var dataBytes = interleaved.Length * bytesPerSample;

        var extraLen = 0;
        if (extraChunk is { } ec) extraLen = 8 + ec.Payload.Length + (ec.Payload.Length % 2 == 1 ? 1 : 0);
        var riffSize = 4 + (8 + 16) + extraLen + (8 + dataBytes);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(riffSize);
        bw.Write("WAVE"u8.ToArray());

        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * blockAlign);
        bw.Write((short)blockAlign);
        bw.Write((short)(bytesPerSample * 8));

        if (extraChunk is { } chunk)
        {
            bw.Write(Encoding.ASCII.GetBytes(chunk.Id));
            bw.Write(chunk.Payload.Length);
            bw.Write(chunk.Payload);
            if (chunk.Payload.Length % 2 == 1) bw.Write((byte)0);
        }

        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        foreach (var s in interleaved) bw.Write(s);

        return path;
    }

    private string TempWavPath(string tag)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_beatenergy_{tag}_{Guid.NewGuid():N}.wav");
        _tempFiles.Add(path);
        return path;
    }
}
