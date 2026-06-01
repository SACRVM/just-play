# AI Key Detection — integration plan (for review)

**Status (2026-06-02):** accuracy proven, not yet wired into the app.
A learned MLP(128,64) on JustPlay's own 36-bin HPCP chroma scores **MIREX ~0.754 / ~69%
exact** on the GiantSteps ground truth (track-level 5-fold CV + 12× transposition
augmentation — `ml/train_key.py`), vs the shipped DSP template's **0.712 / 64%**. That clears
the 0.74 goal. This doc is the plan to ship it as the opt-in "AI key" mode — **no code in the
shipped app has changed yet; this needs Chloe's sign-off on the dependency.**

## Why this is lightweight (the nice surprise)

We do NOT need a big spectrogram CNN or the 1500-track training set. A *tiny MLP* on the
**chroma feature we already compute** (`HpcpKeyDetector.BuildFine36`, 36 floats) is enough.
So: our own model, our own features, clean licence, model file is *tiny* (~tens of KB, not
20–30 MB). The only real "weight" is the ONNX Runtime native lib.

## Steps

1. **Train the final model** (`ml/train_final.py`, to be written):
   - MLP(128,64) on ALL 604 tracks × 12 transpositions (the CV-validated architecture).
   - Export to ONNX (`torch.onnx.export`; input = 36 floats, output = 24 logits).
   - Honest accuracy claim stays the CV number (~0.754) — the shipped model trains on more
     data so should be ≥ that. (For a fully independent number, later train on the separate
     `giantsteps-mtg-key` 1500-track set and test on the 604 — optional rigour.)

2. **Inference in C# — respect the layering** (CLAUDE.md):
   - Feature extraction stays in `JustPlay.Analysis` (`BuildFine36`) — platform-agnostic. ✅
   - ONNX inference is a native dep → it must NOT go in Core/Analysis. Put an
     `MlKeyDetector : IKeyDetector` in a NEW adapter project (e.g. `JustPlay.ML`) or in
     `JustPlay.App`, mirroring how `JustPlay.Audio.Bass` isolates ManagedBass.
   - `Microsoft.ML.OnnxRuntime` runs via P/Invoke — same shape as ManagedBass, so AOT-compatible
     in principle, but **this is the flag-day dependency decision** (CLAUDE.md). Decide with Chloe.
   - Apply the SAME transposition trick at inference is unnecessary — just run the 36-vector
     through the net once → argmax of 24 → MusicalKey. Confidence = softmax max (gate the UI).

3. **Opt-in + lazy-load** (the product identity — see `justplay-product-positioning` memo):
   - Base app ships WITHOUT the model/runtime. A setting "AI key detection (downloads ~X MB)".
   - On enable: download the ONNX model (host on a GitHub release) + the ONNX runtime native
     lib if not bundled. Cache locally. Fall back to `HpcpKeyDetector` (0.712) if absent.
   - `TrackAnalysisService` picks `MlKeyDetector` when enabled+present, else `HpcpKeyDetector`.

4. **Verify the shipped path matches training:** the model trains on `BuildFine36` output, and
   the app feeds `BuildFine36` output — identical feature, so CV accuracy should transfer. Add
   a `--giantsteps-ml` benchmark that runs the ONNX model end-to-end to confirm ~0.754 in-app.

## Open decisions for Chloe
- OK to add `Microsoft.ML.OnnxRuntime` (native dep) — bundled, or downloaded on first enable?
- New `JustPlay.ML` project, or keep the detector in `JustPlay.App`?
- Where to host the model file for lazy download (GitHub release asset)?
- Ship the DSP detector (0.712) as the default and ML (0.754) as opt-in? (Recommended.)

## Risks / notes
- The MLP didn't fully converge at max_iter in the first sweep; the final train should use
  early-stopping on a val split. Architecture/regularisation can likely push a bit past 0.754.
- Keep `HpcpKeyDetector` as the always-available fallback (no-network, AOT-safe).
