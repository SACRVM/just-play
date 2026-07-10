using JustPlay.Analysis;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay beatbed &lt;audiofile&gt; [--out path.wav] [--bpm x]</c>
///
/// <para>POC-0 of the beatbed feature — the live-remix beat carrier ("a donor track that
/// is 100 % mixable sections, rendered onto THIS recording's measured, breathing beat
/// timeline"). This first phase renders the <b>click check</b>: the original audio plus
/// a metronome pip on every beat found by <see cref="BeatMapTracker"/>.</para>
///
/// <para>Ear test (the whole point): play the WAV. If the pips stay ON the drummer for
/// the entire track — verses, choruses, breakdowns — the drifting beat map is
/// trustworthy and the carrier renderer can build on it. HIGH pip = real tracked hit,
/// LOW pip = interpolated (bridged) beat, so you hear where the tracker was guessing.</para>
///
/// <para>Carrier synthesis (kick/hat/bass through the bus rack) is the next phase and
/// will become this verb's default mode; the click check will stay as <c>--clicks</c>.</para>
/// </summary>
internal static class BeatbedCommand
{
    private const int AnalysisRate = 11025;   // same rate the analysis stack decodes at
    private const int RenderRate   = 44100;

    public static int Run(string filePath, string? outPath, double bpmOverride)
    {
        filePath = Path.GetFullPath(filePath);
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[beatbed] ERROR: file not found: {filePath}");
            return 1;
        }

        outPath ??= Path.Combine(
            Path.GetDirectoryName(filePath) ?? ".",
            Path.GetFileNameWithoutExtension(filePath) + ".beatcheck.wav");

        Console.WriteLine($"[beatbed] Input  : {filePath}");
        Console.WriteLine($"[beatbed] Output : {outPath}");

        using var composer = EngineComposer.Build();

        // ── 1. Seed BPM: --bpm override, else the canonical corrected detector stack ──
        // (Same ITrackAnalysisService as the app, so beatbed sees the identical BPM the
        // library shows — and the octave-corrected value BeatMapTracker requires.)
        var bpm = bpmOverride;
        if (bpm <= 0)
        {
            Console.Write("[beatbed] Detecting BPM...");
            try
            {
                var analysed = composer.AnalysisService
                    .AnalyzeAsync(filePath)
                    .GetAwaiter().GetResult();
                bpm = analysed.Bpm ?? 0.0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n[beatbed] ERROR analyzing: {ex.Message}");
                return 1;
            }
            if (bpm <= 0)
            {
                Console.Error.WriteLine("\n[beatbed] ERROR: BPM detection failed — pass --bpm <value>.");
                return 1;
            }
            Console.WriteLine($" {bpm:F2}");
        }
        else
        {
            Console.WriteLine($"[beatbed] Seed BPM: {bpm:F2} (override)");
        }

        // ── 2. Track the full drifting beat map at the analysis rate ─────────────
        Console.Write("[beatbed] Tracking beats...");
        Core.Models.BeatMap? map;
        try
        {
            var mono = composer.Decoder.DecodeMono(filePath, AnalysisRate);
            map = BeatMapTracker.Track(mono.Samples, AnalysisRate, bpm);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[beatbed] ERROR decoding/tracking: {ex.Message}");
            return 1;
        }
        if (map is null)
        {
            Console.Error.WriteLine("\n[beatbed] ERROR: beat tracking produced no usable map " +
                                    "(track too short / no rhythmic signal?).");
            return 1;
        }
        Console.WriteLine(
            $" {map.Count} beats · median {map.MedianBpm:F2} BPM · " +
            $"drift {map.MinBpm:F1}–{map.MaxBpm:F1} BPM · coverage {map.Coverage:P0}");

        // ── 3. Render the click check at listening quality ────────────────────────
        Console.Write("[beatbed] Rendering click check...");
        try
        {
            var full = composer.Decoder.DecodeMono(filePath, RenderRate);
            var mix  = ClickTrackRenderer.RenderMix(full.Samples, RenderRate, map);
            WavWriter.WriteMono16(outPath, mix, RenderRate);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[beatbed] ERROR rendering: {ex.Message}");
            return 1;
        }
        Console.WriteLine(" done");
        Console.WriteLine("[beatbed] Ear test: HIGH pip = tracked hit · LOW pip = bridged/interpolated beat.");
        Console.WriteLine("[beatbed] PASS = pips stay on the drummer for the WHOLE track, breakdowns included.");
        return 0;
    }
}
