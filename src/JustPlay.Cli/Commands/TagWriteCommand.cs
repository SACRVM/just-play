using JustPlay.Cli.Index;
using JustPlay.Cli.Tags;
using JustPlay.Engine.Dtos;

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
///   <item><b>Comment</b> → JP vibe string prepended to the existing comment (idempotent):
///     <c>JP|E{n}|K{c}|bpm{b}|gc.{NN}|gr.{NN}|pu.{NN}|hy.{NN}|dk.{NN}|hx.{NN} | {user text}</c></item>
///   <item><b>Grouping</b> → pure JP vibe string in the Content Group tag (ID3v2 TIT1 / FLAC GROUPING),
///     where DJ software (Traktor, Rekordbox) often surfaces a searchable Grouping column.</item>
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

            // Build the vibe tag string.
            var vibeTag = VibeTagEncoder.Encode(
                energy:         entry.Energy,
                camelot:        entry.KeyCamelot,
                bpm:            entry.Bpm,
                gridConfidence: entry.GridConfidence,
                bassGroove:     entry.BassGroove,
                bassPunch:      entry.BassPunch,
                hypnotic:       entry.Hypnotic,
                dark:           entry.Dark,
                harshness:      entry.Harshness);

            // For the Comment: read the existing comment first so user text is preserved.
            string? existingComment = null;
            try
            {
                var meta = composer.MetadataReader.Read(entry.FilePath);
                existingComment = meta.Comment;
            }
            catch
            {
                // Tag read failure: we'll just overwrite without preserving user text.
            }

            var newComment = VibeTagEncoder.BuildComment(vibeTag, existingComment);

            // Build the write request. BPM/Energy are written as standard tags;
            // Key is written via Camelot (engine parses it to MusicalKey internally).
            var request = new WriteTagsRequest
            {
                Bpm      = entry.Bpm      is { } bpm ? (double?)Math.Round(bpm) : null,
                Key      = entry.KeyCamelot,
                Energy   = entry.Energy,
                Comment  = newComment,
                Grouping = noGrouping ? null : vibeTag,   // --no-grouping preserves catalog/label codes
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
                Console.WriteLine($"          grouping: {vibeTag}");
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
                Console.WriteLine($"          grouping: {vibeTag}");
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
