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
/// Headless key-detection accuracy check, invoked via <c>--key-report &lt;folder&gt;</c>.
/// For every audio file under the folder it reads the key another tool already wrote
/// (Mixed In Key / Rekordbox, from the key tag or the comment) and compares it to our
/// <see cref="IKeyDetector"/> output - so the detector can be validated on Windows
/// against an existing MIK-tagged library without running MIK. Reuses the app's DI
/// services (decoder, detector, metadata reader); never touches files.
/// </summary>
internal static class KeyReport
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif",
    };

    private const int AnalysisSampleRate = 11025;

    public static void Run(IServiceProvider services, string folder)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return;
        }

        var reader = services.GetRequiredService<IMetadataReader>();
        var decoder = services.GetRequiredService<IAudioDecoder>();
        var detector = services.GetRequiredService<IKeyDetector>();
        var energyDetector = services.GetRequiredService<IEnergyDetector>();

        // No-sound BASS init is enough for decode-only streams.
        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} - decoding may fail)");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Scanning {files.Count} audio file(s) under {folder}");
        Console.WriteLine("Reference = existing key tag / comment (e.g. Mixed In Key). 'ours' = JustPlay detection.\n");

        int exact = 0, relative = 0, fifth = 0, off = 0, undetected = 0, noRef = 0;
        var mismatches = new List<string>();

        // Energy calibration: collect (reference MIK energy, our energy) pairs.
        var energyPairs = new List<(int Ref, int Ours)>();
        var energyLines = new List<string>();

        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var md = reader.Read(file);
                var reference = MikKey(md);

                // Decode once, reuse for key + energy.
                DecodedAudio? audio = null;
                try { audio = decoder.DecodeMono(file, AnalysisSampleRate); }
                catch (Exception ex) { Console.WriteLine($"  [decode FAIL] {name}: {ex.Message}"); }

                // ---- Energy comparison (independent of key reference) ----
                var refEnergy = MikEnergy(md);
                if (audio is { } a1 && refEnergy is { } re && energyDetector.Detect(a1) is { } oe)
                {
                    energyPairs.Add((re, oe));
                    if (Math.Abs(re - oe) >= 3)
                        energyLines.Add($"  E ref {re,2} ours {oe,2}  (delta{oe - re,+2})  {name}");
                }

                // ---- Key comparison ----
                if (reference is null) { noRef++; continue; }

                MusicalKey? ours = null;
                double conf = 0;
                if (audio is { } a2 && detector.Detect(a2) is { } d) { ours = d.Key; conf = d.Confidence; }

                if (ours is null) { undetected++; continue; }

                switch (Categorize(ours.Value, reference.Value))
                {
                    case "exact": exact++; break;
                    case "relative": relative++; break;
                    case "fifth": fifth++; break;
                    default:
                        off++;
                        mismatches.Add($"  OFF  ref {reference.Value.Camelot,-3} ours {ours.Value.Camelot,-3} (conf {conf:0.00})  {name}");
                        break;
                }
            }
        }
        finally
        {
            ManagedBass.Bass.Free();
        }

        var compared = exact + relative + fifth + off;
        Console.WriteLine();
        if (mismatches.Count > 0)
        {
            Console.WriteLine("Clear mismatches (not relative / not fifth-neighbour):");
            foreach (var m in mismatches) Console.WriteLine(m);
            Console.WriteLine();
        }

        Console.WriteLine($"=== Key accuracy vs reference ({compared} compared) ===");
        if (compared > 0)
        {
            Console.WriteLine($"  exact:            {Pct(exact, compared)}  ({exact})");
            Console.WriteLine($"  relative maj/min: {Pct(relative, compared)}  ({relative})   [same notes - 8A<->8B]");
            Console.WriteLine($"  fifth neighbour:  {Pct(fifth, compared)}  ({fifth})   [+/-1 Camelot, mixable]");
            Console.WriteLine($"  off:              {Pct(off, compared)}  ({off})");
            Console.WriteLine($"  harmonically ok:  {Pct(exact + relative + fifth, compared)}  (exact+relative+fifth)");
        }
        Console.WriteLine($"  (undetected by us: {undetected};  no reference key in file: {noRef})");

        // ---- Energy accuracy summary ----
        Console.WriteLine();
        if (energyLines.Count > 0)
        {
            Console.WriteLine("Energy off by >=3:");
            foreach (var l in energyLines) Console.WriteLine(l);
            Console.WriteLine();
        }
        Console.WriteLine($"=== Energy accuracy vs reference ({energyPairs.Count} compared) ===");
        if (energyPairs.Count > 0)
        {
            var mae = energyPairs.Average(p => Math.Abs(p.Ref - p.Ours));
            var within1 = energyPairs.Count(p => Math.Abs(p.Ref - p.Ours) <= 1);
            var within2 = energyPairs.Count(p => Math.Abs(p.Ref - p.Ours) <= 2);
            var meanRef = energyPairs.Average(p => p.Ref);
            var meanOurs = energyPairs.Average(p => p.Ours);
            var bias = meanOurs - meanRef;
            Console.WriteLine($"  mean abs error:   {mae:0.00}  (lower = better)");
            Console.WriteLine($"  within +/-1:        {Pct(within1, energyPairs.Count)}  ({within1})");
            Console.WriteLine($"  within +/-2:        {Pct(within2, energyPairs.Count)}  ({within2})");
            Console.WriteLine($"  mean ref {meanRef:0.0} vs ours {meanOurs:0.0}  (bias {bias:+0.0;-0.0})");
        }
        else
        {
            Console.WriteLine("  (no reference energy found in comments)");
        }
    }

    /// <summary>
    /// Tuning sweep: decode + build each track's chroma ONCE, then re-score it under a range
    /// of <c>MinorBias</c> values (the cheap, FFT-free <see cref="ChromagramKeyDetector.Classify"/>
    /// half), printing exact / harmonically-ok per bias. Lets the relative-tie bias be tuned
    /// for the cosine classifier in a single pass over the (networked) library.
    /// </summary>
    public static void RunBiasSweep(IServiceProvider services, string folder)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return;
        }

        var reader = services.GetRequiredService<IMetadataReader>();
        var decoder = services.GetRequiredService<IAudioDecoder>();
        if (services.GetRequiredService<IKeyDetector>() is not ChromagramKeyDetector detector)
        {
            Console.WriteLine("Bias sweep needs the ChromagramKeyDetector implementation.");
            return;
        }

        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} - decoding may fail)");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Bias sweep over {files.Count} file(s) under {folder}");

        // Decode + build chroma once per file; keep the chroma alongside its reference key.
        var cases = new List<(double[] Chroma, MusicalKey Ref)>();
        try
        {
            foreach (var file in files)
            {
                var md = reader.Read(file);
                if (MikKey(md) is not { } reference) continue;
                DecodedAudio? audio;
                try { audio = decoder.DecodeMono(file, AnalysisSampleRate); }
                catch (Exception ex) { Console.WriteLine($"  [decode FAIL] {Path.GetFileName(file)}: {ex.Message}"); continue; }
                if (audio is { } a && detector.BuildChroma(a) is { } chroma)
                    cases.Add((chroma, reference));
            }
        }
        finally
        {
            ManagedBass.Bass.Free();
        }

        Console.WriteLine($"Built {cases.Count} chroma(s). Re-scoring across profiles x bias values:\n");

        // Profile vectors verified verbatim against MTG/essentia src/algorithms/tonal/key.cpp.
        (string Name, double[] Maj, double[] Min)[] profiles =
        [
            ("edma (shipped)",
                [1.00, 0.29, 0.50, 0.40, 0.60, 0.56, 0.32, 0.80, 0.31, 0.45, 0.42, 0.39],
                [1.00, 0.31, 0.44, 0.58, 0.33, 0.49, 0.29, 0.78, 0.43, 0.29, 0.53, 0.32]),
            ("bgate (Essentia default)",
                [1.00, 0.00, 0.42, 0.00, 0.53, 0.37, 0.00, 0.77, 0.00, 0.38, 0.21, 0.30],
                [1.00, 0.00, 0.36, 0.39, 0.00, 0.38, 0.00, 0.74, 0.27, 0.00, 0.42, 0.23]),
            ("braw",
                [1.0000, 0.1573, 0.4200, 0.1570, 0.5296, 0.3669, 0.1632, 0.7711, 0.1676, 0.3827, 0.2113, 0.2965],
                [1.0000, 0.2330, 0.3615, 0.3905, 0.2925, 0.3777, 0.1961, 0.7425, 0.2701, 0.2161, 0.4228, 0.2272]),
            ("shaath",
                [6.6, 2.0, 3.5, 2.3, 4.6, 4.0, 2.5, 5.2, 2.4, 3.7, 2.3, 3.4],
                [6.5, 2.7, 3.5, 5.4, 2.6, 3.5, 2.5, 5.2, 4.0, 2.7, 4.3, 3.2]),
            ("edmm",
                [0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083, 0.083],
                [0.17235348, 0.04, 0.0761009, 0.12, 0.05621498, 0.08527853, 0.0497915, 0.13451001, 0.07458916, 0.05003023, 0.09187879, 0.05545106]),
        ];

        double[] biases = [0.00, 0.01, 0.02, 0.03, 0.04, 0.06];

        foreach (var (pname, maj, min) in profiles)
        {
            Console.WriteLine($"--- {pname} ---");
            Console.WriteLine("  bias    exact   relative  fifth   off    harmonically-ok");
            var bestOk = -1; double bestBias = 0;
            foreach (var bias in biases)
            {
                int exact = 0, relative = 0, fifth = 0, off = 0;
                foreach (var (chroma, reference) in cases)
                {
                    if (ChromagramKeyDetector.Classify(chroma, bias, maj, min) is not { } d) continue;
                    switch (Categorize(d.Key, reference))
                    {
                        case "exact": exact++; break;
                        case "relative": relative++; break;
                        case "fifth": fifth++; break;
                        default: off++; break;
                    }
                }
                var n = exact + relative + fifth + off;
                var ok = exact + relative + fifth;
                var okPct = n == 0 ? 0 : 100.0 * ok / n;
                if (ok > bestOk) { bestOk = ok; bestBias = bias; }
                Console.WriteLine($"  {bias:0.000}   {Pct(exact, n)}    {Pct(relative, n)}     {Pct(fifth, n)}    {Pct(off, n)}    {okPct,3:0}%  ({ok}/{n})");
            }
            Console.WriteLine($"  => best harmonically-ok {bestOk}/{cases.Count} at bias {bestBias:0.000}\n");
        }
    }

    /// <summary>
    /// Benchmarks against the GiantSteps Key dataset (Knees/Faraldo et al., ISMIR 2015):
    /// 604 two-minute Beatport previews with HAND-VERIFIED key ground truth, the set the
    /// braw/edma profiles were themselves derived on. Unlike <see cref="Run"/> (which scores
    /// agreement with whatever a *tool* like Mixed In Key wrote), this scores against true
    /// labels - so the number is real accuracy, comparable to published tool benchmarks.
    ///
    /// <para>Layout (cloned from github.com/GiantSteps/giantsteps-key-dataset): audio at
    /// <c>&lt;root&gt;/audio/&lt;id&gt;.LOFI.mp3</c>, truth at
    /// <c>&lt;root&gt;/annotations/key/&lt;id&gt;.LOFI.key</c> containing e.g. "C minor".
    /// Reports the MIREX weighted key score (correct 1.0 / fifth 0.5 / relative 0.3 /
    /// parallel 0.2) alongside the raw category breakdown.</para>
    /// </summary>
    public static void RunGiantSteps(IServiceProvider services, string root, int maxFiles = int.MaxValue)
    {
        // The shipped IKeyDetector (the routing detector -> ML or HPCP) analyses at 44.1 kHz
        // (TrackAnalysisService.KeySampleRate); decode at that rate, not the legacy 11 kHz.
        var detector = services.GetRequiredService<IKeyDetector>();
        RunGiantStepsCore(services, detector, 44100, root, maxFiles, $"shipped {detector.GetType().Name} @ 44100 Hz");
    }

    /// <summary>Validates the trained ONNX model end-to-end in C# (should reach ~0.75 - the
    /// same number the Python CV showed), confirming the export + inference path is correct.</summary>
    public static void RunGiantStepsMl(IServiceProvider services, string root, int maxFiles = int.MaxValue)
        => RunGiantStepsCore(services, new JustPlay.ML.MlKeyDetector(), 44100, root, maxFiles, "MlKeyDetector (ONNX) @ 44100 Hz");

    /// <summary>
    /// Same ground-truth benchmark but with the experimental peak-picked
    /// <see cref="HpcpKeyDetector"/> decoded at a HIGHER sample rate (so HPCP's harmonic
    /// summation keeps the upper harmonics). A/B this against <see cref="RunGiantSteps"/>
    /// before swapping any DI registration.
    /// </summary>
    public static void RunGiantStepsHpcp(IServiceProvider services, string root, int maxFiles = int.MaxValue)
        => RunGiantStepsCore(services, new HpcpKeyDetector(), 44100, root, maxFiles, "HPCP (peak-picked @ 44.1 kHz)");

    private static void RunGiantStepsCore(
        IServiceProvider services, IKeyDetector detector, int sampleRate,
        string root, int maxFiles, string label)
    {
        var audioDir = Path.Combine(root, "audio");
        var keyDir = Path.Combine(root, "annotations", "key");
        if (!Directory.Exists(audioDir) || !Directory.Exists(keyDir))
        {
            Console.WriteLine($"Expected {audioDir} and {keyDir} to exist (clone giantsteps-key-dataset).");
            return;
        }

        var decoder = services.GetRequiredService<IAudioDecoder>();

        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} - decoding may fail)");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10000) // skip empty/failed downloads
            .OrderBy(f => f)
            .Take(maxFiles)
            .ToList();

        Console.WriteLine($"GiantSteps benchmark [{label}, decode @ {sampleRate} Hz]: {files.Count} file(s).\n");

        int exact = 0, fifth = 0, relative = 0, parallel = 0, other = 0, undetected = 0, noRef = 0;
        double mirexSum = 0;
        var n = 0;
        var errInterval = new int[12];     // semitone interval (ours - ref) for every non-exact call
        var otherExamples = new List<string>();

        try
        {
            foreach (var file in files)
            {
                var keyFile = Path.Combine(keyDir, Path.GetFileNameWithoutExtension(file) + ".key");
                if (!File.Exists(keyFile)) { noRef++; continue; }
                var keyLabel = File.ReadAllText(keyFile).Trim();
                if (MusicalKey.TryParse(keyLabel) is not { } reference) { noRef++; continue; }

                DecodedAudio? audio;
                try { audio = decoder.DecodeMono(file, sampleRate); }
                catch (Exception ex) { Console.WriteLine($"  [decode FAIL] {Path.GetFileName(file)}: {ex.Message}"); continue; }
                if (audio is not { } a || detector.Detect(a) is not { } d) { undetected++; continue; }

                n++;
                var (score, cat) = MirexScore(d.Key, reference);
                mirexSum += score;
                switch (cat)
                {
                    case "exact": exact++; break;
                    case "fifth": fifth++; break;
                    case "relative": relative++; break;
                    case "parallel": parallel++; break;
                    default: other++; break;
                }
                if (cat != "exact")
                {
                    var iv = ((d.Key.PitchClass - reference.PitchClass) % 12 + 12) % 12;
                    errInterval[iv]++;
                    if (cat == "other" && otherExamples.Count < 20)
                        otherExamples.Add($"    ref {reference.Camelot,-3} ours {d.Key.Camelot,-3}  delta{iv,2} st{(d.Key.Mode != reference.Mode ? " +mode" : "")}");
                }
            }
        }
        finally
        {
            ManagedBass.Bass.Free();
        }

        Console.WriteLine($"=== GiantSteps key accuracy vs TRUE labels ({n} scored) ===");
        if (n > 0)
        {
            Console.WriteLine($"  exact (correct):  {Pct(exact, n)}  ({exact})");
            Console.WriteLine($"  fifth neighbour:  {Pct(fifth, n)}  ({fifth})   [+/-1 Camelot]");
            Console.WriteLine($"  relative maj/min: {Pct(relative, n)}  ({relative})");
            Console.WriteLine($"  parallel maj/min: {Pct(parallel, n)}  ({parallel})");
            Console.WriteLine($"  other (wrong):    {Pct(other, n)}  ({other})");
            Console.WriteLine($"  harmonically ok:  {Pct(exact + fifth + relative, n)}  (exact+fifth+relative)");
            Console.WriteLine($"  MIREX weighted:   {mirexSum / n:0.000}  (1.0/0.5/0.3/0.2 - compare to published tools)");
        }
        Console.WriteLine($"  (undetected by us: {undetected};  no/unparsable label: {noRef})");

        // Error-interval histogram: where do WRONG calls land relative to the true key?
        // 0=mode-only, 7/5=fifth, 2/10=whole-tone, 6=tritone, 1/11=semitone. A spike reveals
        // a systematic failure (tuning/octave/harmonic); a flat spread = genuinely hard tracks.
        Console.WriteLine("\nError intervals (ours - ref, semitones; all non-exact calls):");
        string[] names = ["0(mode)", "1", "2(tone)", "3(rel)", "4", "5(4th)", "6(trit)", "7(5th)", "8", "9(rel)", "10(tone)", "11"];
        for (var i = 0; i < 12; i++)
            if (errInterval[i] > 0)
                Console.WriteLine($"  {names[i],-9} {errInterval[i],3}  {new string('#', Math.Min(errInterval[i], 60))}");
        if (otherExamples.Count > 0)
        {
            Console.WriteLine("\nSample 'other' (clear-miss) cases:");
            foreach (var e in otherExamples) Console.WriteLine(e);
        }
    }

    /// <summary>
    /// Exports each GiantSteps track's 36-bin fine HPCP chroma + true label to a CSV
    /// (one row: pitchClass,mode,then 36 floats), for offline ML training in Python.
    /// Decodes @ 44.1 kHz (the HpcpKeyDetector feature). One-time; then Python iterates fast.
    /// </summary>
    public static void RunDumpChroma(IServiceProvider services, string root, string outPath)
    {
        var audioDir = Path.Combine(root, "audio");
        var keyDir = Path.Combine(root, "annotations", "key");
        var decoder = services.GetRequiredService<IAudioDecoder>();
        var hpcp = new HpcpKeyDetector();
        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError})");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10000).OrderBy(f => f).ToList();

        var rows = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var keyFile = Path.Combine(keyDir, Path.GetFileNameWithoutExtension(file) + ".key");
                if (!File.Exists(keyFile)) continue;
                // Handles both formats: GiantSteps-Key ("C minor") and MTG-Key ("d minor\t2\t"
                // = key TAB confidence[0-2]). Take the first tab field as the key; for MTG,
                // drop confidence-0 (uncertain) labels to keep the training set clean.
                var raw = File.ReadAllText(keyFile).Trim();
                var parts = raw.Split('\t');
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var conf) && conf < 1) continue;
                if (MusicalKey.TryParse(parts[0].Trim()) is not { } reference) continue;
                DecodedAudio? audio;
                try { audio = decoder.DecodeMono(file, 44100); } catch { continue; }
                if (audio is not { } a || hpcp.BuildFine36(a) is not { } fine) continue;
                var mode = reference.Mode == KeyMode.Major ? 0 : 1;
                rows.Add($"{reference.PitchClass},{mode}," + string.Join(",", fine.Select(v => v.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
        finally { ManagedBass.Bass.Free(); }

        File.WriteAllLines(outPath, rows);
        Console.WriteLine($"Wrote {rows.Count} rows to {outPath}");
    }

    /// <summary>
    /// HPCP matching sweep: decode + build each track's 12-bin HPCP chroma ONCE, then
    /// re-score it under every (profile x metric) combo - efficiently A/B's edmkey's
    /// Pearson+bgate against our cosine+braw without re-decoding @ 44.1 kHz.
    /// </summary>
    public static void RunGiantStepsHpcpSweep(IServiceProvider services, string root, int maxFiles = int.MaxValue)
    {
        var audioDir = Path.Combine(root, "audio");
        var keyDir = Path.Combine(root, "annotations", "key");
        if (!Directory.Exists(audioDir) || !Directory.Exists(keyDir))
        {
            Console.WriteLine($"Expected {audioDir} and {keyDir} to exist."); return;
        }

        var decoder = services.GetRequiredService<IAudioDecoder>();
        var hpcp = new HpcpKeyDetector();
        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError})");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10000).OrderBy(f => f).Take(maxFiles).ToList();

        Console.WriteLine($"HPCP matching sweep: building {files.Count} chroma(s) @ 44.1 kHz once...\n");
        var cases = new List<(double[] Chroma, MusicalKey Ref)>();
        try
        {
            foreach (var file in files)
            {
                var keyFile = Path.Combine(keyDir, Path.GetFileNameWithoutExtension(file) + ".key");
                if (!File.Exists(keyFile)) continue;
                if (MusicalKey.TryParse(File.ReadAllText(keyFile).Trim()) is not { } reference) continue;
                DecodedAudio? audio;
                try { audio = decoder.DecodeMono(file, 44100); } catch { continue; }
                if (audio is { } a && hpcp.BuildChroma12(a) is { } c) cases.Add((c, reference));
            }
        }
        finally { ManagedBass.Bass.Free(); }

        (string Name, double[] Maj, double[] Min)[] profiles =
        [
            ("braw", [1.0000, 0.1573, 0.4200, 0.1570, 0.5296, 0.3669, 0.1632, 0.7711, 0.1676, 0.3827, 0.2113, 0.2965],
                     [1.0000, 0.2330, 0.3615, 0.3905, 0.2925, 0.3777, 0.1961, 0.7425, 0.2701, 0.2161, 0.4228, 0.2272]),
            ("bgate", [1.00, 0.00, 0.42, 0.00, 0.53, 0.37, 0.00, 0.77, 0.00, 0.38, 0.21, 0.30],
                      [1.00, 0.00, 0.36, 0.39, 0.00, 0.38, 0.00, 0.74, 0.27, 0.00, 0.42, 0.23]),
            ("edma", [1.00, 0.29, 0.50, 0.40, 0.60, 0.56, 0.32, 0.80, 0.31, 0.45, 0.42, 0.39],
                     [1.00, 0.31, 0.44, 0.58, 0.33, 0.49, 0.29, 0.78, 0.43, 0.29, 0.53, 0.32]),
        ];

        double[] biases = [0.00, 0.02, 0.05, 0.10, 0.15];
        Console.WriteLine($"Re-scoring {cases.Count} chroma(s):\n  profile  metric   bias    exact   MIREX");
        var bestMirex = -1.0; var bestDesc = "";
        foreach (var (pname, maj, min) in profiles)
            foreach (var useCosine in new[] { true, false })
                foreach (var bias in biases)
                {
                    int exact = 0; double mirex = 0; var n = 0;
                    foreach (var (chroma, reference) in cases)
                    {
                        var ours = ScoreKey(chroma, maj, min, useCosine, bias);
                        n++;
                        var (score, cat) = MirexScore(ours, reference);
                        mirex += score;
                        if (cat == "exact") exact++;
                    }
                    var m = mirex / n;
                    if (m > bestMirex) { bestMirex = m; bestDesc = $"{pname} {(useCosine ? "cosine" : "pearson")} bias {bias:0.00}"; }
                    Console.WriteLine($"  {pname,-7}  {(useCosine ? "cosine " : "pearson")}  {bias:0.00}   {Pct(exact, n)}   {m:0.000}");
                }
        Console.WriteLine($"\n  => BEST: {bestDesc} -> MIREX {bestMirex:0.000}");
    }

    /// <summary>Best of the 24 rotated major/minor profiles for a 12-bin chroma, by cosine
    /// similarity or Pearson correlation (the two matching metrics edmkey vs JustPlay use).</summary>
    private static MusicalKey ScoreKey(double[] chroma, double[] maj, double[] min, bool useCosine, double minorBias = 0.0)
    {
        var best = double.NegativeInfinity;
        var bestPc = 0; var bestMode = KeyMode.Major;
        for (var tonic = 0; tonic < 12; tonic++)
        {
            var sMaj = useCosine ? Cosine(chroma, maj, tonic) : Pearson(chroma, maj, tonic);
            if (sMaj > best) { best = sMaj; bestPc = tonic; bestMode = KeyMode.Major; }
            var sMin = (useCosine ? Cosine(chroma, min, tonic) : Pearson(chroma, min, tonic)) + minorBias;
            if (sMin > best) { best = sMin; bestPc = tonic; bestMode = KeyMode.Minor; }
        }
        return new MusicalKey(bestPc, bestMode);
    }

    private static double Cosine(double[] c, double[] p, int tonic)
    {
        double dot = 0, nc = 0, np = 0;
        for (var i = 0; i < 12; i++)
        {
            var pv = p[((i - tonic) % 12 + 12) % 12];
            dot += c[i] * pv; nc += c[i] * c[i]; np += pv * pv;
        }
        var d = Math.Sqrt(nc * np);
        return d <= 0 ? 0 : dot / d;
    }

    private static double Pearson(double[] c, double[] p, int tonic)
    {
        double mc = 0, mp = 0;
        for (var i = 0; i < 12; i++) { mc += c[i]; mp += p[i]; }
        mc /= 12; mp /= 12;
        double cov = 0, vc = 0, vp = 0;
        for (var i = 0; i < 12; i++)
        {
            var pv = p[((i - tonic) % 12 + 12) % 12] - mp;
            var cv = c[i] - mc;
            cov += cv * pv; vc += cv * cv; vp += pv * pv;
        }
        var d = Math.Sqrt(vc * vp);
        return d <= 0 ? 0 : cov / d;
    }

    /// <summary>
    /// MIREX Audio Key Detection score for one estimate vs ground truth, with the matching
    /// category: correct=1.0, perfect-fifth (same mode)=0.5, relative maj/min=0.3,
    /// parallel maj/min=0.2, else 0. Computed from pitch classes (12-TET).
    /// </summary>
    private static (double Score, string Category) MirexScore(MusicalKey ours, MusicalKey reference)
    {
        if (ours == reference) return (1.0, "exact");

        var diff = ((ours.PitchClass - reference.PitchClass) % 12 + 12) % 12;
        var sameMode = ours.Mode == reference.Mode;

        // Perfect fifth above or below, same mode (7 or 5 semitones).
        if (sameMode && (diff == 7 || diff == 5)) return (0.5, "fifth");

        if (!sameMode)
        {
            // Relative: major key whose tonic is 3 semitones above the minor's (A minor<->C major).
            if (ours.Mode == KeyMode.Major && reference.Mode == KeyMode.Minor && diff == 3) return (0.3, "relative");
            if (ours.Mode == KeyMode.Minor && reference.Mode == KeyMode.Major && diff == 9) return (0.3, "relative");
            // Parallel: same tonic, opposite mode (C major<->C minor).
            if (diff == 0) return (0.2, "parallel");
        }

        return (0.0, "other");
    }

    /// <summary>Mixed In Key writes "Energy N" (often "8A - Energy 7") into the comment.</summary>
    private static int? MikEnergy(TrackMetadata md)
    {
        var c = md.Comment;
        if (string.IsNullOrWhiteSpace(c)) return null;
        var idx = c.IndexOf("energy", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        // First integer after the word "Energy".
        var i = idx + "energy".Length;
        while (i < c.Length && !char.IsDigit(c[i])) i++;
        var start = i;
        while (i < c.Length && char.IsDigit(c[i])) i++;
        return i > start && int.TryParse(c[start..i], out var e) && e is >= 1 and <= 10 ? e : null;
    }

    /// <summary>The key another tool already wrote - key tag first, then a Camelot/musical token in the comment.</summary>
    private static MusicalKey? MikKey(TrackMetadata md)
    {
        if (MusicalKey.TryParse(md.TaggedKey) is { } fromTag) return fromTag;

        if (md.Comment is { } c)
            foreach (var token in c.Split([' ', '\t', '-', '/', ',', ';', '|', '(', ')', '[', ']'],
                         StringSplitOptions.RemoveEmptyEntries))
                if (MusicalKey.TryParse(token) is { } fromComment) return fromComment;

        return null;
    }

    /// <summary>
    /// Dumps per-file energy features to a CSV for offline calibration against an
    /// arousal-labelled dataset (e.g. DEAM). For each audio file found under
    /// <paramref name="audioFolder"/>, runs the real <see cref="SpectralEnergyDetector"/>
    /// feature extraction (using the shipped K-weighting + spectral pipeline at 11 025 Hz)
    /// and writes one CSV row containing the raw + normalised features plus the current
    /// 1-10 integer score.
    ///
    /// <para>CSV columns: <c>filename, rawLufs, rawFlux, rawCentroid, rawRmsSd,
    /// nLoud, nFlux, nBright, nRmsSd, energyScore</c>.</para>
    ///
    /// <para>Usage: <c>--dump-energy-features &lt;audioFolder&gt; &lt;out.csv&gt;</c></para>
    /// [energy-detection.md Sec.Grounding the 1-10 scale, ml/calibrate_energy.py]
    /// </summary>
    public static void RunDumpEnergyFeatures(IServiceProvider services, string audioFolder, string outCsv)
    {
        if (!Directory.Exists(audioFolder))
        {
            Console.WriteLine($"Folder not found: {audioFolder}");
            return;
        }

        var decoder = services.GetRequiredService<IAudioDecoder>();
        var detector = new SpectralEnergyDetector();   // direct instance - reuses shipped feature computation

        // Energy analysis runs at 11 025 Hz (same rate as TrackAnalysisService.EnergySampleRate).
        const int SampleRate = 11025;

        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} - decoding may fail)");

        var files = Directory.EnumerateFiles(audioFolder, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f)
            .ToList();

        Console.WriteLine($"Dumping energy features for {files.Count} file(s) under {audioFolder}");
        Console.WriteLine($"Output: {outCsv}\n");

        var rows = new List<string>(files.Count + 1)
        {
            "filename,rawLufs,rawFlux,rawCentroid,rawRmsSd,nLoud,nFlux,nBright,nRmsSd,energyScore"
        };
        var done = 0;
        var skipped = 0;

        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                DecodedAudio? audio = null;
                try { audio = decoder.DecodeMono(file, SampleRate); }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [decode FAIL] {name}: {ex.Message}");
                    skipped++;
                    continue;
                }

                if (audio is not { } a)
                {
                    skipped++;
                    continue;
                }

                var features = detector.ExtractFeatures(a);
                if (features is null)
                {
                    Console.WriteLine($"  [silent/short] {name}");
                    skipped++;
                    continue;
                }

                var f = features.Value;
                var score = SpectralEnergyDetector.ScoreFeatures(f);

                // Escape filename for CSV (replace any commas or quotes).
                var safeName = name.Replace("\"", "'").Replace(",", ";");
                rows.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"\"{safeName}\",{f.RawLufs:F4},{f.RawFlux:F6},{f.RawCentroid:F2},{f.RawRmsSd:F6}," +
                    $"{f.NLoud:F4},{f.NFlux:F4},{f.NBright:F4},{f.NRmsSd:F4},{score}"));
                done++;

                if (done % 50 == 0)
                    Console.WriteLine($"  {done}/{files.Count} processed...");
            }
        }
        finally
        {
            ManagedBass.Bass.Free();
        }

        File.WriteAllLines(outCsv, rows, System.Text.Encoding.UTF8);
        Console.WriteLine($"\nDone. {done} rows written to {outCsv}  ({skipped} skipped).");
    }

    private static string Categorize(MusicalKey ours, MusicalKey reference)
    {
        if (ours == reference) return "exact";

        var (n1, l1) = Cam(ours);
        var (n2, l2) = Cam(reference);

        if (n1 == n2 && l1 != l2) return "relative"; // same Camelot number, A<->B = relative maj/min

        var d = Math.Abs(n1 - n2);
        d = Math.Min(d, 12 - d);
        if (l1 == l2 && d == 1) return "fifth";       // adjacent on the wheel, same mode

        return "off";
    }

    private static (int Num, char Letter) Cam(MusicalKey k)
    {
        var c = k.Camelot;            // e.g. "8A"
        return (int.Parse(c[..^1]), c[^1]);
    }

    private static string Pct(int n, int total) => total == 0 ? "  0%" : $"{100.0 * n / total,3:0}%";

    // -- N19 helpers (V4 segment + HPSS combination) -----------------------------------
    private static DecodedAudio DownsampleBy4ForReport(DecodedAudio audio)
    {
        var samples = audio.Samples!;
        var newLen  = samples.Length / 4;
        var dst     = new float[newLen];
        for (var i = 0; i < newLen; i++) dst[i] = samples[i * 4];
        return new DecodedAudio(dst, audio.SampleRate / 4);
    }

    private static List<(int Start, int End)> BuildSegmentsForReport(
        IReadOnlyList<JustPlay.Analysis.StructureBoundary> boundaries, int sampleRate, int totalSamples)
    {
        var times = new List<double> { 0.0 };
        foreach (var b in boundaries) times.Add(b.TimeSeconds);
        times.Add(totalSamples / (double)sampleRate);
        var segs = new List<(int, int)>(times.Count - 1);
        for (var i = 0; i < times.Count - 1; i++)
        {
            var s = (int)(times[i] * sampleRate);
            var e = (int)(times[i + 1] * sampleRate);
            if (e > s + sampleRate) segs.Add((s, e));
        }
        return segs;
    }

    private static DecodedAudio SliceForReport(DecodedAudio audio, int startSample, int endSample)
    {
        endSample = Math.Min(endSample, audio.Samples!.Length);
        var len   = endSample - startSample;
        if (len <= 0) return new DecodedAudio([], audio.SampleRate);
        var slice = new float[len];
        Array.Copy(audio.Samples!, startSample, slice, 0, len);
        return new DecodedAudio(slice, audio.SampleRate);
    }

    // -- N19 TUNING AUDIT ------------------------------------------------------------------

    /// <summary>
    /// N19 tuning audit: for each GiantSteps track, determine the dominant tuning offset
    /// (the sub-semitone bin with the most energy in the 36-bin fine HPCP chroma), then
    /// compare classification accuracy WITH vs WITHOUT the 36-bin tuning correction.
    ///
    /// <para>Our shipped 36-bin folding corrects for detuning (non-A440 tracks) by choosing
    /// the dominant sub-bin before folding to 12. edmkey uses a different approach: a 12-bin
    /// HPCP + shift_pcp to correct the whole vector. This audit measures HOW MUCH each track
    /// is detuned and whether the correction helped.</para>
    ///
    /// Usage: <c>--giantsteps-tuning &lt;root&gt; [maxFiles]</c>
    /// </summary>
    public static void RunGiantStepsTuningAudit(IServiceProvider services, string root, int maxFiles = int.MaxValue)
    {
        var audioDir = Path.Combine(root, "audio");
        var keyDir   = Path.Combine(root, "annotations", "key");
        if (!Directory.Exists(audioDir) || !Directory.Exists(keyDir))
        {
            Console.WriteLine($"Expected {audioDir} and {keyDir} to exist.");
            return;
        }

        var decoder = services.GetRequiredService<IAudioDecoder>();
        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError})");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10_000)
            .OrderBy(f => f)
            .Take(maxFiles)
            .ToList();

        Console.WriteLine($"N19 tuning audit [{files.Count} files]\n");

        var hpcp   = new JustPlay.Analysis.HpcpKeyDetector();

        // Counters per tuning class:
        // offset 0 = on-pitch (A440), offset 1 = slightly sharp, offset 2 = slightly flat.
        // Each offset class tracks: total, correct WITH tuning correction, correct WITHOUT.
        var countByOffset  = new int[3];
        var correctWith    = new int[3];
        var correctWithout = new int[3];
        var mirexWith      = new double[3];
        var mirexWithout   = new double[3];

        // Track how many detuned tracks there are.
        var detuned = 0;
        var n = 0;

        try
        {
            foreach (var file in files)
            {
                var keyFile = Path.Combine(keyDir, Path.GetFileNameWithoutExtension(file) + ".key");
                if (!File.Exists(keyFile)) continue;
                var keyLabel = File.ReadAllText(keyFile).Trim();
                if (MusicalKey.TryParse(keyLabel) is not { } reference) continue;

                DecodedAudio audio;
                try { audio = decoder.DecodeMono(file, 44100); }
                catch { continue; }

                // Build the 36-bin fine HPCP.
                var fine36 = hpcp.BuildFine36(audio);
                if (fine36 is null) continue;

                // Determine dominant tuning sub-bin (0, 1, or 2).
                const int BPS = 3; // bins per semitone
                const int CB  = 36;
                var subEnergy = new double[BPS];
                for (var b = 0; b < CB; b++) subEnergy[b % BPS] += fine36[b];
                var tuningBin = 0;
                if (subEnergy[1] > subEnergy[0]) tuningBin = 1;
                if (subEnergy[2] > subEnergy[tuningBin]) tuningBin = 2;
                if (tuningBin != 0) detuned++;

                // WITH tuning correction: fold centred on tuningBin.
                var chromaWith = FoldWithTuning(fine36, tuningBin);
                // WITHOUT tuning correction: fold always centred on bin 0 (A440 assumption).
                var chromaWithout = FoldWithTuning(fine36, 0);

                var r_with    = JustPlay.Analysis.ChromagramKeyDetector.Classify(chromaWith, 0.0);
                var r_without = JustPlay.Analysis.ChromagramKeyDetector.Classify(chromaWithout, 0.0);

                if (r_with is null && r_without is null) continue;

                n++;
                countByOffset[tuningBin]++;

                if (r_with is { } rw)
                {
                    var (sc, cat) = MirexScore(rw.Key, reference);
                    mirexWith[tuningBin] += sc;
                    if (cat == "exact") correctWith[tuningBin]++;
                }
                if (r_without is { } rwo)
                {
                    var (sc, cat) = MirexScore(rwo.Key, reference);
                    mirexWithout[tuningBin] += sc;
                    if (cat == "exact") correctWithout[tuningBin]++;
                }
            }
        }
        finally { ManagedBass.Bass.Free(); }

        Console.WriteLine($"=== N19 Tuning audit ({n} scored) ===");
        Console.WriteLine($"  Detuned tracks (offset != 0): {detuned}/{n}  ({100.0*detuned/Math.Max(1,n):0}%)");
        Console.WriteLine();
        Console.WriteLine($"{"Offset",-8} {"count",6} {"exact WITH",11} {"exact WITHOUT",14} {"MIREX WITH",11} {"MIREX WITHOUT",14}");
        var offNames = new[] { "centre(0)", "sharp(+1)", "flat(-1)" };
        for (var o = 0; o < 3; o++)
        {
            var c = countByOffset[o];
            if (c == 0) continue;
            Console.WriteLine($"{offNames[o],-8} {c,6}  {Pct(correctWith[o], c),11}  {Pct(correctWithout[o], c),14}  {mirexWith[o]/c,11:0.000}  {mirexWithout[o]/c,14:0.000}");
        }
        var totalWith    = mirexWith.Sum();
        var totalWithout = mirexWithout.Sum();
        Console.WriteLine($"\n  Overall: WITH tuning MIREX {totalWith/Math.Max(1,n):0.000}  vs  WITHOUT tuning MIREX {totalWithout/Math.Max(1,n):0.000}");
        Console.WriteLine($"  Tuning correction gain: {(totalWith - totalWithout)/Math.Max(1,n):+0.000;-0.000} MIREX");
    }

    /// <summary>Fold 36-bin fine chroma to 12, centred on <paramref name="tuningBin"/> (0, 1, or 2).</summary>
    private static double[] FoldWithTuning(double[] fine, int tuningBin)
    {
        const int BPS = 3;
        const int CB  = 36;
        var chroma = new double[12];
        for (var pc = 0; pc < 12; pc++)
        {
            var c = pc * BPS + tuningBin;
            chroma[pc]  = fine[Mod12(c, CB)];
            chroma[pc] += 0.5 * fine[Mod12(c - 1, CB)];
            chroma[pc] += 0.5 * fine[Mod12(c + 1, CB)];
        }
        // Normalise.
        var sum = 0.0; for (var i = 0; i < 12; i++) sum += chroma[i];
        if (sum > 1e-9) for (var i = 0; i < 12; i++) chroma[i] /= sum;
        return chroma;
    }

    private static int Mod12(int x, int m) => ((x % m) + m) % m;

    // -- N19 EXPERIMENT: segment-aware + HPSS variants ----------------------------------

    /// <summary>
    /// N19 experiment harness - scores four key-detection variants against GiantSteps ground
    /// truth in a single pass over the dataset:
    /// <list type="bullet">
    ///   <item><b>V1 global</b>: shipped HpcpKeyDetector (identical to RunGiantStepsHpcp)</item>
    ///   <item><b>V2 segment-vote</b>: StructureDetector segments + tonal-segment weighted vote</item>
    ///   <item><b>V3 HPSS</b>: median-filter drum suppression before HPCP chroma</item>
    ///   <item><b>V4 segment+HPSS</b>: both combined</item>
    /// </list>
    /// Usage: <c>--giantsteps-segment &lt;root&gt; [maxFiles]</c>
    /// </summary>
    public static void RunGiantStepsSegmentExperiment(
        IServiceProvider services, string root, int maxFiles = int.MaxValue)
    {
        var audioDir = Path.Combine(root, "audio");
        var keyDir   = Path.Combine(root, "annotations", "key");
        if (!Directory.Exists(audioDir) || !Directory.Exists(keyDir))
        {
            Console.WriteLine($"Expected {audioDir} and {keyDir} to exist (clone giantsteps-key-dataset).");
            return;
        }

        var decoder = services.GetRequiredService<IAudioDecoder>();
        if (!ManagedBass.Bass.Init(0))
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} - decoding may fail)");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10_000)
            .OrderBy(f => f)
            .Take(maxFiles)
            .ToList();

        Console.WriteLine($"N19 segment-aware key experiment [{files.Count} files @ 44.1 kHz]");
        Console.WriteLine("Variants: V1=global(HPCP) | V2=segment-vote | V3=HPSS | V4=segment+HPSS\n");

        // Per-variant accumulators.
        var names = new[] { "V1-global", "V2-segment", "V3-HPSS", "V4-seg+HPSS" };
        var exact   = new int[4];
        var fifth   = new int[4];
        var rel     = new int[4];
        var par     = new int[4];
        var other   = new int[4];
        var missed  = new int[4];
        var mirex   = new double[4];

        // Per-track agreement stats (do the variants AGREE with each other? - diagnostic).
        int agreeV1V2 = 0, agreeV1V3 = 0, agreeV1V4 = 0;
        int disagreeV1V2 = 0, disagreeV1V3 = 0, disagreeV1V4 = 0;
        int v2BetterThanV1 = 0, v3BetterThanV1 = 0, v4BetterThanV1 = 0;

        // Segment diagnostics.
        var totalSegments = new List<int>();
        var tonalSegments = new List<int>();
        var fallbackCount = 0;

        // Tuning audit: track how many tracks have a non-zero tuning offset detected,
        // and whether the correct key was only reachable with tuning correction.
        // (We compare HpcpKeyDetector which does tuning vs a variant without it.)
        var tuningOffsets = new List<double>(); // the dominant sub-bin offset per track

        var n = 0;
        var segDetector  = new JustPlay.Analysis.SegmentKeyDetector();

        try
        {
            foreach (var file in files)
            {
                var keyFile = Path.Combine(keyDir, Path.GetFileNameWithoutExtension(file) + ".key");
                if (!File.Exists(keyFile)) continue;
                var keyLabel = File.ReadAllText(keyFile).Trim();
                if (MusicalKey.TryParse(keyLabel) is not { } reference) continue;

                DecodedAudio audio;
                try { audio = decoder.DecodeMono(file, 44100); }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [decode FAIL] {Path.GetFileName(file)}: {ex.Message}");
                    for (var v = 0; v < 4; v++) missed[v]++;
                    continue;
                }

                // V1: global HPCP
                var r1 = segDetector.DetectGlobal(audio);

                // V2: per-tonal-segment vote
                var r2full = segDetector.DetectSegmented(audio);
                (MusicalKey Key, double Confidence)? r2 = r2full is null ? null
                    : (r2full.Value.Key, r2full.Value.Confidence);
                if (r2full is { } r2d)
                {
                    totalSegments.Add(r2d.Details.TotalSegments);
                    tonalSegments.Add(r2d.Details.TonalSegmentCount);
                    if (r2d.Details.UsedFallback) fallbackCount++;
                }

                // V3: HPSS flatness-gated chroma (per-frame tonalness weighting).
                // Downsample to 11025 Hz first: the chromagram approach (used inside DetectHpssGated)
                // was calibrated at 11025 Hz where 8192-pt frames give 1.35 Hz/bin resolution -
                // enough to resolve semitones at the bass octave. At 44.1 kHz, 8192-pt frames give
                // 5.38 Hz/bin and the lowest semitones are unresolved, degrading accuracy.
                var audio11k = DownsampleBy4ForReport(audio);
                var r3 = segDetector.DetectHpssGated(audio11k);

                // V4: segment-vote on HPSS-gated chroma segments
                // (combines V2's structural segmentation with V3's tonalness weighting at segment level;
                //  V2 already does per-segment tonalness VOTING - so V4 adds V3-style gating WITHIN each
                //  segment's HPCP build. This requires a segment-level gated chroma. For V4 we use the
                //  simplest combination: the V2 segment-vote but with HPSS-gated chroma per segment.)
                (MusicalKey Key, double Confidence)? r4 = null;
                if (r2full is { } r2d4 && !r2d4.Details.UsedFallback)
                {
                    // Re-run segment detection with gated chroma per segment.
                    var boundaries4 = new JustPlay.Analysis.StructureDetector { MinBoundaryGapSeconds = 15.0 }
                        .Detect(DownsampleBy4ForReport(audio));
                    var segs4 = BuildSegmentsForReport(boundaries4, audio.SampleRate, audio.Samples!.Length);
                    var votes4 = new List<(double[] Chroma, double Weight)>();
                    foreach (var (s4, e4) in segs4)
                    {
                        // Slice at 44100 then downsample to 11025 for the gated chroma build.
                        var seg4raw  = SliceForReport(audio, s4, e4);
                        var seg4     = DownsampleBy4ForReport(seg4raw);
                        var c4 = JustPlay.Analysis.HpssDrumSuppressor.BuildGatedChroma12(seg4);
                        if (c4 is null) continue;
                        var dur4 = (double)(e4 - s4) / audio.SampleRate;
                        votes4.Add((c4, dur4));  // weight = duration (flatness already embedded in chroma)
                    }
                    if (votes4.Count >= 2)
                    {
                        var ks4 = new double[24];
                        foreach (var (c4, w4) in votes4)
                        {
                            var d4 = JustPlay.Analysis.ChromagramKeyDetector.Classify(c4, 0.0);
                            if (d4 is null) continue;
                            ks4[d4.Value.Key.PitchClass * 2 + (d4.Value.Key.Mode == KeyMode.Minor ? 1 : 0)] += w4;
                        }
                        var best4 = 0;
                        for (var i = 1; i < 24; i++) if (ks4[i] > ks4[best4]) best4 = i;
                        var sec4 = -1;
                        for (var i = 0; i < 24; i++) if (i != best4 && (sec4 < 0 || ks4[i] > ks4[sec4])) sec4 = i;
                        var conf4 = (sec4 < 0 || ks4[best4] <= 0) ? 0.0
                            : Math.Clamp((ks4[best4] - ks4[sec4]) / ks4[best4], 0.0, 1.0);
                        r4 = (new MusicalKey(best4 / 2, best4 % 2 == 1 ? KeyMode.Minor : KeyMode.Major), conf4);
                    }
                }
                if (r4 is null) r4 = r3;  // V4 falls back to V3 if segmentation didn't work

                var results = new[] { r1, r2, r3, r4 };
                n++;  // count one per track (tracks where all 4 could run)
                for (var v = 0; v < 4; v++)
                {
                    if (results[v] is not { } res) { missed[v]++; continue; }
                    var (score, cat) = MirexScore(res.Key, reference);
                    mirex[v] += score;
                    switch (cat)
                    {
                        case "exact":    exact[v]++; break;
                        case "fifth":    fifth[v]++; break;
                        case "relative": rel[v]++;   break;
                        case "parallel": par[v]++;   break;
                        default:         other[v]++;  break;
                    }
                }

                // Agreement diagnostics (only when both detectors returned a result).
                if (r1.HasValue && r2.HasValue)
                {
                    if (r1.Value.Key == r2.Value.Key) agreeV1V2++;
                    else disagreeV1V2++;
                }
                if (r1.HasValue && r3.HasValue)
                {
                    if (r1.Value.Key == r3.Value.Key) agreeV1V3++;
                    else disagreeV1V3++;
                }
                if (r1.HasValue && r4.HasValue)
                {
                    if (r1.Value.Key == r4.Value.Key) agreeV1V4++;
                    else disagreeV1V4++;
                }

                // Track-level "better than V1" count (scores 1 if variant's MIREX > V1's MIREX).
                if (r1.HasValue && r2.HasValue)
                {
                    var s1 = MirexScore(r1.Value.Key, reference).Score;
                    var s2 = MirexScore(r2.Value.Key, reference).Score;
                    if (s2 > s1) v2BetterThanV1++;
                }
                if (r1.HasValue && r3.HasValue)
                {
                    var s1 = MirexScore(r1.Value.Key, reference).Score;
                    var s3 = MirexScore(r3.Value.Key, reference).Score;
                    if (s3 > s1) v3BetterThanV1++;
                }
                if (r1.HasValue && r4.HasValue)
                {
                    var s1 = MirexScore(r1.Value.Key, reference).Score;
                    var s4 = MirexScore(r4.Value.Key, reference).Score;
                    if (s4 > s1) v4BetterThanV1++;
                }

                // Incremental checkpoint: print a full results table every 50 tracks so a
                // killed/interrupted run never loses its aggregate numbers (the full 604-file
                // pass takes ~2.5h and is fragile). The table prints "results (running)".
                if (n % 50 == 0)
                {
                    Console.WriteLine($"  ... {n}/{files.Count} processed");
                    PrintResults(running: true);
                }
            }
        }
        finally
        {
            ManagedBass.Bass.Free();
            // Always print the final table, even on exception / early break.
            PrintResults(running: false);
        }

        // -- Local result-printing function (callable for checkpoints + final) -----------
        void PrintResults(bool running)
        {
            var tag = running ? $"results (running - {n}/{files.Count})" : $"results ({n} scored, {files.Count} total)";
            Console.WriteLine($"\n=== N19 segment-key experiment {tag} ===\n");
            Console.WriteLine($"{"Variant",-16} {"exact%",7} {"MIREX",6} {"harmonically-ok",17} {"fifth%",7} {"rel%",5} {"par%",5} {"other%",7} {"missed",7}");
            Console.WriteLine(new string('-', 80));
            for (var v = 0; v < 4; v++)
            {
                var scored = exact[v] + fifth[v] + rel[v] + par[v] + other[v];
                if (scored == 0) { Console.WriteLine($"{names[v],-16}  (no results)"); continue; }
                var harmOk = exact[v] + fifth[v] + rel[v];
                Console.WriteLine($"{names[v],-16} {Pct(exact[v], scored),7} {mirex[v] / scored,6:0.000} {Pct(harmOk, scored),17} {Pct(fifth[v], scored),7} {Pct(rel[v], scored),5} {Pct(par[v], scored),5} {Pct(other[v], scored),7} {missed[v],7}");
            }

            Console.WriteLine("\n-- Segment diagnostics ------------------------------------------------------");
            if (totalSegments.Count > 0)
            {
                Console.WriteLine($"  avg segments per track:       {totalSegments.Average():0.1} (range {totalSegments.Min()}-{totalSegments.Max()})");
                Console.WriteLine($"  avg tonal segments per track: {tonalSegments.Average():0.1}");
                Console.WriteLine($"  tracks that used fallback:    {fallbackCount}/{totalSegments.Count}");
            }

            Console.WriteLine("\n-- V1 vs variant agreement (when both returned a key) -----------------------");
            Console.WriteLine($"  V1 vs V2 (segment): agree={agreeV1V2}, disagree={disagreeV1V2}, V2-better-on-track={v2BetterThanV1}");
            Console.WriteLine($"  V1 vs V3 (HPSS):    agree={agreeV1V3}, disagree={disagreeV1V3}, V3-better-on-track={v3BetterThanV1}");
            Console.WriteLine($"  V1 vs V4 (both):    agree={agreeV1V4}, disagree={disagreeV1V4}, V4-better-on-track={v4BetterThanV1}");
        }
    }
}
