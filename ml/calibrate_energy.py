"""
ml/calibrate_energy.py
======================
Calibrate JustPlay's 1–10 energy scale against the DEAM static arousal labels.

DEAM dataset:  cvml.unige.ch/databases/DEAM  (CC BY-NC, research-only)
Arousal scale: 1–9 (Russell circumplex AROUSAL dimension)

Pipeline
--------
1. Load the DEAM static arousal CSV + the feature CSV produced by
   `JustPlay.App --dump-energy-features <deam_audio> features.csv`.
2. Merge on song_id (DEAM files are named <id>.mp3 → filename "<id>.mp3").
3. Diagnostic: Spearman/Pearson correlations of each raw feature vs arousal.
4. Find calibrated per-feature normalisation ranges (robust 5th–95th percentile
   across the DEAM corpus, so real tracks span [0,1] instead of clumping).
5. Fit a constrained linear regression of normalised features → arousal to derive
   blend weights (non-negative, sum-to-1).
6. Fit a linear floor/span mapping from blended score → 1–10 energy so the
   output distribution matches the arousal distribution (ambient → peak-time).
7. Report: Spearman/Pearson before vs after, energy-decile distribution.
8. Print the calibrated constants in copy-paste C# format.

Usage
-----
    python ml/calibrate_energy.py \
        --features  ml/_deam/features.csv \
        --arousal   "ml/_deam/annotations/annotations averaged per song/song_level/static_annotations_averaged_songs_1_2000.csv" \
        --arousal2  "ml/_deam/annotations/annotations averaged per song/song_level/static_annotations_averaged_songs_2000_2058.csv"

Requirements: numpy, pandas, scipy, scikit-learn (all standard ML stack).
"""

import argparse
import sys
import numpy as np
import pandas as pd
from scipy import stats
from sklearn.linear_model import LinearRegression
from sklearn.model_selection import cross_val_score

# ── Current shipped constants (for before/after comparison) ──────────────────
SHIPPED_LUFS_LO    = -23.0
SHIPPED_LUFS_HI    =  -6.0
SHIPPED_FLUX_LO    = 0.010
SHIPPED_FLUX_HI    = 0.060
SHIPPED_CENT_LO    = 800.0
SHIPPED_CENT_HI    = 3000.0
SHIPPED_RMSSD_LO   = 0.00
SHIPPED_RMSSD_HI   = 0.25
SHIPPED_W_LOUD     = 0.25
SHIPPED_W_FLUX     = 0.40
SHIPPED_W_BRIGHT   = 0.15
SHIPPED_W_RMSSD    = 0.20
SHIPPED_FLOOR      = 2.5
SHIPPED_SPAN       = 5.0


def norm_clamp(v, lo, hi):
    return np.clip((v - lo) / (hi - lo), 0.0, 1.0)


def shipped_score(df):
    """Reproduce the current shipped energy calculation."""
    nl = norm_clamp(df['rawLufs'],     SHIPPED_LUFS_LO,  SHIPPED_LUFS_HI)
    nf = norm_clamp(df['rawFlux'],     SHIPPED_FLUX_LO,  SHIPPED_FLUX_HI)
    nb = norm_clamp(df['rawCentroid'], SHIPPED_CENT_LO,  SHIPPED_CENT_HI)
    nr = norm_clamp(df['rawRmsSd'],    SHIPPED_RMSSD_LO, SHIPPED_RMSSD_HI)
    score = (SHIPPED_W_LOUD  * nl
           + SHIPPED_W_FLUX  * nf
           + SHIPPED_W_BRIGHT * nb
           + SHIPPED_W_RMSSD * nr)
    return np.clip(np.round(SHIPPED_FLOOR + SHIPPED_SPAN * score), 1, 10)


def arousal_to_energy(arousal):
    """Map DEAM 1–9 arousal → JustPlay 1–10 energy (linear rescale)."""
    # DEAM arousal: 1 (calm) → 9 (excited). JustPlay: 1 (ambient) → 10 (peak-time).
    # Simple linear: energy = 1 + 9/8 * (arousal - 1)  →  [1..10]
    return 1.0 + (9.0 / 8.0) * (arousal - 1.0)


def main():
    ap = argparse.ArgumentParser(description="Calibrate JustPlay energy scale vs DEAM arousal")
    ap.add_argument('--features',  required=True, help='features.csv from --dump-energy-features')
    ap.add_argument('--arousal',   required=True, help='DEAM static_annotations_averaged_songs_1_2000.csv')
    ap.add_argument('--arousal2',  default=None,   help='DEAM static_annotations_averaged_songs_2000_2058.csv')
    ap.add_argument('--holdout',   type=float, default=0.15, help='Fraction held out for validation')
    args = ap.parse_args()

    # ── Load features ─────────────────────────────────────────────────────────
    print(f"Loading features: {args.features}")
    feat = pd.read_csv(args.features)
    print(f"  {len(feat)} rows loaded.")

    # Extract song_id from filename (DEAM files named "<id>.mp3")
    feat['song_id'] = feat['filename'].str.extract(r'(\d+)').astype(int)

    # ── Load DEAM arousal annotations ─────────────────────────────────────────
    print(f"Loading DEAM arousal: {args.arousal}")
    ann1 = pd.read_csv(args.arousal, skipinitialspace=True)
    ann_parts = [ann1]
    if args.arousal2:
        print(f"Loading DEAM arousal (part 2): {args.arousal2}")
        ann2 = pd.read_csv(args.arousal2, skipinitialspace=True)
        ann_parts.append(ann2)
    ann = pd.concat(ann_parts, ignore_index=True)
    # Normalise column names (strip spaces)
    ann.columns = [c.strip() for c in ann.columns]
    print(f"  {len(ann)} annotations loaded. Columns: {list(ann.columns)}")

    # ── Merge ─────────────────────────────────────────────────────────────────
    df = feat.merge(ann[['song_id', 'arousal_mean', 'arousal_std']], on='song_id', how='inner')
    print(f"  {len(df)} tracks matched features <-> arousal.")
    if len(df) < 50:
        print("ERROR: Too few matched tracks — check that the features CSV was generated "
              "from the DEAM audio folder (filenames should be <song_id>.mp3).")
        sys.exit(1)

    arousal = df['arousal_mean'].values    # DEAM 1–9

    # ── Section 1: Per-feature correlations (raw) ────────────────────────────
    print("\n=== Per-feature Spearman correlation with DEAM arousal (raw values) ===")
    features_raw = ['rawLufs', 'rawFlux', 'rawCentroid', 'rawRmsSd']
    for col in features_raw:
        r_s, p_s = stats.spearmanr(df[col], arousal)
        r_p, p_p = stats.pearsonr( df[col], arousal)
        print(f"  {col:20s}  Spearman={r_s:+.3f} (p={p_s:.3e})   Pearson={r_p:+.3f} (p={p_p:.3e})")

    # ── Section 2: Current shipped energy vs arousal ──────────────────────────
    shipped = shipped_score(df)
    r_ship, p_ship = stats.spearmanr(shipped, arousal)
    r_ship_p, _ = stats.pearsonr(shipped, arousal)
    print(f"\n=== Shipped energy score vs DEAM arousal ===")
    print(f"  Spearman={r_ship:+.3f}  Pearson={r_ship_p:+.3f}")
    print(f"  Shipped energy distribution (deciles):")
    for p in range(0, 101, 10):
        print(f"    p{p:3d}: {np.percentile(shipped, p):.1f}")

    # ── Section 3: Calibrated normalisation ranges (robust percentiles) ───────
    print("\n=== Calibrated feature ranges (p5–p95 across DEAM corpus) ===")
    pct5  = df[features_raw].quantile(0.05)
    pct95 = df[features_raw].quantile(0.95)
    for col in features_raw:
        lo, hi = pct5[col], pct95[col]
        print(f"  {col:20s}  [{lo:.4f} .. {hi:.4f}]  (current: see shipped constants)")

    cal_lufs_lo    = pct5['rawLufs']
    cal_lufs_hi    = pct95['rawLufs']
    cal_flux_lo    = pct5['rawFlux']
    cal_flux_hi    = pct95['rawFlux']
    cal_cent_lo    = pct5['rawCentroid']
    cal_cent_hi    = pct95['rawCentroid']
    cal_rmssd_lo   = pct5['rawRmsSd']
    cal_rmssd_hi   = pct95['rawRmsSd']

    # Re-normalise with calibrated ranges
    df['cn_loud']   = norm_clamp(df['rawLufs'],     cal_lufs_lo,  cal_lufs_hi)
    df['cn_flux']   = norm_clamp(df['rawFlux'],     cal_flux_lo,  cal_flux_hi)
    df['cn_bright'] = norm_clamp(df['rawCentroid'], cal_cent_lo,  cal_cent_hi)
    df['cn_rmssd']  = norm_clamp(df['rawRmsSd'],    cal_rmssd_lo, cal_rmssd_hi)

    # ── Section 4: Fit blend weights via constrained linear regression ────────
    # Constraint: non-negative weights (arousal increases with all four features).
    # We use NNLS (non-negative least squares) to fit: arousal ~ w·X, then normalise w to sum=1.
    from scipy.optimize import nnls

    X = df[['cn_loud', 'cn_flux', 'cn_bright', 'cn_rmssd']].values
    y = arousal

    # Holdout split (last 15% by sorted song_id — deterministic)
    n = len(df)
    n_holdout = max(10, int(n * args.holdout))
    idx_sorted = df['song_id'].argsort().values
    train_idx = idx_sorted[:n - n_holdout]
    val_idx   = idx_sorted[n - n_holdout:]

    X_train, y_train = X[train_idx], y[train_idx]
    X_val,   y_val   = X[val_idx],   y[val_idx]

    # NNLS regression on training set
    w_raw, rnorm = nnls(X_train, y_train)
    w_sum = w_raw.sum()
    if w_sum <= 0:
        print("WARNING: NNLS returned all-zero weights — falling back to equal weights.")
        w_raw = np.array([0.25, 0.40, 0.15, 0.20])
        w_sum = w_raw.sum()
    w_norm = w_raw / w_sum   # normalise to sum=1 (unit simplex)

    feat_names = ['loud', 'flux', 'bright', 'rmssd']
    print("\n=== Calibrated blend weights (NNLS, non-negative, sum-to-1) ===")
    for nm, w in zip(feat_names, w_norm):
        print(f"  W{nm.capitalize():10s} = {w:.4f}")

    # Blended score with calibrated weights
    blended = X @ w_norm   # [0,1] range

    # ── Section 5: Floor/span mapping score → 1–10 ───────────────────────────
    # We want: energy_out = floor + span * blended_score, such that the
    # output 1–10 distribution matches the DEAM arousal 1–9 rescaled to 1–10.
    # Simple linear fit: arousal_10 = floor + span * blended_score.
    arousal_10 = arousal_to_energy(arousal)   # 1–10

    # Fit floor + span on the TRAINING set.
    blended_train = blended[train_idx]
    a_10_train    = arousal_10[train_idx]

    # 1D linear: a_10 = floor + span * blended_score  →  LS fit.
    A_mat = np.stack([np.ones_like(blended_train), blended_train], axis=1)
    (cal_floor, cal_span), _, _, _ = np.linalg.lstsq(A_mat, a_10_train, rcond=None)

    # Clamp span to be positive (regression might drift negative if blended is near-constant).
    if cal_span <= 0:
        print("WARNING: fitted span is non-positive — forcing span=7.0 (full range).")
        cal_span = 7.0
    if cal_floor < 0.5:
        print(f"WARNING: fitted floor ({cal_floor:.2f}) < 0.5 — clamping to 1.0.")
        cal_floor = 1.0

    print(f"\n=== Calibrated floor/span mapping ===")
    print(f"  floor = {cal_floor:.4f}  span = {cal_span:.4f}")
    print(f"  (before: floor={SHIPPED_FLOOR}, span={SHIPPED_SPAN})")

    # ── Section 6: Validation ─────────────────────────────────────────────────
    def energy_output(X_, floor, span):
        blended_ = X_ @ w_norm
        raw = floor + span * blended_
        return np.clip(np.round(raw), 1, 10)

    e_cal_train = energy_output(X_train, cal_floor, cal_span)
    e_cal_val   = energy_output(X_val,   cal_floor, cal_span)
    e_ship_val  = shipped_score(df.iloc[val_idx].reset_index(drop=True))

    a_val_10    = arousal_to_energy(y_val)

    print("\n=== Validation results (holdout n={}) ===".format(len(val_idx)))
    r_cal,  _ = stats.spearmanr(e_cal_val, y_val)
    r_ship2,_ = stats.spearmanr(e_ship_val, y_val)
    print(f"  Calibrated Spearman(energy, arousal): {r_cal:+.3f}  vs shipped: {r_ship2:+.3f}")

    # Full-set arousal correlation after calibration
    e_cal_all = energy_output(X, cal_floor, cal_span)
    r_all, _  = stats.spearmanr(e_cal_all, arousal)
    print(f"  Calibrated Spearman (all n={n}): {r_all:+.3f}")

    # Energy decile distribution after calibration
    print("\n  Calibrated energy output distribution (deciles over all DEAM tracks):")
    for p in range(0, 101, 10):
        print(f"    p{p:3d}: {np.percentile(e_cal_all, p):.1f}")

    counts = {v: int(np.sum(e_cal_all == v)) for v in range(1, 11)}
    print(f"\n  Counts per energy level (1–10): { {k: v for k, v in counts.items()} }")

    # ── Section 7: Sanity checks ──────────────────────────────────────────────
    print("\n=== Sanity check: archetypal inputs ===")
    # Hard-techno-like: loud (−8 LUFS), lots of flux (0.08), bright (3500 Hz), high RMS-SD (0.30)
    ht = np.array([[
        norm_clamp(-8.0,  cal_lufs_lo,  cal_lufs_hi),
        norm_clamp(0.08,  cal_flux_lo,  cal_flux_hi),
        norm_clamp(3500., cal_cent_lo,  cal_cent_hi),
        norm_clamp(0.30,  cal_rmssd_lo, cal_rmssd_hi),
    ]])
    ht_score = int(np.clip(np.round(cal_floor + cal_span * float(ht @ w_norm)), 1, 10))

    # Ambient-like: quiet (−30 LUFS), sparse flux (0.005), dull (600 Hz), steady (RMS-SD 0.005)
    am = np.array([[
        norm_clamp(-30., cal_lufs_lo,  cal_lufs_hi),
        norm_clamp(0.005, cal_flux_lo, cal_flux_hi),
        norm_clamp(600., cal_cent_lo,  cal_cent_hi),
        norm_clamp(0.005, cal_rmssd_lo, cal_rmssd_hi),
    ]])
    am_score = int(np.clip(np.round(cal_floor + cal_span * float(am @ w_norm)), 1, 10))

    # Mid-energy dance: −14 LUFS, flux 0.035, centroid 1500 Hz, RMS-SD 0.12
    mid = np.array([[
        norm_clamp(-14., cal_lufs_lo,  cal_lufs_hi),
        norm_clamp(0.035, cal_flux_lo, cal_flux_hi),
        norm_clamp(1500., cal_cent_lo, cal_cent_hi),
        norm_clamp(0.12,  cal_rmssd_lo, cal_rmssd_hi),
    ]])
    mid_score = int(np.clip(np.round(cal_floor + cal_span * float(mid @ w_norm)), 1, 10))

    print(f"  Hard techno-like (loud/busy/bright):  energy = {ht_score}   (target 8–10)")
    print(f"  Mid-energy dance:                     energy = {mid_score}  (target 5–7)")
    print(f"  Ambient-like (quiet/sparse/dull):     energy = {am_score}   (target 1–4)")

    # ── Section 8: C# constants output ───────────────────────────────────────
    print("\n" + "="*70)
    print("=== Copy-paste C# constants (SpectralEnergyDetector.cs) ===")
    print("="*70)
    print(f"""
    // ---- Feature normalisation ranges (calibrated against DEAM 2026-06-03, ml/calibrate_energy.py) ----
    // Ranges = robust p5–p95 across 1802 DEAM excerpts so real tracks span [0,1].
    // Previous MIK-only calibration only validated centring (mean 7.0, near-zero variance).
    private const double LufsLo    = {cal_lufs_lo:.1f};
    private const double LufsHi    = {cal_lufs_hi:.1f};
    private const double FluxLo    = {cal_flux_lo:.5f};
    private const double FluxHi    = {cal_flux_hi:.5f};
    private const double CentroidLo = {cal_cent_lo:.1f};    // Hz
    private const double CentroidHi = {cal_cent_hi:.1f};   // Hz
    private const double RmsSdLo    = {cal_rmssd_lo:.5f};
    private const double RmsSdHi    = {cal_rmssd_hi:.5f};

    // ---- Blend weights (NNLS fit to DEAM arousal, non-negative, sum-to-1) ----
    // Calibrated 2026-06-03. See ml/calibrate_energy.py §fit blend weights.
    private const double WLoud    = {w_norm[0]:.4f};
    private const double WFlux    = {w_norm[1]:.4f};
    private const double WBright  = {w_norm[2]:.4f};
    private const double WRmsSd   = {w_norm[3]:.4f};  // groove / dynamics variability

    // ---- 1–10 output mapping (fitted to DEAM arousal → 1–10 scale) ----
    // Calibrated 2026-06-03 via linear LS: energy = floor + span * blended_score.
    private const double EnergyFloor = {cal_floor:.4f};
    private const double EnergySpan  = {cal_span:.4f};
""")

    print(f"Spearman(calibrated, arousal) = {r_all:+.3f}  vs shipped = {r_ship:+.3f}")
    print(f"Sanity: ambient={am_score} / mid={mid_score} / hard-techno={ht_score}")
    print("Done.")


if __name__ == '__main__':
    main()
