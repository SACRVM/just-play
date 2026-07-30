using System.Security.Cryptography;
using System.Text.Json;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay promote --index &lt;v9-index&gt; --root &lt;genres-root&gt; [--apply] [--no-grouping]</c>
///
/// <para>
/// N15: Make JustPlay's analysis the AUTHORITATIVE TRUTH in every GENRES file.
/// Eliminates key-conflict dots (the red dot on the KEY cell in the app) by ensuring
/// every file has a JUSTPLAY blob with <see cref="FieldDecision.Applied"/> decisions
/// and matching standard tags (TKEY/TBPM/ENERGY).
/// </para>
///
/// <para>
/// <b>Problem being solved:</b>
/// <see cref="TrackViewModel.KeyConflict"/> fires when
/// <c>KeyDecision == Pending AND DetectedKey != null AND TKEY is set AND TKEY.Camelot != DetectedKey.Camelot</c>.
/// After N12 wrote TKEY from the v9 index but NOT the JUSTPLAY blob, ~89 % of files
/// have TKEY but no blob → in-session analysis creates the conflict dot.
/// </para>
///
/// <para>
/// <b>Strategy (no re-DSP):</b>
/// <list type="number">
///   <item>Files that ALREADY have all decisions = Applied → skip (already correct).</item>
///   <item>Files with a blob but not fully Applied → update decisions to Applied, re-stamp
///     the blob with CurrentVersion, leave Detected values untouched.</item>
///   <item>Files with NO blob → compute SHA-256, look up in the v9 SETS index by hash,
///     build a full <see cref="TrackAnalysisState"/> from the index entry, stamp
///     decisions = Applied, write TKEY + blob in one TagLib# save.</item>
///   <item>Files not found in the index → skip with a warning (needs manual analysis).</item>
/// </list>
/// </para>
///
/// <para><b>Default = DRY-RUN.</b> Pass <c>--apply</c> to commit writes.</para>
/// </summary>
internal static class PromoteCommand
{
    // ─── Backup JSON: (filePath, tkey, energy, blobJson) before first write ───
    private const string BackupFileName = "n15-promote-backup.json";

    public static int Run(
        string  indexPath,
        string  root,
        bool    apply,
        bool    noGrouping,
        string? backupDir,
        // N21 CLEAN SLATE: when true, re-stamp TKEY/TBPM/ENERGY from blob even when
        // decisions are already Applied. Needed because N15 left some TKEY values stale
        // (applied the blob but did not fix TKEY for some files). --retag corrects those.
        bool    retag = false,
        // Path to a text file with one audio-file path per line: promote exactly these
        // files (each must live under root) instead of walking the whole root. Ingest
        // scoping — new files only, the rest of the library is out of reach by construction.
        string? filesListPath = null)
    {
        indexPath = Path.GetFullPath(indexPath);
        root      = Path.GetFullPath(root);

        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"[promote] ERROR: index not found: {indexPath}");
            return 1;
        }
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[promote] ERROR: root directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[promote] Index   : {indexPath}");
        Console.WriteLine($"[promote] Root    : {root}");
        Console.WriteLine($"[promote] Mode    : {(apply ? "APPLY (files will be written)" : "DRY-RUN (no files touched)")}");
        if (retag)
            Console.WriteLine($"[promote] --retag : ON (will re-stamp TKEY/TBPM/ENERGY even for Applied files)");
        Console.WriteLine();

        // ─── Load index, build lookup tables ─────────────────────────────
        Console.WriteLine("[promote] Loading index...");
        var index = TrackIndex.Load(indexPath);
        Console.WriteLine($"[promote] Index entries: {index.Entries.Count:N0}");

        // Primary: hash → entry (exact match, robust to rename; fails after tag-writes change bytes).
        // Fallback: filename (case-insensitive) → entry (the N12 tag-writes changed hashes,
        //           so hash-matching fails for all tagged files; filenames are stable).
        var byHash     = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var fileNameCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in index.Entries.Values)
        {
            if (!e.Success) continue;

            if (!string.IsNullOrEmpty(e.ContentHash))
                byHash.TryAdd(e.ContentHash, e);

            var fname = Path.GetFileName(e.FilePath);
            if (!byFileName.TryAdd(fname, e))
                fileNameCollisions.Add(fname);  // track duplicates; first-in wins
        }

        Console.WriteLine($"[promote] Hash-lookup entries    : {byHash.Count:N0}");
        Console.WriteLine($"[promote] Filename-lookup entries: {byFileName.Count:N0}");
        if (fileNameCollisions.Count > 0)
            Console.WriteLine($"[promote] Filename collisions (first entry wins): {string.Join(", ", fileNameCollisions)}");
        Console.WriteLine();

        // ─── Enumerate GENRES audio files (or take the explicit --files scope) ───
        List<string> files;
        if (filesListPath is not null)
        {
            files = File.ReadAllLines(filesListPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(Path.GetFullPath)
                .ToList();
            var outside = files.Where(f => !f.StartsWith(root + Path.DirectorySeparatorChar,
                                                         StringComparison.OrdinalIgnoreCase)).ToList();
            if (outside.Count > 0)
            {
                Console.Error.WriteLine($"[promote] ERROR: {outside.Count} --files entries are outside root " +
                                        $"(first: {outside[0]}) — refusing to run.");
                return 1;
            }
            var missing = files.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"[promote] ERROR: {missing.Count} --files entries do not exist " +
                                        $"(first: {missing[0]}) — refusing to run.");
                return 1;
            }
            Console.WriteLine($"[promote] Scoped to --files list: {files.Count:N0} files");
        }
        else
        {
            files = AudioFiles.Enumerate(root).ToList();
            Console.WriteLine($"[promote] Audio files under root: {files.Count:N0}");
        }
        Console.WriteLine();

        // ─── Backup: record current tags before any write ─────────────────
        //   N12 may already have left a backup. This command's backup is separate
        //   and covers the CURRENT state (post-N12) as the undo source for this run.
        var backupPath = Path.Combine(backupDir ?? Path.GetDirectoryName(indexPath)!, BackupFileName);
        var backupExists = File.Exists(backupPath);
        if (backupExists)
            Console.WriteLine($"[promote] Backup already exists at {backupPath} — will NOT overwrite.");
        else
            Console.WriteLine($"[promote] Backup will be written to: {backupPath}");
        Console.WriteLine();

        using var composer = EngineComposer.Build();

        var processed      = 0;
        var skipped        = 0;
        var notInIndex     = 0;
        var failed         = 0;
        var alreadyDone    = 0;
        var matchedByHash  = 0;
        var matchedByName  = 0;

        // Collect backup records (only for files we'll actually write).
        var backupRecords = new List<PromoteBackupEntry>();

        foreach (var filePath in files)
        {
            // ── 1. Read current metadata (tag-I/O, no decode) ─────────────
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

            var existingBlob = meta.StoredAnalysis;

            // ── 2. Check if already fully Applied ──────────────────────────
            // N21 --retag: even if decisions=Applied, re-stamp the standard tags
            // (TKEY/TBPM/ENERGY) from the blob to fix the ~4% of files where N15
            // left a stale TKEY. In retag mode we never skip Applied files entirely;
            // we still use the fast path (no index lookup needed) because the blob
            // already has the correct Detected values.
            if (IsFullyApplied(existingBlob) && !retag)
            {
                alreadyDone++;
                continue; // already correct, no write needed
            }

            // ── 3. Determine the new state ─────────────────────────────────
            TrackAnalysisState? newState;
            string? detectedKeyStr = null; // Camelot string, for display

            if (existingBlob is not null)
            {
                // Has blob but decisions not fully Applied (or --retag forces re-stamp).
                // N21 --retag path: blob is Applied but TKEY may be stale. Check if
                // TKEY already matches the blob key — if yes, skip the write to avoid
                // touching files unnecessarily (only ~4% of Applied files need this fix).
                if (retag && IsFullyApplied(existingBlob))
                {
                    var blobKey    = existingBlob.Detected.Key;
                    var currentKey = MusicalKey.TryParse(meta.TaggedKey);
                    var blobBpm    = existingBlob.Detected.Bpm is { } b
                                     ? (uint)Math.Clamp(Math.Round(b), 0, 999) : 0u;
                    var currentBpm = meta.TaggedBpm is { } tb
                                     ? (uint)Math.Clamp(Math.Round(tb), 0, 999) : 0u;
                    var blobEnergy = existingBlob.Detected.Energy;
                    var currentEnergy = meta.TaggedEnergy;

                    // Check if standard tags already match blob values exactly.
                    var keyMatch    = blobKey is null || (currentKey is not null && blobKey.Value.Camelot == currentKey.Value.Camelot);
                    var bpmMatch    = blobBpm == currentBpm;
                    var energyMatch = blobEnergy == currentEnergy;
                    if (keyMatch && bpmMatch && energyMatch)
                    {
                        alreadyDone++;
                        continue; // standard tags are already consistent with blob — skip
                    }
                    // Falls through: standard tags need updating (TKEY/TBPM/ENERGY stale).
                }

                // Keep ALL Detected values as-is (don't change what the DSP found);
                // only stamp decisions = Applied.
                newState = existingBlob with
                {
                    Version        = TrackAnalysisState.CurrentVersion,
                    BpmDecision    = FieldDecision.Applied,
                    KeyDecision    = FieldDecision.Applied,
                    EnergyDecision = FieldDecision.Applied,
                };
                detectedKeyStr = existingBlob.Detected.Key?.Camelot;
            }
            else
            {
                // No blob → match to v9 index.
                // Strategy: filename first (fast, zero file I/O), hash as fallback.
                //
                // After N12's tag writes (which changed SHA-256 for all tagged files),
                // hash-first would read every file fully over the NAS just to miss.
                // Filename is stable across the SETS→GENRES move and across tag writes.
                //
                // Hash fallback catches the rare edge case where two files share a name
                // (only 2 such collisions exist in this index) by disambiguating via hash.
                TrackIndexEntry? entry = null;
                var fname = Path.GetFileName(filePath);

                // Primary: filename lookup.
                if (fileNameCollisions.Contains(fname))
                    Console.Error.WriteLine($"  [WARN] filename-collision {fname} — using first index entry");
                if (byFileName.TryGetValue(fname, out entry))
                    matchedByName++;

                // Fallback: SHA-256 hash (only for files that didn't match by filename,
                // which means they're either genuinely new files or filename changed).
                if (entry is null)
                {
                    try
                    {
                        var hash = ComputeSha256(filePath);
                        if (byHash.TryGetValue(hash, out entry))
                            matchedByHash++;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  [WARN] hash      {fname}: {ex.Message}");
                    }
                }

                if (entry is null)
                {
                    // Not found by hash OR filename → needs fresh analysis.
                    Console.Error.WriteLine($"  [skip] not-in-index  {fname}");
                    notInIndex++;
                    continue;
                }

                // Build full AnalysisResult from index entry.
                var detected = TrackIndexMapping.ToAnalysisResult(entry);
                detectedKeyStr = entry.KeyCamelot;

                // The "original" is what TKEY / TBPM / ENERGY currently hold.
                // This mirrors what Persist() does in MainWindowViewModel — stash the
                // pre-overwrite foreign value so the write is reversible in the app.
                // NOTE: N12 already wrote TKEY = detected key from the same v9 analysis,
                // so Original.Key will equal Detected.Key for most files. The "Restore
                // original" menu in the app will be available but will restore the same
                // value — harmless, since the true pre-N12 original is no longer in the file.
                var origKey    = MusicalKey.TryParse(meta.TaggedKey);
                var origBpm    = meta.TaggedBpm;
                var origEnergy = meta.TaggedEnergy;
                AnalysisResult? original = (origBpm is null && origKey is null && origEnergy is null)
                    ? null
                    : new AnalysisResult { Bpm = origBpm, Key = origKey, Energy = origEnergy };

                newState = new TrackAnalysisState
                {
                    Version        = TrackAnalysisState.CurrentVersion,
                    Detected       = detected,
                    Original       = original,
                    BpmDecision    = FieldDecision.Applied,
                    KeyDecision    = FieldDecision.Applied,
                    EnergyDecision = FieldDecision.Applied,
                };
            }

            // ── 4. Collect backup entry (before the first actual write) ────
            backupRecords.Add(new PromoteBackupEntry
            {
                FilePath   = filePath,
                TaggedBpm  = meta.TaggedBpm,
                TaggedKey  = meta.TaggedKey,
                TaggedEnergy = meta.TaggedEnergy,
                HadBlob    = existingBlob is not null,
                BlobJson   = existingBlob is not null
                    ? AnalysisStateCodec.Serialize(existingBlob)
                    : null,
            });

            // ── 5. Build the write: standard tags + blob ───────────────────
            //   §4 tag contract (NAS CLAUDE.md): TKEY = Camelot, TBPM always set,
            //   TXXX:ENERGY, and EXACTLY ONE clean COMM "<Camelot> - Energy <nrg>"
            //   rebuilt from the SAME detected values — so standard tag == COMM ==
            //   blob and no field can drift (the 2026-07-16 ingest shipped files
            //   whose COMM disagreed with TKEY/ENERGY; fixed by fix_contract.py).
            //   Also preserves the ReplayGain tag if we have loudness data.

            // TBPM fallback: detected BPM, else the ORIGINAL tag BPM (blob 'obpm') —
            // the analyzer leaves Detected.Bpm null when it finds no confident beat
            // ("Billie Jean"). NOTE: the serialized blob's 'abpm' is the BPM DECISION
            // code (a letter, e.g. 'A' = Applied), never a tempo — only these two
            // numeric sources are eligible.
            var contractBpm = newState.Detected.Bpm ?? newState.Original?.Bpm;

            // Contract COMM from the current detection (never from a stale index or
            // original-key value); DjCommentBuilder strips any legacy JP/MIK prefix
            // and keeps genuine user text behind " | ".
            string? existingComment = null;
            try { existingComment = composer.MetadataReader.ReadEditable(filePath).Comment; }
            catch { /* unreadable comment → build the bare segment */ }

            var tagWrite = new TagWrite
            {
                Bpm          = contractBpm,
                Key          = newState.Detected.Key,
                Energy       = newState.Detected.Energy,
                State        = newState,
                // ReplayGain: stamp if available (non-destructive, mirrors Persist).
                ReplayGainDb = newState.Detected.ReplayGainDb,
                Peak         = newState.Detected.Peak,
                Comment      = DjCommentBuilder.Build(
                    newState.Detected.Key, newState.Detected.Energy, existingComment),
                // Grouping: leave untouched (real label/catalog data, never ours).
            };

            if (apply)
            {
                try
                {
                    composer.MetadataWriter.Write(filePath, tagWrite);
                    Console.WriteLine($"  [ ok ] {Path.GetFileName(filePath),-60} key={detectedKeyStr}");
                    processed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [FAIL] write     {Path.GetFileName(filePath)}: {ex.Message}");
                    failed++;
                }
            }
            else
            {
                // Dry-run: print planned change.
                var bpmStr    = newState.Detected.Bpm    is { } b ? $"{(int)Math.Round(b)}" : "(null)";
                var energyStr = newState.Detected.Energy is { } e ? $"{e}" : "(null)";
                Console.WriteLine($"  [plan] {Path.GetFileName(filePath),-60} key={detectedKeyStr ?? "(null)"}  bpm={bpmStr}  energy={energyStr}  hadBlob={existingBlob is not null}");
                processed++;
            }
        }

        // ── 6. Save backup (only if applying and we have records) ─────────
        if (apply && backupRecords.Count > 0 && !backupExists)
        {
            try
            {
                var backupJson = JsonSerializer.Serialize(backupRecords, PromoteJsonContext.Default.ListPromoteBackupEntry);
                var backupTmp  = backupPath + ".tmp";
                File.WriteAllText(backupTmp, backupJson);
                File.Move(backupTmp, backupPath, overwrite: true);
                Console.WriteLine();
                Console.WriteLine($"[promote] Backup written: {backupPath}  ({backupRecords.Count:N0} entries)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[promote] WARN: backup write failed: {ex.Message}");
                // Non-fatal: the actual writes already succeeded.
            }
        }

        // ── Summary ───────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"[promote] Done.");
        Console.WriteLine($"  Already Applied  : {alreadyDone,6:N0}");
        Console.WriteLine($"  {(apply ? "Written" : "Planned"),-15} : {processed,6:N0}");
        Console.WriteLine($"    (matched by hash)    : {matchedByHash,6:N0}");
        Console.WriteLine($"    (matched by filename): {matchedByName,6:N0}");
        Console.WriteLine($"  Not in index     : {notInIndex,6:N0}  (need fresh analysis)");
        Console.WriteLine($"  Skipped (other)  : {skipped,6:N0}");
        Console.WriteLine($"  Failed           : {failed,6:N0}");
        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("[promote] DRY-RUN only — no files were written. Re-run with --apply to commit.");
        }

        return failed > 0 ? 2 : 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>All three decisions = Applied → no conflict dots possible.</summary>
    private static bool IsFullyApplied(TrackAnalysisState? state) =>
        state is
        {
            BpmDecision:    FieldDecision.Applied,
            KeyDecision:    FieldDecision.Applied,
            EnergyDecision: FieldDecision.Applied,
        };

    /// <summary>SHA-256 hex digest of the file bytes. Mirrors AudioFiles.Sha256.</summary>
    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ─── Backup DTO + JSON context ────────────────────────────────────────────────

/// <summary>One entry in the promote-backup JSON: the pre-write tag state.</summary>
internal sealed record PromoteBackupEntry
{
    public required string FilePath     { get; init; }
    public double?  TaggedBpm           { get; init; }
    public string?  TaggedKey           { get; init; }
    public int?     TaggedEnergy        { get; init; }
    /// <summary>True when the file had a JUSTPLAY blob before this promote run.</summary>
    public bool     HadBlob             { get; init; }
    /// <summary>The serialised blob JSON (null when HadBlob=false).</summary>
    public string?  BlobJson            { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<PromoteBackupEntry>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PromoteBackupEntry))]
internal sealed partial class PromoteJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
