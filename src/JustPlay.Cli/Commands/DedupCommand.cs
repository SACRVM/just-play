using System.Text.Json;
using JustPlay.Cli.Reports;
using JustPlay.Metadata;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay dedup &lt;root&gt; [--json &lt;out&gt;]</c>
///
/// PHASE 0 - detects duplicate audio files in two passes:
///
/// (a) EXACT dupes: files with the same byte count are SHA-256 hashed and grouped.
///     Only files that share a size collide, so the hash pass is fast.
///
/// (b) NEAR dupes: files whose (artist + title) tag pair normalises to the same string
///     AND whose durations are within ~2 seconds of each other.  The "same recording,
///     different encode" case common in DJ libraries.
///
/// READ-ONLY - never deletes or modifies any file.
/// </summary>
internal static class DedupCommand
{
    /// <summary>Duration window within which two tracks count as near-dupes (seconds).</summary>
    public const double NearDupeDurationWindowSec = 2.0;

    public static int Run(string root, string? jsonOut)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[dedup] ERROR: directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[dedup] Scanning: {root}");

        var files = AudioFiles.Enumerate(root).ToList();
        Console.WriteLine($"[dedup] Found {files.Count:N0} audio files.");

        // -- Phase (a): exact dupes --------------------------------------------
        Console.WriteLine("[dedup] Checking exact duplicates (size -> SHA-256)...");
        var exactGroups = FindExactDupes(files);

        // -- Phase (b): near dupes ---------------------------------------------
        Console.WriteLine("[dedup] Checking near-duplicates (artist+title+duration)...");
        var nearGroups = FindNearDupes(files);

        // -- Build report ------------------------------------------------------
        long wastedBytes = exactGroups.Sum(g =>
        {
            // Wasted = all copies beyond the first. We assume each copy in the group
            // is the same size (exact dupe), so wasted = (count - 1) x size_of_first.
            var size = new FileInfo(g.FilePaths[0]).Length;
            return size * (g.FilePaths.Count - 1);
        });

        var report = new DedupReport
        {
            Root            = root,
            ScannedFiles    = files.Count,
            ExactDupeSets   = exactGroups.Count,
            ExactDupeFiles  = exactGroups.Sum(g => g.FilePaths.Count),
            WastedBytes     = wastedBytes,
            WastedSizeGb    = wastedBytes / (1024.0 * 1024.0 * 1024.0),
            NearDupeSets    = nearGroups.Count,
            NearDupeFiles   = nearGroups.Sum(g => g.FilePaths.Count),
            ExactGroups     = exactGroups,
            NearGroups      = nearGroups,
        };

        // -- Console summary --------------------------------------------------
        Console.WriteLine();
        Console.WriteLine($"  Files scanned       : {report.ScannedFiles:N0}");
        Console.WriteLine();
        Console.WriteLine($"  Exact duplicate sets: {report.ExactDupeSets:N0}  ({report.ExactDupeFiles:N0} files)");
        Console.WriteLine($"  Wasted space        : {AudioFiles.FormatBytes(report.WastedBytes)}");
        Console.WriteLine();
        Console.WriteLine($"  Near-duplicate sets : {report.NearDupeSets:N0}  ({report.NearDupeFiles:N0} files)");

        if (exactGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Top exact dupe groups (by wasted size):");
            foreach (var g in exactGroups.OrderByDescending(g => g.WastedBytes).Take(10))
            {
                Console.WriteLine($"    {AudioFiles.FormatBytes(g.WastedBytes)} wasted - {g.Key[..Math.Min(16, g.Key.Length)]}...");
                foreach (var p in g.FilePaths.Take(3))
                    Console.WriteLine($"      {p}");
                if (g.FilePaths.Count > 3)
                    Console.WriteLine($"      ... +{g.FilePaths.Count - 3} more");
            }
        }

        if (nearGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Near-dupe groups (sample):");
            foreach (var g in nearGroups.Take(10))
            {
                Console.WriteLine($"    [{g.Key}]");
                foreach (var p in g.FilePaths.Take(4))
                    Console.WriteLine($"      {p}");
                if (g.FilePaths.Count > 4)
                    Console.WriteLine($"      ... +{g.FilePaths.Count - 4} more");
            }
            if (nearGroups.Count > 10)
                Console.WriteLine($"  ... and {nearGroups.Count - 10} more near-dupe groups (see JSON).");
        }

        // -- JSON output ------------------------------------------------------
        if (jsonOut is not null)
        {
            var json = JsonSerializer.Serialize(report, CliJsonContext.Default.DedupReport);
            var dir  = Path.GetDirectoryName(jsonOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(jsonOut, json);
            Console.WriteLine();
            Console.WriteLine($"  JSON written to: {jsonOut}");
        }

        return 0;
    }

    // -- Exact-dupe detection -------------------------------------------------

    /// <summary>
    /// Groups files by size first (cheap), then SHA-256 within each size-collision group.
    /// Returns only groups with 2+ files (true duplicates).
    /// </summary>
    public static List<DedupGroup> FindExactDupes(IReadOnlyList<string> files)
    {
        // 1. Group by file size
        var bySize = new Dictionary<long, List<string>>();
        foreach (var f in files)
        {
            try
            {
                var size = new FileInfo(f).Length;
                if (!bySize.TryGetValue(size, out var bucket))
                    bySize[size] = bucket = [];
                bucket.Add(f);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dedup] WARN: cannot stat {f}: {ex.Message}");
            }
        }

        // 2. Within each size group, hash and group by hash
        var result = new List<DedupGroup>();
        foreach (var (_, bucket) in bySize)
        {
            if (bucket.Count < 2) continue;

            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in bucket)
            {
                try
                {
                    var hash = AudioFiles.Sha256(f);
                    if (!byHash.TryGetValue(hash, out var hashBucket))
                        byHash[hash] = hashBucket = [];
                    hashBucket.Add(f);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[dedup] WARN: cannot hash {f}: {ex.Message}");
                }
            }

            foreach (var (hash, group) in byHash)
            {
                if (group.Count < 2) continue;
                var fileSize   = new FileInfo(group[0]).Length;
                var wasted     = fileSize * (group.Count - 1);
                result.Add(new DedupGroup
                {
                    Key          = hash,
                    FilePaths    = group,
                    WastedBytes  = wasted,
                });
            }
        }

        return [.. result.OrderByDescending(g => g.WastedBytes)];
    }

    // -- Near-dupe detection --------------------------------------------------

    /// <summary>
    /// Groups files by normalised (artist + "|" + title) + duration window.
    /// Reads tags via TagLib# (<see cref="TagLibMetadataReader"/>).
    /// Returns only groups with 2+ files.
    /// </summary>
    public static List<DedupGroup> FindNearDupes(IReadOnlyList<string> files)
    {
        var reader   = new TagLibMetadataReader();
        var records  = new List<(string FilePath, string Key, double DurationSec)>(files.Count);

        foreach (var f in files)
        {
            try
            {
                var meta     = reader.Read(f);
                var artist   = (meta.Artist ?? "").Trim().ToLowerInvariant();
                var title    = (meta.Title  ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(title))
                    continue; // can't near-dedup without any tag info

                var key = $"{artist}|{title}";
                records.Add((f, key, meta.Duration.TotalSeconds));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[dedup] WARN: cannot read tags for {f}: {ex.Message}");
            }
        }

        // Group by (artist+title) key, then sub-group by duration window
        var byKey = records
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2);

        var result = new List<DedupGroup>();
        foreach (var keyGroup in byKey)
        {
            // Within matching (artist+title), find clusters of files with durations within 2s.
            var items = keyGroup.OrderBy(r => r.DurationSec).ToList();
            var visited = new bool[items.Count];

            for (var i = 0; i < items.Count; i++)
            {
                if (visited[i]) continue;
                var cluster = new List<string> { items[i].FilePath };
                visited[i] = true;

                for (var j = i + 1; j < items.Count; j++)
                {
                    if (visited[j]) continue;
                    if (Math.Abs(items[j].DurationSec - items[i].DurationSec) <= NearDupeDurationWindowSec)
                    {
                        cluster.Add(items[j].FilePath);
                        visited[j] = true;
                    }
                }

                if (cluster.Count >= 2)
                {
                    result.Add(new DedupGroup
                    {
                        Key       = keyGroup.Key,
                        FilePaths = cluster,
                    });
                }
            }
        }

        return [.. result.OrderByDescending(g => g.FilePaths.Count)];
    }
}
