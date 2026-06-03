"""
Hypothesis test from confusion_analysis.py: the MLP is 80% WRONG on major tracks vs 23% on minor,
because GiantSteps is ~85% minor (511 vs 93) so the model learns a strong minor bias. Transposition
aug (x12) balances PITCH CLASS but NOT mode. Does oversampling major tracks to ~1:1 mode balance fix
majors without wrecking minors / overall MIREX?

  A baseline : x12 transposition aug (shipped recipe)
  B balanced : same, but major tracks replicated ~5x (511/93) so major:minor ~ 1:1 in training

5-fold CV, MLP(128,64). Reports overall MIREX + per-mode exact accuracy.
Usage: python train_balanced.py chroma36.csv
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
        X.append([float(v) for v in p[2:2+BINS]])
    return np.array(pcs), np.array(modes), np.array(X, dtype=np.float32)

def transpose(x, k): return np.roll(x, k*3)
def kidx(pc, mode): return mode*12 + pc

def mirex(pp, pm, tp, tm):
    if pp == tp and pm == tm: return 1.0
    d = (pp-tp) % 12; same = pm == tm
    if same and d in (5,7): return 0.5
    if not same:
        if pm==0 and tm==1 and d==3: return 0.3
        if pm==1 and tm==0 and d==9: return 0.3
        if d==0: return 0.2
    return 0.0

def augment(idx, pcs, modes, X, major_rep=1):
    Xa, ya = [], []
    for i in idx:
        rep = major_rep if modes[i] == 0 else 1
        for _ in range(rep):
            for k in range(12):
                Xa.append(transpose(X[i], k)); ya.append(kidx((pcs[i]+k)%12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def run(name, pcs, modes, X, major_rep):
    n = len(pcs); perm = np.random.RandomState(0).permutation(n)
    fold = np.array([np.where(perm==i)[0][0] % 5 for i in range(n)])
    sc = []; maj_ok=maj_n=min_ok=min_n=0
    for f in range(5):
        tr = np.where(fold!=f)[0]; te = np.where(fold==f)[0]
        Xa, ya = augment(tr, pcs, modes, X, major_rep)
        clf = MLPClassifier(hidden_layer_sizes=(128,64), max_iter=800, alpha=1e-3, random_state=0).fit(Xa, ya)
        pred = clf.predict(X[te])
        for j,i in enumerate(te):
            s = mirex(pred[j]%12, pred[j]//12, pcs[i], modes[i]); sc.append(s)
            exact = (pred[j]%12==pcs[i] and pred[j]//12==modes[i])
            if modes[i]==0: maj_n+=1; maj_ok += exact
            else: min_n+=1; min_ok += exact
    print(f"  {name:16s} MIREX {np.mean(sc):.3f}  major-exact {maj_ok/maj_n*100:4.0f}% ({maj_ok}/{maj_n})  "
          f"minor-exact {min_ok/min_n*100:4.0f}% ({min_ok}/{min_n})", flush=True)

if __name__ == "__main__":
    csv = sys.argv[1] if len(sys.argv) > 1 else "chroma36.csv"
    pcs, modes, X = load(csv)
    print(f"loaded {len(pcs)} ({(modes==0).sum()} major / {(modes==1).sum()} minor)", flush=True)
    run("A baseline x1", pcs, modes, X, major_rep=1)
    run("B major x5", pcs, modes, X, major_rep=5)
