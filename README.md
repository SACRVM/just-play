# JustPlay

The music player for DJs and music lovers. Drop tracks in, double-click to play.
Camelot key, BPM and energy detected on the spot — no library to build, no sign-in, no nag.

*Part of **J.U.S.T.** — Just Useful Sound Tools. **From DJs to DJs**: built and gig-tested by a working DJ.*

> **v0.6.0 beta.** Daily driver on Windows, still pre-1.0. macOS and Linux share the codebase and
> are not validated. [What isn't finished](#not-finished-yet) is named below rather than left for
> you to find.

## Install

[**Download the installer**](https://github.com/SACRVM/just-play/releases/tag/v0.6.0-beta.1) —
per-user, no admin prompt. One install, four tools, one shared runtime:

| | |
|---|---|
| **JUST PLAY** | the player — analysis, Harmonic Sort, DSP rack, Icecast |
| **JUST STREAM** | the broadcaster — capture one app, broadcast DSP, listener count |
| **JUST TAG** | the tag editor — one file or three hundred, every write previewed |
| **JustPlayCLI** | headless scan / analyse / tag / sort, for scripts and agents |

They share one look by design: frameless rounded windows, a theme-gradient icon that repaints on
theme switch, About top-left. A new suite app feels like a sibling on first run.

## What it does

- **Camelot key** — our own chromagram + EDMA profiles, on your machine. An optional ONNX model is
  used automatically when present. This is the headline feature.
- **BPM** (BASS_FX, async on add), **energy 1–10**, beat fingerprint, structure detection.
- **Harmonic Sort** — reorders the queue for the smoothest mix, scoring each pair on key, tempo,
  energy and groove.
- **Analysis written into your files**, if you ask. The file is the memory, not a hidden database.
- **DSP rack** on the output bus — 3-band DJ EQ, AutoTilt, Punch, true-peak limiter (BS.1770-4).
  EBU R128 loudness and ReplayGain 2.0, clip-safe playback normalisation.
- **Live Icecast broadcast** from the player, or from JUST STREAM.
- **Tag writing that leaves other tools alone.** 787 / 787 vendor frame payloads byte-identical
  across 128 real writes on MP3s carrying Serato, Traktor and Mixed In Key data — measured, MP3 +
  ID3v2 only. We keep the *bytes*; we do not decode Serato's cue blobs and never will claim to.
- **Six themes**, live palette swap, no restart.

Serious DJ analysis inside the player, without a library to feed.

### "But 0.6 added a library index"

It did, and the promise holds — because the promise was never *"there will never be an index"*. It
is **you never have to build one before you can play**. Switch it off and no index file is created
at all; browsing behaves exactly as it did in 0.5.

Three features needed it: search everything *below* a folder, filter across a whole crate of
playlists at once, paint a big folder the instant you step into it. All three answer questions about
files you have not opened.

What it is: a cache, not a source of truth. Every value was read out of your files' own tags, so
delete it and you lose speed, nothing else. It lives in your user profile — **nothing is written
next to your music** — which is also why two machines on one NAS need no shared database. It cannot
hide a track from you: files it has never seen are still listed, with blank columns, and a missing
file is flagged rather than dropped.

## New in 0.6.0 — THE LIBRARY

**JUST TAG ships.** The tag editor is packaged, installed and on the Start menu for the first time.
It browses the disk, not a library — the tool for a download that just landed somewhere no index has
seen.

- Multi-file editing, a tick per field. Fields the selection disagrees on start off and say
  `different values`, so nothing is flattened by accident.
- `%artist% - %title%` builds a file name from tags, or reads tags out of a name. Both directions,
  one language, with a live "fits 24 of 37" count.
- Transform: Replace, Title / Sentence case, lower, UPPER, tidy spacing. Apply is unreachable
  without the preview.
- Move / copy / delete, previewed. **There is no overwrite, ever** — a taken name is a collision,
  and the answer is "leave both alone" or "keep both". Delete goes to the recycle bin. A
  cross-volume move is copy, verify by SHA-256, remove.
- Search that reads as a sentence: sixteen fields, seven comparisons, including **is empty**.
  "Genre is empty" is the one that finds the damage.
- Raw-tag viewer — every frame as it sits on disk, other tools' included, read-only by contract.
- ID3 write format: the default converts nothing, and a warning above Save counts how many files a
  conversion would actually re-encode.

**The rest:**

- **The Pre-Cue Finder gained the index.** Include subfolders, filter across all playlists below a
  folder, and folders paint instantly and verify behind you (reading the directory first cost 276 ms
  on a 1,092-file folder, on every open). *Fits what's playing* moves the visible filters, so you
  can widen any of it by hand.
- **One track table** across queue, finder and JUST TAG — same rows, same widths, plus the columns
  tagging needs. Column widths size to the content in front of you.
- **JUST STREAM shows listener count** off the server's public status page. No admin password stored.
- **Every icon is a vector we ship**, not a font character — so icons follow the theme instead of
  whatever font the OS picks.
- Wheel scrolling glides. Round progress indicator. Maximize behaves. `JustPlayCLI analyze` takes
  `--playlist <m3u>` as well as a folder.

Older releases: [the Releases page](https://github.com/SACRVM/just-play/releases).

## Not finished yet

- **Nothing watches your library folder.** The observer is built and tested as a library, wired into
  no app. The index catches up when you press Scan.
- **A scan indexes; it does not analyse.** The batch runner exists and has no screen — use
  right-click → Analyse, or the CLI.
- **The tag-write policy has no settings screen.** What 0.6 writes by default is byte-for-byte what
  0.5 wrote, pinned by SHA-256 goldens.
- **"In how many sets is this track?"** — the index has no playlist-membership table.
- **The analysis light lies about old lossless files.** Builds up to v0.4.0 handed the sample-based
  analyzers interleaved stereo as "mono" on FLAC / WAV / AIFF. Key and BPM were unaffected. The fix
  landed without a detector-version bump, so the staleness check **cannot find those files for you**
  — re-analyse them by hand.
- **macOS / Linux are not validated.** Cross-platform code, no real-hardware run. There is a macOS
  publish script and no Linux one.

## Stack

.NET 10 · [Avalonia 12](https://avaloniaui.net/) (compiled bindings, frameless windows) ·
[ManagedBass](https://github.com/ManagedBass/ManagedBass) + BASS_FX / BASSmix / BASSenc ·
[TagLib#](https://github.com/mono/taglib-sharp) · [ONNX Runtime](https://onnxruntime.ai/) (optional
key model) · [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (source generators,
no reflection, trim/AOT-friendly).

Strict-layered, so analysis, playback, metadata and DSP stay testable with no UI dependency:

```
JustPlay.Core         platform-agnostic: Track, Metadata, MusicalKey, audio abstractions
JustPlay.Audio.Bass   ManagedBass playback, BPM, capture + Icecast broadcast
JustPlay.Metadata     TagLib# reader + writer (consent-gated tag persistence)
JustPlay.Analysis     key, energy, beat fingerprint, structure, sequencer, DSP bus
JustPlay.ML           optional ONNX key detector (falls back to DSP when absent)
JustPlay.Engine       analysis / tagging facade
JustPlay.Library      local SQLite index, batch scan
JustPlay.UI           shared suite UI: chrome, themes, track table, tag editor
JustPlay.App          JUST PLAY      JustPlay.Stream  JUST STREAM
JustPlay.Tag          JUST TAG       JustPlay.Cli     JustPlayCLI
```

The three app shells and `JustPlay.UI` are the only projects that know Avalonia. `JustPlay.Core`
knows nothing platform-specific.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\build\watch.ps1                          # hot-reload dev loop (F12 = Avalonia DevTools)
dotnet run --project src/JustPlay.App      # or .Stream / .Tag
dotnet test                                # the suite
.\build\publish-win-x64.ps1                # self-contained drop, all four tools
.\build\publish-installer.ps1              # per-user installer (needs Inno Setup 6)
```

The product version is the single `<Version>` in `Directory.Build.props`. The About dialogs, the
installer name and the release tag all derive from it.

## Roadmap

**Next:** the parts of THE LIBRARY that ship as engines without screens — folder observer, in-app
batch analysis, tag-write policy settings. Then validated macOS and Linux builds.

After that: a transparent broadcast maximiser + anti-harshness stage for JUST STREAM · an MCP
surface over `JustPlay.Engine` so agents can drive analysis and tagging without the GUI · Harmonic
Sort P2, "what mixes next" as a ranked list · the byte-identity measurement extended to FLAC / AIFF
/ WAV / MP4.

Not on the roadmap: decoding another app's cue and grid blobs.

## License

[MIT](LICENSE) for the JustPlay source.

**BASS is not MIT.** `src/JustPlay.Audio.Bass/native/` ships BASS, BASS_FX, BASSmix, BASSenc,
bassenc_mp3 and bassenc_opus from [un4seen.com](https://www.un4seen.com/), which are free for
personal and non-commercial use. Shipping something commercial on top of JustPlay needs your own
BASS licence — the MIT licence here grants you no rights to BASS.

## Credits

By **[SACRVM](https://sacrvm.dev)**, published under the suite's name.

UI ported from the [Claude Design](https://claude.ai) mockups in `.design/`. Spinning vinyl and
glossy chrome from the heyday of iTunes, repainted in a 2026 aurora palette. Built on
[Avalonia](https://avaloniaui.net/), [ManagedBass](https://github.com/ManagedBass/ManagedBass) and
[TagLib#](https://github.com/mono/taglib-sharp).
