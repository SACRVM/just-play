using JustPlay.Cli.Index;
using JustPlay.Cli.Tags;
using JustPlay.Engine.Dtos;
using JustPlay.Metadata;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay tag write --index &lt;path&gt; [--root &lt;dir&gt;] [--apply]</c>
///
/// <para>
/// Batch-writes JustPlay analysis tags from a sidecar index into the audio files:
/// <list type="bullet">
///   <item><b>BPM</b> → standard tempo tag (rounded integer).</item>
///   <item><b>Key</b> → standard key tag (e.g. "Am", via Camelot conversion in the engine).</item>
///   <item><b>Energy</b> → <c>TXXX:ENERGY</c> custom tag (1–10).</item>
///   <item><b>Comment</b> → clean Mixed-In-Key-style <c>8A - Energy 7</c> prepended to the
///     existing comment, preserving user text (idempotent — any prior JP/MIK prefix is stripped).</item>
///   <item><b>Grouping</b> → any legacy JP vibe blob is stripped (the remainder, e.g. catalog
///     codes, is kept). The machine-readable vibe data is NOT written to human-facing tags —
///     it lives in the JUSTPLAY blob (written by <c>promote</c>) + the sidecar index.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Default = DRY-RUN.</b> Prints every planned write without touching any file.
/// Pass <c>--apply</c> to commit the writes.
/// </para>
///
/// <para>
/// Only entries in the index with <see cref="TrackIndexEntry.Success"/> = true are processed.
/// Entries whose file does not exist on disk are skipped with a warning.
/// If <c>--root</c> is supplied only entries under that directory are considered.
/// </para>
/// </summary>
internal static class TagWriteCommand
{
    public static int Run(
        string  indexPath,
        string? root,
        bool    apply,
        bool    noGrouping = false)
    {
        indexPath = Path.GetFullPath(indexPath);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"[tag write] ERROR: index not found: {indexPath}");
            return 1;
        }

        if (root is not null)
            root = Path.GetFullPath(root);

        Console.WriteLine($"[tag write] Index   : {indexPath}");
        if (root is not null)
            Console.WriteLine($"[tag write] Root    : {root}");
        Console.WriteLine($"[tag write] Mode    : {(apply ? "APPLY (files will be written)" : "DRY-RUN (no files touched)")}");
        Console.WriteLine();

        var index = TrackIndex.Load(indexPath);
        Console.WriteLine($"[tag write] Index entries : {index.Entries.Count:N0}");

        // Filter to entries in scope.
        var inScope = index.Entries.Values
            .Where(e => e.Success)
            .Where(e => root is null ||
                        e.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"[tag write] Eligible (success + in root): {inScope.Count:N0}");
        Console.WriteLine();

        if (!apply)
        {
            Console.WriteLine("  (Dry-run — no writes. Pass --apply to commit.)");
            Console.WriteLine();
        }

        using var composer = EngineComposer.Build();

        var processed = 0;
        var skipped   = 0;
        var failed    = 0;

        foreach (var entry in inScope)
        {
            if (!File.Exists(entry.FilePath))
            {
                Console.Error.WriteLine($"  [skip]  (not found) {Path.GetFileName(entry.FilePath)}");
                skipped++;
                continue;
            }

            // Read the existing comment + grouping first so user text is preserved and any
            // legacy JP blob is stripped (idempotent re-write).
            string? existingComment = null, existingGrouping = null;
            try
            {
                var meta = composer.MetadataReader.Read(entry.FilePath);
                existingComment  = meta.Comment;
                existingGrouping = meta.Grouping;
            }
            catch
            {
                // Tag read failure: we'll just overwrite without preserving user text.
            }

            // Comment: clean Mixed-In-Key style "8A - Energy 7" (NOT the machine blob).
            // Build() strips any prior JP/MIK prefix and keeps trailing user text; when neither
            // key nor energy is known it returns null → fall back to stripping only.
            var key = JustPlay.Core.Models.MusicalKey.TryParse(entry.KeyCamelot);
            var newComment = DjCommentBuilder.Build(key, entry.Energy, existingComment)
                             ?? VibeTagEncoder.StripJpPrefix(existingComment);

            // Grouping: never write the vibe blob here anymore — strip any legacy JP block and
            // keep the remainder (catalog/label codes etc.). The full vibe data lives in the
            // JUSTPLAY blob (written by `promote`) + the sidecar index, never in human-facing tags.
            var newGrouping = VibeTagEncoder.StripJpPrefix(existingGrouping);

            // Build the write request. BPM/Energy are written as standard tags;
            // Key is written via Camelot (engine parses it to MusicalKey internally).
            var request = new WriteTagsRequest
            {
                Bpm      = entry.Bpm      is { } bpm ? (double?)Math.Round(bpm) : null,
                Key      = entry.KeyCamelot,
                Energy   = entry.Energy,
                Comment  = newComment,
                Grouping = newGrouping,
            };

            if (apply)
            {
                WriteTagsResult result;
                try
                {
                    result = composer.Engine.WriteTagsAsync(entry.FilePath, request)
                                    .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"  [FAIL]  {Path.GetFileName(entry.FilePath)}: {ex.Message}");
                    failed++;
                    continue;
                }

                if (!result.Success)
                {
                    Console.Error.WriteLine(
                        $"  [FAIL]  {Path.GetFileName(entry.FilePath)}: {result.Error}");
                    failed++;
                    continue;
                }

                Console.WriteLine($"  [ ok ]  {Path.GetFileName(entry.FilePath)}");
                Console.WriteLine($"          comment : {newComment}");
                Console.WriteLine($"          grouping: {(string.IsNullOrEmpty(newGrouping) ? "(cleared)" : newGrouping)}");
            }
            else
            {
                // Dry-run: print plan.
                var bpmStr    = entry.Bpm    is { } b ? $"{(int)Math.Round(b)}" : "(none)";
                var keyStr    = entry.KeyCamelot ?? "(none)";
                var energyStr = entry.Energy is { } en ? en.ToString() : "(none)";
                Console.WriteLine($"  [plan]  {Path.GetFileName(entry.FilePath)}");
                Console.WriteLine($"          bpm={bpmStr}  key={keyStr}  energy={energyStr}");
                Console.WriteLine($"          comment : {newComment}");
                Console.WriteLine($"          grouping: {(string.IsNullOrEmpty(newGrouping) ? "(cleared)" : newGrouping)}");
            }

            processed++;
        }

        Console.WriteLine();
        Console.WriteLine($"[tag write] Done. Processed={processed}  Skipped={skipped}  Failed={failed}");
        if (!apply)
            Console.WriteLine("[tag write] This was a DRY-RUN. Re-run with --apply to write tags.");

        return failed > 0 ? 2 : 0;
    }
}
