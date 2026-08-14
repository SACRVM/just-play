# JustPlay

The music player for DJs and music lovers — Windows / macOS / Linux.
Drop tracks in, double-click to play. No library to build, no memory between sessions, no nag — just play.

*Part of **J.U.S.T.** — Just Useful Sound Tools · **FROM DJS TO DJS** — built and gig-tested by a working DJ.*

> **v0.6.0 beta — still pre-1.0, but a daily driver on Windows.**
> This is the milestone the project calls **THE LIBRARY**, and for you the headline is a third app:
> **JUST TAG**, the tag editor, which is packaged, installed and on the Start menu for the first
> time. Underneath it the suite gained a **local library index** that the Pre-Cue Finder and JUST TAG
> both read — opt-in, off until you switch it on, and never written next to your music.
> The installer ships the suite side by side: **JUST PLAY** (the player), **JUST STREAM** (the
> broadcaster), **JUST TAG** (the tag editor) and the headless **JustPlayCLI** tool, all sharing one
> runtime.
> It is a beta and it says where it is thin: several parts of this milestone are finished as
> libraries and wired to no screen, and they are named under
> [What isn't finished in this beta](#what-isnt-finished-in-this-beta) rather than left for you to
> discover. macOS / Linux share the codebase but aren't validated yet. The core functionality plays,
> analyses, sorts, tags and streams today.

## Why another music player

Every "modern" desktop player insists on managing a library, scanning folders, deciding
which artist photo to fetch, asking you to log in. JustPlay aims for the opposite:

- A quick window you **drop tracks onto and play.**
- **No state between sessions** — close it, reopen it, the queue is empty again. The only
  memory it keeps is the one *you* allow: detected values written into the files' own tags
  (the file is the memory, not a hidden database).
- **Cross-platform from day 1** — Windows / macOS / Linux, single codebase.
- **Small + fast deployment** — self-contained (no .NET install on the end user's machine),
  no C++ build tools required to build.

### "But 0.6 added a library index"

It did — and the promise is intact, because the promise was never *"there will never be an
index"*. It is **you never have to build one before you can play**. That is still true: the
index is off until you switch it on, and with it off, no index file is created at all and
browsing behaves exactly as it did in 0.5.

Why it exists, and what it actually is:

- **Three features needed it.** Search everything *below* a folder instead of one directory at
  a time; filter across all the playlists in a crate at once; paint a big folder the instant you
  step into it. Each of those has to answer a question about files you have not opened, and
  there is no way to do that from nothing.
- **It is a cache, not a source of truth.** Every value in it was read out of your files' own
  tags. Delete it and you lose speed, nothing else — it rebuilds from the same tags. That is
  also why two machines pointed at one NAS each keep their own and need no shared database
  between them.
- **Nothing is written next to your music.** The index lives in your user profile. A music
  folder after JustPlay looks exactly like a music folder before it.
- **It cannot hide a track from you.** Files it has never seen are still listed, with blank
  columns; a file that has gone missing is flagged rather than quietly dropped.
- **Opt-in and explicit.** One toggle in the finder settings, and scanning is a button you
  press — nothing crawls your disk in the background.

A library you *must* build before the player is useful: no, and that will not change. An index
you *may* build because it makes three specific things fast: yes, since 0.6.

The DJ tilt comes from analysis baked straight into the player:

- **BPM detection** — BASS_FX, offline on track add.
- **Camelot key detection** — the headline feature: a hand-rolled chromagram + EDMA key
  profiles, with an optional ONNX "AI key" model used automatically when present. Build a
  harmonic set without a separate tool.
- **Energy score** (1–10) for ordering by intensity.
- **Harmonic Sort** — reorder the whole queue for the smoothest mix, scoring each pair on
  key + tempo + energy + groove (beat fingerprint).

Serious DJ analysis, living *inside* the player — without the library overhead.

## New in 0.6.0 — THE LIBRARY

### JUST TAG — the tag editor, and the third app in the installer

A third app in the same installer, sharing JUST PLAY's shell, track table and tag editor. It browses
the **disk**, not a library — the tool you reach for when a download just landed somewhere no index
has ever seen — and it edits one file or a whole selection with a look-before-you-leap summary in
front of every write. It has never been in a release before — 0.6.0 is the first build that packages
and installs it, and everything below came with it.

- **Your DJ metadata survives, and that is measured.** 128 real writes through the shipped writer,
  on MP3s carrying `GEOB:Serato Markers2`, an 85,166-byte `PRIV:TRAKTOR4` blob, Mixed In Key's
  fields and a `POPM` rating, diffed frame by frame with a hand-written ID3v2 walker:
  **787 / 787 vendor frame payloads byte-identical, 128 / 128 audio streams untouched** (measured
  2026-07-31, `.claude/night-reports/2026-07-31-L3-taglib-bytes.md`). Measured on **MP3 + ID3v2
  only** — FLAC / AIFF / WAV / MP4 are untested. We preserve **bytes**; we do not decode Serato's
  cue blobs and never claim to.
- **Multi-file editing** — a tick per field decides what takes part in the save; ticked-and-empty
  means "clear this on all of them", the cover included. Fields the selection agrees on start ticked
  and editable in one go; fields where they differ start off and say `different values`, so nothing
  is flattened by accident.
- **One mask language, both directions** — `%artist% - %title%` builds a name out of the tags, or
  reads tags out of a name; folder segments included, and `%dummy%` throws a segment away. It counts
  the selection against the pattern as you type ("fits 24 of 37"), there is a **Pattern help**
  window with the whole language in it, and a name that does not fit is left exactly as it was.
- **Transform** — Replace text, Title Case, Sentence case, lowercase, UPPERCASE and Tidy spacing,
  applied to what each file already has. Apply cannot be reached without the preview: one line per
  file *per field*, before and after, unchanged files not listed.
- **Move / copy / delete, previewed** — **there is no overwrite, ever**; a taken name is a collision
  and the answer is "leave both alone" (default) or "keep both". Delete goes to the recycle bin and
  refuses where there is none. A cross-volume move is copy - verify (SHA-256) - remove.
- **A search that reads as a sentence** — sixteen fields (every editable tag, plus cover, ID3
  version, file type and file name) and seven ways to compare, including **is empty**. "Genre is
  empty" is the one that finds the damage. A second condition joins with AND or OR.
- **Raw tags** — every frame in the file exactly as it sits on disk, other tools' frames included,
  read-only by contract. ID3v2, Xiph and APEv2 in full; ID3v1 and MP4 atoms come back with a named
  reason rather than an empty table. The proof of the measurement above, in the app.
- **ID3 write format** — the default is *keep each file's version* (convert nothing); the three
  converting modes are a deliberate act, and a warning above Save counts how many files in the
  selection a conversion would actually re-encode, because Serato and Mixed In Key look their data
  up by the frame labels a conversion rewrites.
- **Listen while you tag** — a preview transport in the panel (load / play / pause / seek) that
  releases the file by itself when a save needs it.

### A library index — opt-in, local, and off until you ask for it

The suite now keeps an index of your library, and the shape of it follows from one constraint: **the
files stay the truth.** Everything JustPlay measures already lives in the tags, so each computer
derives its own local SQLite index from them. Two machines on the same NAS therefore both stay
current without a shared database, and **nothing is ever written next to your music** — the index
lives in your user profile.

It is off until you turn it on (*"Keep an index of this library"* in the finder settings), and
scanning is one explicit button, *"Scan library"*. If you never scan, no index file is created at
all and browsing behaves exactly as it did in 0.5. The CLI is the one deliberate exception:
running `JustPlayCLI analyze` *is* the opt-in, and `--no-db` turns it off.

A track on disk is never invisible because of the index: un-analysed and un-indexed files are listed
with blank columns, a file that has gone missing is flagged rather than dropped, and a folder is
compared against disk behind the list you are already looking at.

### The Pre-Cue Finder, with the index behind it

- **Include subfolders** — search everything below the folder you are standing in instead of one
  directory. It needs the index, so when it cannot be used the checkbox is disabled *with the reason
  on it* rather than left as a bare grey control.
- **Filter across the playlists below a folder** — stand above your sets and search the whole crate
  at once instead of opening eight playlists one at a time. A track in five sets is one row, and the
  detail pane says which sets it came from.
- **Folders paint from the index and are verified behind you.** Reading the directory first cost
  276 ms on a 1,092-file folder, on every single open, almost always to learn that nothing had
  changed; the enumeration still happens, just behind an already-painted list.
- **Fits what's playing** narrows to the playing track's key plus its harmonic neighbours, a ±3 BPM
  window and energy from one step below upward — by moving the *visible* filters, so the key wheel
  lights up, the sliders move, and you can widen any of it by hand.
- Every state of the index says what it is: never scanned with an empty pane offers the scan; never
  scanned with songs on screen gets a slim strip explaining why searching deeper stays locked.

### One track table across all three apps

The queue, the Pre-Cue Finder and JUST TAG are now literally the same table — same rows, same
widths, same click-to-sort headers — plus the columns tagging needs: ALBUM, ALBUM ARTIST, YEAR,
`#`, COV (has a cover), ID3 (which version), TYPE, FILE NAME, and AN, a three-state analysis light
(current / older detector / none). Right-click the header to choose them.

Column widths are sized from the content actually in front of you — each text column weighted by
the 90th percentile of its character count, per view — so genre stops getting exactly as much room
as the title. The shared tag editor is one panel with three hosts now: JUST TAG, the Pre-Cue Finder
and the UP NEXT queue can all hand it a multi-file selection. Writing onto the **playing** track
still defers to the track change, so in a selection of twelve the eleven that are idle are written
immediately and the report counts them separately.

### JUST STREAM — how many people are listening

A **LISTENERS** readout on the console, with the session peak beside it. It comes off the server's
public status page over an ordinary HTTP GET, so no admin password is stored anywhere; it polls
every 15 s on air and 60 s off, one request at a time, and a dead server costs the poll and never
the broadcast.

### Smaller, but you'll notice them

- **Every icon is a vector we ship.** Not a font character: the same codepoint came out bright
  yellow in one window and monochrome white in another, on the same machine, because the OS picks
  the font and we do not — and on macOS many of those symbols would render as colour emoji. Icons
  now follow the theme like everything else.
- **The wheel glides.** A wheel notch used to move a list in one jump — three rows at a time at
  30-px rows — which no frame rate can smooth. Only the wheel is intercepted; scrollbar drags,
  keyboard and touch are untouched. The scrollbar also stops fading out while you drag its thumb.
- **A round progress indicator** for long jobs, with the phase and the counter inside the disc at
  fixed sizes, so nothing shifts as the text changes.
- **Maximize does what it says** — the rounded corners and the transparent shadow margin go away,
  and the caption button finally shows *restore* on a maximized window.
- **CLI:** `analyze` takes `--playlist <m3u>` as well as a folder, so a scattered list of tracks can
  be re-analysed without pointing it at their common root and redoing thousands of healthy files. A
  listed file that no longer exists is reported by name instead of dropped in silence.

### What isn't finished in this beta

- **Nothing watches your library folder yet.** The observer — settle window, batched sweeps, yields
  to playback — is built and tested as a library and is wired into no app. The index catches up when
  you press Scan, or when you open a folder whose fingerprint moved.
- **A scan indexes; it does not analyse.** Tracks with no analysis are listed with blank columns and
  nothing offers to run the detectors over them in bulk. Analysing is still per-selection
  (right-click → Analyse) or `JustPlayCLI analyze`. The batch runner exists and has no screen.
- **The tag-write policy has no settings screen.** The writer can be told which frame families to
  write, and there is a preview that answers "what would this change" without opening the file for
  writing — but neither has a UI, and there are no dry-run counts on screen. What 0.6 writes by
  default is byte-for-byte what 0.5 wrote, pinned by SHA-256 goldens over the test corpus.
- **"In how many sets is this track?" is not built.** The index has no playlist-membership table.
- **The analysis light can be wrong on lossless files analysed by v0.4.0 or earlier.** Those builds
  handed the sample-based analyzers interleaved stereo as "mono" on FLAC, WAV and AIFF, so loudness
  and the vibe / groove scalars were computed on octave-shifted input. Key and BPM were not affected
  — they take other paths. The fix landed during 0.5 without bumping the detector version, so the new
  AN light calls those files current and **the staleness check cannot find them for you**. If you
  have lossless tracks analysed that long ago, re-analyse them by hand.
- **macOS / Linux still are not validated.** There is a macOS publish script and the code is
  cross-platform; nobody has run a build on real hardware. Windows is the only tested target, and
  there is no Linux publish script at all.

## Previously — v0.5.0

### Pre-Cue Finder — audition on headphones (JUST PLAY)

A dedicated, keyboard-first window to browse your whole library — folders and playlists both — with the
same analysis columns as the queue (Camelot key, BPM, energy, groove). Land on a track and it plays
instantly on your **headphones** while the set keeps playing to the room. On a single output it
**ducks** the main sound while you cue and brings it back after; if you're streaming, the broadcast is
never touched. A filter tab narrows a big folder fast — name/artist search, a **Camelot key wheel**
(harmonic neighbours one tap away) and range sliders per analysed field. **Right-click a folder or
playlist** to add it to the queue or open it as your set.

### Record your set (JUST STREAM)

**Set-and-forget capture — you record like you stream.** The file is the finished, limited master that
goes on air, so it's already loudness-managed. **Auto-record** starts with CONNECT; **auto-trim** begins
the file on the first beat and cuts the dead air (no "15 minutes of silence before the music"). MP3 /
FLAC / AIFF / WAV, and a recording glitch can never interrupt the broadcast.

### Station metadata (both apps)

DJ-owned stream metadata carried into Icecast — **website, genre, description** and a
**public-directory** toggle — so your station shows up properly in players and directories.
Functionally identical in JUST PLAY and JUST STREAM; the UI differs to match each app's focus.

## Previously — v0.4.0

### JUST STREAM — the broadcaster app

A standalone sister app, shipped in the same installer, sharing JUST PLAY's engine, bus DSP and
look. It takes a live audio source, runs it through the shared broadcast chain, and casts it to an
Icecast server — the point being to stream your set without a rat's nest of virtual cables.

- **Capture a specific app** — the headline feature. Instead of grabbing a whole sound card,
  JUST STREAM can capture the audio of **one application** (Windows per-process loopback):
  your DJ software (Traktor / rekordbox / Serato / VirtualDJ / djay), a browser tab, anything.
  Nothing else on your system leaks into the stream.
- **Cue-free by default** — on a multi-out DJ interface (Master on one channel pair, headphone
  Cue on another), it auto-isolates the **Master pair** so your private cue never goes on air.
  No routing to set up; it just works.
- **MP3 + Opus** — 128 / 192 / 256 / 320 kbps, in-process encoders (LAME / libopus), multiple
  saved server profiles, PUT or SOURCE, optional TLS.
- **Broadcast DSP** — the same EQ · AutoTilt · Punch · Limiter bus as the player, tuned for
  loudness so a live set stays competitively loud without you having to gain-stage mid-mix.
- **Live monitor + spectrum + level/limiter meters**, framed in the same look as the player.

> Capturing a specific app uses the Windows shared audio engine, so apps running in **ASIO /
> WASAPI-exclusive** mode can't be captured that way — the app shows an inline note when that's
> the case.

### Genre DSP presets (shared across both apps)

The one-click presets are now genre starting points — **Electronic · Hard · Rock** (plus
**Neutral**, the flat bypass). The *tonal* identity is identical in both apps; JUST STREAM just
runs the limiter one notch louder for broadcast, JUST PLAY stays transparent for monitoring.
Every preset is fully editable, and you can save your own.

### In-app spectral analyser

A live before/after tonal-balance view — DRY (pre-bus) vs WET (post-bus) plus limiter
gain-reduction — shared by JUST PLAY and JUST STREAM, so you can actually *see* what the DSP
rack is doing to your sound.

### Carried over from 0.3.x

The full output bus DSP rack (3-band DJ EQ · AutoTilt one-dial master · Transient/Punch ·
true-peak Limiter to BS.1770-4), EBU R128 loudness + ReplayGain 2.0 with clip-safe playback
normalisation (Quiet / Normal / Loud), equal-power crossfade on auto-advance, six live themes
(Aurora · Sunset · Midnight · Onyx · Neon · Hardcore), and the per-user installer with in-app
auto-update all remain.

---

## Status

| Area                                                            | State                                            |
| --------------------------------------------------------------- | ------------------------------------------------ |
| Drop / play / pause / next / prev                               | ✅ works                                          |
| Shuffle (bag-with-history) · repeat · consume mode              | ✅ works                                          |
| Metadata read **+ write** (TagLib#)                             | ✅ works                                          |
| BPM detection                                                   | ✅ BASS_FX, async per track on add               |
| Camelot key detection                                           | ✅ chromagram + EDMA profiles (+ optional ONNX)  |
| Energy score                                                    | ✅ spectral, 1–10                                |
| Beat fingerprint + structure detection                          | ✅ feeds Harmonic Sort                            |
| Harmonic Sort (mix sequencer)                                   | ✅ key + tempo + energy + groove                 |
| Tag persistence — write BPM/Key/Energy to file tags             | ✅ consent-gated, per-field, full undo            |
| Loudness analysis (EBU R128) + ReplayGain tags                  | ✅ written on analysis, readable by any player   |
| Playback normalisation (Quiet / Normal / Loud)                  | ✅ clip-safe, non-destructive                    |
| Crossfade on auto-advance (Off / 2 / 4 / 8 s)                  | ✅ equal-power, smart-lite skip                  |
| Output bus DSP rack (EQ · AutoTilt · Punch · Limiter)          | ✅ shapes local playback + Icecast stream         |
| Genre DSP presets (Electronic / Hard / Rock / Neutral)          | ✅ shared across JUST PLAY + JUST STREAM          |
| In-app spectral analyser (DRY/WET + limiter GR)                 | ✅ shared player + stream                         |
| Live Icecast broadcast (from the player)                        | ✅ BASSmix + BASSenc, MP3 + Opus, multi-server    |
| **JUST STREAM** — standalone broadcaster                        | ✅ ships in the same installer                    |
| **JUST TAG** — standalone tag editor                            | ✅ built, packaged and installed since 0.6.0      |
| JUST TAG — multi-file edit · mask both ways · transform         | ✅ every bulk write previewed first               |
| JUST TAG — move / copy / delete                                 | ✅ previewed · recycle bin · never overwrites     |
| Vendor frames (Serato / Traktor / MIK) survive our writes       | ✅ measured 787/787 byte-identical (MP3 + ID3v2)  |
| Raw-tag viewer — every frame as it sits on disk                 | ✅ read-only by contract                          |
| **Local library index** (SQLite, per machine)                   | ✅ opt-in · never written next to your music      |
| Finder — include subfolders · search across playlists           | ✅ index-backed, locked with a reason without it  |
| Shared track table (queue · finder · JUST TAG)                  | ✅ one table, content-sized columns               |
| JUST STREAM — listener count off the public status page         | ✅ no admin password stored                       |
| Library observer (watch a folder, keep the index fresh)         | ⬜ engine + tests, wired into no app yet          |
| Batch analyse from inside the app                               | ⬜ runner exists, no screen; use the CLI          |
| Tag-write policy — pick which frame families get written        | ⬜ gated in the writer, no settings UI yet        |
| JUST STREAM — capture a specific app (per-process loopback)     | ✅ auto-isolates Master on multi-out DJ gear      |
| **Pre-Cue Finder** — headphone audition + cue ducking           | ✅ browse · filter (key wheel) · open-playlist    |
| Set recording (JUST STREAM) — auto-record + auto-trim silence   | ✅ records the limited master · MP3/FLAC/AIFF/WAV  |
| Station metadata (website / genre / description / public dir)    | ✅ JUST PLAY + JUST STREAM                         |
| Theme switch (Aurora / Sunset / Midnight / Onyx / Neon / Hardcore) | ✅ live palette swap, repaints the app icon   |
| Installer + in-app auto-update                                  | ✅ Inno Setup (per-user) + GitHub Releases       |
| macOS / Linux builds                                            | ❌ target, not validated yet                    |

## The J.U.S.T. suite

One installer drops the whole suite into a single shared folder — one copy of the .NET runtime,
the Avalonia natives and the BASS natives serve every tool:

- **JustPlay.exe** — the GUI player (analysis, Harmonic Sort, DSP, local Icecast streaming).
- **JustStream.exe** — the standalone broadcaster (capture-an-app, broadcast DSP, Icecast).
- **JustTag.exe** — the tag editor (browse a folder, edit one file or a whole selection, rename from
  tags and read tags out of names, move / copy / delete with a preview, raw-tag viewer).
- **JustPlayCLI.exe** — the headless library tool (scan / analyse / tag / sort / stats) over the
  same engine, for power-user and agent workflows.

They share one look and feel by design — frameless rounded windows, a theme-gradient app icon that
repaints on theme switch, the About mark top-left — so a new suite app feels like a sibling on
first run.

## Stack

- **.NET 10** SDK (the project's libraries target net8+)
- **[Avalonia 12](https://avaloniaui.net/)** for the UI — frameless window, transparent
  surround, custom skeumorphic Templates, compiled bindings throughout
- **[ManagedBass](https://github.com/ManagedBass/ManagedBass) + BASS_FX / BASSmix / BASSenc**
  (MP3 via bassenc_mp3, Opus via bassenc_opus) for playback, BPM detection and Icecast broadcasting
- **[TagLib#](https://github.com/mono/taglib-sharp)** for tag read/write
- **[ONNX Runtime](https://onnxruntime.ai/)** for the optional trained key model
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** — source generators,
  no reflection, trim/AOT-friendly
- **Microsoft.Extensions.DependencyInjection** — composition root in `Program.cs`

The codebase is **strict-layered** so the analysis / playback / metadata / DSP libraries stay
testable without any UI dependency:

```
JustPlay.Core         — platform-agnostic: Track, Metadata, MusicalKey, audio abstractions
JustPlay.Audio.Bass   — ManagedBass playback, BPM detection, capture + Icecast broadcast
JustPlay.Metadata     — TagLib# metadata reader + writer (consent-gated tag persistence)
JustPlay.Analysis     — key (chromagram/EDMA), energy, beat fingerprint, structure,
                        sequencer, DSP bus (EQ / AutoTilt / Punch / Limiter)
JustPlay.ML           — optional ONNX "AI key" detector (falls back to DSP when absent)
JustPlay.Engine       — analysis / tagging facade (shared library)
JustPlay.Library      — local SQLite index, batch scan, "what counts as a track"
JustPlay.UI           — shared suite UI lib: window chrome, themed icon, theme engine, spectrum,
                        the shared track table, the shared tag editor, organise / transform windows
JustPlay.App          — JUST PLAY: the Avalonia player shell
JustPlay.Stream       — JUST STREAM: the Avalonia broadcaster shell
JustPlay.Tag          — JUST TAG: the Avalonia tag-editor shell
JustPlay.Cli          — JustPlayCLI: headless analysis / tagging / sorting tool
```

`JustPlay.App`, `JustPlay.Stream` and `JustPlay.Tag` are the only projects that know about Avalonia
(with the shared `JustPlay.UI`). `JustPlay.Core` knows about nothing platform-specific.

## Build & run

You need the .NET 10 SDK installed (download from https://dotnet.microsoft.com/download).

```powershell
# Hot-reload dev loop — save a .axaml and the running window updates without restart.
# Pairs nicely with Avalonia DevTools (F12 in the running app).
.\build\watch.ps1

# One-off run:
dotnet run --project src/JustPlay.App      # the player
dotnet run --project src/JustPlay.Stream   # the broadcaster
dotnet run --project src/JustPlay.Tag      # the tag editor

# Release publish (Windows self-contained shared folder — player + broadcaster + tagger + CLI side
# by side, one runtime, no .NET install needed on the target machine, no C++ toolchain to build):
.\build\publish-win-x64.ps1

# Build the per-user installer (needs Inno Setup 6):
.\build\publish-installer.ps1

# Run the test suite:
dotnet test
```

The repo is a multi-project solution — `JustPlay.slnx` ties everything together. The product
version is the single `<Version>` in `Directory.Build.props`; the About dialogs read it back
at runtime and the installer / release pipeline read the same value.

## Roadmap

**Next:** finish the parts of THE LIBRARY that this beta ships as engines without screens — the
folder observer, batch analysis from inside the app, and the tag-write policy's settings and
dry-run counts. Then validated **macOS + Linux** builds: the codebase is cross-platform, but they
need real-hardware runs and any OS-specific device-picker wiring.

Beyond that, roughly in priority order:

1. **JUST STREAM depth** — a transparent broadcast maximiser + anti-harshness (de-fatigue)
   stage on top of the shared bus, for loud-but-listenable streams on harsh material.
2. **MCP / agent surface** over `JustPlay.Engine` — so external agents and tools can drive
   analysis, tagging and sorting without the GUI.
3. **Harmonic Sort P2 — "what mixes next"** — surface a ranked list of compatible next tracks
   from the queue, not just a global sort order.
4. **DJ metadata interop** — the preservation half is settled and measured (787/787 vendor frame
   payloads byte-identical on MP3 + ID3v2), and JUST TAG's raw-tag viewer shows it. What is *not*
   on the roadmap: decoding another app's cue / grid blobs. We keep the bytes; we do not read them.
   Open: the same measurement for FLAC / AIFF / WAV / MP4.

## License

[MIT](LICENSE) for the JustPlay source.

### Third-party notice

The `src/JustPlay.Audio.Bass/native/` folder ships **BASS**, **BASS_FX**, **BASSmix**,
**BASSenc**, **bassenc_mp3** and **bassenc_opus** from [un4seen.com](https://www.un4seen.com/).
BASS is **free for personal / non-commercial use**. If you intend to ship something based on
JustPlay commercially you will need your own BASS licence from un4seen — JustPlay's MIT licence
does NOT grant you any rights to BASS itself. See https://www.un4seen.com/ for licence terms.

## Credits

- By **Marcus Wilhelm**. Part of **J.U.S.T. — Just Useful Sound Tools**, published under the
  suite's name. **From DJs to DJs**: built and gig-tested by a working DJ.
- UI faithfully ported from [Claude Design](https://claude.ai) mockups bundled in `.design/`.
- Spinning-vinyl + glossy chrome inspired by the heyday of iTunes / Music.app, repainted
  with a 2026 aurora palette.
- Built with [Avalonia](https://avaloniaui.net/), [ManagedBass](https://github.com/ManagedBass/ManagedBass)
  and [TagLib#](https://github.com/mono/taglib-sharp) — projects that quietly do the
  heavy lifting so the rest of us can write XAML.
