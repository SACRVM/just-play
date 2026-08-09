# JustPlay

The music player for DJs and music lovers — Windows / macOS / Linux.
Drop tracks in, double-click to play. No library, no memory between sessions, no nag — just play.

*Part of **J.U.S.T.** — Just Useful Sound Tools · **FROM DJS TO DJS** — built and gig-tested by a working DJ.*

> **v0.5.0 — still pre-1.0, but a daily driver on Windows.**
> This release adds the headphone **Pre-Cue Finder** — a keyboard-first window to browse your whole
> library and audition the next track on your headphones while the set (or the stream) keeps playing —
> plus **set recording** in JUST STREAM (record like you stream) and DJ-owned **station metadata**.
> The installer ships the suite side by side: **JUST PLAY** (the player), **JUST STREAM** (the
> broadcaster), **JUST TAG** (the tag editor) and the headless **JustPlayCLI** tool, all sharing one
> runtime. JUST TAG landed after v0.5.0 was tagged, so it arrives with the next build.
> macOS / Linux share the codebase but aren't validated yet. Expect rough edges; the core
> functionality plays, analyses, sorts and streams today.

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

The DJ tilt comes from analysis baked straight into the player:

- **BPM detection** — BASS_FX, offline on track add.
- **Camelot key detection** — the headline feature: a hand-rolled chromagram + EDMA key
  profiles, with an optional ONNX "AI key" model used automatically when present. Build a
  harmonic set without a separate tool.
- **Energy score** (1–10) for ordering by intensity.
- **Harmonic Sort** — reorder the whole queue for the smoothest mix, scoring each pair on
  key + tempo + energy + groove (beat fingerprint).

Serious DJ analysis, living *inside* the player — without the library overhead.

## New in 0.5.0

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

## JUST TAG — the tag editor

A third app in the same installer, sharing JUST PLAY's shell, track table and tag editor. It browses
the **disk**, not a library — the tool you reach for when a download just landed somewhere no index
has ever seen — and it edits one file or a whole selection with a look-before-you-leap summary in
front of every write. Landed after v0.5.0 was tagged; ships with the next build.

- **Your DJ metadata survives, and that is measured.** 128 real writes through the shipped writer,
  on MP3s carrying `GEOB:Serato Markers2`, an 85,166-byte `PRIV:TRAKTOR4` blob, Mixed In Key's
  fields and a `POPM` rating, diffed frame by frame with a hand-written ID3v2 walker:
  **787 / 787 vendor frame payloads byte-identical, 128 / 128 audio streams untouched** (measured
  2026-07-31, `.claude/night-reports/2026-07-31-L3-taglib-bytes.md`). Measured on **MP3 + ID3v2
  only** — FLAC / AIFF / WAV / MP4 are untested. We preserve **bytes**; we do not decode Serato's
  cue blobs and never claim to.
- **Multi-file editing** — a tick per field decides what takes part in the save; ticked-and-empty
  means "clear this on all of them", the cover included. Fields the selection agrees on start ticked
  and editable in one go; fields where they differ start off, so nothing is flattened by accident.
- **One mask language, both directions** — `%artist% - %title%` builds a name out of the tags, or
  reads tags out of a name; folder segments included. It counts the selection against the pattern as
  you type ("fits 24 of 37"), there is a cheat-sheet window, and a name that does not fit is left
  exactly as it was.
- **Transform** — replace, Title/Sentence case, tidy whitespace. Apply cannot be reached without the
  preview: one line per file per field, before and after, unchanged files not listed.
- **Move / copy / delete, previewed** — **there is no overwrite, ever**; a taken name is a collision
  and the answer is "leave both alone" (default) or "keep both". Delete goes to the recycle bin and
  refuses where there is none. A cross-volume move is copy - verify (SHA-256) - remove.
- **Raw tags** — every frame in the file exactly as it sits on disk, other tools' frames included,
  read-only by contract. The proof of the measurement above, in the app.
- **ID3 write format** — default is *keep the file's version* (convert nothing); the three
  converting modes are a deliberate act, with a warning where a conversion would actually re-encode
  the GEOB labels Serato and Mixed In Key look their data up by.
- **Listen while you tag** — a preview transport in the panel (load / play / pause / seek) that
  releases the file by itself when a save needs it.
- **The shared track table** — same rows, widths and sorting as the queue and the Pre-Cue Finder,
  with a right-click column picker and JUST TAG's own columns: the analysis traffic light, cover
  tick, ID3 version, file type. It *shows* analysis; detection stays JUST PLAY's and the CLI's job.

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
| **JUST TAG** — standalone tag editor                            | ✅ in the installer, lands in the next build      |
| JUST TAG — multi-file edit · mask both ways · transform         | ✅ every bulk write previewed first               |
| JUST TAG — move / copy / delete                                 | ✅ previewed · recycle bin · never overwrites     |
| Vendor frames (Serato / Traktor / MIK) survive our writes       | ✅ measured 787/787 byte-identical (MP3 + ID3v2)  |
| Raw-tag viewer — every frame as it sits on disk                 | ✅ read-only by contract                          |
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

**Next — v0.6.0:** validated **macOS + Linux** builds — the codebase is cross-platform; they just
need real-hardware runs and any OS-specific device-picker wiring — plus a metadata **cache** for the
Pre-Cue Finder so revisiting a big folder is instant.

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

- By **Chloe Dream**.
- UI faithfully ported from [Claude Design](https://claude.ai) mockups bundled in `.design/`.
- Spinning-vinyl + glossy chrome inspired by the heyday of iTunes / Music.app, repainted
  with a 2026 aurora palette.
- Built with [Avalonia](https://avaloniaui.net/), [ManagedBass](https://github.com/ManagedBass/ManagedBass)
  and [TagLib#](https://github.com/mono/taglib-sharp) — projects that quietly do the
  heavy lifting so the rest of us can write XAML.
