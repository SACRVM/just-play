# JustPlay — Working notes for Claude Code

These are the things you need to know to write code in this repo without
guessing. The README is for humans; this file is for the agent.

## Stack snapshot (verify before assuming)

- **.NET 10**, single solution (`JustPlay.slnx`).
- **Avalonia 12.0.3** with `<AvaloniaUseCompiledBindingsByDefault>true</...>`
  set in `src/JustPlay.App/JustPlay.App.csproj`. Every `.axaml` therefore
  needs an `x:DataType` on its root, or compiled bindings will fail silently
  at runtime (you'll see warnings in the Avalonia trace, not exceptions).
- **CommunityToolkit.Mvvm 8.4.1** — source generators (`[ObservableProperty]`,
  `[RelayCommand]`, `[NotifyPropertyChangedFor]`). No reflection-based MVVM
  primitives. Trim/AOT-friendly is a deliberate goal.
- **ManagedBass + BASS_FX** — audio engine, native via P/Invoke.
- **Microsoft.Extensions.DependencyInjection** — composition root is
  `Program.ConfigureServices()`. ViewModels resolve from there.

## Layering (hard rule, don't break)

```
JustPlay.Core         — platform-agnostic; no Avalonia, no ManagedBass
JustPlay.Audio.Bass   — ManagedBass implementation of Core abstractions
JustPlay.Metadata     — TagLib# implementation of IMetadataReader
JustPlay.Analysis     — BPM / key / energy analyzers (also platform-agnostic)
JustPlay.App          — the only project that knows Avalonia
```

If you need a platform service in Core, add it as an interface in
`JustPlay.Core/Abstractions/` and implement it in the appropriate adapter
project. **Never** reference `Avalonia.*` from anywhere outside
`JustPlay.App`.

## JUST suite UI philosophy — JUST PLAY is THE reference (do not re-litigate)

Every JUST app (JUST PLAY, JUST STREAM, and any future one) MUST share one look &
feel. **JUST PLAY is the canonical implementation — copy its patterns; do not
re-invent or iterate the UI from scratch per app.** The brand is J.U.S.T. = "Just
Useful Sound Tools" (see the brand notes); each app is part of that suite.

Non-negotiable rules (all proven in JUST PLAY — cite these files when building a new app's UI):

1. **Frameless, rounded, modern windows — NO classic Windows title bar.**
   `WindowDecorations="None"` + a rounded card Border (`CornerRadius` ~14) + a drop
   shadow blooming into a transparent margin. Drag via the chrome bar, custom caption
   buttons. Reference: `Views/MainWindow.axaml`, `Controls/WindowChrome.cs`,
   `Controls/WindowControls.axaml.cs`, and the chrome bar in `Views/MaxView.axaml`
   (`OnChromePressed` drag).

2. **App icon = the active THEME gradient + one simple glyph**, and it **REPAINTS on
   theme switch.** JUST PLAY's glyph is the play triangle; JUST STREAM's is the radio
   tower ("Funkturm" — the same mark that manages the radio station in JUST PLAY, see
   `Controls/StreamingPanel.axaml`). Pick one simple glyph per app on the theme
   gradient chip. Reference: `Theming/ThemedWindowIcon.cs` (re-rendered on
   `IThemeService.ThemeChanged`, wired in `App.axaml.cs`), `Theming/AvaloniaThemeService.cs`.

3. **About opens from the brand mark, top-LEFT of the chrome bar** (not a menu, not a
   classic Help/About). The About dialog is itself a frameless rounded themed card with
   the gradient brand chip + glyph, and it carries the "Part of J.U.S.T. — Just Useful
   Sound Tools" line. Reference: `Views/MaxView.axaml` brand button → `OnAboutClick`,
   and `Views/AboutWindow.axaml`.

4. **Everything is hand-drawn / themed — no stock OS chrome, no default control skins
   left bare.** Controls follow the active palette via `DynamicResource` keys
   (`BgLinear`/`BgGlowA/B`, `AccentGradient`, `TitleGradient`, `MutedBrush`, …) so they
   track live theme switches with zero extra wiring. Reference: `App.axaml` styles,
   `Controls/Vinyl.axaml`, the `DynamicResource` usage throughout `AboutWindow.axaml`.

5. **⛔ NO EMOJI / FONT GLYPHS IN THE UI — ship the vector.** Every icon in every JUST
   app is a path we ship, drawn by `JustPlay.UI/Controls/JustIcon.cs`. Never put a
   pictograph character (`📁`, `☰`, `🔒`, `♥`, `✕`, `↻`, `▲`) into `Text=`, `Content=`
   or a view-model string.

   The reason is not taste, it is that **a character is rendered by whatever font the
   OS picks, and that is not under our control**: the same `📁` (U+1F4C1), from the same
   source line, rendered BRIGHT YELLOW in JUST TAG and monochrome white in the PRE CUE
   FINDER — two processes, same Avalonia, same `WithInterFont()`, same codepoint
   (verified: identical UTF-16 in both files). An emoji in the UI is therefore a bet
   that every OS will render it the same way, and that bet is lost on Mac and Linux
   before it is ever made. The macOS port makes it concrete:
   Apple renders many BMP symbols (`♥`, `✓`, `⚠`, `▶`, `■`) with **emoji presentation
   by default** — they would come out red/coloured on the Mac.

   A vector also gets what a glyph never can: it follows `Foreground`, so it tracks the
   live theme like everything else (rule 4).

   Adding an icon = one `IconKind` value + one path string in `JustIcon.cs`, and it is
   then available to every app. ⛔ Do not draw a second copy of an existing icon in an
   app-local `Path`/style — that is how two windows drift (see `RowGlyph`'s history and
   the `Button.ghost` note in `JustStyles.axaml`).

   **Same rule for raster images: vector by default, a bitmap only as an approved
   exception.** No PNG/JPG/ICO shipped as chrome, decoration or an icon — it does not
   scale, does not follow the theme, and needs a set per DPI. Vector is the default and
   an exception is asked for and granted **per image, before** it is
   added. (Album artwork is not chrome — it is user content and obviously stays a
   bitmap; so is the rendered window icon, which `ThemedWindowIcon.cs` rasterises from a
   vector at runtime because Win32 demands a bitmap handle.)

When building JUST STREAM (or any new app) UI: start by mirroring these files, swap the
glyph, reuse the same `DynamicResource` theme keys and `App.axaml` style patterns. The
goal is zero UI re-iteration — it should look like a JUST PLAY sibling on first run.

## Source is ASCII, English, and unattributed (enforced by test)

`SourceIsAsciiTests` fails the build if any `.cs` or `.axaml` under `src/` or `tests/`
contains a byte above 127. Three rules, one of which a machine can check:

1. **ASCII only.** A source file with no BOM and a non-ASCII byte is a file that any
   tool which guesses its encoding can silently destroy — and one did (a PowerShell
   find-and-replace read a UTF-8 file as ANSI and mojibake'd 250 characters). Giving
   358 files a BOM was rejected: a BOM is not self-enforcing, the next templated file
   has none, and the hazard returns unnoticed. Use the plain equivalents:
   `-` `=` `->` `<=` `>=` `x` `~` `+/-` `^2` `...` `(!)` `(!!)` `(*)`, and
   `tau` / `alpha` / `pi` / `sigma` for the Greek — you cannot type a `τ` into a
   search box, so the ASCII form is also the greppable one.
   ⚠ Two traps the conversion hit, both caught by the build: `--` is illegal inside an
   XML comment (use `=` for a section rule in `.axaml`), and a typographic quote inside
   a C# string literal becomes a plain `"` and ends the string.
2. **English only.** Not "mostly English" — no German sentences in comments, not even
   quoted. State the decision and its reason in English; that is shorter than the quote
   and needs no translation from the next reader.
3. **No attribution.** No names, no "X decided on DATE". There is one developer, so a
   name carries no information and reads as noise in public code. Keep a date only when
   the date IS the fact — a MEASUREMENT ages ("measured 2026-08-07: 957 files"), a
   preference does not.

## Avalonia conventions in this repo

### Bindings inside reusable controls — use `$parent`, not `DataContext = this`

Compiled bindings sometimes evaluate against the inherited `DataContext`
before a constructor-side assignment lands. That manifests as bindings that
never refresh. Pattern in this repo (see `Controls/Vinyl.axaml`):

```xml
<Image Source="{Binding $parent[controls:Vinyl].Cover}" Stretch="UniformToFill"/>
```

`$parent[T]` walks the visual tree to find the typed ancestor and is
independent of `DataContext`.

### Styled properties on custom controls

```csharp
public static readonly StyledProperty<bool> SpinningProperty =
    AvaloniaProperty.Register<Vinyl, bool>(nameof(Spinning));

public bool Spinning
{
    get => GetValue(SpinningProperty);
    set => SetValue(SpinningProperty, value);
}
```

React to changes with `SpinningProperty.Changed.AddClassHandler<T>(...)` in
the constructor, not by overriding `OnPropertyChanged` (cleaner and avoids
the boxed-value dance for value types).

### `RenderTransform` — let Avalonia's default origin do the work

`Visual.RenderTransformOrigin` defaults to `RelativePoint.Center`
(verified against Avalonia 12.0.3 source — `src/Avalonia.Base/Visual.cs`).
That means a bare `RotateTransform` / `ScaleTransform` already pivots
around the element's centre. **Do not** wrap it in a Translate-sandwich or
set `RotateTransform.CenterX/Y` to half the width — both stack on top of
the implicit origin transform and produce a 2× offset (the disc rotates
around its bottom-right corner instead of its centre).

### Styles in `App.axaml`

- Selectors use FluentTheme as the base (`<FluentTheme/>` is the first style).
- State pseudo-classes: `:pointerover`, `:pressed`, `:checked`.
- Template parts: `Selector="Button.primary /template/ Border#Halo"`.
- **State-dependent properties (`BoxShadow`, etc.) live OUTSIDE the
  `<ControlTemplate>` as `Style Setter`s.** Inline values inside the
  template have `LocalValue` precedence and beat `:pointerover` setters,
  so the hover state silently does nothing. See the `Button.primary` /
  `Button.primary:pointerover` pair in `App.axaml` for the canonical example.
- FluentTheme leaks `Background` / `BorderBrush` onto the templated
  `ContentPresenter`. When a button has a custom Template you usually have
  to override these explicitly for all three states (base, pointerover,
  pressed). Search for "Belt-and-braces" in `App.axaml`.

### BoxShadow syntax

`X Y Blur Spread Color`. Stack multiple with commas. `inset` prefix is
supported. Avalonia clips shadows to the parent's render bounds unless
the parent chain has `ClipToBounds="False"` — for big pink glows like
the play-button halo, every ancestor up to the rounded `Card` Border
needs `ClipToBounds="False"`.

## Threading

The native BASS engine raises events on its own internal threads.
`SyncProcedure` callbacks (`BassAudioEngine.cs`) fire from BASS's playback
thread; UI updates triggered from those events **must** be marshalled with
`Dispatcher.UIThread.Post(...)`. The canonical site is
`MainWindowViewModel.OnEngineStateChanged` / `OnTrackEnded`.

Pin BASS callback delegates as fields (e.g. `_endSync` in
`BassAudioEngine`) — if the delegate is GC'd while BASS holds the native
function pointer, you get a CallbackOnCollectedDelegate crash.

`DispatcherTimer` is also the right tool for any UI-rate polling
(playback position tick, vinyl spin) — see
`MainWindowViewModel._timer` (200 ms) and `Vinyl.UpdateSpin` (16 ms).

## Composition root

```csharp
// Program.cs
Services = ConfigureServices();
BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

`Program.Services` is the static service provider; `App.axaml.cs`
resolves the root `MainWindowViewModel` from it and assigns it to
`MainWindow.DataContext`. New backends (a future macOS audio engine, a
real theme service) register here and **only** here.

## Build pitfall: never use `dotnet build -t:Compile` here

Tempting shortcut when the running app holds a lock on `JustPlay.App.exe`
and a normal build fails on the file-copy step:

```powershell
# DON'T do this on JustPlay.App
dotnet build src/JustPlay.App/JustPlay.App.csproj -t:Compile
```

`-t:Compile` only runs the C# compile target. It produces a fresh
`JustPlay.App.dll` but **skips `GenerateAvaloniaResources`**, so the new
DLL is written *without* the precompiled XAML embedded as resources.
The next full `dotnet build` then sees the DLL timestamp newer than the
inputs and skips `GenerateAvaloniaResources` again ("up-to-date") —
the broken DLL sticks around, and the app crashes at startup with:

```
Avalonia.Markup.Xaml.XamlLoadException: No precompiled XAML found for
JustPlay.App.App, make sure to specify x:Class and include your XAML
file as AvaloniaResource
```

Recovery: `dotnet clean src/JustPlay.App/JustPlay.App.csproj` followed by
a normal `dotnet build`. The clean wipes the stale DLL so the
AvaloniaResource generation runs again.

If you need to check whether code compiles without touching the running
app, prefer running the actual full build after asking the user to close
the app, or build a *different* project (e.g. the test project) that
exercises the same code.

## Hot-reload dev loop

```powershell
.\build\watch.ps1     # save a .axaml, window updates without restart
```

Pair with Avalonia DevTools — F12 in the running app — for visual-tree
inspection and live binding diagnostics. The `AvaloniaUI.DiagnosticsSupport`
package is referenced Debug-only so DevTools-related code is trimmed from
Release.

## Tests

xUnit projects under `tests/`. `JustPlay.Core.Tests` and
`JustPlay.Analysis.Tests` exist — both are *unit* level (no Avalonia, no
BASS). UI/XAML doesn't have a test project; visual regressions are caught
by running the app and using DevTools. Run all tests:

```powershell
dotnet test
```

## Builds

```powershell
dotnet run --project src/JustPlay.App   # debug
.\build\publish-win-x64.ps1             # self-contained single-file release
```

The `JustPlay.App.csproj` sets `InvariantGlobalization=true` and the
code stays reflection-free / trim-safe so the project can flip to NativeAOT
later without source-level changes. Don't introduce reflection or unbounded
`ResourceManager` lookups without a flag-day discussion.

## When porting from `.design/`

The UI is a port of JSX mockups in `.design/just-play-music-player/project/`
(`app.jsx`, `player.jsx`, `tweaks-panel.jsx`). Treat those files as the
**source of truth for visual values** — colours, gradients, shadow stacks,
font sizes, spacings.

When porting a CSS `background`, `box-shadow`, or `linear-gradient` to
Avalonia:

1. **Convert colour values exactly.** Avalonia uses `#AARRGGBB` ARGB hex.
   CSS `rgba(r,g,b,a)` → `#AARRGGBB` where `AA = round(a × 255)` as hex.
   Common alphas: 0.04 → `0A`, 0.063 → `10`, 0.133 → `22`, 0.18 → `2E`,
   0.5 → `80`, 0.65 → `A6`, 0.75 → `BF`. Do **not** substitute "close
   enough" greys for tinted colours — a purple `rgba(138,95,255,.133)` is
   `#228a5fff` and reads completely differently from `#22FFFFFF`.

2. **CSS `linear-gradient(90deg, …)` → Avalonia LinearGradientBrush**
   `StartPoint="0%,50%" EndPoint="100%,50%"` (the `%` suffix is
   mandatory — see the Styling pitfalls below). 0deg = bottom→top,
   90deg = left→right, 180deg = top→bottom. Per-stop positions
   `colour 60%` → `<GradientStop Offset="0.6" Color="…"/>`.

3. **CSS `box-shadow: X Y blur spread color, …`** → Avalonia
   `BoxShadow="X Y blur spread color, …"` (same syntax, same order).
   `inset` prefix is supported. Stack with commas.

4. **CSS `filter: drop-shadow(...)` on a non-rectangular element** →
   either set `Effect` on the element with a `DropShadowEffect`, or put
   a transparent shaped `Border` behind it with `BoxShadow` (preferred
   for rotating elements — see Vinyl, where the drop-shadow must NOT
   spin with the disc).

5. **Cite the design file.** When a style differs from the design
   intentionally (e.g. an Aurora-only tint we added), say so in the
   comment. When it matches, drop the `app.jsx:LINE` reference so the
   next reader can diff. The historical bug was Avalonia styles drifting
   silently from the JSX without anyone noticing.

## When you're not sure about an Avalonia API

Read the actual Avalonia source at
`https://github.com/AvaloniaUI/Avalonia/tree/release/12.0.3`
(branch matches the package version) instead of guessing. Defaults,
property metadata, and selector semantics live in:

- `src/Avalonia.Base/Visual.cs` — render-transform-related properties
- `src/Avalonia.Base/Layout/Layoutable.cs` — alignment + measure/arrange
- `src/Avalonia.Controls/...` — control-specific behaviour
- `src/Avalonia.Styling/...` — selectors, style precedence

The behaviour you need is in the source. Cite the file when you change
something based on it.
