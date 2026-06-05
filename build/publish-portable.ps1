# Publishes JustPlay as a PORTABLE zip — the self-contained single-file drop, packed
# as-is with no installer. Unzip anywhere and run JustPlay.App.exe; nothing is written
# to Program Files and no uninstaller is registered.
#
# Output: <repo>\bin\installer\JustPlay-<version>-win-x64-portable.zip
#
# Why this is the right shape:
#  - publish\win-x64\ is already a run-in-place app (JustPlay.App.exe + the native
#    sidecars that can't fold into the single file: bass / bass_fx / Skia / HarfBuzz).
#    It's a *folder*, not one loose exe, so a zip is the honest portable package.
#  - The asset name deliberately does NOT start with "JustPlaySetup" and is a .zip,
#    so the auto-updater (GitHubUpdateChecker matches JustPlaySetup*.exe) never mistakes
#    it for the installer. A portable copy has no unins000.exe, so the updater's
#    TryGetInnoInstallDir returns false and it correctly offers "Open in browser"
#    instead of trying to silently self-install over a folder it doesn't own.
#
# Run alongside publish-installer.ps1 to produce both release artifacts in bin\installer\.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1. Resolve the version (single source of truth — same as the installer script).
$propsPath = Join-Path $root "Directory.Build.props"
$version = ([xml](Get-Content $propsPath -Raw)).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Version not found in Directory.Build.props" }
Write-Host "Packaging JustPlay $version (portable)" -ForegroundColor Cyan

# 2. Publish the self-contained single-file drop into publish\win-x64.
& (Join-Path $PSScriptRoot "publish-win-x64.ps1")
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$drop = Join-Path $root "publish\win-x64"
if (-not (Test-Path $drop)) { throw "Expected publish drop not found at $drop" }

# 3. Zip the whole drop. Output beside the installer so both release artifacts share a folder.
$outDir = Join-Path $root "bin\installer"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir "JustPlay-$version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# Compress-Archive the directory CONTENTS (the \* glob), so the zip root holds
# JustPlay.App.exe directly rather than a nested win-x64\ folder.
Compress-Archive -Path (Join-Path $drop "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
if (Test-Path $zip) {
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ("Portable ready: {0} ({1} MB)" -f $zip, $mb) -ForegroundColor Green
} else {
    Write-Host "Compress-Archive reported success but the expected zip was not found." -ForegroundColor Yellow
}
