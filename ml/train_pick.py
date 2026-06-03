"""
Decision experiment: which training set ships the best model for HIGH-QUALITY user music?

The MTG-Key tracks are LOFI mp3s, so a CV over the mixed pool tests partly on degraded
audio and looks pessimistic. To pick the shipping model honestly we hold out 20% of the
high-quality GiantSteps-604 and test three training sets on exactly that clean held-out:
  A) 604 only (the other 80%)
  B) MTG only (1387)
  C) MTG + the 604's 80%   <- the "combined" candidate

Highest score on the clean held-out wins. sklearn MLP(128,64) to match the 0.738 holdout.

Usage: python train_pick.py chroma36_mtg.csv chroma36.csv
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
def kidx(pc, mode): return mode * 12 + pc

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
            Xa.append(transpose(X[i], k)); ya.append(kidx((pcs[i] + k) % 12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def run(name, trp, trm, trX, tep, tem, teX):
    Xa, ya = augment(trp, trm, trX)
    clf = MLPClassifier(hidden_layer_sizes=(128, 64), max_iter=800, alpha=1e-3).fit(Xa, ya)
    pred = clf.predict(teX)
    sc = [mirex(pred[j] % 12, pred[j] // 12, tep[j], tem[j]) for j in range(len(tep))]
    ex = sum(1 for s in sc if s == 1.0)
    print(f"  {name:22s} -> held-out MIREX {np.mean(sc):.3f}  exact {ex/len(tep)*100:.0f}%", flush=True)

if __name__ == "__main__":
    mp, mm, mX = load(sys.argv[1])              # MTG 1387
    gp, gm, gX = load(sys.argv[2])              # GiantSteps 604 (high quality)
    n = len(gp); perm = np.random.RandomState(0).permutation(n)
    te = perm[:n // 5]; tr = perm[n // 5:]      # 20% clean held-out, 80% train
    print(f"604: {len(tr)} train / {len(te)} held-out (high-quality)  |  MTG: {len(mp)}", flush=True)

    tep, tem, teX = gp[te], gm[te], gX[te]
    # A) 604-only (80%)
    run("A 604-only(80%)", gp[tr], gm[tr], gX[tr], tep, tem, teX)
    # B) MTG-only
    run("B MTG-only(1387)", mp, mm, mX, tep, tem, teX)
    # C) MTG + 604(80%)
    cp = np.concatenate([mp, gp[tr]]); cm = np.concatenate([mm, gm[tr]]); cX = np.concatenate([mX, gX[tr]])
    run("C MTG+604(80%)", cp, cm, cX, tep, tem, teX)
