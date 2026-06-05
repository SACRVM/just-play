# Publishes JustPlay (self-contained single-file) then packs a per-user Inno Setup installer.
#
# Output: <repo>\bin\installer\JustPlaySetup-<version>-win-x64.exe
#
# Version comes from the single <Version> in Directory.Build.props, so a release is just:
#   bump that version → run this → (later) tag v<version> and attach the artifact.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1. Resolve the version (single source of truth).
$propsPath = Join-Path $root "Directory.Build.props"
$version = ([xml](Get-Content $propsPath -Raw)).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Version not found in Directory.Build.props" }
Write-Host "Packaging JustPlay $version" -ForegroundColor Cyan

# 2. Publish the self-contained single-file drop into publish\win-x64.
& (Join-Path $PSScriptRoot "publish-win-x64.ps1")
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

# 3. Locate the Inno Setup compiler.
$iscc = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php"
}

# 4. Compile the installer, passing the resolved version.
$iss = Join-Path $root "installer\justplay.iss"
& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $root "bin\installer\JustPlaySetup-$version-win-x64.exe"
Write-Host ""
if (Test-Path $setup) {
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ("Installer ready: {0} ({1} MB)" -f $setup, $mb) -ForegroundColor Green
} else {
    Write-Host "ISCC reported success but the expected output was not found." -ForegroundColor Yellow
}
