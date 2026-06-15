using JustPlay.Cli.Commands;

// ── Just Sort CLI ─────────────────────────────────────────────────────────────
// Usage:
//   justplay scan    <root> [--json <out>]
//   justplay dedup   <root> [--json <out>]
//   justplay analyze <root> --index <path> [--threads N] [--limit N]
//   justplay stats   --index <path> [--json <out>]
//   justplay tag write --index <path> [--root <dir>] [--apply]
//   justplay promote --index <path> --root <dir> [--apply] [--backup-dir <dir>]
//
// All commands are READ-ONLY on the audio library (except analyze which writes the
// sidecar index file, "tag write --apply" which writes tags into audio files, and
// "promote --apply" which writes JUSTPLAY blobs + standard tags into audio files).
// Phase 0 = scan + dedup. Phase 1 = analyze + stats. Phase 2 = tag write.
// N15 = promote (make our analysis the authoritative truth, kill conflict dots).
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var verb = args[0].ToLowerInvariant();

return verb switch
{
    "scan"    => RunScan(args[1..]),
    "dedup"   => RunDedup(args[1..]),
    "analyze" => RunAnalyze(args[1..]),
    "stats"   => RunStats(args[1..]),
    "tag"     => RunTag(args[1..]),
    "promote" => RunPromote(args[1..]),
    _ => Fail($"Unknown command '{args[0]}'. Run 'justplay --help' for usage."),
};

// ── Scan ────────────────────────────────────────────────────────────────────
static int RunScan(string[] args)
{
    if (args.Length == 0)
        return Fail("scan requires a <root> directory.");

    var root    = args[0];
    var jsonOut = ParseStringFlag(args[1..], "--json");
    return ScanCommand.Run(root, jsonOut);
}

// ── Dedup ───────────────────────────────────────────────────────────────────
static int RunDedup(string[] args)
{
    if (args.Length == 0)
        return Fail("dedup requires a <root> directory.");

    var root    = args[0];
    var jsonOut = ParseStringFlag(args[1..], "--json");
    return DedupCommand.Run(root, jsonOut);
}

// ── Analyze ─────────────────────────────────────────────────────────────────
static int RunAnalyze(string[] args)
{
    if (args.Length == 0)
        return Fail("analyze requires a <root> directory.");

    var root      = args[0];
    var indexPath = ParseStringFlag(args[1..], "--index");
    if (indexPath is null)
        return Fail("analyze requires --index <path>.");
    var threads   = ParseIntFlag(args[1..], "--threads", Environment.ProcessorCount);
    var limit     = ParseIntFlag(args[1..], "--limit", int.MaxValue);

    return AnalyzeCommand.Run(root, indexPath, threads, limit);
}

// ── Stats ────────────────────────────────────────────────────────────────────
static int RunStats(string[] args)
{
    var indexPath = ParseStringFlag(args, "--index");
    if (indexPath is null)
        return Fail("stats requires --index <path>.");
    var jsonOut   = ParseStringFlag(args, "--json");
    return StatsCommand.Run(indexPath, jsonOut);
}

// ── Promote ─────────────────────────────────────────────────────────────────
static int RunPromote(string[] args)
{
    var indexPath = ParseStringFlag(args, "--index");
    if (indexPath is null)
        return Fail("promote requires --index <v9-index-path>.");

    var root = ParseStringFlag(args, "--root");
    if (root is null)
        return Fail("promote requires --root <genres-root-dir>.");

    var apply      = ParseBoolFlag(args, "--apply");
    var noGrouping = ParseBoolFlag(args, "--no-grouping");
    var backupDir  = ParseStringFlag(args, "--backup-dir");

    return PromoteCommand.Run(indexPath, root, apply, noGrouping, backupDir);
}

// ── Tag ──────────────────────────────────────────────────────────────────────
static int RunTag(string[] args)
{
    if (args.Length == 0 || !args[0].Equals("write", StringComparison.OrdinalIgnoreCase))
        return Fail("Unknown 'tag' sub-command. Usage: justplay tag write --index <path> [--root <dir>] [--apply]");

    var rest      = args[1..];
    var indexPath = ParseStringFlag(rest, "--index");
    if (indexPath is null)
        return Fail("tag write requires --index <path>.");

    var root       = ParseStringFlag(rest, "--root");
    var apply      = ParseBoolFlag(rest, "--apply");
    var noGrouping = ParseBoolFlag(rest, "--no-grouping");

    return TagWriteCommand.Run(indexPath, root, apply, noGrouping);
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static int Fail(string msg)
{
    Console.Error.WriteLine($"[justplay] ERROR: {msg}");
    return 1;
}

static string? ParseStringFlag(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static int ParseIntFlag(string[] args, string flag, int defaultValue)
{
    var s = ParseStringFlag(args, flag);
    return s is not null && int.TryParse(s, out var v) && v > 0 ? v : defaultValue;
}

static bool ParseBoolFlag(string[] args, string flag)
    => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

static void PrintHelp()
{
    Console.WriteLine("""
        Just Sort — JustPlay library analysis CLI

        COMMANDS

          scan <root> [--json <out>]
              Inventory audio files under <root>. Prints count, total size,
              by-format and by-folder breakdown. Optionally writes JSON to <out>.

          dedup <root> [--json <out>]
              Phase 0 — detect duplicates without decoding:
              (a) Exact dupes: same size → SHA-256 match.
              (b) Near dupes: same artist+title AND duration within ~2s.
              READ-ONLY. Optionally writes JSON to <out>.

          analyze <root> --index <path> [--threads N] [--limit N]
              Phase 1 — full resumable analysis pass.
              Runs BPM / key / energy / loudness / beat-fingerprint / RhythmPattern
              on every audio file and writes results to the sidecar index at <path>.
              Skips files already in the index with the current detection version.
              READ-ONLY on audio files. Writes only the index file.

              --threads N     Degree of parallelism (default: CPU count).
              --limit N       Process at most N files (smoke-test mode).

          stats --index <path> [--json <out>]
              Read the sidecar index and print histograms:
              BPM decades, energy 1..10, danceability, BeatType, rhythm scalars.
              Use the output to tune beat-type bucket thresholds before apply phase.

          tag write --index <path> [--root <dir>] [--apply]
              Phase 2 — batch-write analysis tags from the sidecar index into each
              audio file. Writes: BPM (standard tempo tag), Key (standard key tag),
              Energy (TXXX:ENERGY), Comment (JP vibe prefix + preserved user text),
              Grouping (TIT1/GROUPING — pure JP vibe string for DJ software columns).

              Default: DRY-RUN — prints every planned change without touching files.
              --root <dir>    Only process files whose path starts with <dir>.
              --apply         Commit the writes (required to actually modify files).

              JP vibe format: JP|E{energy}|K{camelot}|bpm{N}|gc.NN|gr.NN|pu.NN|hy.NN|dk.NN|hx.NN
              (See src/JustPlay.Cli/Tags/VibeTagEncoder.cs for the full parsing spec.)

        EXAMPLES

          justplay scan \\nas\music\SETS
          justplay dedup \\nas\music\SETS --json C:\tmp\dedup.json
          justplay analyze \\nas\music\SETS --index C:\tmp\sets.index.json --threads 4
          justplay analyze \\nas\music\SETS --index C:\tmp\sets.index.json --limit 3
          justplay stats --index C:\tmp\sets.index.json --json C:\tmp\stats.json
          justplay tag write --index C:\tmp\sets.index.json
          justplay tag write --index C:\tmp\sets.index.json --root \\nas\music\SETS\Techno --apply

          promote --index <v9-index> --root <genres-root> [--apply] [--backup-dir <dir>]
              N15: Make JustPlay's v9 analysis the authoritative truth in every file under
              <genres-root>. Writes the JUSTPLAY blob (TrackAnalysisState) with all three
              decisions = Applied, eliminating key-conflict dots in the app. Standard tags
              (TKEY/TBPM/ENERGY) are also stamped from the detected values.

              Files are matched to the index by SHA-256 content hash (robust after N12 move).
              Files with blobs but non-Applied decisions: decisions upgraded, values preserved.
              Files already fully Applied: skipped (no write).
              Files not in the index: logged as "needs fresh analysis", skipped safely.

              Writes a pre-write tag backup to --backup-dir (default: same dir as --index).
              Default: DRY-RUN — prints every planned change without touching files.
              --apply         Commit the writes.
              --backup-dir    Directory for the n15-promote-backup.json undo file.

          EXAMPLES
          justplay promote --index C:\tmp\sets.v9.index.json --root \\nas\music\GENRES
          justplay promote --index C:\tmp\sets.v9.index.json --root \\nas\music\GENRES --apply
        """);
}
