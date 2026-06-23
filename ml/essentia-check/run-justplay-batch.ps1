# run-justplay-batch.ps1 — Run JustPlay analyze on sample genre folders
# Produces a merged index at $OutputIndex
# Usage: .\run-justplay-batch.ps1 -OutputIndex "path/to/merged.json"

param(
    [string]$OutputIndex = "$PSScriptRoot\_justplay_genres.json",
    [string]$RepoRoot = "D:\repos\just-play"
)

$CliProject = Join-Path $RepoRoot "src\JustPlay.Cli"
$NasBase = "\\nas\music\GENRES"

# Genre → how many files to pick (first N alphabetically)
# We pick more than needed and take evenly-spaced subset in compare.js
$Genres = @{
    "Hard_Techno"  = 6
    "Techno"       = 6
    "Hardstyle"    = 6
    "Bass_House"   = 6
    "Tech_House"   = 5
    "Deep_House"   = 5
    "House"        = 5
    "Future_House" = 5
    "Trance"       = 5
    "EDM"          = 6
}

$TmpDir = Split-Path $OutputIndex -Parent
if (-not (Test-Path $TmpDir)) { New-Item -ItemType Directory -Force $TmpDir | Out-Null }

$MergedEntries = @{}

foreach ($genre in $Genres.Keys) {
    $n = $Genres[$genre]
    $genreDir = Join-Path $NasBase $genre
    $genreIndex = Join-Path $TmpDir "_jp_${genre}.json"

    Write-Host "`n--- Genre: $genre (want ~$n files) ---"

    # Run justplay analyze with a slightly higher limit to ensure we get enough
    $result = & dotnet run --project $CliProject --no-build -- `
        analyze $genreDir --index $genreIndex --threads 4 --limit ($n * 50)

    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 255) {
        Write-Warning "JustPlay analyze failed for $genre (exit $LASTEXITCODE)"
        continue
    }

    # Merge into combined index
    if (Test-Path $genreIndex) {
        $data = Get-Content $genreIndex -Raw | ConvertFrom-Json
        $entries = $data.entries
        $props = $entries | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name
        foreach ($p in $props) {
            $MergedEntries[$p] = $entries.$p
        }
        Write-Host "  Loaded $($props.Count) entries from $genre"
    }
}

# Write merged index
$merged = @{ entries = $MergedEntries }
$merged | ConvertTo-Json -Depth 20 -Compress | Out-File $OutputIndex -Encoding utf8
Write-Host "`nMerged index written to: $OutputIndex"
Write-Host "Total entries: $($MergedEntries.Count)"
