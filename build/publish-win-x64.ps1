# Publishes JustPlay as a self-contained, single-file .exe for Windows x64.
#
# - Self-contained: the .NET runtime is bundled IN — the target machine needs no
#   .NET install.
# - Single-file: all managed assemblies fold into one JustPlay.App.exe.
# - Needs NO C++ build tools (that was only NativeAOT). Just the .NET SDK.
#
# Output: <repo>\publish\win-x64\  (wiped clean each run for a deterministic drop)
#   JustPlay.App.exe  + native sidecars that can't live inside the bundle
#   (IncludeNativeLibrariesForSelfExtract defaults to false):
#     libSkiaSharp / av_libglesv2 / libHarfBuzzSharp  (Avalonia renderer)
#     bass.dll + bass_fx.dll                          (audio)
#   No .NET DLLs, no .pdb shipped.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\JustPlay.App"
$out  = Join-Path $root "publish\win-x64"

# Always start from an empty folder — incremental publishes into a dirty dir
# leave stale files and confuse the file/size picture.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj -c Release -r win-x64 --self-contained -o $out `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

# Drop debug symbols that third-party native NuGets (SkiaSharp/HarfBuzz) drag in —
# ~100 MB of .pdb that have no place in a release drop.
Get-ChildItem $out -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$files = Get-ChildItem $out -File
$total = ($files | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host ("Shipped to $out") -ForegroundColor Green
Write-Host ("{0} files, {1:N1} MB total" -f $files.Count, ($total / 1MB))
$files | Sort-Object Length -Descending |
    Format-Table Name, @{N='MB'; E={ [math]::Round($_.Length / 1MB, 2) }} -AutoSize | Out-String | Write-Host
