using System.Text.Json;
using JustPlay.Cli.Index;
using JustPlay.Cli.Reports;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay stats --index &lt;path&gt; [--json &lt;out&gt;]</c>
///
/// Reads the sidecar analysis index and emits distribution histograms for:
///   • BPM in 10-decade bands (100-109, 110-119, 120-129, …, 170-179, 180+, unknown)
///   • Energy (1..10)
///   • Danceability (bucketed by 0.5)
///   • BeatType (from RhythmPattern, if populated)
///   • Rhythm scalar averages (FourOnFloor, OffbeatEnergy, Swing, Syncopation, HalfTimeFeel)
///
/// This is the data Chloe uses to tune the beat-type bucket thresholds before the apply phase.
/// </summary>
internal static class StatsCommand
{
    public static int Run(string indexPath, string? jsonOut)
    {
        indexPath = Path.GetFullPath(indexPath);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"[stats] ERROR: index not found: {indexPath}");
            return 1;
        }

        Console.WriteLine($"[stats] Loading index: {indexPath}");
        var index = TrackIndex.Load(indexPath);

        var entries = index.Entries.Values.ToList();
        var analysed = entries.Where(e => e.Success).ToList();
        var failed   = entries.Count(e => !e.Success);

        Console.WriteLine($"  Total indexed : {entries.Count:N0}");
        Console.WriteLine($"  Analysed ok   : {analysed.Count:N0}");
        Console.WriteLine($"  Failed        : {failed:N0}");
        Console.WriteLine();

        // ── BPM histogram (10-decade bands, 100..180+) ───────────────────────
        var bpmBuckets = BuildBpmBuckets(analysed);
        Console.WriteLine("  BPM distribution (10-decade bands):");
        PrintHist(bpmBuckets.Select(b => (b.Label, b.Count)));

        // ── Energy histogram ──────────────────────────────────────────────────
        var energyHist = BuildEnergyHist(analysed);
        Console.WriteLine();
        Console.WriteLine("  Energy distribution (1..10):");
        PrintHist(energyHist.Select(kv => (kv.Key, kv.Value)));

        // ── Danceability histogram ────────────────────────────────────────────
        var danceHist = BuildDanceabilityHist(analysed);
        Console.WriteLine();
        Console.WriteLine("  Danceability distribution (0.5 buckets):");
        PrintHist(danceHist.Select(kv => (kv.Key, kv.Value)));

        // ── BeatType histogram ────────────────────────────────────────────────
        var beatTypeHist = BuildBeatTypeHist(analysed);
        Console.WriteLine();
        if (beatTypeHist.Count == 0)
            Console.WriteLine("  BeatType: no data (RhythmPattern not yet computed).");
        else
        {
            Console.WriteLine("  BeatType distribution:");
            PrintHist(beatTypeHist.Select(kv => (kv.Key, kv.Value)));
        }

        // ── Rhythm scalar averages ────────────────────────────────────────────
        var rhythmAvg = BuildRhythmAverages(analysed);
        Console.WriteLine();
        if (rhythmAvg.Count == 0)
            Console.WriteLine("  Rhythm averages: no data.");
        else
        {
            Console.WriteLine("  Rhythm scalar averages (across tracks with RhythmPattern):");
            foreach (var (k, v) in rhythmAvg.OrderBy(kv => kv.Key))
                Console.WriteLine($"    {k,-20} {v:F4}");
        }

        // ── Vibe quartet histograms (v8+) ─────────────────────────────────────
        var harshnessHist   = BuildDecileHist(analysed, e => e.Harshness);
        var basePunchHist   = BuildDecileHist(analysed, e => e.BassPunch);
        var bassGrooveHist  = BuildDecileHist(analysed, e => e.BassGroove);
        var rawEnergyHist   = BuildDecileHist(analysed, e => e.RawEnergyScore);
        var darkHist        = BuildDecileHist(analysed, e => e.Dark);
        var hypnoticHist    = BuildDecileHist(analysed, e => e.Hypnotic);

        if (harshnessHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Harshness distribution (0.1 buckets):");
            PrintHist(harshnessHist.Select(kv => (kv.Key, kv.Value)));
        }
        if (basePunchHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Punch distribution (0.1 buckets):");
            PrintHist(basePunchHist.Select(kv => (kv.Key, kv.Value)));
        }
        if (bassGrooveHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Groove distribution (0.1 buckets):");
            PrintHist(bassGrooveHist.Select(kv => (kv.Key, kv.Value)));
        }
        if (darkHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Dark distribution (0.1 buckets; 1=dark/dull, 0=bright):");
            PrintHist(darkHist.Select(kv => (kv.Key, kv.Value)));
        }
        if (hypnoticHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Hypnotic distribution (0.1 buckets; 1=looping/minimal, 0=evolving):");
            PrintHist(hypnoticHist.Select(kv => (kv.Key, kv.Value)));
        }
        if (rawEnergyHist.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  RawEnergyScore distribution (0.1 buckets, ~maps to energy 1-10):");
            PrintHist(rawEnergyHist.Select(kv => (kv.Key, kv.Value)));
        }

        // ── Build report + optionally write JSON ─────────────────────────────
        var report = new StatsReport
        {
            IndexPath      = indexPath,
            TotalIndexed   = entries.Count,
            AnalysedCount  = analysed.Count,
            FailedCount    = failed,
            BpmBuckets     = bpmBuckets,
            EnergyHist     = energyHist,
            DanceabilityHist = danceHist,
            BeatTypeHist   = beatTypeHist,
            RhythmAverages = rhythmAvg,
            HarshnessHist  = harshnessHist,
            BasePunchHist  = basePunchHist,
            BassGrooveHist = bassGrooveHist,
            DarkHist       = darkHist,
            HypnoticHist   = hypnoticHist,
            RawEnergyHist  = rawEnergyHist,
        };

        if (jsonOut is not null)
        {
            jsonOut = Path.GetFullPath(jsonOut);
            var dir = Path.GetDirectoryName(jsonOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(report, CliJsonContext.Default.StatsReport);
            File.WriteAllText(jsonOut, json);
            Console.WriteLine();
            Console.WriteLine($"  JSON written to: {jsonOut}");
        }

        return 0;
    }

    // ── Histogram builders ───────────────────────────────────────────────────

    private static List<BpmBucket> BuildBpmBuckets(IReadOnlyList<Index.TrackIndexEntry> entries)
    {
        // Bands: 60-69, 70-79, …, 170-179, 180+, Unknown
        var bandMin = 60;
        var bandMax = 180;
        var step    = 10;

        var counts = new Dictionary<int, int>(); // key = band start
        var above  = 0;
        var unknown = 0;

        foreach (var e in entries)
        {
            if (e.Bpm is not { } bpm) { unknown++; continue; }
            var b = (int)(bpm / step) * step;
            if (bpm >= bandMax)       { above++; continue; }
            if (bpm < bandMin)        { unknown++; continue; }
            counts[b] = counts.GetValueOrDefault(b) + 1;
        }

        var result = new List<BpmBucket>();
        for (var lo = bandMin; lo < bandMax; lo += step)
        {
            result.Add(new BpmBucket
            {
                Label = $"{lo}-{lo + step - 1}",
                Min   = lo,
                Max   = lo + step - 1,
                Count = counts.GetValueOrDefault(lo),
            });
        }
        result.Add(new BpmBucket { Label = $"{bandMax}+", Min = bandMax, Max = int.MaxValue, Count = above });
        result.Add(new BpmBucket { Label = "unknown",     Min = 0,       Max = 0,            Count = unknown });
        return result;
    }

    private static Dictionary<string, int> BuildEnergyHist(IReadOnlyList<Index.TrackIndexEntry> entries)
    {
        var hist = new Dictionary<string, int>();
        for (var i = 1; i <= 10; i++)
            hist[i.ToString()] = 0;
        hist["unknown"] = 0;

        foreach (var e in entries)
        {
            if (e.Energy is { } en && en >= 1 && en <= 10)
                hist[en.ToString()]++;
            else
                hist["unknown"]++;
        }
        return hist;
    }

    private static Dictionary<string, int> BuildDanceabilityHist(IReadOnlyList<Index.TrackIndexEntry> entries)
    {
        var hist    = new Dictionary<string, int>();
        var unknown = 0;
        foreach (var e in entries)
        {
            if (e.Danceability is not { } d) { unknown++; continue; }
            // Bucket by 0.5
            var lo  = Math.Floor((double)d / 0.5) * 0.5;
            var key = $"{lo:F1}-{lo + 0.5:F1}";
            hist[key] = hist.GetValueOrDefault(key) + 1;
        }
        if (unknown > 0) hist["unknown"] = unknown;
        return hist.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Dictionary<string, int> BuildBeatTypeHist(IReadOnlyList<Index.TrackIndexEntry> entries)
    {
        var hist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.BeatType)) continue;
            hist[e.BeatType] = hist.GetValueOrDefault(e.BeatType) + 1;
        }
        return hist.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Builds a histogram with 0.1-wide buckets (e.g. "0.0-0.1", "0.1-0.2", ..., "0.9-1.0")
    /// for any scalar property in [0,1]. Entries where the value is null are skipped.
    /// </summary>
    private static Dictionary<string, int> BuildDecileHist(
        IReadOnlyList<Index.TrackIndexEntry> entries,
        Func<Index.TrackIndexEntry, double?> selector)
    {
        const double bucketSize = 0.1;
        var hist = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            if (selector(e) is not { } v) continue;
            var lo  = Math.Floor(v / bucketSize) * bucketSize;
            lo      = Math.Clamp(lo, 0.0, 0.9);
            var key = $"{lo:F1}-{lo + bucketSize:F1}";
            hist[key] = hist.GetValueOrDefault(key) + 1;
        }
        return hist.Count == 0
            ? hist
            : hist.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Dictionary<string, double> BuildRhythmAverages(IReadOnlyList<Index.TrackIndexEntry> entries)
    {
        var rhythmEntries = entries.Where(e => e.BeatType is not null).ToList();
        if (rhythmEntries.Count == 0) return [];

        static double Avg(IReadOnlyList<Index.TrackIndexEntry> list, Func<Index.TrackIndexEntry, double?> sel)
        {
            var vals = list.Select(sel).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? vals.Average() : 0.0;
        }

        return new Dictionary<string, double>
        {
            ["FourOnFloor"]   = Avg(rhythmEntries, e => e.FourOnFloor),
            ["OffbeatEnergy"] = Avg(rhythmEntries, e => e.OffbeatEnergy),
            ["Swing"]         = Avg(rhythmEntries, e => e.Swing),
            ["Syncopation"]   = Avg(rhythmEntries, e => e.Syncopation),
            ["HalfTimeFeel"]  = Avg(rhythmEntries, e => e.HalfTimeFeel),
        };
    }

    // ── Console helpers ──────────────────────────────────────────────────────

    private static void PrintHist(IEnumerable<(string Label, int Count)> items)
    {
        var list = items.ToList();
        var max  = list.Count > 0 ? list.Max(x => x.Count) : 1;
        var barW = 40;
        foreach (var (label, count) in list)
        {
            var bar = count > 0
                ? new string('█', (int)Math.Ceiling((double)count / max * barW))
                : "";
            Console.WriteLine($"    {label,-12} {count,6:N0}  {bar}");
        }
    }
}
