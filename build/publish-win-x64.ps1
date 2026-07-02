# Publishes the JustPlay SUITE — JustPlay.exe (GUI player) + JustStream.exe (streaming app)
# + the headless JustPlayCLI tool — into ONE shared folder for Windows x64.
#
# Why one flat folder (not single-file, not subfolders): the exes SHARE their DLLs —
# one copy of the .NET runtime, the BASS natives, the ONNX runtime, and keymodel.onnx
# (the ML key model) serves all of them. A single-file exe hides its managed DLLs
# INSIDE the exe, so it can't share; and a cli\ subfolder would duplicate the ~11 MB
# ONNX runtime + the model. So: self-contained, multi-file, everything side by side.
#
# - Self-contained: the .NET runtime ships in the folder — target machine needs no
#   .NET install.
# - Needs NO C++ build tools (that was only NativeAOT). Just the .NET SDK.
#
# Output: <repo>\publish\win-x64\  (wiped clean each run for a deterministic drop)
#   JustPlay.exe       — the GUI player
#   JustStream.exe     — the streaming app (shares the engine + bus DSP + BASS natives)
#   JustPlayCLI.exe    — the headless library tool (same analysis engine as the app)
#   JustPlayCLI.howto.txt
#   shared, single copy: the .NET runtime DLLs, the Avalonia natives
#   (libSkiaSharp / av_libglesv2 / libHarfBuzzSharp), the BASS natives
#   (bass / bass_fx / bassmix / bassenc), the ONNX runtime, and keymodel.onnx.
#   No .pdb shipped. The installer (justplay.iss) ships this whole folder verbatim.

$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $PSScriptRoot
$proj       = Join-Path $root "src\JustPlay.App"
$cliProj    = Join-Path $root "src\JustPlay.Cli"
$streamProj = Join-Path $root "src\JustPlay.Stream"
$out        = Join-Path $root "publish\win-x64"

# Always start from an empty folder — incremental publishes into a dirty dir
# leave stale files and confuse the file/size picture.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# 1. The GUI app — self-contained, multi-file (so its DLLs are shareable), into $out.
dotnet publish $proj -c Release -r win-x64 --self-contained -o $out `
    -p:DebugType=none `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "app publish failed ($LASTEXITCODE)" }

# 2. The headless CLI — into the SAME folder. Shared framework + native files just
#    overwrite the app's identical copies; only JustPlayCLI.exe / .dll / its deps.json
#    + keymodel.onnx + the howto are net-new. One ONNX runtime, one model, shared BASS.
dotnet publish $cliProj -c Release -r win-x64 --self-contained -o $out `
    -p:DebugType=none `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)" }

# 3. JUST STREAM — the sibling streaming app, into the SAME folder. Shares the .NET runtime +
#    Avalonia natives + BASS natives with the app; only JustStream.exe / .dll / its deps.json +
#    a few Stream-only managed DLLs are net-new.
dotnet publish $streamProj -c Release -r win-x64 --self-contained -o $out `
    -p:DebugType=none `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "STREAM publish failed ($LASTEXITCODE)" }

# Drop debug symbols that third-party native NuGets (SkiaSharp/HarfBuzz) drag in —
# ~100 MB of .pdb that have no place in a release drop.
Get-ChildItem $out -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$files = Get-ChildItem $out -File
$total = ($files | Measure-Object Length -Sum).Sum
Write-Host ""
Write-Host ("Shipped to $out") -ForegroundColor Green
Write-Host ("{0} files, {1:N1} MB total" -f $files.Count, ($total / 1MB))
$files | Sort-Object Length -Descending | Select-Object -First 15 |
    Format-Table Name, @{N='MB'; E={ [math]::Round($_.Length / 1MB, 2) }} -AutoSize | Out-String | Write-Host
