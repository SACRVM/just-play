"""
Learned key classifier on JustPlay's 36-bin HPCP chroma, evaluated with track-level
5-fold cross-validation + 12x transposition augmentation (exploits the key symmetry,
no track leakage). Goal: beat the hand-set template's MIREX 0.712 on GiantSteps,
ideally reaching 0.74+.

Usage: python train_key.py chroma36.csv
"""
import sys, numpy as np
from sklearn.neural_network import MLPClassifier
from sklearn.linear_model import LogisticRegression

BINS = 36
def load(path):
    pcs, modes, X = [], [], []
    for line in open(path):
        p = line.strip().split(",")
        if len(p) < 2 + BINS:
            continue
        pcs.append(int(p[0])); modes.append(int(p[1]))
        X.append([float(v) for v in p[2:2 + BINS]])
    return np.array(pcs), np.array(modes), np.array(X)

def key_idx(pc, mode):          # 0-11 major, 12-23 minor
    return mode * 12 + pc

def transpose(x, k):            # shift chroma up k semitones (k*3 fine bins), circular
    return np.roll(x, k * 3)

def mirex(pred_pc, pred_mode, true_pc, true_mode):
    if pred_pc == true_pc and pred_mode == true_mode:
        return 1.0
    diff = (pred_pc - true_pc) % 12
    same = pred_mode == true_mode
    if same and diff in (5, 7):          # perfect fifth
        return 0.5
    if not same:
        if pred_mode == 0 and true_mode == 1 and diff == 3:  # rel major
            return 0.3
        if pred_mode == 1 and true_mode == 0 and diff == 9:  # rel minor
            return 0.3
        if diff == 0:                    # parallel
            return 0.2
    return 0.0

def make_model(kind):
    if kind == "logreg":
        return LogisticRegression(max_iter=2000, C=1.0, multi_class="multinomial")
    if kind == "mlp64":
        return MLPClassifier(hidden_layer_sizes=(64,), max_iter=1500, alpha=1e-3)
    if kind == "mlp128_64":
        return MLPClassifier(hidden_layer_sizes=(128, 64), max_iter=1500, alpha=1e-3)
    if kind == "mlp256_128":
        return MLPClassifier(hidden_layer_sizes=(256, 128), max_iter=1500, alpha=1e-3)
    raise ValueError(kind)

def evaluate(kind, pcs, modes, X, folds=5, seed_perm=None):
    n = len(pcs)
    order = seed_perm if seed_perm is not None else np.arange(n)
    fold_of = np.array([order[i] % folds for i in range(n)])
    scores, exact = [], 0
    for f in range(folds):
        tr = np.where(fold_of != f)[0]
        te = np.where(fold_of == f)[0]
        # augment training set with all 12 transpositions
        Xtr, ytr = [], []
        for i in tr:
            for k in range(12):
                Xtr.append(transpose(X[i], k))
                ytr.append(key_idx((pcs[i] + k) % 12, modes[i]))
        clf = make_model(kind).fit(np.array(Xtr), np.array(ytr))
        pred = clf.predict(X[te])
        for j, i in enumerate(te):
            pm, pp = pred[j] // 12, pred[j] % 12
            s = mirex(pp, pm, pcs[i], modes[i])
            scores.append(s)
            if s == 1.0:
                exact += 1
    return np.mean(scores), exact / n

def fold12(X):  # sum each pitch class's 3 sub-bins -> 12-bin chroma
    return X.reshape(len(X), 12, 3).sum(axis=2)

if __name__ == "__main__":
    pcs, modes, X36 = load(sys.argv[1] if len(sys.argv) > 1 else "chroma36.csv")
    print(f"Loaded {len(pcs)} tracks, feature dim {X36.shape[1]}")
    rng = np.random.RandomState(0)
    perm = rng.permutation(len(pcs))

    # NOTE: transpose() rolls by k*3 fine bins (= k semitones) on the 36-bin feature.
    # For the folded-12 feature we roll by k bins instead, so define a feature-aware eval.
    print("\n== 36-bin fine feature (converged) ==")
    for kind in ["mlp64", "mlp128_64", "mlp256_128"]:
        m, ex = evaluate(kind, pcs, modes, X36, seed_perm=perm)
        print(f"  {kind:12s}  MIREX {m:.3f}   exact {ex*100:.0f}%   (template: 0.712 / 64%)")

    print("\n== robustness: 36-bin mlp128_64 across 3 fold-seeds ==")
    ms = []
    for s in range(3):
        pp = np.random.RandomState(s).permutation(len(pcs))
        m, ex = evaluate("mlp128_64", pcs, modes, X36, seed_perm=pp)
        ms.append(m)
        print(f"  seed {s}: MIREX {m:.3f}  exact {ex*100:.0f}%")
    print(f"  >>> mean MIREX {np.mean(ms):.3f} (+/- {np.std(ms):.3f}) — goal 0.74")
