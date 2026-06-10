using JustPlay.Cli.Index;
using JustPlay.Core.Models;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay analyze &lt;root&gt; --index &lt;path&gt; [--threads N] [--limit N]</c>
///
/// PHASE 1 — the heavy resumable analysis pass.
/// For each audio file under <paramref name="root"/>:
///   1. Compute a SHA-256 content hash (cheap — no decode needed).
///   2. If an up-to-date entry already exists in the sidecar index, skip it.
///   3. Read tag metadata (artist, title, duration, …) via IMetadataReader.
///   4. Run the full DSP pipeline (BPM → key → energy → loudness → beat fingerprint
///      + RhythmPattern if available) via ITrackAnalysisService.
///   5. Write (or update) the entry in the in-memory index.
///   6. Flush the index to disk every <see cref="FlushEveryN"/> files for durability.
///
/// READ-ONLY on the audio library — no tags are written tonight.
/// The sidecar index file is the ONLY output, written atomically via a .tmp rename.
/// </summary>
internal static class AnalyzeCommand
{
    private const int FlushEveryN = 50;

    public static int Run(
        string root,
        string indexPath,
        int threads,
        int limit)
    {
        root      = Path.GetFullPath(root);
        indexPath = Path.GetFullPath(indexPath);

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[analyze] ERROR: directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[analyze] Root      : {root}");
        Console.WriteLine($"[analyze] Index     : {indexPath}");
        Console.WriteLine($"[analyze] Threads   : {threads}");
        if (limit < int.MaxValue)
            Console.WriteLine($"[analyze] Limit     : {limit}");

        // Load existing index for resumability
        var index = TrackIndex.Load(indexPath);
        Console.WriteLine($"[analyze] Existing entries: {index.Entries.Count:N0}");

        // Enumerate files
        var allFiles = AudioFiles.Enumerate(root).ToList();
        if (limit < allFiles.Count)
            allFiles = allFiles.Take(limit).ToList();

        Console.WriteLine($"[analyze] Files to process: {allFiles.Count:N0}");

        // Count how many we can skip upfront (without reading content hash yet)
        // We'll do the real skip check after hashing inside the loop.

        // ── Compose the analysis stack once ─────────────────────────────────
        using var composer = EngineComposer.Build();

        var startTime      = DateTime.UtcNow;
        var processed      = 0;
        var skipped        = 0;
        var failed         = 0;
        var since          = 0; // files since last flush
        var lockObj        = new object();

        // For thread-safe progress reporting we use a simple shared counter.
        // The analysis itself is parallelised; the index write is serialised.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
        };

        Parallel.ForEach(allFiles, parallelOptions, filePath =>
        {
            try
            {
                // 1. Hash first — cheap, used for resume check
                string hash;
                try
                {
                    hash = AudioFiles.Sha256(filePath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[analyze] WARN: cannot hash {filePath}: {ex.Message}");
                    Interlocked.Increment(ref failed);
                    return;
                }

                // 2. Resume check
                lock (lockObj)
                {
                    if (index.IsUpToDate(filePath, hash))
                    {
                        Interlocked.Increment(ref skipped);
                        PrintProgress(++processed, allFiles.Count, startTime, skipped, failed, "[skip]");
                        return;
                    }
                }

                // 3. Read tag metadata (fast, no decode)
                string?  title      = null, artist = null, album = null, genre = null;
                uint?    year       = null;
                double   durationSec = 0;
                int?     bitrateKbps = null;
                try
                {
                    var meta = composer.MetadataReader.Read(filePath);
                    title       = meta.Title;
                    artist      = meta.Artist;
                    album       = meta.Album;
                    genre       = meta.Genre;
                    year        = meta.Year;
                    durationSec = meta.Duration.TotalSeconds;
                    bitrateKbps = meta.Bitrate;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[analyze] WARN: tag read failed for {Path.GetFileName(filePath)}: {ex.Message}");
                    // Continue — DSP can still run without tags
                }

                // 4. DSP analysis
                AnalysisResult? result = null;
                string?         error  = null;
                try
                {
                    using var cts = new CancellationTokenSource();
                    result = composer.AnalysisService.AnalyzeAsync(filePath, null, cts.Token)
                                     .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Console.Error.WriteLine($"[analyze] WARN: analysis failed for {Path.GetFileName(filePath)}: {ex.Message}");
                    Interlocked.Increment(ref failed);
                }

                // 5. Build index entry (read Rhythm defensively — may be null)
                var rhythm = result?.Rhythm;
                var fp     = result?.Fingerprint;

                var fileSize = new FileInfo(filePath).Length;
                var entry = new TrackIndexEntry
                {
                    FilePath         = filePath,
                    ContentHash      = hash,
                    AnalysedAt       = DateTime.UtcNow.ToString("o"),
                    DetectionVersion = TrackIndex.CurrentDetectionVersion,
                    FileSizeBytes    = fileSize,

                    Title       = title,
                    Artist      = artist,
                    Album       = album,
                    Genre       = genre,
                    Year        = year,
                    DurationSec = durationSec,
                    BitrateKbps = bitrateKbps,

                    Success      = result is not null && error is null,
                    Error        = error,
                    Bpm          = result?.Bpm,
                    KeyName      = result?.Key?.Name,
                    KeyCamelot   = result?.Key?.Camelot,
                    KeyConfidence= result?.KeyConfidence,
                    Energy       = result?.Energy,
                    LoudnessLufs = result?.LoudnessLufs,
                    ReplayGainDb = result?.ReplayGainDb,
                    Peak         = result?.Peak,
                    Danceability = fp?.Danceability,

                    // RhythmPattern fields — null until other agent's DSP lands
                    BeatType      = rhythm?.BeatType,
                    FourOnFloor   = rhythm?.FourOnFloor,
                    OffbeatEnergy = rhythm?.OffbeatEnergy,
                    Swing         = rhythm?.Swing,
                    Syncopation   = rhythm?.Syncopation,
                    HalfTimeFeel  = rhythm?.HalfTimeFeel,

                    // Vibe quartet + fatigue flag (v8+)
                    RawEnergyScore   = result?.RawEnergyScore,
                    SpectralFlatness = result?.SpectralFlatness,
                    Harshness        = result?.Harshness,
                    BassPunch        = result?.BassPunch,
                    BassGroove       = result?.BassGroove,
                    Dark             = result?.Dark,
                    Hypnotic         = result?.Hypnotic,
                };

                // 6. Write to index (serialised)
                lock (lockObj)
                {
                    index.Entries[filePath] = entry;
                    since++;
                    var n = ++processed;
                    PrintProgress(n, allFiles.Count, startTime, skipped, failed,
                        result is not null && error is null ? "  ok " : " FAIL");

                    // Flush periodically for durability
                    if (since >= FlushEveryN)
                    {
                        index.Save(indexPath);
                        since = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[analyze] ERROR (unexpected): {filePath}: {ex.Message}");
                Interlocked.Increment(ref failed);
            }
        });

        // Final save
        index.Save(indexPath);

        Console.WriteLine();
        Console.WriteLine($"[analyze] Done. Processed={processed}  Skipped={skipped}  Failed={failed}");
        Console.WriteLine($"[analyze] Index entries total: {index.Entries.Count:N0}");
        Console.WriteLine($"[analyze] Index written to: {indexPath}");

        return failed > 0 ? 2 : 0; // exit 2 = partial failure (some files failed)
    }

    private static void PrintProgress(int n, int total, DateTime start, int skipped, int failed, string status)
    {
        var pct     = total > 0 ? (double)n / total * 100.0 : 0;
        var elapsed = DateTime.UtcNow - start;
        var eta     = n > 0 && total > n
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds / n * (total - n))
            : TimeSpan.Zero;
        Console.Write($"\r  {status} {n,6}/{total,-6} ({pct,5:F1}%)  " +
                      $"skip={skipped,-5} fail={failed,-5} " +
                      $"elapsed={elapsed:hh\\:mm\\:ss}  ETA={eta:hh\\:mm\\:ss}   ");
    }
}
