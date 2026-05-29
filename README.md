# JustPlay

A stateless, DJ-focused music player for Windows / macOS / Linux.
Drop tracks in, double-click to play. No library, no memory between sessions, no nag — just play.

> ⚠️ **Pre-alpha, experimental.** This repo is a work-in-progress. Things that look done in
> screenshots are often still wired wrong under the hood. Treat it as a code tour, not a
> usable player (yet). Open issues, file feedback, send mean tweets — all welcome.

## Why another music player

Every "modern" desktop player insists on managing a library, scanning folders, deciding
which artist photo to fetch, asking you to log in. JustPlay aims for the opposite:

- A quick window you **drop MP3s onto and play.**
- **No state between sessions** — close it, reopen it, queue is empty again.
- **Cross-platform from day 1** — Windows / macOS / Linux, single codebase.
- **Small + fast deployment** — self-contained single-file `.exe`, no .NET install on the
  end user's machine, no C++ build tools required to build.

The DJ tilt comes from the headline features-in-progress:

- **BPM detection** (offline, on track add)
- **Camelot key** (so you can build a harmonic set without a separate tool)
- **Energy score** (1–10, Mixed-In-Key style) for ordering by intensity

Think MIK's analysis baked into the player itself, without the library overhead.

## Status

| Area                          | State                                            |
| ----------------------------- | ------------------------------------------------ |
| Drop / play / pause / next    | ✅ works                                          |
| Volume + position slider      | ✅ works                                          |
| Track metadata reading        | ✅ TagLib# under the hood                         |
| Mini / Max view layout        | ✅ shared transport cluster, polished skeu look   |
| BPM detection                 | 🟡 backend ready, end-to-end wiring in progress   |
| Camelot key detection         | 🟡 backend ready, end-to-end wiring in progress   |
| Energy score                  | 🟡 backend ready, end-to-end wiring in progress   |
| Waveform header               | 🟡 static, not driven by playback yet             |
| Cover-art display             | 🟡 binding refactor in progress                   |
| Vinyl spin animation          | 🟡 working when the pivot maths cooperates :)     |
| Theme switcher (4 palettes)   | ❌ UI present, no theme swap implementation yet   |
| Lyrics tab                    | ❌ placeholder only                               |
| macOS / Linux builds          | ❌ target, not validated yet                      |

## Stack

- **.NET 10** SDK (the project's libraries target net8+)
- **[Avalonia 12](https://avaloniaui.net/)** for the UI — frameless window, transparent
  surround, custom skeumorphic Templates, compiled bindings throughout
- **[ManagedBass](https://github.com/ManagedBass/ManagedBass) + BASS_FX** for audio playback
- **[TagLib#](https://github.com/mono/taglib-sharp)** for tag reading
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** — source generators,
  no reflection, trim/AOT-friendly

The codebase is **strict-layered** so the analysis / playback / metadata libraries stay
testable without any UI dependency:

```
JustPlay.Core         — platform-agnostic: Track, Metadata, MusicalKey, abstractions
JustPlay.Audio.Bass   — ManagedBass + BASS_FX playback implementation
JustPlay.Metadata     — TagLib# metadata reader
JustPlay.Analysis     — BPM / key / energy analyzers
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
```

The repo is a multi-project solution — `JustPlay.slnx` ties everything together.

## Repository layout

```
src/
  JustPlay.Core/         platform-agnostic models + abstractions
  JustPlay.Audio.Bass/   ManagedBass + BASS_FX playback implementation
  JustPlay.Metadata/     TagLib#-backed metadata reader
  JustPlay.Analysis/     BPM / key / energy analyzers (in progress)
  JustPlay.App/          Avalonia shell — Views, ViewModels, Controls
tests/                   xUnit test projects
build/
  watch.ps1              dotnet watch dev loop
  publish-win-x64.ps1    self-contained single-file release publish
.design/                 original Claude-Design mockups (JSX) — the UI is a port of these
```

## Roadmap

Things actively being worked on or planned, roughly in priority order:

1. Working BPM / key / energy analysis end-to-end (currently UI-only)
2. Animated waveform driven by actual playback position
3. Theme swap (Sunset / Midnight / Neon — design exists, wiring does not)
4. Validated macOS + Linux builds
5. Lyrics tab (LRC parsing? online lookup? still deciding)
6. Optional cue-point / hot-loops layer for DJ-style preview

PRs welcome — but please open an issue first so we don't both refactor the same area of
XAML at once. There's a non-trivial amount of skeumorphic styling and pixel-tuning
already in flight.

## License

[MIT](LICENSE) — use it, fork it, embed it, whatever. No warranty.

### Third-party notice

The `src/JustPlay.Audio.Bass/native/` folder ships **BASS** and **BASS_FX** from
[un4seen.com](https://www.un4seen.com/). BASS is **free for personal / non-commercial
use**. If you intend to ship something based on JustPlay commercially you will need
your own BASS licence from un4seen — JustPlay's MIT licence does NOT grant you any
rights to BASS itself. See https://www.un4seen.com/ for licence terms.

## Credits

- UI faithfully ported from [Claude Design](https://claude.ai) mockups bundled in `.design/`.
- Spinning-vinyl + glossy chrome inspired by the heyday of iTunes / Music.app, repainted
  with a 2026 aurora palette.
- Built with [Avalonia](https://avaloniaui.net/), [ManagedBass](https://github.com/ManagedBass/ManagedBass)
  and [TagLib#](https://github.com/mono/taglib-sharp) — projects that quietly do the
  heavy lifting so the rest of us can write XAML.
