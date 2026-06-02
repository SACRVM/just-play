"""
The HONEST cross-dataset evaluation: train on the GiantSteps-MTG-Key set (~1486 human-
verified EDM tracks) and test on the separate GiantSteps-Key set (604) as a true held-out.
No data leakage, no tool labels — measures real generalization to unseen music.

This is the methodologically clean version of train_key.py (which did CV on one set).

Usage: python train_holdout.py chroma36_mtg.csv chroma36.csv [out.onnx]
"""
import sys, numpy as np
from sklearn.neural_network import MLPClassifier

BINS = 36
def load(path):
    pcs, modes, X = [], [], []
    for line in open(path):
        p = line.strip().split(",")
        if len(p) < 2 + BINS: continue
        pcs.append(int(p[0])); modes.append(int(p[1]))
        X.append([float(v) for v in p[2:2 + BINS]])
    return np.array(pcs), np.array(modes), np.array(X, dtype=np.float32)

def transpose(x, k): return np.roll(x, k * 3)
def key_idx(pc, mode): return mode * 12 + pc

def mirex(pp, pm, tp, tm):
    if pp == tp and pm == tm: return 1.0
    d = (pp - tp) % 12; same = pm == tm
    if same and d in (5, 7): return 0.5
    if not same:
        if pm == 0 and tm == 1 and d == 3: return 0.3
        if pm == 1 and tm == 0 and d == 9: return 0.3
        if d == 0: return 0.2
    return 0.0

def augment(pcs, modes, X):
    Xa, ya = [], []
    for i in range(len(pcs)):
        for k in range(12):
            Xa.append(transpose(X[i], k)); ya.append(key_idx((pcs[i] + k) % 12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

if __name__ == "__main__":
    trainCsv = sys.argv[1]; testCsv = sys.argv[2]
    trp, trm, trX = load(trainCsv)
    tep, tem, teX = load(testCsv)
    print(f"train (MTG): {len(trp)} tracks | test (GiantSteps): {len(tep)} tracks", flush=True)

    Xa, ya = augment(trp, trm, trX)
    for kind, hls in [("mlp128_64", (128, 64)), ("mlp256_128", (256, 128))]:
        clf = MLPClassifier(hidden_layer_sizes=hls, max_iter=800, alpha=1e-3).fit(Xa, ya)
        pred = clf.predict(teX)
        scores, exact = [], 0
        for j in range(len(tep)):
            s = mirex(pred[j] % 12, pred[j] // 12, tep[j], tem[j]); scores.append(s)
            if s == 1.0: exact += 1
        print(f"  {kind:11s} TRUE held-out MIREX {np.mean(scores):.3f}  exact {exact/len(tep)*100:.0f}%"
              f"  (CV-on-604 was ~0.75; template 0.712)", flush=True)
