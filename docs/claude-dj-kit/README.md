# Claude DJ Kit — manage your music library by talking to it

Let [Claude Code](https://claude.com/claude-code) take care of your DJ library, powered by the
**JustPlay CLI** that ships with the J.U.S.T. suite. Find duplicates, analyze BPM / key / energy /
vibe, get beatgrid warnings before your DJ software trips over them, and build key-compatible set
drafts — by asking in plain language.

## Quick start (3 steps)

1. **Install Claude Code** → <https://claude.com/claude-code>
   (and the J.U.S.T. suite, if you haven't → <https://github.com/chloe-dream/just-play/releases/latest>)
2. **Copy `CLAUDE.md` from this kit into the ROOT folder of your music library**
   (the folder that contains your tracks). This file teaches the agent its rules and tools.
3. **Open a terminal in that folder and run `claude`.** Then just talk:
   - *"Find my duplicates"*
   - *"Analyze my library"* (runs overnight, resumable)
   - *"Which tracks will get a shaky beatgrid?"*
   - *"Build me a 90-minute set, 126–130 BPM, key-compatible, rising energy"*

## Is this safe for my library?

Yes — the rules in `CLAUDE.md` make the agent conservative by design:

- **It never deletes, moves or renames anything on its own.** Destructive actions only happen
  after it shows you the exact plan and you say yes — recycle bin, not hard delete.
- **Mixed In Key users:** set `I use Mixed In Key: YES` at the top of `CLAUDE.md` (it's the
  default). The agent then works strictly read-only on your files — all analysis lives in a
  separate index file, and your MIK key / energy / comment tags are never touched.
- Every write-capable CLI command runs as a dry-run first, and backups are kept.

## What you get that your other tools don't do

- **Beatgrid early-warning** — tracks with a low grid-confidence score are flagged *before*
  Rekordbox / Traktor mis-grids them live. No other DJ tool does this.
- **Vibe analysis** beyond key & BPM: energy, darkness, hypnotic factor, punch, harshness —
  and set drafts built from all of it.
- **Duplicate detection** by content hash and by artist/title + duration.
- **Loudness report** — find your too-quiet or tinny tracks (LUFS).
- Portable playlists: relative paths, forward slashes — they survive Windows ↔ Mac and every
  DJ software.

## Tips for Mixed In Key users

Your MIK tags stay the boss — the agent only reads them. But combined with the JustPlay analysis
index you get things MIK doesn't do:

1. **Run the analysis once, overnight:**
   `analyze <your library> --index library.index.json --threads 4`
   Writes ONLY the sidecar index — your files and MIK tags stay untouched. Re-running resumes.
2. **Beatgrid early-warning:** ask for all tracks with `gridConfidence < 0.45` — those are the
   ones Rekordbox/Traktor will likely mis-grid. Check and fix their grids BEFORE the gig,
   not live on stage.
3. **Key cross-check instead of key overwrite:** let the agent compare MIK's key (your tags)
   against JustPlay's detection (the index). Where they agree — mix with confidence. Where they
   disagree — that's your shortlist to double-check by ear before harmonic-mixing. Two opinions
   beat one, nothing gets overwritten.
4. **Loudness report:** ask for the quietest / tinniest tracks (LUFS lives in the index) — you'll
   know which tracks need gain attention or a better rip.
5. **Set drafts your DJ software can import:** the agent builds sets using YOUR MIK keys plus
   energy/vibe/grid data from the index, and writes an `.m3u` you import straight into
   Rekordbox/Traktor.
6. **Duplicates:** `dedup` finds exact and near duplicates. The agent shows the list, YOU decide
   what goes — recycle bin only.

## Questions

Ask in the JUST PLAY Discord (#help or #feedback) — feedback is very welcome, the kit is young.
