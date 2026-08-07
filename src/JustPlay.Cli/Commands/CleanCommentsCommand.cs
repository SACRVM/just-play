using System.Text.Json;
using JustPlay.Cli.Tags;
using JustPlay.Core.Models;
using JustPlay.Core.Playlists;
using JustPlay.Engine.Dtos;
using JustPlay.Metadata;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay tag clean --root &lt;dir&gt; [--apply] [--backup-dir &lt;dir&gt;]</c>
///
/// <para>
/// One-shot cleanup: replace the cryptic machine-readable <c>JP|E7|K8A|bpm140|gc.57|...</c>
/// vibe blob that an early "tag write" batch left in the human-facing Comment (and Grouping)
/// tags with the clean, Mixed-In-Key-style <c>8A - Energy 7</c> comment - and remove the blob
/// from Grouping entirely.
/// </para>
///
/// <para>
/// <b>Why this exists / why it enumerates by root (not the index):</b> the v9 SETS index paths
/// are stale (files moved SETS -> GENRES), so an index-driven pass can't find the files. This
/// command walks the GENRES root directly and uses each file's OWN tags - it needs no index.
/// Key + Energy for the clean comment come from the standard TKEY/ENERGY tags (written by
/// <see cref="PromoteCommand"/>), falling back to the JUSTPLAY blob's detected values.
/// </para>
///
/// <para>
/// <b>No data loss:</b> the full vibe data still lives in the JUSTPLAY blob + the sidecar index.
/// We only strip it from the two human-facing fields. Standard tags (BPM/Key/Energy) and the
/// JUSTPLAY blob are left untouched.
/// </para>
///
/// <para>
/// <b>Idempotent:</b> <see cref="DjCommentBuilder.Strip"/> removes any existing JP or MIK prefix
/// before the clean segment is prepended, so re-running never doubles or corrupts anything.
/// Files whose Comment + Grouping are already clean are skipped (no write).
/// </para>
///
/// <para><b>Default = DRY-RUN.</b> Pass <c>--apply</c> to commit writes.</para>
/// </summary>
internal static class CleanCommentsCommand
{
    private const string BackupFileName = "comment-clean-backup.json";

    /// <summary>Clean every audio file under a directory tree (recursive).</summary>
    public static int RunRoot(string root, bool apply, string? backupDir, bool force = false)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[tag clean] ERROR: root directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[tag clean] Root : {root}");
        var files = AudioFiles.Enumerate(root).ToList();
        return Clean(files, $"under root: {files.Count:N0}", backupDir ?? root, apply, backupDir, force);
    }

    /// <summary>Clean only the tracks referenced by an .m3u/.m3u8 playlist (paths resolved
    /// against the playlist's own folder). Use this to re-tag exactly one set.</summary>
    public static int RunPlaylist(string playlistPath, bool apply, string? backupDir, bool force = false)
    {
        playlistPath = Path.GetFullPath(playlistPath);
        if (!File.Exists(playlistPath))
        {
            Console.Error.WriteLine($"[tag clean] ERROR: playlist not found: {playlistPath}");
            return 1;
        }

        Console.WriteLine($"[tag clean] Playlist : {playlistPath}");
        var files = M3uPlaylist.ReadPaths(playlistPath);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("[tag clean] ERROR: playlist references no existing audio files.");
            return 1;
        }

        var defaultBackupDir = Path.GetDirectoryName(playlistPath) ?? ".";
        return Clean(files, $"in playlist: {files.Count:N0}", defaultBackupDir, apply, backupDir, force);
    }

    private static int Clean(
        IReadOnlyList<string> files,
        string sourceLabel,
        string defaultBackupDir,
        bool apply,
        string? backupDir,
        bool force = false)
    {
        Console.WriteLine($"[tag clean] Mode : {(apply ? "APPLY (files will be written)" : "DRY-RUN (no files touched)")}{(force ? " [FORCE: rewrite even when the visible comment already looks clean - collapses hidden COMM frames]" : "")}");
        Console.WriteLine();

        Console.WriteLine($"[tag clean] Audio files {sourceLabel}");
        Console.WriteLine();

        var backupPath   = Path.Combine(backupDir ?? defaultBackupDir, BackupFileName);
        var backupExists = File.Exists(backupPath);
        if (apply)
            Console.WriteLine(backupExists
                ? $"[tag clean] Backup already exists at {backupPath} - will NOT overwrite."
                : $"[tag clean] Backup will be written to: {backupPath}");
        Console.WriteLine();

        using var composer = EngineComposer.Build();

        var changed   = 0;
        var unchanged = 0;
        var failed    = 0;
        var samples   = 0;
        var backupRecords = new List<CleanBackupEntry>();

        foreach (var filePath in files)
        {
            TrackMetadata meta;
            try
            {
                meta = composer.MetadataReader.Read(filePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [FAIL] tag read  {Path.GetFileName(filePath)}: {ex.Message}");
                failed++;
                continue;
            }

            // Key + Energy for the clean MIK comment: standard tags first, blob as fallback.
            var key = MusicalKey.TryParse(meta.TaggedKey)
                      ?? meta.StoredAnalysis?.Detected.Key;
            var energy = meta.TaggedEnergy
                      ?? meta.StoredAnalysis?.Detected.Energy;

            // Comment: rebuild clean. Build() strips any JP/MIK prefix and prepends "8A - Energy 7".
            // When neither key nor energy is known (un-promoted new ingest), Build() returns null ->
            // fall back to stripping ONLY the JP blob, keeping whatever follows (e.g. an older MIK
            // segment or user text). We never delete the file's best-available key/energy info.
            var newComment = DjCommentBuilder.Build(key, energy, meta.Comment)
                             ?? VibeTagEncoder.StripJpPrefix(meta.Comment);

            // Grouping: the blob is removed entirely (user's decision). Strip the JP block,
            // keep any non-JP remainder (it was written as a pure blob, so this is normally "").
            var newGrouping = VibeTagEncoder.StripJpPrefix(meta.Grouping);

            var commentChanged  = !SameTag(meta.Comment, newComment);
            var groupingChanged = !SameTag(meta.Grouping, newGrouping);

            // With --force we rewrite even when the visible comment already matches, so the
            // writer's RemoveFrames+single-write collapses any HIDDEN second COMM frame that
            // TagLib# never surfaced (it returns only the preferred frame). Only meaningful
            // when there is a clean comment value to write.
            var forceRewrite = force && newComment is not null;
            if (!commentChanged && !groupingChanged && !forceRewrite)
            {
                unchanged++;
                continue;
            }

            // Show a handful of before/after samples so the dry-run is verifiable at a glance.
            if (samples < 12)
            {
                Console.WriteLine($"  {Path.GetFileName(filePath)}");
                if (commentChanged)
                    Console.WriteLine($"      comment  : {Show(meta.Comment)}  ->  {Show(newComment)}");
                if (groupingChanged)
                    Console.WriteLine($"      grouping : {Show(meta.Grouping)}  ->  {Show(newGrouping)}");
                samples++;
                if (samples == 12)
                    Console.WriteLine("      ... (further changes not printed) ...");
            }

            backupRecords.Add(new CleanBackupEntry
            {
                FilePath = filePath,
                Comment  = meta.Comment,
                Grouping = meta.Grouping,
            });

            if (apply)
            {
                // Only touch the two human-facing fields. null = leave untouched;
                // "" = clear. BPM/Key/Energy/blob stay exactly as they are.
                var request = new WriteTagsRequest
                {
                    Comment  = (commentChanged || forceRewrite) ? newComment  : null,
                    Grouping = groupingChanged ? newGrouping : null,
                };

                WriteTagsResult result;
                try
                {
                    result = composer.Engine.WriteTagsAsync(filePath, request)
                                     .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [FAIL] write     {Path.GetFileName(filePath)}: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!result.Success)
                {
                    Console.Error.WriteLine($"  [FAIL] write     {Path.GetFileName(filePath)}: {result.Error}");
                    failed++;
                    continue;
                }
            }

            changed++;
        }

        // Save backup (once, before the writes are lost) - only when applying.
        if (apply && backupRecords.Count > 0 && !backupExists)
        {
            try
            {
                var json = JsonSerializer.Serialize(
                    backupRecords, CleanBackupJsonContext.Default.ListCleanBackupEntry);
                var tmp = backupPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, backupPath, overwrite: true);
                Console.WriteLine();
                Console.WriteLine($"[tag clean] Backup written: {backupPath}  ({backupRecords.Count:N0} entries)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tag clean] WARN: backup write failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("[tag clean] Done.");
        Console.WriteLine($"  {(apply ? "Cleaned" : "Would clean"),-12} : {changed,6:N0}");
        Console.WriteLine($"  Already clean : {unchanged,6:N0}");
        Console.WriteLine($"  Failed        : {failed,6:N0}");
        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("[tag clean] DRY-RUN only - no files were written. Re-run with --apply to commit.");
        }

        return failed > 0 ? 2 : 0;
    }

    /// <summary>True when two tag values are equivalent, treating null and "" as the same.</summary>
    private static bool SameTag(string? a, string? b)
        => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    /// <summary>Render a tag value for the console (quote it, mark empty/cleared).</summary>
    private static string Show(string? s)
        => string.IsNullOrEmpty(s) ? "(empty)" : $"\"{s}\"";
}

// --- Backup DTO + JSON context ------------------------------------------------

/// <summary>One entry in the comment-clean backup: the pre-clean Comment + Grouping.</summary>
internal sealed record CleanBackupEntry
{
    public required string FilePath { get; init; }
    public string? Comment  { get; init; }
    public string? Grouping { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<CleanBackupEntry>))]
internal sealed partial class CleanBackupJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
