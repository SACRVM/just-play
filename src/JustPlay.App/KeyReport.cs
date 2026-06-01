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
