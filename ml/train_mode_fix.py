"""
Major-key bias fix — design experiment. Baseline is 80% wrong on majors (mode class-imbalance, 85% minor).
Compare strategies on GiantSteps 604 (5-fold CV, sklearn MLP for fast iteration):

  A baseline 24-way (x12 aug)                 — shipped recipe
  B oversample major x2 / x3 (24-way)         — milder than the x5 that over-corrected
  C TWO-STAGE: 12-way tonic  +  balanced binary major/minor on TONIC-ALIGNED chroma
      (rationale: x12 aug already balances the 12 tonics; only MODE is imbalanced. Predict the
       tonic, roll the chroma so tonic->bin0, then a balanced binary net decides maj/min where the
       third sits at a fixed position — separating the mode decision from the tonic decision.)

Reports per method: tonic-acc, mode-acc, major-exact, minor-exact, overall-exact, MIREX.
Usage: python train_mode_fix.py chroma36.csv
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

def roll(x, k): return np.roll(x, k*3)          # k semitones on a 3-bins/semitone chroma
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

def aug24(idx, pcs, modes, X, major_rep=1):
    Xa, ya = [], []
    for i in idx:
        rep = major_rep if modes[i] == 0 else 1
        for _ in range(rep):
            for k in range(12):
                Xa.append(roll(X[i], k)); ya.append(kidx((pcs[i]+k)%12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def aug_tonic(idx, pcs, modes, X):
    """12-way tonic training set (label = pitch class), x12 transposition aug."""
    Xa, ya = [], []
    for i in idx:
        for k in range(12):
            Xa.append(roll(X[i], k)); ya.append((pcs[i]+k)%12)
    return np.array(Xa, dtype=np.float32), np.array(ya)

def aug_mode(idx, pcs, modes, X, balance=True):
    """Binary mode set on TONIC-ALIGNED chroma (roll so tonic->bin0). Oversample majors to ~1:1."""
    nmaj = sum(1 for i in idx if modes[i]==0); nmin = sum(1 for i in idx if modes[i]==1)
    rep_major = max(1, round(nmin/max(nmaj,1))) if balance else 1
    Xa, ya = [], []
    for i in idx:
        aligned = roll(X[i], (-pcs[i]) % 12)     # tonic -> bin 0
        rep = rep_major if modes[i]==0 else 1
        for _ in range(rep):
            Xa.append(aligned); ya.append(modes[i])
    return np.array(Xa, dtype=np.float32), np.array(ya)

def mlp(seed=0): return MLPClassifier(hidden_layer_sizes=(128,64), max_iter=800, alpha=1e-3, random_state=seed)

def evaluate(name, preds_pc, preds_mode, pcs, modes):
    sc=[]; maj_ok=maj_n=min_ok=min_n=tonic_ok=mode_ok=0
    n=len(pcs)
    for i in range(n):
        pp,pm = preds_pc[i], preds_mode[i]
        sc.append(mirex(pp,pm,pcs[i],modes[i]))
        if pp==pcs[i]: tonic_ok+=1
        if pm==modes[i]: mode_ok+=1
        exact = (pp==pcs[i] and pm==modes[i])
        if modes[i]==0: maj_n+=1; maj_ok+=exact
        else: min_n+=1; min_ok+=exact
    print(f"  {name:26s} MIREX {np.mean(sc):.3f}  exact {(maj_ok+min_ok)/n*100:4.1f}%  "
          f"tonic {tonic_ok/n*100:4.0f}%  mode {mode_ok/n*100:4.0f}%  "
          f"MAJ {maj_ok/maj_n*100:4.0f}% ({maj_ok}/{maj_n})  min {min_ok/min_n*100:3.0f}%", flush=True)

if __name__ == "__main__":
    csv = sys.argv[1] if len(sys.argv)>1 else "chroma36.csv"
    pcs, modes, X = load(csv); n=len(pcs)
    print(f"loaded {n} ({(modes==0).sum()} major / {(modes==1).sum()} minor)", flush=True)
    perm = np.random.RandomState(0).permutation(n)
    fold = np.array([np.where(perm==i)[0][0] % 5 for i in range(n)])

    methods = ["A base x1", "B major x2", "B major x3", "C two-stage"]
    P = {m: (np.zeros(n,int), np.zeros(n,int)) for m in methods}

    for f in range(5):
        tr = np.where(fold!=f)[0]; te = np.where(fold==f)[0]
        # A / B: 24-way with oversample
        for name, rep in [("A base x1",1), ("B major x2",2), ("B major x3",3)]:
            Xa, ya = aug24(tr, pcs, modes, X, major_rep=rep)
            pred = mlp().fit(Xa, ya).predict(X[te])
            for j,i in enumerate(te): P[name][0][i]=pred[j]%12; P[name][1][i]=pred[j]//12
        # C two-stage
        Xt, yt = aug_tonic(tr, pcs, modes, X)
        tonic_clf = mlp(1).fit(Xt, yt)
        Xm, ym = aug_mode(tr, pcs, modes, X, balance=True)
        mode_clf = mlp(2).fit(Xm, ym)
        t_pred = tonic_clf.predict(X[te])
        aligned_te = np.array([roll(X[i], (-t_pred[j]) % 12) for j,i in enumerate(te)], dtype=np.float32)
        m_pred = mode_clf.predict(aligned_te)
        for j,i in enumerate(te): P["C two-stage"][0][i]=t_pred[j]; P["C two-stage"][1][i]=m_pred[j]
        print(f"  fold {f} done", flush=True)

    print("\n=== results ===")
    for m in methods:
        evaluate(m, P[m][0], P[m][1], pcs, modes)
