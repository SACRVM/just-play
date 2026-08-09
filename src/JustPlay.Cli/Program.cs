using JustPlay.Analysis;
using JustPlay.Cli.Commands;
using static JustPlay.Cli.CliArgs;

// -- Just Sort CLI -------------------------------------------------------------
// Usage:
//   justplay scan    <root> [--json <out>]
//   justplay dedup   <root> [--json <out>]
//   justplay analyze <root> --index <path> [--threads N] [--limit N]
//   justplay analyze --playlist <m3u> --index <path> [--root <dir>] [--threads N] [--limit N]
//   justplay stats   --index <path> [--json <out>]
//   justplay tag write --index <path> [--root <dir>] [--apply]
//   justplay promote --index <path> --root <dir> [--apply] [--backup-dir <dir>]
//   justplay squeeze --index <path> --keep N [--root <dir>] [--playlist <m3u>]
//                    [--threshold 0..1] [--no-sequence] [--out <file.m3u|.json>]
//   justplay spectrum <audiofile> [--out path.png]
//             [--eq-low x] [--eq-mid x] [--eq-high x]
//             [--tilt x] [--punch x]
//             [--limiter off|soft|club|loud]
//
// All commands are READ-ONLY on the audio library (except analyze which writes the
// sidecar index file, "tag write --apply" which writes tags into audio files, and
// "promote --apply" which writes JUSTPLAY blobs + standard tags into audio files).
// Phase 0 = scan + dedup. Phase 1 = analyze + stats. Phase 2 = tag write.
// N15 = promote (make our analysis the authoritative truth, kill conflict dots).
// spectrum = offline DSP tuning tool (Phase 1 of spectral diagram feature).
// -----------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var verb = args[0].ToLowerInvariant();

return verb switch
{
    "scan"     => RunScan(args[1..]),
    "dedup"    => RunDedup(args[1..]),
    "analyze"  => RunAnalyze(args[1..]),
    "stats"    => RunStats(args[1..]),
    "tag"      => RunTag(args[1..]),
    "promote"  => RunPromote(args[1..]),
    "squeeze"  => RunSqueeze(args[1..]),
    "spectrum" => RunSpectrum(args[1..]),
    _ => Fail($"Unknown command '{args[0]}'. Run 'justplay --help' for usage."),
};

// -- Scan --------------------------------------------------------------------
static int RunScan(string[] args)
{
    if (args.Length == 0)
        return Fail("scan requires a <root> directory.");

    var root    = args[0];
    var jsonOut = ParseStringFlag(args[1..], "--json");
    return ScanCommand.Run(root, jsonOut);
}

// -- Dedup -------------------------------------------------------------------
static int RunDedup(string[] args)
{
    if (args.Length == 0)
        return Fail("dedup requires a <root> directory.");

    var root    = args[0];
    var jsonOut = ParseStringFlag(args[1..], "--json");
    return DedupCommand.Run(root, jsonOut);
}

// -- Analyze -----------------------------------------------------------------
static int RunAnalyze(string[] args)
{
    // Both input forms and their validation live in AnalyzeArgs so they are testable.
    if (AnalyzeArgs.Parse(args, out var error) is not { } a)
        return Fail(error ?? "analyze: bad arguments.");

    // --playlist wins over a root when both are given (same precedence as tag clean / squeeze);
    // the root then only picks the library database.
    return a.UsesPlaylist
        ? AnalyzeCommand.RunPlaylist(a.Playlist!, a.Root, a.IndexPath, a.Threads, a.Limit,
                                     a.DbPath, a.NoDb, a.Force)
        : AnalyzeCommand.Run(a.Root!, a.IndexPath, a.Threads, a.Limit,
                             a.DbPath, a.NoDb, a.Force);
}

// -- Stats --------------------------------------------------------------------
static int RunStats(string[] args)
{
    var indexPath = ParseStringFlag(args, "--index");
    if (indexPath is null)
        return Fail("stats requires --index <path>.");
    var jsonOut   = ParseStringFlag(args, "--json");
    return StatsCommand.Run(indexPath, jsonOut);
}

// -- Promote -----------------------------------------------------------------
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
    // N21: --retag forces re-stamping of TKEY/TBPM/ENERGY even for fully-Applied files
    // (fixes ~4% of files where N15 left a stale TKEY despite the blob being Applied).
    var retag      = ParseBoolFlag(args, "--retag");
    // Scope to an explicit file list (one path per line) instead of the whole root walk -
    // an ingest promotes ONLY its new files and physically cannot touch the rest of the lib.
    var filesList  = ParseStringFlag(args, "--files");

    return PromoteCommand.Run(indexPath, root, apply, noGrouping, backupDir, retag, filesList);
}

// -- Tag ----------------------------------------------------------------------
static int RunTag(string[] args)
{
    if (args.Length == 0)
        return Fail("Unknown 'tag' sub-command. Usage: justplay tag write ... | tag clean ...");

    var sub  = args[0].ToLowerInvariant();
    var rest = args[1..];

    switch (sub)
    {
        case "write":
        {
            var indexPath = ParseStringFlag(rest, "--index");
            if (indexPath is null)
                return Fail("tag write requires --index <path>.");

            var root       = ParseStringFlag(rest, "--root");
            var apply      = ParseBoolFlag(rest, "--apply");
            var noGrouping = ParseBoolFlag(rest, "--no-grouping");

            return TagWriteCommand.Run(indexPath, root, apply, noGrouping);
        }

        case "clean":
        {
            var root      = ParseStringFlag(rest, "--root");
            var playlist  = ParseStringFlag(rest, "--playlist");
            var apply     = ParseBoolFlag(rest, "--apply");
            var backupDir = ParseStringFlag(rest, "--backup-dir");
            // --force: rewrite the clean comment even when the visible value already matches,
            // so the writer collapses any HIDDEN duplicate COMM frame (a stale/truncated second
            // frame TagLib# doesn't surface). Use to normalize files that read as "already clean".
            var force     = ParseBoolFlag(rest, "--force");

            if (playlist is not null)
                return CleanCommentsCommand.RunPlaylist(playlist, apply, backupDir, force);
            if (root is not null)
                return CleanCommentsCommand.RunRoot(root, apply, backupDir, force);
            return Fail("tag clean requires --root <dir> or --playlist <m3u>.");
        }

        default:
            return Fail("Unknown 'tag' sub-command. Usage: justplay tag write ... | tag clean ...");
    }
}

// -- Squeeze ------------------------------------------------------------------
static int RunSqueeze(string[] args)
{
    var indexPath = ParseStringFlag(args, "--index");
    if (indexPath is null)
        return Fail("squeeze requires --index <path>.");

    var keep = ParseIntFlag(args, "--keep", -1);
    if (keep < 1)
        return Fail("squeeze requires --keep <N> (N >= 1).");

    var root      = ParseStringFlag(args, "--root");
    var playlist  = ParseStringFlag(args, "--playlist");
    var threshold = ParseDoubleFlag(args, "--threshold", SetSqueezer.DefaultCoherenceThreshold);
    var sequence  = !ParseBoolFlag(args, "--no-sequence");
    var outPath   = ParseStringFlag(args, "--out");

    return SqueezeCommand.Run(indexPath, keep, root, playlist, threshold, sequence, outPath);
}

// -- Spectrum -----------------------------------------------------------------
static int RunSpectrum(string[] args)
{
    if (args.Length == 0)
        return Fail("spectrum requires an <audiofile> path.");

    var file    = args[0];
    var outPath = ParseStringFlag(args[1..], "--out");

    var opts = new SpectrumOptions
    {
        EqLow          = ParseDoubleFlag(args[1..], "--eq-low",    1.0),
        EqMid          = ParseDoubleFlag(args[1..], "--eq-mid",    1.0),
        EqHigh         = ParseDoubleFlag(args[1..], "--eq-high",   1.0),
        TiltStrength   = ParseDoubleFlag(args[1..], "--tilt",      0.0),
        TransientPunch = ParseDoubleFlag(args[1..], "--punch",     0.0),
        LimiterMode    = ParseStringFlag(args[1..], "--limiter") ?? "off",
        Mode           = ParseStringFlag(args[1..], "--mode")   ?? "shape",
    };

    return SpectrumCommand.Run(file, outPath, opts);
}

// -- Helpers ------------------------------------------------------------------

static int Fail(string msg)
{
    Console.Error.WriteLine($"[justplay] ERROR: {msg}");
    return 1;
}

// ParseStringFlag / ParseIntFlag / ParseBoolFlag / ParseDoubleFlag live in CliArgs (pulled in by
// the "using static" above) - a command that owns its own argument rules has to reach them too.

static void PrintHelp()
{
    Console.WriteLine("""
        J.U.S.T. - Just Useful Sound Tools
        JustPlayCLI - headless library tools (analyze - tag - sort)

        COMMANDS

          scan <root> [--json <out>]
              Inventory audio files under <root>. Prints count, total size,
              by-format and by-folder breakdown. Optionally writes JSON to <out>.

          dedup <root> [--json <out>]
              Phase 0 - detect duplicates without decoding:
              (a) Exact dupes: same size -> SHA-256 match.
              (b) Near dupes: same artist+title AND duration within ~2s.
              READ-ONLY. Optionally writes JSON to <out>.

          analyze <root> --index <path> [--threads N] [--limit N] [--db <path>|--no-db] [--force]
          analyze --playlist <m3u> --index <path> [--root <dir>] [--threads N] [--limit N]
                  [--db <path>|--no-db] [--force]
              Phase 1 - full resumable analysis pass.
              Runs BPM / key / energy / loudness / beat-fingerprint / RhythmPattern
              on every audio file and writes results to the sidecar index at <path>
              AND to this machine's library database.
              READ-ONLY on audio files. Writes only the index and the database.

              Work it does NOT redo:
                - unchanged + already analysed (size+mtime match) -> skipped, file never opened
                - a JUSTPLAY blob at the current detection version -> imported from the tags,
                  no DSP. This is what makes a second machine cheap: whichever machine
                  analysed a track first already wrote the answer into the file.
              Files are hashed only when they are actually analysed (hashing reads every
              byte, i.e. the whole library over the network).

              --playlist <m3u>  Analyse EXACTLY the files an .m3u/.m3u8 lists, instead of a
                                <root> tree - the form for re-running a known subset (say the
                                files a decoder fix invalidated) without touching the rest of
                                the library. Relative entries resolve against the .m3u's folder;
                                absolute / UNC entries are used verbatim. Every listed file that
                                is no longer on disk is NAMED in the output and counted
                                separately from failures - nothing is dropped in silence.
              --root <dir>      The directory form's <root>, spelled as a flag. With --playlist
                                it does not select files; it only says which library database to
                                update. Without it, the registered root that contains every
                                listed file is used, and if there is none the run writes the
                                index only. A playlist run NEVER creates a database or registers
                                a root - point the directory form at a folder to do that.
              --threads N     Degree of parallelism (default: CPU count).
              --limit N       Process at most N files (smoke-test mode).
              --db <path>     Use this database file instead of the default for <root>.
              --no-db         Write the sidecar index only.
              --force         Re-run the DSP on every file, ignoring skips and stored blobs.

          stats --index <path> [--json <out>]
              Read the sidecar index and print histograms:
              BPM decades, energy 1..10, danceability, BeatType, rhythm scalars.
              Use the output to tune beat-type bucket thresholds before apply phase.

          tag write --index <path> [--root <dir>] [--apply]
              Phase 2 - batch-write analysis tags from the sidecar index into each
              audio file. Writes: BPM (standard tempo tag), Key (standard key tag),
              Energy (TXXX:ENERGY), and a clean Mixed-In-Key-style Comment ("8A - Energy 7",
              user text preserved). The machine-readable vibe data is NOT written to the
              Comment/Grouping anymore - it lives in the JUSTPLAY blob (promote) + the index.

              Default: DRY-RUN - prints every planned change without touching files.
              --root <dir>    Only process files whose path starts with <dir>.
              --apply         Commit the writes (required to actually modify files).

          tag clean (--root <dir> | --playlist <m3u>) [--apply] [--backup-dir <dir>]
              One-shot cleanup: replace the cryptic legacy "JP|E7|K8A|bpm140|gc.57|..." vibe
              blob in the Comment with the clean "8A - Energy 7" form, and strip the blob from
              Grouping. Uses each file's own tags (no index needed): Key/Energy come from the
              standard tags, falling back to the JUSTPLAY blob.
              No data loss: the vibe data stays in the JUSTPLAY blob + the index. Idempotent.

              --root <dir>        Clean every audio file under <dir> (recursive).
              --playlist <m3u>    Clean ONLY the tracks in a playlist (paths resolved against
                                  the .m3u's folder) - e.g. re-tag exactly one set.
              Default: DRY-RUN - prints sample before/after changes without touching files.
              --apply             Commit the writes.
              --backup-dir        Directory for the comment-clean-backup.json undo file
                                  (default: the root dir / the playlist's folder).

        EXAMPLES

          justplay scan \\nas\music\SETS
          justplay dedup \\nas\music\SETS --json C:\tmp\dedup.json
          justplay analyze \\nas\music\SETS --index C:\tmp\sets.index.json --threads 4
          justplay analyze \\nas\music\SETS --index C:\tmp\sets.index.json --limit 3
          justplay analyze --playlist C:\tmp\redo-957.m3u --index C:\tmp\genres.index.json --force
          justplay analyze --playlist C:\tmp\redo-957.m3u --root \\nas\music\GENRES --index C:\tmp\genres.index.json --force
          justplay stats --index C:\tmp\sets.index.json --json C:\tmp\stats.json
          justplay tag write --index C:\tmp\sets.index.json
          justplay tag write --index C:\tmp\sets.index.json --root \\nas\music\SETS\Techno --apply
          justplay tag clean --playlist \\nas\music\SETS\2026-06-19_MINDFUCK.m3u
          justplay tag clean --root \\nas\music\GENRES --apply

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
              Default: DRY-RUN - prints every planned change without touching files.
              --apply         Commit the writes.
              --backup-dir    Directory for the n15-promote-backup.json undo file.

          EXAMPLES
          justplay promote --index C:\tmp\sets.v9.index.json --root \\nas\music\GENRES
          justplay promote --index C:\tmp\sets.v9.index.json --root \\nas\music\GENRES --apply

          squeeze --index <v9-index> --keep N [--root <dir>] [--playlist <m3u>]
                  [--threshold 0..1] [--no-sequence] [--out <file.m3u|.json>]
              Phase 2 of harmonic sort - compress a pool down to the N tracks that mix best
              together (the densest mutually-compatible core), drop the outliers, and report
              HONESTLY if fewer than N are truly coherent. Uses the SAME MixCompatibility scorer
              as Harmonic Sort; pure over the index, no audio decode.

              Pool source (first given wins): --playlist (tracks in an .m3u, matched by filename) >
              --root (index entries under the folder) > the whole index.
              --keep N         Target kept-set size (required, >= 1).
              --threshold x    Coherence cut for "counts as part of the set" (default 0.60).
              --no-sequence    Keep greedy growth order instead of HarmonicSequencer play order.
              --out <file>     Write the kept set: .m3u/.m3u8 (portable paths) or .json (report).
              Default: DRY-RUN - prints the plan; writes nothing without --out.

          EXAMPLES
          justplay squeeze --index C:\tmp\sets.v9.index.json --keep 20
          justplay squeeze --index C:\tmp\sets.v9.index.json --root \\nas\music\GENRES\Techno --keep 15 --out C:\tmp\tight.m3u
          justplay squeeze --index C:\tmp\sets.v9.index.json --playlist \\nas\music\SETS\pile.m3u --keep 12 --threshold 0.7

          spectrum <audiofile> [--out path.png]
                   [--eq-low x] [--eq-mid x] [--eq-high x]
                   [--tilt x] [--punch x]
                   [--limiter off|soft|club|loud]
              Offline DSP tuning tool (Phase 1 of spectral diagram feature).
              Decodes <audiofile>, computes DRY and WET long-term-average spectra
              (LTAS via Welch's method), compares against a pink-noise reference
              target curve, and saves a before/after PNG plot.
              DSP chain: EQ -> Tilt -> Transient -> Limiter.
              All DSP flags default to NEUTRAL (all-bypass).  Pass at least one flag
              to see the chain's effect on the WET curve.
              --limiter: off (default) | soft (0 dB drive) | club (3 dB) | loud (6 dB)

          EXAMPLES
          justplay spectrum C:\music\track.flac
          justplay spectrum C:\music\track.flac --eq-low 1.5 --limiter club --out C:\tmp\plot.png
        """);
}
