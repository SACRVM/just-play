# JustPlay

The music player for DJs and music lovers — Windows / macOS / Linux.
Drop tracks in, double-click to play. No library, no memory between sessions, no nag — just play.

*Part of **J.U.S.T.** — Just Useful Sound Tools.*

> **v0.3.0 — still pre-1.0, but daily-driver ready on Windows.**
> Headline analysis (BPM · Camelot key · energy), a full output DSP bus, loudness normalisation,
> crossfade, and an installer with auto-update all ship in this release.
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
- **Small + fast deployment** — self-contained single-file `.exe`, no .NET install on the
  end user's machine, no C++ build tools required to build.

The DJ tilt comes from analysis baked straight into the player:

- **BPM detection** — BASS_FX, offline on track add.
- **Camelot key detection** — the headline feature: a hand-rolled chromagram + EDMA key
  profiles, with an optional ONNX "AI key" model used automatically when present. Build a
  harmonic set without a separate tool.
- **Energy score** (1–10) for ordering by intensity.
- **Harmonic Sort** — reorder the whole queue for the smoothest mix, scoring each pair on
  key + tempo + energy + groove (beat fingerprint).

Serious DJ analysis, living *inside* the player — without the library overhead.

## New in 0.3.0

### Output bus DSP rack

Every track — whether playing locally or streaming to Icecast — passes through a shared bus:

- **3-band DJ EQ** (series RBJ shelves): boost or cut low / mid / high, with a deep-kill
  per band. Non-destructive; bypass at neutral.
- **AutoTilt** — a gentle auto-master that nudges the track's tonal balance toward a reference
  ("golden") spectral curve. Think of it as a one-dial mastering assistant: at zero it does
  nothing; dialled up it brings a harsh or dull mix closer to a target sound.
- **Transient / Punch** — adds or softens attack to sharpen or smooth the groove feel.
- **Mastering Limiter** — true-peak limiter to ITU-R BS.1770-4, ceiling −1 dBTP. Transparent
  at normal levels; catches the occasional hot peak before it clips your output or your stream.

**One-click presets:** *Neutral* (full bypass — flat signal path) and *Hard* (targets the
harsh top-end of hard-techno / hardstyle toward the reference curve and pushes it loud via
the limiter).

### Loudness / ReplayGain

EBU R128 LUFS measurement, written as ReplayGain 2.0 tags. **Playback normalisation**
switchable per-session: *Quiet / Normal / Loud*. Clip-safe (true-peak aware), non-destructive
(no file is reencoded).

### Crossfade on auto-advance

Equal-power crossfade when one track ends and the next begins. Duration: Off / 2 s / 4 s / 8 s.
A "smart-lite" mode skips the crossfade when key or tempo distance between tracks is too large
to blend gracefully.

### Hardcore theme + five live palettes

A fifth theme: **Hardcore** (black / red accent / cyan highlight). The theme picker now offers
Aurora · Sunset · Midnight · Neon · Hardcore — live palette swap, no restart.

### Installer + in-app auto-update

Inno Setup installer (per-user, no UAC prompt). In-app update check polls GitHub Releases;
a one-click flow downloads and installs the new version silently and relaunches.

### Playlist save / update · Like column · A|B|C column views

Save and reload `.m3u` playlists. The track grid now supports A|B|C column-view lenses plus an
optional **Like** column (POPM tag) so you can flag favourites without leaving the queue.

---

## Status

| Area                                                            | State                                            |
| --------------------------------------------------------------- | ------------------------------------------------ |
| Drop / play / pause / next / prev                               | ✅ works                                          |
| Shuffle (bag-with-history) · repeat · consume mode              | ✅ works                                          |
| Volume + position slider                                        | ✅ works                                          |
| Metadata read **+ write** (TagLib#)                             | ✅ works                                          |
| Mini / Max view layout                                          | ✅ shared transport cluster, skeu look            |
| BPM detection                                                   | ✅ BASS_FX, async per track on add               |
| Camelot key detection                                           | ✅ chromagram + EDMA profiles (+ optional ONNX)  |
| Energy score                                                    | ✅ spectral, 1–10                                |
| Beat fingerprint + structure detection                          | ✅ feeds Harmonic Sort                            |
| Harmonic Sort (mix sequencer)                                   | ✅ key + tempo + energy + groove                 |
| Tag persistence — write BPM/Key/Energy to file tags             | ✅ consent-gated, per-field, full undo            |
| Like / favourite (POPM) · remove duplicates                     | ✅ works                                          |
| Playlist save / update                                          | ✅ works                                          |
| A|B|C column views + Like column                                | ✅ works                                          |
| Loudness analysis (EBU R128) + ReplayGain tags                  | ✅ written on analysis, readable by any player   |
| Playback normalisation (Quiet / Normal / Loud)                  | ✅ clip-safe, non-destructive                    |
| Crossfade on auto-advance (Off / 2 / 4 / 8 s)                  | ✅ equal-power, smart-lite skip                  |
| Output bus DSP rack (EQ · AutoTilt · Punch · Limiter)          | ✅ shapes local playback + Icecast stream         |
| Hard / Neutral one-click DSP presets                            | ✅ works                                          |
| Theme switch (Aurora / Sunset / Midnight / Neon / Hardcore)     | ✅ live palette swap                              |
| Waveform header                                                 | ✅ FFT-driven 4-band scaleY + beat-pulse         |
| Vinyl spin animation                                            | ✅ spins around its centre, layered shadows      |
| Output device picker                                            | ✅ per-device routing                            |
| Live Icecast broadcast (stream your set)                        | ✅ BASSmix + BASSenc, multi-server profiles      |
| Installer + in-app auto-update                                  | ✅ Inno Setup (per-user) + GitHub Releases       |
| About dialog + version                                          | ✅ themed, build-stamped                         |
| macOS / Linux builds                                            | ❌ target, not validated yet                    |

## Stack

- **.NET 10** SDK (the project's libraries target net8+)
- **[Avalonia 12](https://avaloniaui.net/)** for the UI — frameless window, transparent
  surround, custom skeumorphic Templates, compiled bindings throughout
- **[ManagedBass](https://github.com/ManagedBass/ManagedBass) + BASS_FX / BASSmix / BASSenc**
  for playback, BPM detection and Icecast broadcasting
- **[TagLib#](https://github.com/mono/taglib-sharp)** for tag read/write
- **[ONNX Runtime](https://onnxruntime.ai/)** for the optional trained key model
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** — source generators,
  no reflection, trim/AOT-friendly
- **Microsoft.Extensions.DependencyInjection** — composition root in `Program.cs`

The codebase is **strict-layered** so the analysis / playback / metadata / DSP libraries stay
testable without any UI dependency:

```
JustPlay.Core         — platform-agnostic: Track, Metadata, MusicalKey, abstractions
JustPlay.Audio.Bass   — ManagedBass playback, BPM detection, Icecast broadcast
JustPlay.Metadata     — TagLib# metadata reader + writer (consent-gated tag persistence)
JustPlay.Analysis     — key (chromagram/EDMA), energy, beat fingerprint, structure,
                        sequencer, DSP bus (EQ / AutoTilt / Punch / Limiter)
JustPlay.ML           — optional ONNX "AI key" detector (falls back to DSP when absent)
JustPlay.Engine       — analysis / tagging facade (shared library)
JustPlay.App          — Avalonia shell: Views, ViewModels, Controls
```

`JustPlay.App` is the only project that knows about Avalonia. `JustPlay.Core` knows about
nothing platform-specific.

## Build & run

You need the .NET 10 SDK installed (download from https://dotnet.microsoft.com/download).

```powershell
# Hot-reload dev loop — save a .axaml and the running window updates without restart.
# Pairs nicely with Avalonia DevTools (F12 in the running app).
.\build\watch.ps1

# One-off run:
dotnet run --project src/JustPlay.App

# Release publish (Windows self-contained single-file .exe, no .NET install needed
# on the target machine, no C++ toolchain needed to build):
.\build\publish-win-x64.ps1

# Run the test suite:
dotnet test
```

The repo is a multi-project solution — `JustPlay.slnx` ties everything together. The product
version is the single `<Version>` in `Directory.Build.props`; the About dialog reads it back
at runtime and the installer / release pipeline read the same value.

## Repository layout

```
src/
  JustPlay.Core/         platform-agnostic models + abstractions
  JustPlay.Audio.Bass/   ManagedBass playback, BPM detection, Icecast broadcast
  JustPlay.Metadata/     TagLib#-backed metadata reader + writer
  JustPlay.Analysis/     key / energy / beat-fingerprint / structure / harmonic sequencer /
                         DSP bus (EQ, AutoTilt, Punch, Limiter)
  JustPlay.ML/           optional ONNX "AI key" detector
  JustPlay.Engine/       analysis / tagging facade (shared library)
  JustPlay.App/          Avalonia shell — Views, ViewModels, Controls
tests/                   xUnit test projects (Core, Analysis, Metadata, Engine)
build/
  watch.ps1              dotnet watch dev loop
  publish-win-x64.ps1    self-contained single-file release publish
.design/                 original Claude-Design mockups (JSX) — the UI is a port of these
```

## Roadmap

**Next — v0.4.0:** bug-fixes, plus the **in-app spectral analyser** — a live before/after
tonal-balance view *inside* the app (the offline `spectrum` tool grows a real GUI home).

Beyond that, roughly in priority order:

1. **Validated macOS + Linux builds** — the codebase is cross-platform; they just need
   real-hardware CI runs and any OS-specific device-picker wiring.
2. **JUST STREAM** — a standalone streaming sister app sharing the Icecast / DSP bus
   libraries. Killer feature: dynamic loudness maximisation for live DJ sets so streamers
   don't have to gain-stage mid-mix.
3. **MCP / agent surface** over `JustPlay.Engine` — so external agents and tools can
   drive analysis, tagging and sorting without the GUI.
4. **Harmonic Sort P2 — "what mixes next"** — surface a ranked list of compatible next
   tracks from the queue, not just a global sort order.
5. **User-savable DSP presets** — name, save and recall custom EQ / AutoTilt / Punch /
   Limiter configurations beyond the built-in Hard / Neutral pair.

## License

[MIT](LICENSE) for the JustPlay source.

### Third-party notice

The `src/JustPlay.Audio.Bass/native/` folder ships **BASS**, **BASS_FX**, **BASSmix** and
**BASSenc** from [un4seen.com](https://www.un4seen.com/). BASS is **free for personal /
non-commercial use**. If you intend to ship something based on JustPlay commercially you will
need your own BASS licence from un4seen — JustPlay's MIT licence does NOT grant you any rights
to BASS itself. See https://www.un4seen.com/ for licence terms.

## Credits

- By **Chloe Dream**.
- UI faithfully ported from [Claude Design](https://claude.ai) mockups bundled in `.design/`.
- Spinning-vinyl + glossy chrome inspired by the heyday of iTunes / Music.app, repainted
  with a 2026 aurora palette.
- Built with [Avalonia](https://avaloniaui.net/), [ManagedBass](https://github.com/ManagedBass/ManagedBass)
  and [TagLib#](https://github.com/mono/taglib-sharp) — projects that quietly do the
  heavy lifting so the rest of us can write XAML.
