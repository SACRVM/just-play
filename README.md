# JustPlay

A stateless, DJ-focused music player for Windows / macOS / Linux.
Drop tracks in, double-click to play. No library, no memory between sessions, no nag — just play.

> 🎧 **v0.1.0 — early, but functional.** The headline analysis (BPM · Camelot key · energy)
> works end-to-end and can write itself back into your file tags on consent. Windows is the
> daily-driver target; macOS / Linux share the codebase but aren't validated yet. Pre-1.0, so
> rough edges remain — but it plays, analyses, sorts and streams today.

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
- **Energy score** (1–10, Mixed-In-Key style) for ordering by intensity.
- **Harmonic Sort** — reorder the whole queue for the smoothest mix, scoring each pair on
  key + tempo + energy + groove (beat fingerprint).

Think MIK's analysis living *inside* the player — without the library overhead.

## Status

| Area                                                            | State                                          |
| --------------------------------------------------------------- | ---------------------------------------------- |
| Drop / play / pause / next / prev                               | ✅ works                                        |
| Shuffle (bag-with-history) · repeat · consume mode              | ✅ works                                        |
| Volume + position slider                                        | ✅ works                                        |
| Metadata read **+ write** (TagLib#)                             | ✅ works                                        |
| Mini / Max view layout                                          | ✅ shared transport cluster, skeu look          |
| BPM detection                                                   | ✅ BASS_FX, async per track on add              |
| Camelot key detection                                           | ✅ chromagram + EDMA profiles (+ optional ONNX) |
| Energy score                                                    | ✅ spectral, 1–10                               |
| Beat fingerprint + structure detection                          | ✅ feeds Harmonic Sort                           |
| Harmonic Sort (mix sequencer)                                   | ✅ key + tempo + energy + groove                |
| Tag persistence — write BPM/Key/Energy to file tags             | ✅ consent-gated, per-field, full undo           |
| Like / favourite (POPM) · remove duplicates                     | ✅ works                                         |
| Theme switch (Aurora / Sunset / Midnight / Neon)                | ✅ live palette swap                             |
| Waveform header                                                 | ✅ FFT-driven 4-band scaleY + beat-pulse         |
| Vinyl spin animation                                            | ✅ spins around its centre, layered shadows      |
| Output device picker                                            | ✅ per-device routing                            |
| Live Icecast broadcast (stream your set)                        | ✅ BASSmix + BASSenc, multi-server profiles      |
| About dialog + version                                          | ✅ themed, build-stamped                         |
| Installer + auto-update                                         | 🟡 in progress (Inno Setup + GitHub Releases)   |
| macOS / Linux builds                                            | ❌ target, not validated yet                    |
| Lyrics tab                                                      | ❌ placeholder only                             |

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

The codebase is **strict-layered** so the analysis / playback / metadata libraries stay
testable without any UI dependency:

```
JustPlay.Core         — platform-agnostic: Track, Metadata, MusicalKey, abstractions
JustPlay.Audio.Bass   — ManagedBass playback, BPM detection, Icecast broadcast
JustPlay.Metadata     — TagLib# metadata reader + writer (consent-gated tag persistence)
JustPlay.Analysis     — key (chromagram/EDMA), energy, beat fingerprint, structure, sequencer
JustPlay.ML           — optional ONNX "AI key" detector (falls back to DSP when absent)
JustPlay.Engine       — headless analysis/tagging facade (for future CLI / MCP / agent use)
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
  JustPlay.Analysis/     key / energy / beat-fingerprint / structure / harmonic sequencer
  JustPlay.ML/           optional ONNX "AI key" detector
  JustPlay.Engine/       headless analysis/tagging facade
  JustPlay.App/          Avalonia shell — Views, ViewModels, Controls
tests/                   xUnit test projects (Core, Analysis, Metadata, Engine)
build/
  watch.ps1              dotnet watch dev loop
  publish-win-x64.ps1    self-contained single-file release publish
.design/                 original Claude-Design mockups (JSX) — the UI is a port of these
```

## Roadmap

Roughly in priority order toward a shippable v0.1.0 and beyond:

1. **Installer + auto-update** — Inno Setup (per-user, no UAC), release artifacts on GitHub
   Releases, in-app update check.
2. **Landing page** — a small static site pointing at the latest release.
3. **Validated macOS + Linux builds.**
4. **Loudness / gain analysis** — EBU R128 → ReplayGain tags (replace mp3gain / MIK gain).
5. **Lyrics tab** — LRC parsing / online lookup (still deciding).
6. **Headless engine** — CLI + MCP surface over `JustPlay.Engine` so agents can analyse,
   tag and sort music libraries.

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
