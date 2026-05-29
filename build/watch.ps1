# Runs JustPlay under `dotnet watch` -- file-change driven dev loop.
#
# What you get:
#   - Save a .axaml  -> Avalonia recompiles the XAML and pushes the markup into the
#                       running window. Layout / colours / brushes / templates update
#                       LIVE without restarting. (Won't carry across structural changes
#                       that break code-behind bindings -- see below.)
#   - Save a .cs     -> Hot Reload patches the method body if it can. Cosmetic edits to
#                       methods, lambda bodies, string literals: hot-applied. New fields,
#                       changed signatures, ctor logic: forces an automatic rebuild + relaunch.
#   - Ctrl+R         -> force a manual rebuild + relaunch (when hot-reload bails).
#   - Ctrl+C         -> stop the watcher and the running app.
#
# Pairs nicely with the in-app DevTools (F12 once the window is up): tweak BoxShadow /
# Margin / Background live in the inspector, copy the value into the XAML, save --
# watcher pushes it back to the same window without losing your scroll position or
# playback state.
#
# Notes on what hot-reload does NOT cover (you'll see "Restart required" in the terminal):
#   - Constructor / static-init changes
#   - Changes to App.axaml's <Application.Resources> root
#   - Changes to Window XAML root attributes (Title, Width, Transparency)
# When that happens dotnet watch auto-restarts the app -- usually 2-3 seconds.
#
# NOTE on encoding: this file is pure ASCII on purpose. Windows PowerShell 5.1 reads
# scripts as Windows-1252 when there is no BOM, and a stray Unicode bullet/arrow then
# tears strings in half ("missing terminator" parse error). Keep it ASCII or save the
# file as UTF-8 with BOM.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\JustPlay.App\JustPlay.App.csproj"

Write-Host ""
Write-Host "  JustPlay -- hot-reload watcher" -ForegroundColor Cyan
Write-Host "  Project:  $proj" -ForegroundColor DarkGray
Write-Host "  Save XAML to reload markup, Ctrl+R to force rebuild, Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

# `dotnet watch run` is the default action; hot-reload is enabled by default on net6+.
& dotnet watch --project $proj run
