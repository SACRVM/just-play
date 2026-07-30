using System.Text.Json;
using JustPlay.Analysis;
using JustPlay.Cli.Reports;
using JustPlay.Core.Models;
using JustPlay.Core.Playlists;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay squeeze --index &lt;v9-index&gt; --keep &lt;N&gt; [--root &lt;dir&gt;] [--playlist &lt;m3u&gt;]
/// [--threshold &lt;0..1&gt;] [--no-sequence] [--out &lt;file.m3u|.json&gt;]</c>
///
/// <para>Phase 2 of the "sort by harmony" north star (see
/// <c>.claude/skills/dj-audio-analysis/references/harmonic-sort-design.md</c>). Compresses a pool
/// of tracks down to the <c>--keep</c> tracks that mix best together, using
/// <see cref="SetSqueezer"/> over the SAME <see cref="MixCompatibility"/> scorer that powers
/// Harmonic Sort. Pure over the existing sidecar index data — no audio decode.</para>
///
/// <para><b>Pool source</b> (first that is given wins): <c>--playlist</c> (the tracks in an .m3u,
/// matched to the index by filename) &gt; <c>--root</c> (index entries whose path is under the
/// folder) &gt; the whole index.</para>
///
/// <para><b>Default = DRY-RUN.</b> Nothing is written unless <c>--out</c> is given: an .m3u/.m3u8
/// gets the kept (sequenced) set with portable paths; a .json gets a <see cref="SqueezeReport"/>.</para>
///
/// <para>⚠ The sidecar index stores BPM/Camelot-key/energy but NOT the BeatFingerprint, so the
/// Beat axis is excluded and the other compat weights renormalise (see the design doc).</para>
/// </summary>
internal static class SqueezeCommand
{
    public static int Run(
        string  indexPath,
        int     keep,
        string? root,
        string? playlist,
        double  threshold,
        bool    sequence,
        string? outPath)
    {
        indexPath = Path.GetFullPath(indexPath);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"[squeeze] ERROR: index not found: {indexPath}");
            return 1;
        }
        if (keep < 1)
        {
            Console.Error.WriteLine("[squeeze] ERROR: --keep must be >= 1.");
            return 1;
        }
        threshold = Math.Clamp(threshold, 0.0, 1.0);

        Console.WriteLine($"[squeeze] Index     : {indexPath}");
        Console.WriteLine($"[squeeze] Keep      : {keep}");
        Console.WriteLine($"[squeeze] Threshold : {threshold:0.00}");
        Console.WriteLine($"[squeeze] Sequence  : {(sequence ? "yes (HarmonicSequencer)" : "no (greedy order)")}");

        var index = TrackIndex.Load(indexPath);

        // ── Build the pool of (path, entry) from the chosen source ───────────────
        List<(string Path, TrackIndexEntry Entry)> pool;
        if (playlist is not null)
        {
            playlist = Path.GetFullPath(playlist);
            Console.WriteLine($"[squeeze] Pool      : playlist {playlist}");
            pool = PoolFromPlaylist(index, playlist);
        }
        else if (root is not null)
        {
            root = Path.GetFullPath(root);
            Console.WriteLine($"[squeeze] Pool      : index entries under {root}");
            pool = PoolFromRoot(index, root);
        }
        else
        {
            Console.WriteLine("[squeeze] Pool      : whole index");
            pool = PoolFromIndex(index);
        }

        Console.WriteLine($"[squeeze] Pool size : {pool.Count:N0}");
        Console.WriteLine();

        if (pool.Count == 0)
        {
            Console.Error.WriteLine("[squeeze] ERROR: pool is empty — nothing to squeeze.");
            return 1;
        }

        // ── Squeeze ──────────────────────────────────────────────────────────────
        var features = pool.Select(p => (TrackFeatures?)ToFeatures(p.Entry)).ToList();
        var result   = SetSqueezer.Squeeze(features, keep, threshold, sequence);

        var kept    = result.KeptIndices.Select(i => pool[i]).ToList();
        var dropped = result.DroppedIndices.Select(i => pool[i]).ToList();
        var seed    = result.SeedIndex >= 0 ? pool[result.SeedIndex] : ((string Path, TrackIndexEntry Entry)?)null;

        // ── Print summary ─────────────────────────────────────────────────────────
        Console.WriteLine($"[squeeze] Kept            : {kept.Count:N0}");
        Console.WriteLine($"[squeeze] Dropped         : {dropped.Count:N0}  (incl. {result.UnanalyzedDropped:N0} unanalysed)");
        Console.WriteLine($"[squeeze] Coherent set    : {result.CoherentCount:N0} of {result.RequestedKeep:N0} requested");
        Console.WriteLine($"[squeeze] Cohesion        : mean {result.MeanCohesion:0.000}, weakest pair {result.MinCohesion:0.000}");
        if (seed is { } s)
            Console.WriteLine($"[squeeze] Seed (central)  : {Path.GetFileName(s.Path)}");
        Console.WriteLine();

        if (!result.EnoughCoherent)
            Console.WriteLine($"[squeeze] ⚠ {result.Message}");
        else
            Console.WriteLine($"[squeeze] {result.Message}");
        Console.WriteLine();

        Console.WriteLine("[squeeze] KEPT (play order):");
        for (var i = 0; i < kept.Count; i++)
            Console.WriteLine($"  {i + 1,3}. {Describe(kept[i].Entry, kept[i].Path)}");

        if (dropped.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("[squeeze] DROPPED:");
            foreach (var d in dropped)
                Console.WriteLine($"       - {Describe(d.Entry, d.Path)}");
        }

        // ── Optional output ───────────────────────────────────────────────────────
        if (outPath is not null)
        {
            outPath = Path.GetFullPath(outPath);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (outPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                WriteJson(outPath, indexPath, root, playlist, pool.Count, threshold, result, kept, dropped, seed);
            else
                WriteM3u(outPath, kept);

            Console.WriteLine();
            Console.WriteLine($"[squeeze] Written: {outPath}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("[squeeze] DRY-RUN — pass --out <file.m3u|.json> to write the kept set.");
        }

        return 0;
    }

    // ── Pool sources ─────────────────────────────────────────────────────────────

    private static List<(string, TrackIndexEntry)> PoolFromIndex(TrackIndex index) =>
        index.Entries.Values
            .Where(e => e.Success)
            .Select(e => (e.FilePath, e))
            .ToList();

    private static List<(string, TrackIndexEntry)> PoolFromRoot(TrackIndex index, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return index.Entries.Values
            .Where(e => e.Success)
            .Where(e => e.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.FilePath, root, StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.FilePath, e))
            .ToList();
    }

    /// <summary>Pool from a playlist's tracks, matched to the index by filename (robust after the
    /// SETS→GENRES move, mirroring how promote matches). Tracks not in the index are skipped with
    /// a warning — never silently lost.</summary>
    private static List<(string, TrackIndexEntry)> PoolFromPlaylist(TrackIndex index, string playlistPath)
    {
        var byFileName = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.Entries.Values)
        {
            if (!e.Success) continue;
            byFileName.TryAdd(Path.GetFileName(e.FilePath), e);
        }

        var pool = new List<(string, TrackIndexEntry)>();
        foreach (var trackPath in M3uPlaylist.ReadPaths(playlistPath))
        {
            var fname = Path.GetFileName(trackPath);
            if (byFileName.TryGetValue(fname, out var entry))
                pool.Add((trackPath, entry));   // use the real on-disk path for output
            else
                Console.Error.WriteLine($"  [skip] not-in-index  {fname}");
        }
        return pool;
    }

    // ── Index entry → compat features ──────────────────────────────────────────────

    /// <summary>
    /// Convert a sidecar index entry to <see cref="TrackFeatures"/>. BPM/Key/Energy come from the
    /// index; the BeatFingerprint is NOT stored in the sidecar → null (Beat axis renormalised out,
    /// see the design doc). Mirrors PromoteCommand.BuildAnalysisResult's key parsing.
    /// </summary>
    private static TrackFeatures ToFeatures(TrackIndexEntry e)
    {
        MusicalKey? key = null;
        if (e.KeyCamelot is { } cam) key = MusicalKey.TryParse(cam);
        if (key is null && e.KeyName is { } name) key = MusicalKey.TryParse(name);
        return new TrackFeatures(e.Bpm, key, e.Energy, Fingerprint: null);
    }

    // ── Output writers ──────────────────────────────────────────────────────────────

    private static void WriteM3u(string outPath, List<(string Path, TrackIndexEntry Entry)> kept)
    {
        var entries = kept.Select(k => new M3uPlaylist.Entry(
            k.Path,
            k.Entry.DurationSec > 0 ? TimeSpan.FromSeconds(k.Entry.DurationSec) : null,
            BuildTitle(k.Entry, k.Path)));
        M3uPlaylist.Write(outPath, entries);
    }

    private static void WriteJson(
        string outPath, string indexPath, string? root, string? playlist, int poolSize,
        double threshold, SqueezeResult result,
        List<(string Path, TrackIndexEntry Entry)> kept,
        List<(string Path, TrackIndexEntry Entry)> dropped,
        (string Path, TrackIndexEntry Entry)? seed)
    {
        var report = new SqueezeReport
        {
            IndexPath          = indexPath,
            Root               = root,
            Playlist           = playlist,
            PoolSize           = poolSize,
            RequestedKeep      = result.RequestedKeep,
            CoherenceThreshold = threshold,
            KeptCount          = kept.Count,
            DroppedCount       = dropped.Count,
            UnanalyzedDropped  = result.UnanalyzedDropped,
            CoherentCount      = result.CoherentCount,
            EnoughCoherent     = result.EnoughCoherent,
            MeanCohesion       = result.MeanCohesion,
            MinCohesion        = result.MinCohesion,
            SeedPath           = seed?.Path,
            Message            = result.Message,
            Kept               = kept.Select(k => k.Path).ToList(),
            Dropped            = dropped.Select(d => d.Path).ToList(),
        };
        var json = JsonSerializer.Serialize(report, CliJsonContext.Default.SqueezeReport);
        File.WriteAllText(outPath, json);
    }

    // ── Display helpers ───────────────────────────────────────────────────────────

    private static string Describe(TrackIndexEntry e, string path)
    {
        var name = Path.GetFileName(path);
        var bpm  = e.Bpm is { } b ? $"{(int)Math.Round(b)}" : "—";
        var key  = e.KeyCamelot ?? e.KeyName ?? "—";
        var en   = e.Energy is { } x ? $"E{x}" : "E—";
        return $"{name,-60}  {bpm,4} bpm  {key,-4} {en}";
    }

    private static string BuildTitle(TrackIndexEntry e, string path)
    {
        if (!string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
            return $"{e.Artist} - {e.Title}";
        if (!string.IsNullOrWhiteSpace(e.Title))
            return e.Title!;
        return Path.GetFileNameWithoutExtension(path);
    }
}
