"""
Experiment (a): does training on CLEAN (consensus) labels beat training on ALL
labels, even though it shrinks the set?

We have two independent annotations of the GiantSteps-604 audio: the original
604 labels and the GiantSteps+ (Camhi & Faraldo) re-annotation. They agree
exactly on ~466 tracks ("consensus") and disagree on ~100. The consensus labels
are our most trustworthy ground truth.

  Test  = 20% of consensus tracks (clean; 604==GS+ here by definition)
  A all : train on all 604 minus the held-out ids        (more data, ~18% noisy)
  B cons: train on consensus minus the held-out ids       (clean, fewer)

Averaged over 5 split seeds. sklearn MLP(128,64), x12 transposition aug,
36-bin HPCP, MIREX-weighted scoring. Chroma rows are in sorted-track-id order
(verified 604/604), so row i -> ids[i].

Usage: python train_consensus.py
"""
import glob, os, json, numpy as np
from sklearn.neural_network import MLPClassifier

CHROMA = r"C:/Users/goosefx/datasets/giantsteps-key/chroma36.csv"
KEYDIR = r"C:/Users/goosefx/datasets/giantsteps-key/annotations/key"
PLUSDIR = r"C:/Users/goosefx/datasets/giantsteps-plus/extracted/new_annotations"
BINS = 36

PC = {'C':0,'C#':1,'DB':1,'D':2,'D#':3,'EB':3,'E':4,'FB':4,'E#':5,'F':5,'F#':6,
      'GB':6,'G':7,'G#':8,'AB':8,'A':9,'A#':10,'BB':10,'B':11,'CB':11,'B#':0}

def parse_key(s):
    s = s.strip()
    if not s: return None
    if s.lower().startswith('key'): s = ' '.join(s.split()[2:])
    p = s.split()
    if len(p) < 2 or p[0].upper() not in PC: return None
    m = p[1].lower()
    if m.startswith('maj'): return (PC[p[0].upper()], 0)
    if m.startswith('min'): return (PC[p[0].upper()], 1)
    return None

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

def augment(idx, pcs, modes, X):
    Xa, ya = [], []
    for i in idx:
        for k in range(12):
            Xa.append(transpose(X[i], k)); ya.append(kidx((pcs[i]+k)%12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def fit_eval(tr_idx, te_idx, pcs, modes, X, seed):
    Xa, ya = augment(tr_idx, pcs, modes, X)
    clf = MLPClassifier(hidden_layer_sizes=(128,64), max_iter=800, alpha=1e-3,
                        random_state=seed).fit(Xa, ya)
    pred = clf.predict(X[te_idx])
    sc = [mirex(pred[j]%12, pred[j]//12, pcs[i], modes[i]) for j,i in enumerate(te_idx)]
    ex = sum(1 for s in sc if s == 1.0)
    return np.mean(sc), ex/len(te_idx)

# --- load chroma (sorted-id order) ---
files = sorted(glob.glob(os.path.join(KEYDIR, "*.key")))
ids = [os.path.basename(f).split('.')[0] for f in files]
rows = [l.strip().split(',') for l in open(CHROMA)]
assert len(rows) == len(ids), f"{len(rows)} rows vs {len(ids)} ids"
pcs = np.array([int(r[0]) for r in rows])
modes = np.array([int(r[1]) for r in rows])
X = np.array([[float(v) for v in r[2:2+BINS]] for r in rows], dtype=np.float32)

# --- GS+ primary labels, by id ---
plus = {}
for f in glob.glob(os.path.join(PLUSDIR, "*.json")):
    tid = os.path.basename(f).split('.')[0]
    k = (json.load(open(f, encoding='utf-8')).get('key') or [None])[0]
    pk = parse_key(k) if k else None
    if pk: plus[tid] = pk

# --- consensus mask: 604 label == GS+ primary ---
consensus = np.array([ids[i] in plus and (pcs[i], modes[i]) == plus[ids[i]]
                      for i in range(len(ids))])
cons_idx = np.where(consensus)[0]
print(f"604 total={len(ids)}  with GS+={sum(1 for t in ids if t in plus)}  "
      f"consensus={len(cons_idx)}  disputed={sum(1 for t in ids if t in plus)-len(cons_idx)}")

resA, resB = [], []
for seed in range(5):
    perm = np.random.RandomState(seed).permutation(cons_idx)
    n = len(perm); te = perm[:n//5]; cons_tr = perm[n//5:]
    teset = set(te.tolist())
    all_tr = np.array([i for i in range(len(ids)) if i not in teset])     # A: all 604 minus test
    a = fit_eval(all_tr, te, pcs, modes, X, seed)
    b = fit_eval(cons_tr, te, pcs, modes, X, seed)                        # B: consensus minus test
    resA.append(a); resB.append(b)
    print(f"  seed {seed}: A(all {len(all_tr)})  MIREX {a[0]:.3f} ex {a[1]*100:.0f}%   "
          f"|  B(cons {len(cons_tr)})  MIREX {b[0]:.3f} ex {b[1]*100:.0f}%", flush=True)

A = np.array(resA); B = np.array(resB)
print(f"\n=== averaged over 5 splits (test = clean consensus held-out) ===")
print(f"  A all-604     : MIREX {A[:,0].mean():.3f} ± {A[:,0].std():.3f}   exact {A[:,1].mean()*100:.1f}%")
print(f"  B consensus   : MIREX {B[:,0].mean():.3f} ± {B[:,0].std():.3f}   exact {B[:,1].mean()*100:.1f}%")
print(f"  delta (B-A)   : MIREX {B[:,0].mean()-A[:,0].mean():+.3f}")
