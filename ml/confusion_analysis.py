"""
Fine-grained error-structure analysis of the shipped MLP key model on GiantSteps-604.

The MIREX score lumps everything not exact/fifth/relative/parallel into "other". This asks the
sharper question: of the WRONG calls, how are they distributed by pitch-class interval + mode
relationship? That tells us whether the remaining error is musically-near (fifths, relative,
semitone/tuning) — i.e. a ceiling we can chip at with better DSP/profiles — or effectively random
(tritone, unrelated), which would point at genuinely ambiguous tracks.

5-fold CV, MLP(128,64), 12x transposition aug (same recipe as the shipped model).
Usage: python confusion_analysis.py chroma36.csv
"""
import sys, numpy as np
from collections import Counter
from sklearn.neural_network import MLPClassifier

BINS = 36
NAMES = ['C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B']

def load(path):
    pcs, modes, X = [], [], []
    for line in open(path):
        p = line.strip().split(",")
        if len(p) < 2 + BINS: continue
        pcs.append(int(p[0])); modes.append(int(p[1]))
        X.append([float(v) for v in p[2:2+BINS]])
    return np.array(pcs), np.array(modes), np.array(X, dtype=np.float32)

def transpose(x, k): return np.roll(x, k*3)
def kidx(pc, mode): return mode*12 + pc

def augment(idx, pcs, modes, X):
    Xa, ya = [], []
    for i in idx:
        for k in range(12):
            Xa.append(transpose(X[i], k)); ya.append(kidx((pcs[i]+k)%12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def relationship(pp, pm, tp, tm):
    """Classify a (predicted vs true) key pair into a musical relationship bucket."""
    if pp == tp and pm == tm: return "exact"
    d = (pp - tp) % 12
    same = pm == tm
    if same and d in (5, 7): return "fifth"
    if same and d == 1 or same and d == 11: return "semitone (same mode)"
    if same and d in (2, 10): return "whole-tone (same mode)"
    if same and d == 6: return "tritone (same mode)"
    if same: return "other (same mode)"
    # cross-mode
    if pm == 0 and tm == 1 and d == 3: return "relative (maj for min)"
    if pm == 1 and tm == 0 and d == 9: return "relative (min for maj)"
    if d == 0: return "parallel"
    return "other (cross mode)"

if __name__ == "__main__":
    csv = sys.argv[1] if len(sys.argv) > 1 else "chroma36.csv"
    pcs, modes, X = load(csv)
    n = len(pcs)
    print(f"loaded {n} tracks", flush=True)

    perm = np.random.RandomState(0).permutation(n)
    fold = np.array([np.where(perm == i)[0][0] % 5 for i in range(n)])

    buckets = Counter()
    minor_total = major_total = 0
    minor_wrong = major_wrong = 0
    examples = {}

    for f in range(5):
        tr = np.where(fold != f)[0]; te = np.where(fold == f)[0]
        Xa, ya = augment(tr, pcs, modes, X)
        clf = MLPClassifier(hidden_layer_sizes=(128,64), max_iter=800, alpha=1e-3,
                            random_state=0).fit(Xa, ya)
        pred = clf.predict(X[te])
        for j, i in enumerate(te):
            pp, pm = pred[j] % 12, pred[j] // 12
            tp, tm = pcs[i], modes[i]
            rel = relationship(pp, pm, tp, tm)
            buckets[rel] += 1
            if tm == 1: minor_total += 1
            else: major_total += 1
            if rel != "exact":
                if tm == 1: minor_wrong += 1
                else: major_wrong += 1
                if rel not in examples:
                    examples[rel] = f"{NAMES[tp]}{'m' if tm else ''} -> {NAMES[pp]}{'m' if pm else ''}"
        print(f"  fold {f} done", flush=True)

    print(f"\n=== error structure (n={n}) ===")
    total = sum(buckets.values())
    for rel, c in buckets.most_common():
        ex = f"   e.g. {examples[rel]}" if rel in examples else ""
        print(f"  {rel:24s} {c:4d}  ({c/total*100:4.1f}%){ex}")

    exact = buckets["exact"]
    wrong = total - exact
    print(f"\n  exact {exact}/{total} = {exact/total*100:.1f}%   wrong = {wrong}")
    print(f"  of WRONG calls: fifth+relative+parallel = "
          f"{buckets['fifth']+buckets['relative (maj for min)']+buckets['relative (min for maj)']+buckets['parallel']}"
          f" ({(buckets['fifth']+buckets['relative (maj for min)']+buckets['relative (min for maj)']+buckets['parallel'])/wrong*100:.0f}% of errors are harmonically-near)")
    print(f"\n  minor tracks: {minor_total}, wrong {minor_wrong} ({minor_wrong/max(minor_total,1)*100:.0f}%)")
    print(f"  major tracks: {major_total}, wrong {major_wrong} ({major_wrong/max(major_total,1)*100:.0f}%)")
