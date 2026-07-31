<#
.SYNOPSIS
    Runs JUST PLAY as a PRISTINE installation — no library root, no index, no settings.

.DESCRIPTION
    Sets JUSTPLAY_DATA_DIR for THIS launch only, so the whole suite data tree
    (settings.json, finder.settings.json, the library index, the window layout)
    lands in a throwaway folder. Your real %LOCALAPPDATA%\JustPlay and
    %LOCALAPPDATA%\JUST are never touched.

    Use it to look at the first-run states — the welcome screen, the "not scanned
    yet" offer, the empty-index notice — without wrecking the setup you actually
    work with, and to reproduce a "fresh install" bug report on a configured machine.

    ⚠ The variable only applies to the process this script starts. Launching JUST PLAY
    from a shortcut, or from another shell, uses your REAL data as usual — which is
    exactly what you want the rest of the time.

.PARAMETER DataDir
    Where the throwaway tree goes. Defaults to a timestamp-free folder under TEMP so
    repeated runs continue where the last one left off.

.PARAMETER Reset
    Wipe DataDir first, so the run really starts at zero.

.EXAMPLE
    .\build\run-fresh.ps1 -Reset
#>
[CmdletBinding()]
param(
    [string] $DataDir = (Join-Path $env:TEMP 'just-fresh'),
    [switch] $Reset
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($Reset -and (Test-Path $DataDir)) {
    Write-Host "wiping $DataDir" -ForegroundColor Yellow
    Remove-Item $DataDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$env:JUSTPLAY_DATA_DIR = $DataDir

Write-Host ""
Write-Host "  JUST PLAY — fresh install" -ForegroundColor Cyan
Write-Host "  data dir : $DataDir"
Write-Host "  your real settings are untouched."
Write-Host ""

dotnet run --project (Join-Path $repo 'src\JustPlay.App') --
