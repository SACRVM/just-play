using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

/// <summary>
/// Headless beat-similarity matrix harness — invoked via <c>--beat-sim &lt;folder&gt;</c>.
///
/// <para>For each audio file in the folder:</para>
/// <list type="number">
///   <item>Decodes mono at 11 025 Hz (same path as BPM corrector and energy detector).</item>
///   <item>Extracts a <see cref="BeatFingerprint"/> (Scale Transform + Cyclic Tempogram + DFA).</item>
///   <item>Builds the N×N pairwise cosine similarity matrix (Scale Transform primary).</item>
///   <item>Prints: per-track nearest neighbours, highest-similarity pairs, lowest-similarity pairs.</item>
/// </list>
///
/// <para><b>Read-only contract:</b> this harness NEVER writes or modifies any audio file.
/// Decode is the only operation performed.</para>
///
/// <para>This is the P1 data-collection tool for the mix-compatibility north star.
/// The output (which pairs score high/low) feeds into daytime weight-tuning with Chloe's ear.
/// Do NOT add weight blending here — that's out of scope for N8.
/// [northstar-harmonic-sort.md §P1; rhythm-similarity.md §Concrete recommendation]</para>
/// </summary>
internal static class BeatSimReport
{
    // Must match TrackAnalysisService.EnergySampleRate so we reuse the same decode path.
    private const int SampleRate = 11025;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aiff", ".aif", ".m4a", ".ogg", ".opus",
    };

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    public static void Run(IServiceProvider services, string folder)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return;
        }

        // BASS no-sound init — required for the BassAudioDecoder.
        if (!ManagedBass.Bass.Init(0))
        {
            var err = ManagedBass.Bass.LastError;
            if (err != ManagedBass.Errors.Already)
            {
                Console.WriteLine($"BASS init failed: {err}");
                return;
            }
        }

        try
        {
            RunInternal(services, folder);
        }
        finally
        {
            ManagedBass.Bass.Free();
        }
    }

    private static void RunInternal(IServiceProvider services, string folder)
    {
        var decoder = services.GetRequiredService<IAudioDecoder>();

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f))
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine("No audio files found in folder (top-level only).");
            return;
        }

        Console.WriteLine($"=== Beat / Groove Similarity Report ===");
        Console.WriteLine($"Folder : {folder}");
        Console.WriteLine($"Files  : {files.Length}");
        Console.WriteLine($"Method : Scale Transform (Holzapfel & Stylianou TASLP 2011 /");
        Console.WriteLine($"         Panteli & Dixon ISMIR 2016) + Cyclic Tempogram");
        Console.WriteLine($"         (Grosche, Müller & Kurth ICASSP 2010) +");
        Console.WriteLine($"         DFA Danceability (Streich & Herrera AES 2005).");
        Console.WriteLine($"         See rhythm-similarity.md for method rationale.");
        Console.WriteLine();

        // ---- 1. Decode + extract fingerprints ----
        Console.WriteLine("--- Extracting fingerprints ---");
        var names        = new List<string>(files.Length);
        var fingerprints = new List<BeatFingerprint?>(files.Length);

        for (var i = 0; i < files.Length; i++)
        {
            var name = Path.GetFileName(files[i]);
            var shortName = name.Length > 50 ? name[..49] + "…" : name;
            Console.Write($"  [{i + 1,3}/{files.Length}] {shortName,-51}  ");

            BeatFingerprint? fp = null;
            try
            {
                var audio = decoder.DecodeMono(files[i], SampleRate);
                fp = BeatFingerprintExtractor.Extract(audio.Samples, audio.SampleRate);
                if (fp is not null)
                    Console.WriteLine($"OK  (dance={fp.Danceability:0.00})");
                else
                    Console.WriteLine("SKIP (audio too short)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL ({ex.Message})");
            }

            names.Add(name);
            fingerprints.Add(fp);
        }

        Console.WriteLine();

        // ---- 2. Build N×N similarity matrix ----
        // Only compute the upper triangle; similarity is symmetric.
        var n = files.Length;
        var validIdx = Enumerable.Range(0, n)
            .Where(i => fingerprints[i] is not null)
            .ToArray();

        if (validIdx.Length < 2)
        {
            Console.WriteLine("Not enough valid fingerprints to compute a similarity matrix.");
            return;
        }

        Console.WriteLine($"--- Pairwise cosine similarity matrix ({validIdx.Length}×{validIdx.Length}) ---");
        Console.WriteLine("(Only upper triangle shown; ST = Scale Transform primary)");
        Console.WriteLine();

        // Collect all pairs for later reporting.
        var pairs = new List<(int I, int J, double StSim, double BlendSim)>(
            capacity: validIdx.Length * (validIdx.Length - 1) / 2);

        foreach (var i in validIdx)
        {
            foreach (var j in validIdx.Where(j2 => j2 > i))
            {
                var fpI = fingerprints[i]!;
                var fpJ = fingerprints[j]!;
                var stSim    = BeatFingerprintExtractor.CosineSimilarity(fpI, fpJ);
                var blendSim = BeatFingerprintExtractor.BlendedSimilarity(fpI, fpJ);
                pairs.Add((i, j, stSim, blendSim));
            }
        }

        // ---- 3. Per-track nearest neighbour (by ST cosine) ----
        Console.WriteLine("--- Per-track nearest neighbour (ST cosine) ---");
        Console.WriteLine($"  {"Track",-50}  {"Dance",5}  {"Best match (ST sim)",-50}  {"StSim",6}  BlendSim");
        Console.WriteLine($"  {new string('-', 130)}");

        foreach (var i in validIdx)
        {
            var fpI = fingerprints[i]!;
            var bestPair = pairs
                .Where(p => p.I == i || p.J == i)
                .OrderByDescending(p => p.StSim)
                .FirstOrDefault();

            var matchIdx  = bestPair.I == i ? bestPair.J : bestPair.I;
            var matchName = names[matchIdx];
            var shortI    = Shorten(names[i], 50);
            var shortM    = Shorten(matchName, 50);

            Console.WriteLine($"  {shortI,-50}  {fpI.Danceability,5:0.00}  {shortM,-50}  {bestPair.StSim,6:0.000}  {bestPair.BlendSim:0.000}");
        }

        Console.WriteLine();

        // ---- 4. Top-5 most similar pairs (likely same groove) ----
        Console.WriteLine("--- Top-5 most similar pairs (same-groove candidates) ---");
        PrintPairs(pairs.OrderByDescending(p => p.StSim).Take(5), names);

        Console.WriteLine();

        // ---- 5. Top-5 most different pairs (likely different groove) ----
        Console.WriteLine("--- Top-5 least similar pairs (different-groove candidates) ---");
        PrintPairs(pairs.OrderBy(p => p.StSim).Take(5), names);

        Console.WriteLine();

        // ---- 6. Summary stats ----
        var stSims = pairs.Select(p => p.StSim).ToArray();
        Console.WriteLine("--- Summary ---");
        Console.WriteLine($"  Pairs analysed    : {pairs.Count}");
        Console.WriteLine($"  ST cosine mean    : {stSims.Average():0.0000}");
        Console.WriteLine($"  ST cosine min     : {stSims.Min():0.0000}");
        Console.WriteLine($"  ST cosine max     : {stSims.Max():0.0000}");
        Console.WriteLine($"  ST cosine stdev   : {StdDev(stSims):0.0000}");
        Console.WriteLine();
        Console.WriteLine("NOTE: numbers are raw fingerprint cosines, not final compat scores.");
        Console.WriteLine("Weight tuning (blending with key/BPM/energy) is P1 daytime work.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void PrintPairs(
        IEnumerable<(int I, int J, double StSim, double BlendSim)> pairs,
        List<string> names)
    {
        Console.WriteLine($"  {"Track A",-42}  {"Track B",-42}  {"StSim",6}  BlendSim");
        Console.WriteLine($"  {new string('-', 105)}");
        foreach (var (i, j, stSim, blend) in pairs)
            Console.WriteLine($"  {Shorten(names[i], 42),-42}  {Shorten(names[j], 42),-42}  {stSim,6:0.000}  {blend:0.000}");
    }

    private static string Shorten(string s, int max) =>
        s.Length > max ? s[..(max - 1)] + "…" : s;

    private static double StdDev(double[] xs)
    {
        if (xs.Length < 2) return 0.0;
        var mean = xs.Average();
        var variance = xs.Select(x => (x - mean) * (x - mean)).Average();
        return Math.Sqrt(variance);
    }
}
