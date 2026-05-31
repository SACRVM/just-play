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
