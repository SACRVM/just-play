using System.Text;
using JustPlay.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// ⭐ Chloe's challenge (2026-07-16): "mal gucken ob du über 6 hinauskommst" — run JUST BEAT's
/// mastered hard-techno loop through JUST PLAY's own energy analyser and see where it lands on the
/// 1..10 scale (ambient→2, mid-dance→5-6, hard techno→9). Throwaway probe (not committed); reads a
/// WAV JustEdit already rendered, so it does NOT depend on the JustEdit build (safe while an agent
/// is editing that repo). The detector runs at 11025 Hz, so we decimate the 44100 Hz render by 4.
/// </summary>
public class BeatEnergyProbeTests
{
    private readonly ITestOutputHelper _out;
    public BeatEnergyProbeTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(@"D:\repos\just-edit\demos\mastered.wav",              "HardTechno rack, ClubDefault master (drive 3)")]
    [InlineData(@"D:\repos\just-edit\demos\unmastered.wav",            "HardTechno rack, RAW (−6 dBFS, no bus)")]
    [InlineData(@"D:\repos\just-edit\demos\after-hardtechno-rack.wav", "HardTechno rack (before/after demo)")]
    public void Probe_Energy(string wav, string label)
    {
        if (!File.Exists(wav)) { _out.WriteLine($"SKIP (not found): {wav}"); return; }

        var (mono44, rate) = ReadWavMono(wav);
        var mono = DecimateBy4(mono44);                 // 44100 → 11025 (box-averaged)
        var audio = new DecodedAudio(mono, rate / 4);

        var det = new SpectralEnergyDetector();
        var feats = det.ExtractFeatures(audio);
        var energy = det.Detect(audio);

        _out.WriteLine($"=== {label} ===");
        _out.WriteLine($"  ENERGY (1-10): {energy}");
        if (feats is { } f)
        {
            _out.WriteLine($"  raw:  LUFS={f.RawLufs:F1}  Flux={f.RawFlux:F3}  Centroid={f.RawCentroid:F0} Hz  RmsSd={f.RawRmsSd:F3}");
            _out.WriteLine($"  norm: loud={f.NLoud:F2} flux={f.NFlux:F2} bright={f.NBright:F2} rmssd={f.NRmsSd:F2}  → blended={SpectralEnergyDetector.BlendedScore(f):F3}");
        }
        Assert.NotNull(energy);
    }

    /// <summary>⭐ The point: the research's #1 slam lever is a clipper/saturator — it adds upper
    /// harmonics (brightness) AND raises RMS (loudness), the two terms holding our energy at 6. Apply
    /// a soft clip (tanh) at increasing drive to the ALREADY-mastered loop, renormalise to the same
    /// peak (so we measure TONE, not just level), and watch the energy climb over 6. tanh at 44100 →
    /// box-decimate to 11025 partially anti-aliases (honest-ish; the real engine would oversample).</summary>
    [Fact]
    public void Probe_Saturation_Sweep_ClearsSix()
    {
        var wav = @"D:\repos\just-edit\demos\mastered.wav";
        if (!File.Exists(wav)) { _out.WriteLine($"SKIP (not found): {wav}"); return; }
        var (mono44, rate) = ReadWavMono(wav);
        var det = new SpectralEnergyDetector();

        _out.WriteLine("Soft-clip (tanh) drive sweep on the mastered loop, peak-matched:");
        foreach (var k in new[] { 1.0, 1.5, 2.0, 3.0, 4.0, 6.0 })
        {
            var sat = new float[mono44.Length];
            var peakIn = Peak(mono44);
            for (var i = 0; i < sat.Length; i++) sat[i] = (float)Math.Tanh(k * mono44[i]);
            var g = Peak(sat) is var po && po > 1e-6f ? peakIn / po : 1f;
            for (var i = 0; i < sat.Length; i++) sat[i] *= g;

            var audio = new DecodedAudio(DecimateBy4(sat), rate / 4);
            var e = det.Detect(audio);
            if (det.ExtractFeatures(audio) is { } f)
                _out.WriteLine($"  drive k={k,3}:  ENERGY={e}  centroid={f.RawCentroid,5:F0} Hz  nBright={f.NBright:F2}  LUFS={f.RawLufs:F1}  nLoud={f.NLoud:F2}  blended={SpectralEnergyDetector.BlendedScore(f):F3}");
        }
    }

    private static float Peak(float[] x)
    {
        var p = 0f;
        foreach (var s in x) { var a = Math.Abs(s); if (a > p) p = a; }
        return p;
    }

    private static float[] DecimateBy4(float[] x)
    {
        var y = new float[x.Length / 4];
        for (var i = 0; i < y.Length; i++)
            y[i] = 0.25f * (x[i * 4] + x[i * 4 + 1] + x[i * 4 + 2] + x[i * 4 + 3]);
        return y;
    }

    /// <summary>Minimal RIFF/WAVE PCM16 reader → mono float[-1,1]. Enough for our own WavWriter output.</summary>
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
}
