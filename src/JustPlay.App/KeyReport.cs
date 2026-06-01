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
/// <see cref="IKeyDetector"/> output — so the detector can be validated on Windows
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
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} — decoding may fail)");

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
                        energyLines.Add($"  E ref {re,2} ours {oe,2}  (Δ{oe - re,+2})  {name}");
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
            Console.WriteLine($"  relative maj/min: {Pct(relative, compared)}  ({relative})   [same notes — 8A↔8B]");
            Console.WriteLine($"  fifth neighbour:  {Pct(fifth, compared)}  ({fifth})   [±1 Camelot, mixable]");
            Console.WriteLine($"  off:              {Pct(off, compared)}  ({off})");
            Console.WriteLine($"  harmonically ok:  {Pct(exact + relative + fifth, compared)}  (exact+relative+fifth)");
        }
        Console.WriteLine($"  (undetected by us: {undetected};  no reference key in file: {noRef})");

        // ---- Energy accuracy summary ----
        Console.WriteLine();
        if (energyLines.Count > 0)
        {
            Console.WriteLine("Energy off by ≥3:");
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
            Console.WriteLine($"  within ±1:        {Pct(within1, energyPairs.Count)}  ({within1})");
            Console.WriteLine($"  within ±2:        {Pct(within2, energyPairs.Count)}  ({within2})");
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
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} — decoding may fail)");

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

        Console.WriteLine($"Built {cases.Count} chroma(s). Re-scoring across profiles × bias values:\n");

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
    /// labels — so the number is real accuracy, comparable to published tool benchmarks.
    ///
    /// <para>Layout (cloned from github.com/GiantSteps/giantsteps-key-dataset): audio at
    /// <c>&lt;root&gt;/audio/&lt;id&gt;.LOFI.mp3</c>, truth at
    /// <c>&lt;root&gt;/annotations/key/&lt;id&gt;.LOFI.key</c> containing e.g. "C minor".
    /// Reports the MIREX weighted key score (correct 1.0 / fifth 0.5 / relative 0.3 /
    /// parallel 0.2) alongside the raw category breakdown.</para>
    /// </summary>
    public static void RunGiantSteps(IServiceProvider services, string root, int maxFiles = int.MaxValue)
    {
        var detector = services.GetRequiredService<IKeyDetector>();
        RunGiantStepsCore(services, detector, AnalysisSampleRate, root, maxFiles, "shipped (braw+cosine @ 11 kHz)");
    }

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
            Console.WriteLine($"(BASS init returned false: {ManagedBass.Bass.LastError} — decoding may fail)");

        var files = Directory.EnumerateFiles(audioDir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Where(f => new FileInfo(f).Length > 10000) // skip empty/failed downloads
            .OrderBy(f => f)
            .Take(maxFiles)
            .ToList();

        Console.WriteLine($"GiantSteps benchmark [{label}, decode @ {sampleRate} Hz]: {files.Count} file(s).\n");

        int exact = 0, fifth = 0, relative = 0, parallel = 0, other = 0, undetected = 0, noRef = 0;
        double mirexSum = 0;
        var n = 0;

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
            Console.WriteLine($"  fifth neighbour:  {Pct(fifth, n)}  ({fifth})   [±1 Camelot]");
            Console.WriteLine($"  relative maj/min: {Pct(relative, n)}  ({relative})");
            Console.WriteLine($"  parallel maj/min: {Pct(parallel, n)}  ({parallel})");
            Console.WriteLine($"  other (wrong):    {Pct(other, n)}  ({other})");
            Console.WriteLine($"  harmonically ok:  {Pct(exact + fifth + relative, n)}  (exact+fifth+relative)");
            Console.WriteLine($"  MIREX weighted:   {mirexSum / n:0.000}  (1.0/0.5/0.3/0.2 — compare to published tools)");
        }
        Console.WriteLine($"  (undetected by us: {undetected};  no/unparsable label: {noRef})");
    }

    /// <summary>
    /// HPCP matching sweep: decode + build each track's 12-bin HPCP chroma ONCE, then
    /// re-score it under every (profile × metric) combo — efficiently A/B's edmkey's
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

        Console.WriteLine($"Re-scoring {cases.Count} chroma(s):\n  profile  metric    exact   MIREX");
        foreach (var (pname, maj, min) in profiles)
            foreach (var useCosine in new[] { true, false })
            {
                int exact = 0; double mirex = 0; var n = 0;
                foreach (var (chroma, reference) in cases)
                {
                    var ours = ScoreKey(chroma, maj, min, useCosine);
                    n++;
                    var (score, cat) = MirexScore(ours, reference);
                    mirex += score;
                    if (cat == "exact") exact++;
                }
                Console.WriteLine($"  {pname,-7}  {(useCosine ? "cosine " : "pearson")}   {Pct(exact, n)}   {mirex / n:0.000}");
            }
    }

    /// <summary>Best of the 24 rotated major/minor profiles for a 12-bin chroma, by cosine
    /// similarity or Pearson correlation (the two matching metrics edmkey vs JustPlay use).</summary>
    private static MusicalKey ScoreKey(double[] chroma, double[] maj, double[] min, bool useCosine)
    {
        var best = double.NegativeInfinity;
        var bestPc = 0; var bestMode = KeyMode.Major;
        for (var tonic = 0; tonic < 12; tonic++)
        {
            var sMaj = useCosine ? Cosine(chroma, maj, tonic) : Pearson(chroma, maj, tonic);
            if (sMaj > best) { best = sMaj; bestPc = tonic; bestMode = KeyMode.Major; }
            var sMin = useCosine ? Cosine(chroma, min, tonic) : Pearson(chroma, min, tonic);
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
            // Relative: major key whose tonic is 3 semitones above the minor's (A minor↔C major).
            if (ours.Mode == KeyMode.Major && reference.Mode == KeyMode.Minor && diff == 3) return (0.3, "relative");
            if (ours.Mode == KeyMode.Minor && reference.Mode == KeyMode.Major && diff == 9) return (0.3, "relative");
            // Parallel: same tonic, opposite mode (C major↔C minor).
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

    /// <summary>The key another tool already wrote — key tag first, then a Camelot/musical token in the comment.</summary>
    private static MusicalKey? MikKey(TrackMetadata md)
    {
        if (MusicalKey.TryParse(md.TaggedKey) is { } fromTag) return fromTag;

        if (md.Comment is { } c)
            foreach (var token in c.Split([' ', '\t', '-', '/', ',', ';', '|', '(', ')', '[', ']'],
                         StringSplitOptions.RemoveEmptyEntries))
                if (MusicalKey.TryParse(token) is { } fromComment) return fromComment;

        return null;
    }

    private static string Categorize(MusicalKey ours, MusicalKey reference)
    {
        if (ours == reference) return "exact";

        var (n1, l1) = Cam(ours);
        var (n2, l2) = Cam(reference);

        if (n1 == n2 && l1 != l2) return "relative"; // same Camelot number, A↔B = relative maj/min

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
}
