# essentia-check — JustPlay ↔ Essentia key detection comparison harness

A reusable dev oracle that cross-checks JustPlay's key detector against Essentia.js
over a folder of real DJ-pool audio.

**AGPL note:** essentia.js is AGPLv3. This directory is a dev tool only. Never link
essentia.js code into JustPlay product code.

## Requirements

- Node.js v18+
- `npm install` (installs essentia.js + ffmpeg-static — no system ffmpeg needed)
- JustPlay CLI built: `dotnet build D:\repos\just-play\src\JustPlay.Cli`

## Usage

### Run on a new DJ-pool folder

1. Build JustPlay CLI (once):
   ```powershell
   dotnet build D:\repos\just-play\src\JustPlay.Cli
   ```

2. Run per-genre analysis (adjust genres/limits as needed):
   ```powershell
   # From repo root
   dotnet run --project src/JustPlay.Cli --no-build -- `
     analyze "\\nas\music\GENRES\Hard_Techno" `
     --index C:\tmp\jp_Hard_Techno.json --threads 4 --limit 6
   ```

3. Run full comparison:
   ```powershell
   node run-compare.js `
     --filelist sample-filelist.txt `
     --index C:\tmp\jp_merged.json `
     --out results.csv
   ```

### Quick oracle test on a single file

```powershell
node oracle.js "\\nas\music\GENRES\Hard_Techno\MyTrack.mp3"
```
Output: JSON with edma + bgate key/scale/strength/camelot.

### Files

| File | Purpose |
|------|---------|
| `oracle.js` | Single-file Essentia oracle (edma + bgate profiles) |
| `camelot.js` | Camelot wheel utilities + relationship classifier (unit-tested) |
| `run-compare.js` | Full comparison harness: reads filelist + JP index, runs Essentia, emits CSV + summary |
| `compare.js` | Alternative entry point that also runs JustPlay analyze internally |
| `sample-filelist.txt` | 55-track sample used for the 2026-06-23 run |
| `results.csv` | Results from the 2026-06-23 run |
| `run-justplay-batch.ps1` | PowerShell helper for per-genre JustPlay analysis |

## CSV columns

`file, genre, ourKeyCamelot, ourConf, essentiaEdma, essentiaEdmaStrength, essentiaBgate, essentiaBgateStrength, relationship, agree`

`relationship` values:
- `exact` — same Camelot code
- `fifth` — adjacent number, same letter (±1 on wheel)
- `relative` — same number, different letter (A↔B)
- `parallel` — same tonic, different mode
- `semitone` — one pitch class apart
- `tritone` — six semitones apart
- `other` — unrelated

## Key findings (2026-06-23 run, n=55, 10 genres)

- **Exact agreement: 49% (23/47 valid pairs)**
- **Harmonically ok: 70% (33/47)**
- **Major finding: JustPlay calls 96% of tracks minor; Essentia (edma) calls 57% minor**
- The dominant disagreement type is semitone (26%) + parallel (13%) — both caused by
  JP's minor bias calling a track minor when Essentia calls it the parallel/nearby major
- All disagreements have JP confidence < 0.031 — the confidence gate is correctly flagging
  these uncertain calls
- See `.claude/night-reports/2026-06-23-essentia-keycheck.md` for full analysis
