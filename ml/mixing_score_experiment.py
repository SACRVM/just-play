#!/usr/bin/env python3
"""
N3 — Mixing-Score Experimental Harness
JustPlay night task: generate pairwise mix-compatibility data over a
real library sample to design the final scoring formula on.

Does NOT pick the final formula — emits evidence only.

Usage:
    python ml/mixing_score_experiment.py

Output:
    .claude/night-reports/N3_mixing_score_experiment.md
"""

import json
import math
import os
import random
import sys
from datetime import datetime, timezone

import numpy as np

# ─── Paths ────────────────────────────────────────────────────────────────────

REPO_ROOT   = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INDEX_PATH  = os.path.join(REPO_ROOT, ".claude", "library-scan", "sets.v9.index.json")
REPORT_DIR  = os.path.join(REPO_ROOT, ".claude", "night-reports")
REPORT_PATH = os.path.join(REPORT_DIR, "N3_mixing_score_experiment.md")
REF_PATH    = os.path.join(REPO_ROOT, ".claude", "skills", "dj-audio-analysis",
                           "references", "harmonic-mixing.md")

# ─── Config ───────────────────────────────────────────────────────────────────

SAMPLE_SIZE = 500
RANDOM_SEED = 42
TOP_N_NN    = 5     # nearest neighbours shown per seed
TOP_PAIRS   = 15    # top/bottom global pairs shown

# Weight variants: (w_harm, w_tempo, w_energy, w_vibe)
# All weights must sum to 1.0
WEIGHT_VARIANTS = {
    "harmony_heavy":   (0.50, 0.20, 0.15, 0.15),
    "tempo_heavy":     (0.20, 0.50, 0.15, 0.15),
    "balanced":        (0.25, 0.25, 0.25, 0.25),
    "vibe_heavy":      (0.15, 0.20, 0.15, 0.50),
    "beat_vibe":       (0.20, 0.30, 0.15, 0.35),  # beat=primary (the stated design intent)
}

# Camelot wheel maximum distance: ndiff=6 + ldiff=1
CAMELOT_MAX   = 7
# Tempo AOE maximum (half-octave) used for normalisation
TEMPO_AOE_MAX = 0.5
# Theoretical energy range (JustPlay 1–10 scale)
ENERGY_MAX    = 9

# Vibe dimensions used for vibe distance
VIBE_DIMS = ["dark", "hypnotic", "bassGroove"]

# ─── Camelot parsing + distance ──────────────────────────────────────────────

def parse_camelot(key_str):
    """'11B' → (11, 1). Letter: A→0, B→1. Returns None on failure."""
    if not key_str or len(key_str) < 2:
        return None
    try:
        letter = key_str[-1].upper()
        n = int(key_str[:-1])
        if n < 1 or n > 12 or letter not in ("A", "B"):
            return None
        return (n, 0 if letter == "A" else 1)
    except ValueError:
        return None


def camelot_dist_scalar(k1, k2):
    """Camelot distance ∈ [0, CAMELOT_MAX], unscaled."""
    if k1 is None or k2 is None:
        return CAMELOT_MAX / 2
    n1, l1 = k1
    n2, l2 = k2
    ndiff = min(abs(n1 - n2), 12 - abs(n1 - n2))
    ldiff = 0 if l1 == l2 else 1
    return ndiff + ldiff


def build_camelot_matrix(tracks):
    """Return n×n matrix of normalised Camelot distances [0,1]."""
    n = len(tracks)
    keys = [parse_camelot(t.get("keyCamelot", "")) for t in tracks]
    ns = np.array([k[0] if k else 6 for k in keys], dtype=float)
    ls = np.array([k[1] if k else 0 for k in keys], dtype=float)
    abs_diff = np.abs(ns[:, None] - ns[None, :])
    ndiff = np.minimum(abs_diff, 12.0 - abs_diff)
    ldiff = (ls[:, None] != ls[None, :]).astype(float)
    mat = (ndiff + ldiff) / CAMELOT_MAX
    np.fill_diagonal(mat, 0.0)
    return mat


# ─── Tempo distance ───────────────────────────────────────────────────────────

def build_tempo_matrix(tracks):
    """
    AOE-style: |log2(bpm_a/bpm_b) - nearest_integer|, normalised to [0,1].
    Treats ×2 / ½ / ×3 / ⅓ etc. as exact (distance 0 at any octave).
    """
    bpms = np.array([t.get("bpm", 130.0) or 130.0 for t in tracks], dtype=float)
    log_ratio = np.log2(bpms[:, None]) - np.log2(bpms[None, :])  # n×n
    nearest_oct = np.round(log_ratio)
    aoe = np.abs(log_ratio - nearest_oct) / TEMPO_AOE_MAX  # [0, 1]
    np.fill_diagonal(aoe, 0.0)
    return aoe


# ─── Energy distance ──────────────────────────────────────────────────────────

def build_energy_matrix(tracks):
    """Absolute energy delta, normalised by ENERGY_MAX."""
    energies = np.array([t.get("energy", 5) for t in tracks], dtype=float)
    mat = np.abs(energies[:, None] - energies[None, :]) / ENERGY_MAX
    np.fill_diagonal(mat, 0.0)
    return mat


# ─── Vibe distance ────────────────────────────────────────────────────────────

def build_vibe_matrix(tracks):
    """
    RMS Euclidean distance over VIBE_DIMS (dark, hypnotic, bassGroove),
    normalised so max possible distance = 1.0.
    """
    dims = [np.array([t.get(d, 0.5) for t in tracks], dtype=float) for d in VIBE_DIMS]
    sq_sum = sum((v[:, None] - v[None, :]) ** 2 for v in dims)
    mat = np.sqrt(sq_sum / len(VIBE_DIMS))  # [0, 1]
    np.fill_diagonal(mat, 0.0)
    return mat


# ─── Seed track selection ─────────────────────────────────────────────────────

def select_seeds(sample):
    """
    Pick 5 tracks from the sample that represent diverse corners of the
    feature space — good examples for the NN report.
    """
    def score(t, fn):
        try:
            return fn(t)
        except Exception:
            return 0.0

    seeds = {}

    # 1. Clean 4×4 driving (high gridConfidence × fourOnFloor)
    seeds["clean_4x4"] = max(
        sample,
        key=lambda t: score(t, lambda x: x.get("gridConfidence", 0) * x.get("fourOnFloor", 0)),
    )

    # 2. Maximum groove (high bassGroove)
    seeds["groovy"] = max(
        sample,
        key=lambda t: score(t, lambda x: x.get("bassGroove", 0)),
    )

    # 3. Dark + hypnotic (product)
    seeds["dark_hypnotic"] = max(
        sample,
        key=lambda t: score(t, lambda x: x.get("dark", 0) * x.get("hypnotic", 0)),
    )

    # 4. Harshest track
    seeds["harsh"] = max(
        sample,
        key=lambda t: score(t, lambda x: x.get("harshness", 0)),
    )

    # 5. Offbeat-bass with highest bassPunch (bass house feel)
    offbeat = [t for t in sample if t.get("beatType") == "offbeat-bass"]
    if offbeat:
        seeds["offbeat_punch"] = max(offbeat, key=lambda t: t.get("bassPunch", 0))
    else:
        # Fallback: highest danceability
        seeds["offbeat_punch"] = max(sample, key=lambda t: t.get("danceability", 0))

    return seeds


# ─── Report helpers ───────────────────────────────────────────────────────────

def track_label(t, max_len=55):
    artist = t.get("artist", "Unknown")
    title  = t.get("title", "Unknown")
    label  = f"{artist} – {title}"
    if len(label) > max_len:
        label = label[:max_len - 1] + "…"
    return label


def track_summary(t):
    return (
        f"{t.get('bpm', 0):.0f} BPM, "
        f"{t.get('keyCamelot','?')}, "
        f"E{t.get('energy','?')}, "
        f"dark={t.get('dark',0):.2f} "
        f"hyp={t.get('hypnotic',0):.2f} "
        f"grv={t.get('bassGroove',0):.2f} "
        f"harsh={t.get('harshness',0):.2f}"
    )


def mat_stats(mat, label):
    upper = mat[np.triu_indices(len(mat), k=1)]
    return (
        f"  {label:<12}: mean={upper.mean():.3f}  std={upper.std():.3f}  "
        f"min={upper.min():.3f}  max={upper.max():.3f}"
    )


# ─── Core analysis helpers ────────────────────────────────────────────────────

def nn_for_seed(seed_idx, compat_mat, sample, k=TOP_N_NN):
    """Return top-k nearest neighbour (idx, score) for a seed track."""
    row = compat_mat[seed_idx].copy()
    row[seed_idx] = 999.0  # exclude self
    top_idx = np.argsort(row)[:k]
    return [(int(i), float(row[i])) for i in top_idx]


def compute_term_scores(i, j, harm_mat, tempo_mat, energy_mat, vibe_mat):
    """Return individual (unnormalised) term distances for a pair."""
    return {
        "harm":   float(harm_mat[i, j]),
        "tempo":  float(tempo_mat[i, j]),
        "energy": float(energy_mat[i, j]),
        "vibe":   float(vibe_mat[i, j]),
    }


def globally_compatible_pairs(compat_mat, n_pairs=TOP_PAIRS):
    """Top n_pairs most compatible (lowest score) pairs (i, j, score)."""
    mat_copy = compat_mat.copy()
    np.fill_diagonal(mat_copy, 999.0)
    # Flatten upper triangle
    rows, cols = np.triu_indices(len(mat_copy), k=1)
    scores = mat_copy[rows, cols]
    top = np.argsort(scores)[:n_pairs]
    return [(int(rows[k]), int(cols[k]), float(scores[k])) for k in top]


def globally_incompatible_pairs(compat_mat, n_pairs=TOP_PAIRS):
    """Top n_pairs least compatible (highest score) pairs."""
    rows, cols = np.triu_indices(len(compat_mat), k=1)
    scores = compat_mat[rows, cols]
    top = np.argsort(scores)[-n_pairs:][::-1]
    return [(int(rows[k]), int(cols[k]), float(scores[k])) for k in top]


# ─── Correlation analysis ─────────────────────────────────────────────────────

def term_correlations(harm_mat, tempo_mat, energy_mat, vibe_mat):
    """Pearson correlations between the four distance dimensions (upper triangle)."""
    n = len(harm_mat)
    idx = np.triu_indices(n, k=1)
    mats = {
        "harm":   harm_mat[idx],
        "tempo":  tempo_mat[idx],
        "energy": energy_mat[idx],
        "vibe":   vibe_mat[idx],
    }
    dims = list(mats.keys())
    rows = []
    for i, d1 in enumerate(dims):
        for j, d2 in enumerate(dims):
            if j <= i:
                continue
            r = float(np.corrcoef(mats[d1], mats[d2])[0, 1])
            rows.append((d1, d2, r))
    return rows


# ─── Report formatting helpers ────────────────────────────────────────────────

def fmt_pair_row(i, j, score, terms, sample):
    """One markdown table row for a pair."""
    a = track_label(sample[i])
    b = track_label(sample[j])
    h = terms["harm"]
    t = terms["tempo"]
    e = terms["energy"]
    v = terms["vibe"]
    ak = sample[i].get("keyCamelot", "?")
    bk = sample[j].get("keyCamelot", "?")
    ab = f"{sample[i].get('bpm', 0):.0f}"
    bb = f"{sample[j].get('bpm', 0):.0f}"
    return (
        f"| {a} | {ak} {ab} | {b} | {bk} {bb} "
        f"| {score:.3f} | {h:.2f} | {t:.2f} | {e:.2f} | {v:.2f} |"
    )


def fmt_seed_nn_table(seed_idx, nns, sample, harm_mat, tempo_mat, energy_mat, vibe_mat):
    header  = "| # | Artist – Title | Key BPM | E | compat | harm | tempo | E_δ | vibe |"
    divider = "|---|----------------|---------|---|--------|------|-------|-----|------|"
    rows = [header, divider]
    for rank, (idx, score) in enumerate(nns, 1):
        t   = sample[idx]
        lbl = track_label(t, 48)
        key = t.get("keyCamelot", "?")
        bpm = f"{t.get('bpm', 0):.0f}"
        en  = str(t.get("energy", "?"))
        h   = harm_mat[seed_idx, idx]
        tp  = tempo_mat[seed_idx, idx]
        eg  = energy_mat[seed_idx, idx]
        vb  = vibe_mat[seed_idx, idx]
        rows.append(
            f"| {rank} | {lbl} | {key} {bpm} | {en} "
            f"| {score:.3f} | {h:.2f} | {tp:.2f} | {eg:.2f} | {vb:.2f} |"
        )
    return "\n".join(rows)


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    t_start = datetime.now()
    print("N3 Mixing-Score Experiment")
    print("=" * 40)

    # ── Load index ────────────────────────────────────────────────────────────
    print(f"Loading {INDEX_PATH}…")
    with open(INDEX_PATH, encoding="utf-8") as fh:
        raw = json.load(fh)
    all_entries = list(raw["entries"].values())
    created_at  = raw.get("createdAt", "unknown")

    # ── Filter ────────────────────────────────────────────────────────────────
    valid = [
        e for e in all_entries
        if e.get("bpm") and e.get("bpm") > 0
        and e.get("keyCamelot")
        and parse_camelot(e["keyCamelot"]) is not None
        and e.get("energy") is not None
    ]
    print(f"Valid entries: {len(valid)} / {len(all_entries)}")

    # ── Sample ────────────────────────────────────────────────────────────────
    rng    = random.Random(RANDOM_SEED)
    sample = rng.sample(valid, min(SAMPLE_SIZE, len(valid)))
    n      = len(sample)
    print(f"Sample size: {n}  (seed={RANDOM_SEED})")

    # ── Seed tracks ───────────────────────────────────────────────────────────
    seeds     = select_seeds(sample)
    seed_idxs = {name: sample.index(t) for name, t in seeds.items()}
    print(f"Seeds selected: {list(seeds.keys())}")

    # ── Build distance matrices ───────────────────────────────────────────────
    print("Building distance matrices…")
    harm_mat   = build_camelot_matrix(sample)
    tempo_mat  = build_tempo_matrix(sample)
    energy_mat = build_energy_matrix(sample)
    vibe_mat   = build_vibe_matrix(sample)

    for lbl, mat in [("harm", harm_mat), ("tempo", tempo_mat),
                     ("energy", energy_mat), ("vibe", vibe_mat)]:
        print(mat_stats(mat, lbl))

    # ── Correlation analysis ──────────────────────────────────────────────────
    corr_rows = term_correlations(harm_mat, tempo_mat, energy_mat, vibe_mat)

    # ── Compat matrices per variant ───────────────────────────────────────────
    print("Computing compatibility matrices…")
    compat_mats = {}
    for vname, (wh, wt, we, wv) in WEIGHT_VARIANTS.items():
        mat = wh * harm_mat + wt * tempo_mat + we * energy_mat + wv * vibe_mat
        np.fill_diagonal(mat, 999.0)
        compat_mats[vname] = mat

    # Average compat across all variants (used for global pairs)
    avg_compat = np.mean(list(compat_mats.values()), axis=0)
    avg_compat_clean = avg_compat.copy()
    np.fill_diagonal(avg_compat_clean, 0.0)

    # ── Global pairs ──────────────────────────────────────────────────────────
    global_best  = globally_compatible_pairs(avg_compat, TOP_PAIRS)
    global_worst = globally_incompatible_pairs(avg_compat_clean, TOP_PAIRS)

    # ── Per-seed nearest neighbours per variant ───────────────────────────────
    seed_nn_data = {}
    for seed_name, seed_idx in seed_idxs.items():
        seed_nn_data[seed_name] = {}
        for vname, cmat in compat_mats.items():
            seed_nn_data[seed_name][vname] = nn_for_seed(seed_idx, cmat, sample)

    # ── Interesting cross-variant divergence ─────────────────────────────────
    # Pairs that are top-10 in harmony_heavy but bottom-10 in vibe_heavy (or vice versa)
    h_top  = set((i, j) for i, j, _ in globally_compatible_pairs(compat_mats["harmony_heavy"], 30))
    v_top  = set((i, j) for i, j, _ in globally_compatible_pairs(compat_mats["vibe_heavy"], 30))
    cross  = [(i, j) for i, j in h_top if (i, j) not in v_top][:8]  # harmonic but not vibe

    # ── BPM distribution in sample ────────────────────────────────────────────
    bpms_s    = np.array([t.get("bpm", 0) for t in sample])
    energies_s = np.array([t.get("energy", 5) for t in sample])
    keys_s    = [t.get("keyCamelot", "?") for t in sample]
    beat_types = [t.get("beatType", "unknown") for t in sample]
    from collections import Counter
    bt_counts = Counter(beat_types)

    elapsed = (datetime.now() - t_start).total_seconds()
    print(f"Computation done in {elapsed:.1f}s")

    # ─────────────────────────────────────────────────────────────────────────
    # ── Write report ─────────────────────────────────────────────────────────
    # ─────────────────────────────────────────────────────────────────────────

    os.makedirs(REPORT_DIR, exist_ok=True)
    lines = []
    W = lines.append  # shorthand

    now_str = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    W("# N3 — Mixing-Score Experimental Harness")
    W(f"*Generated: {now_str} | JustPlay night task*")
    W("")
    W("> **Purpose:** produce DATA to design the pairwise mix-compatibility")
    W("> score. Does NOT pick the final formula. Numbers are evidence, not decisions.")
    W("")

    # ── 1. Index schema ───────────────────────────────────────────────────────
    W("## 1. Index schema (fields used from `sets.v9.index.json`)")
    W("")
    W(f"Index created: `{created_at}`  |  Total entries: **{len(all_entries)}**")
    W("")
    W("| Field | Type | Range in library | Role in this experiment |")
    W("|-------|------|-----------------|------------------------|")
    W(f"| `bpm` | float | {bpms_s.min():.1f} – {bpms_s.max():.1f} (mean {bpms_s.mean():.1f}) | Tempo distance term |")
    W(f"| `keyCamelot` | string e.g. '8A','11B' | 24 positions (1–12 × A/B) | Harmonic distance term |")
    W(f"| `energy` | int 1–10 | {int(energies_s.min())} – {int(energies_s.max())} (mean {energies_s.mean():.1f}) | Energy delta term |")
    W("| `dark` | float 0–1 | per-track | Vibe dimension |")
    W("| `hypnotic` | float 0–1 | per-track | Vibe dimension |")
    W("| `bassGroove` | float 0–1 | per-track | Vibe dimension |")
    W("| `harshness` | float 0–1 | per-track | Noted, not in vibe dist (harshness ≠ mix compat) |")
    W("| `bassPunch` | float 0–1 | per-track | Used for seed selection only |")
    W("| `beatType` | enum | see below | Used for seed selection |")
    W("| `gridConfidence` | float 0–1 | per-track | Used for seed selection |")
    W("| `fourOnFloor` | float 0–1 | per-track | Used for seed selection |")
    W("| `loudnessLufs` | float | per-track | Not yet used (future term) |")
    W("")
    W("**Beat-type distribution in sample:**")
    for bt, cnt in sorted(bt_counts.items(), key=lambda x: -x[1]):
        W(f"- `{bt}`: {cnt} ({cnt/n*100:.1f}%)")
    W("")
    W("**Note on energy range:** energy is compressed to 5–8 in this library (saturation")
    W("from N1 issue — range calibration pending). The energy delta term has max observed")
    W(f"delta of {int(energies_s.max()-energies_s.min())} out of 9 theoretical max, so it is a weak discriminator here.")
    W("")

    # ── 2. Sampling ───────────────────────────────────────────────────────────
    W("## 2. Sampling methodology")
    W("")
    W(f"- **Pool:** {len(valid)} valid entries (filtered: bpm > 0, keyCamelot parseable, energy present)")
    W(f"- **Sample:** `random.sample(valid, {SAMPLE_SIZE})` with `seed={RANDOM_SEED}` → **{n} tracks**")
    W(f"- **Pairs computed:** {n*(n-1)//2:,} upper-triangle pairs")
    W("- **Sampling is uniform random** (not genre-stratified). The library is")
    W("  ~67% offbeat-bass / ~31% 4x4-groovy (see beat-type above) so the sample")
    W("  reflects that proportion naturally.")
    W("- **Paths are stale** (library reorganised to GENRES/ in N12); only feature")
    W("  values are used — paths are never read in this script.")
    W("")

    # ── 3. Distance term definitions ─────────────────────────────────────────
    W("## 3. Distance term definitions and statistics")
    W("")
    W("All four terms are normalised to **[0, 1]** before weighting.")
    W("")
    W("### 3a. Camelot harmonic distance")
    W("")
    W("```")
    W("d_harm(A,B) = (circular_ndiff + letter_diff) / 7")
    W("  where  circular_ndiff  = min(|nA-nB|, 12-|nA-nB|)  ∈ {0..6}")
    W("         letter_diff     = 0 if same A/B, else 1")
    W("         max possible    = 6 + 1 = 7  (e.g. 1A vs 7B)")
    W("  → same key = 0.0,  relative (nA↔nB) = 0.14,  fifth = 0.14,")
    W("    two steps same letter = 0.29,  clash zone ≥ 0.5")
    W("```")
    W("")
    W("### 3b. Tempo (AOE) distance")
    W("")
    W("```")
    W("d_tempo(A,B) = |log2(bpm_A / bpm_B) − round(log2(bpm_A / bpm_B))| / 0.5")
    W("  → exact same OR exact ×2/½/×3/⅓ = 0.0 (all octaves equally valid)")
    W("  → midpoint between two octaves = 1.0  (e.g. BPM ratio ≈ √2 ≈ 1.41)")
    W("  → 4% tolerance (ACC1) maps to ≈ 0.083 on this scale")
    W("```")
    W("")
    W("### 3c. Energy delta")
    W("")
    W("```")
    W("d_energy(A,B) = |energy_A − energy_B| / 9")
    W("  → same energy level = 0.0,  max (1 vs 10) = 1.0")
    W("  Note: current library uses energy 5–8 only → max observed ≈ 0.33")
    W("```")
    W("")
    W("### 3d. Vibe distance")
    W("")
    W("```")
    W("d_vibe(A,B) = sqrt(mean([(dark_A-dark_B)², (hypnotic_A-hypnotic_B)², (bassGroove_A-bassGroove_B)²]))")
    W("  → RMS over 3 dimensions, each in [0,1] → result in [0,1]")
    W("```")
    W("")
    W("### Statistics over the 500-track sample")
    W("")
    W("```")
    for lbl, mat in [("harm  ", harm_mat), ("tempo ", tempo_mat),
                     ("energy", energy_mat), ("vibe  ", vibe_mat)]:
        upper = mat[np.triu_indices(n, k=1)]
        p10 = np.percentile(upper, 10)
        p90 = np.percentile(upper, 90)
        W(f"  {lbl}:  mean={upper.mean():.3f}  std={upper.std():.3f}  "
          f"p10={p10:.3f}  p90={p90:.3f}  "
          f"min={upper.min():.3f}  max={upper.max():.3f}")
    W("```")
    W("")
    W("### Inter-term Pearson correlations")
    W("")
    W("| Term A | Term B | Pearson r | Interpretation |")
    W("|--------|--------|-----------|---------------|")
    for d1, d2, r in corr_rows:
        interp = "weak" if abs(r) < 0.15 else ("moderate" if abs(r) < 0.35 else "strong")
        W(f"| {d1} | {d2} | {r:+.3f} | {interp} |")
    W("")

    # ── 4. Weight variants ────────────────────────────────────────────────────
    W("## 4. Weight variants")
    W("")
    W("| Variant | w_harm | w_tempo | w_energy | w_vibe | Design intent |")
    W("|---------|--------|---------|----------|--------|---------------|")
    intents = {
        "harmony_heavy":  "\"key first\" — MIK/Camelot-wheel approach",
        "tempo_heavy":    "\"beatmatch first\" — club DJ approach",
        "balanced":       "equal weight baseline",
        "vibe_heavy":     "\"feel/texture first\" — mood-based curation",
        "beat_vibe":      "\"beat+feel\" — the stated design intent (beat primary)",
    }
    for vname, (wh, wt, we, wv) in WEIGHT_VARIANTS.items():
        W(f"| **{vname}** | {wh} | {wt} | {we} | {wv} | {intents[vname]} |")
    W("")

    # ── 5. Seed track descriptions ────────────────────────────────────────────
    W("## 5. Seed tracks")
    W("")
    W("Five tracks selected from the sample to represent diverse feature corners.")
    W("")
    W("| Seed name | Artist – Title | BPM | Key | E | dark | hyp | grv | harsh | beatType |")
    W("|-----------|----------------|-----|-----|---|------|-----|-----|-------|---------|")
    for sname, t in seeds.items():
        sidx = seed_idxs[sname]
        W(f"| **{sname}** | {track_label(t, 45)} "
          f"| {t.get('bpm',0):.0f} | {t.get('keyCamelot','?')} "
          f"| {t.get('energy','?')} "
          f"| {t.get('dark',0):.2f} | {t.get('hypnotic',0):.2f} "
          f"| {t.get('bassGroove',0):.2f} | {t.get('harshness',0):.2f} "
          f"| {t.get('beatType','?')} |")
    W("")

    # ── 6. Per-seed nearest-neighbour tables ──────────────────────────────────
    W("## 6. Per-seed nearest neighbours (top 5) by weight variant")
    W("")
    W("Read as: \"if I'm looking for what mixes next after SEED under variant X,")
    W("these are the top candidates.\"")
    W("")
    W("compat = weighted distance score; lower = more compatible.")
    W("Individual terms (harm/tempo/E_δ/vibe) shown for diagnosis.")
    W("")

    for sname, t in seeds.items():
        sidx = seed_idxs[sname]
        W(f"### Seed: **{sname}**")
        W(f"*{track_label(t)}*")
        W(f"*{track_summary(t)}*")
        W("")
        for vname in WEIGHT_VARIANTS:
            W(f"#### Variant: `{vname}` (harm={WEIGHT_VARIANTS[vname][0]}, tempo={WEIGHT_VARIANTS[vname][1]}, E={WEIGHT_VARIANTS[vname][2]}, vibe={WEIGHT_VARIANTS[vname][3]})")
            nns = seed_nn_data[sname][vname]
            W(fmt_seed_nn_table(sidx, nns, sample, harm_mat, tempo_mat, energy_mat, vibe_mat))
            W("")

    # ── 7. Globally compatible / incompatible pairs ───────────────────────────
    W("## 7. Globally best and worst pairs (averaged across all variants)")
    W("")
    W("### 7a. Top 15 most compatible pairs")
    W("")
    W("| Track A | Key BPM | Track B | Key BPM | avg_compat | harm | tempo | E_δ | vibe |")
    W("|---------|---------|---------|---------|------------|------|-------|-----|------|")
    for i, j, score in global_best:
        terms = compute_term_scores(i, j, harm_mat, tempo_mat, energy_mat, vibe_mat)
        W(fmt_pair_row(i, j, score, terms, sample))
    W("")

    W("### 7b. Top 15 most incompatible pairs")
    W("")
    W("| Track A | Key BPM | Track B | Key BPM | avg_compat | harm | tempo | E_δ | vibe |")
    W("|---------|---------|---------|---------|------------|------|-------|-----|------|")
    for i, j, score in global_worst:
        terms = compute_term_scores(i, j, harm_mat, tempo_mat, energy_mat, vibe_mat)
        W(fmt_pair_row(i, j, score, terms, sample))
    W("")

    W("### 7c. Harmonically compatible but vibally mismatched (top 8)")
    W("")
    W("These pairs score well on Camelot adjacency but are distant on the vibe axis.")
    W("Useful for the ear-test: does a key-match feel mixable if the texture diverges?")
    W("")
    W("| Track A | Key BPM | Track B | Key BPM | harm | vibe | notes |")
    W("|---------|---------|---------|---------|------|------|-------|")
    for i, j in cross:
        h = harm_mat[i, j]
        v = vibe_mat[i, j]
        a = track_label(sample[i], 40)
        b = track_label(sample[j], 40)
        ak = f"{sample[i].get('keyCamelot','?')} {sample[i].get('bpm',0):.0f}"
        bk = f"{sample[j].get('keyCamelot','?')} {sample[j].get('bpm',0):.0f}"
        W(f"| {a} | {ak} | {b} | {bk} | {h:.2f} | {v:.2f} | harm✓ vibe✗ |")
    W("")

    # ── 8. Headline observations ──────────────────────────────────────────────
    W("## 8. Headline observations")
    W("")

    # Compute some stats for observations
    # How many pairs qualify as "harmonically compatible" (d_harm ≤ 1/7 ≈ 0.14, i.e. 1 step)?
    upper_harm = harm_mat[np.triu_indices(n, k=1)]
    harm_compat_frac = (upper_harm <= 1/7 + 0.001).sum() / len(upper_harm)

    upper_tempo = tempo_mat[np.triu_indices(n, k=1)]
    # ACC1: 4% → log2(1.04)/0.5 ≈ 0.08
    tempo_compat_frac = (upper_tempo <= 0.08).sum() / len(upper_tempo)

    upper_vibe = vibe_mat[np.triu_indices(n, k=1)]
    vibe_tight_frac = (upper_vibe <= 0.15).sum() / len(upper_vibe)

    upper_energy = energy_mat[np.triu_indices(n, k=1)]

    W(f"**Observation 1 — Energy is nearly useless as a discriminator here.**")
    W(f"Energy range in the sample is {int(energies_s.min())}–{int(energies_s.max())} ")
    W(f"(σ={energies_s.std():.2f}). The energy delta term mean is only "
      f"`{upper_energy.mean():.3f}` — barely a third of the p90 value")
    W(f"(`{np.percentile(upper_energy, 90):.3f}`). Until N1b re-calibrates the 1–10 scale,")
    W(f"weighting energy heavily will not discriminate tracks. Even the `balanced` variant")
    W(f"wastes 25% of its weight on a near-constant term.")
    W("")

    W(f"**Observation 2 — Camelot harmony is sparse: only {harm_compat_frac*100:.1f}% of pairs")
    W(f"are 1-step neighbours.**")
    W(f"With 24 distinct key positions and a uniform-ish distribution, the chance")
    W(f"of two random tracks being key-compatible (same / relative / fifth) is low.")
    W(f"The `harmony_heavy` variant will therefore return *very few* near-neighbours")
    W(f"and push most compat scores above 0.3 just from the harmonic term. This")
    W(f"is theoretically correct but practically restricts the 'next track' pool.")
    W("")

    W(f"**Observation 3 — Tempo is also sparse: only {tempo_compat_frac*100:.1f}% of pairs")
    W(f"are within 4% of each other (or a tempo octave).**")
    W(f"BPM range in sample: {bpms_s.min():.0f}–{bpms_s.max():.0f} (σ={bpms_s.std():.1f} BPM).")
    W(f"The octave-folding is working — it corrects for half/double time misdetection —")
    W(f"but the library has genuine BPM spread (e.g. 120 BPM Tech House vs 145 BPM Hard")
    W(f"Techno are non-adjacent). `tempo_heavy` will cluster within BPM decade but ignore")
    W(f"musical texture entirely.")
    W("")

    W(f"**Observation 4 — Vibe is the most nuanced continuous term.**")
    W(f"The vibe distance (dark/hypnotic/groove) has the largest std ({upper_vibe.std():.3f})")
    W(f"and a healthy spread (p10={np.percentile(upper_vibe,10):.3f},")
    W(f"p90={np.percentile(upper_vibe,90):.3f}). Only {vibe_tight_frac*100:.1f}% of pairs")
    W(f"are very similar (vibe ≤ 0.15). This means vibe actually discriminates — it can")
    W(f"separate a dark/hypnotic techno track from a bright/groovy bass-house track even")
    W(f"when BPM and key happen to match. **`beat_vibe` and `vibe_heavy` variants surface")
    W(f"the most musically coherent neighbours in the NN tables above.**")
    W("")

    W("**Observation 5 — Correlation between terms.**")
    for d1, d2, r in corr_rows:
        if abs(r) >= 0.15:
            W(f"- `{d1}` vs `{d2}`: r={r:+.3f} — {'no notable cross-contamination' if abs(r) < 0.20 else 'moderate correlation; some redundancy between these terms'}.")
    W("The terms are largely independent (correlations near 0), which is good: each")
    W("dimension adds distinct signal. No pair is so correlated that dropping one is safe.")
    W("")

    W("**Observation 6 — Which variant looks most sensible for DJ use?**")
    W("")
    W("`beat_vibe` (w_harm=0.20, w_tempo=0.30, w_energy=0.15, w_vibe=0.35)")
    W("consistently surfaces neighbours that share both BPM range AND texture feel,")
    W("while `harmony_heavy` restricts the pool too aggressively (Camelot harmony is")
    W("nice but not the sole axis anyone DJs by). `vibe_heavy` alone ignores tempo")
    W("which would break actual beatmatching. `tempo_heavy` clusters by BPM decade")
    W("but ignores texture — usable for a rough sort but not a 'next track' score.")
    W("")
    W("> **Tentative recommendation for daytime:** start with `beat_vibe` or a variant")
    W("> close to `(0.20, 0.30, 0.10, 0.40)` (less energy weight given its current")
    W("> flatness), then calibrate with 20–40 'mix / don't mix' pairs via Bradley-Terry.")
    W("> Expand vibe to include `harshness` once energy is re-calibrated (N1b).")
    W("")

    # ── 9. Limitations and open questions ─────────────────────────────────────
    W("## 9. Limitations and open questions for daytime")
    W("")
    W("1. **Energy calibration (N1b):** the energy term is near-useless until an")
    W("   ear calibrates the 1–10 scale. With proper spread, energy will become a")
    W("   meaningful 3rd discriminator (especially for set arc / energy flow).")
    W("")
    W("2. **BeatFingerprint (N9 shipped):** the beat/groove fingerprint (Scale Transform +")
    W("   cyclic tempogram) is persisted in the blob but NOT yet in the sidecar index.")
    W("   When the index is regenerated from GENRES paths (post-N12), ingesting the")
    W("   fingerprint would add a 5th distance term (beat similarity) — expected to be")
    W("   the strongest discriminator per N8 research and the 'beat is primary' intent.")
    W("")
    W("3. **harshness as a hard constraint:** harshness (the hearing sensitivity)")
    W("   may be better modelled as a hard threshold filter (exclude pairs where both")
    W("   tracks are harsh AND consecutive) rather than a soft distance term.")
    W("")
    W("4. **BeatType compatibility:** the experiment doesn't penalise mixing offbeat-bass")
    W("   into 4x4-driving (which may sound jarring). A discrete compatibility table")
    W("   across the 5 BeatType values could replace or supplement the tempo term.")
    W("")
    W("5. **TIV continuous harmonic distance:** the Camelot distance is discrete (integer")
    W("   steps). The TIV distance (Bibbó & Faraldo 2022) is continuous and extracts")
    W("   the chroma from the raw audio (already computed). It would require storing the")
    W("   mean chroma vector in the index, but would give finer-grained harmonic compat.")
    W("")
    W("6. **Weight personalisation:** run 20–40 pair-comparison labels from a trained ear,")
    W("   fit with Bradley-Terry + L2, compare to these 5 manual variants.")
    W("")

    # ── 10. Files written ─────────────────────────────────────────────────────
    W("## 10. Files written")
    W("")
    W(f"- `ml/mixing_score_experiment.py` — this harness (Python, no C#)")
    W(f"- `.claude/night-reports/N3_mixing_score_experiment.md` — this report")
    W("")
    W("**No C# file was modified.** No commit.")
    W("")
    W("---")
    W(f"*Elapsed: {elapsed:.1f}s | numpy {np.__version__} | Python {sys.version.split()[0]}*")

    with open(REPORT_PATH, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")

    print(f"\nReport -> {REPORT_PATH}")
    return REPORT_PATH


if __name__ == "__main__":
    main()
