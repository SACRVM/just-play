"""
MAJOR_W sweep: find the best class-weight for major keys in CrossEntropyLoss.
Sweeps MAJOR_W in {1.0, 2.0, 3.0, 4.0}, reports per-fold CV then aggregate.

Usage: python ml/sweep_major_w.py [chroma36.csv]
"""
import sys, numpy as np, torch, torch.nn as nn

BINS = 36
CSV  = sys.argv[1] if len(sys.argv) > 1 else "C:/Users/goosefx/datasets/giantsteps-key/chroma36.csv"

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

class Net(nn.Module):
    def __init__(self):
        super().__init__()
        self.net = nn.Sequential(nn.Linear(36,128), nn.ReLU(), nn.Dropout(0.1),
                                 nn.Linear(128,64), nn.ReLU(), nn.Dropout(0.1),
                                 nn.Linear(64,24))
    def forward(self, x): return self.net(x)

def augment(idx, pcs, modes, X):
    Xa, ya = [], []
    for i in idx:
        for k in range(12):
            Xa.append(transpose(X[i], k)); ya.append(key_idx((pcs[i]+k)%12, modes[i]))
    return np.array(Xa, dtype=np.float32), np.array(ya)

def make_weight(major_w):
    """24-class weight tensor: classes 0-11 = major (weight=major_w), 12-23 = minor (weight=1.0)."""
    w = torch.ones(24)
    w[:12] = major_w
    return w

def train(Xa, ya, epochs=250, seed=0, major_w=1.0):
    torch.manual_seed(seed)
    net = Net(); opt = torch.optim.Adam(net.parameters(), lr=1e-3, weight_decay=1e-5)
    weight = make_weight(major_w)
    lossf = nn.CrossEntropyLoss(weight=weight)
    Xt, yt = torch.tensor(Xa), torch.tensor(ya)
    ds = torch.utils.data.TensorDataset(Xt, yt)
    dl = torch.utils.data.DataLoader(ds, batch_size=256, shuffle=True)
    net.train()
    for _ in range(epochs):
        for xb, yb in dl:
            opt.zero_grad(); loss = lossf(net(xb), yb); loss.backward(); opt.step()
    return net

def evaluate(preds, te_idx, pcs, modes):
    scores = []; maj_ok = maj_n = min_ok = min_n = 0
    for j, i in enumerate(te_idx):
        pp = preds[j] % 12; pm = preds[j] // 12
        s = mirex(pp, pm, pcs[i], modes[i]); scores.append(s)
        exact = (pp == pcs[i] and pm == modes[i])
        if modes[i] == 0: maj_n += 1; maj_ok += exact
        else: min_n += 1; min_ok += exact
    overall_exact = (maj_ok + min_ok) / len(te_idx)
    maj_exact = maj_ok / maj_n if maj_n else 0.0
    min_exact = min_ok / min_n if min_n else 0.0
    return np.mean(scores), overall_exact, maj_exact, maj_ok, maj_n, min_exact, min_ok, min_n

def cv_run(pcs, modes, X, major_w, folds=5):
    n = len(pcs)
    perm = np.random.RandomState(0).permutation(n)
    fold = np.array([perm[i] % folds for i in range(n)])
    all_scores = []; all_exact = []; all_maj_ok = all_maj_n = all_min_ok = all_min_n = 0
    for f in range(folds):
        tr = np.where(fold != f)[0]; te = np.where(fold == f)[0]
        Xa, ya = augment(tr, pcs, modes, X)
        net = train(Xa, ya, major_w=major_w); net.eval()
        with torch.no_grad():
            preds = net(torch.tensor(X[te])).argmax(1).numpy()
        mirex_s, oe, maje, maj_ok, maj_n, mine, min_ok, min_n = evaluate(preds, te, pcs, modes)
        all_scores.append(mirex_s); all_exact.append(oe)
        all_maj_ok += maj_ok; all_maj_n += maj_n
        all_min_ok += min_ok; all_min_n += min_n
        print(f"  fold {f}: MIREX {mirex_s:.3f}  exact {oe*100:.1f}%  MAJ {maje*100:.0f}% ({maj_ok}/{maj_n})  min {mine*100:.0f}%", flush=True)
    mirex_mean = np.mean(all_scores)
    exact_mean = (all_maj_ok + all_min_ok) / n
    maj_mean = all_maj_ok / all_maj_n
    min_mean = all_min_ok / all_min_n
    return mirex_mean, exact_mean, maj_mean, all_maj_ok, all_maj_n, min_mean

if __name__ == "__main__":
    pcs, modes, X = load(CSV)
    print(f"Loaded {len(pcs)} tracks ({(modes==0).sum()} major / {(modes==1).sum()} minor)\n", flush=True)

    sweep = [1.0, 2.0, 3.0, 4.0]
    results = {}
    for mw in sweep:
        print(f"=== MAJOR_W = {mw} ===", flush=True)
        mirex_m, exact_m, maj_m, maj_ok, maj_n, min_m = cv_run(pcs, modes, X, major_w=mw)
        results[mw] = (mirex_m, exact_m, maj_m, maj_ok, maj_n, min_m)
        print(f"  >> MAJOR_W={mw:.1f}  MIREX {mirex_m:.3f}  exact {exact_m*100:.1f}%  MAJ {maj_m*100:.0f}% ({maj_ok}/{maj_n})  min {min_m*100:.0f}%\n", flush=True)

    print("\n=== SWEEP SUMMARY ===")
    print(f"  {'MAJOR_W':>8}  {'MIREX':>7}  {'exact%':>7}  {'major%':>8}  {'minor%':>8}")
    for mw, (mirex_m, exact_m, maj_m, maj_ok, maj_n, min_m) in results.items():
        print(f"  {mw:>8.1f}  {mirex_m:>7.3f}  {exact_m*100:>7.1f}%  {maj_m*100:>7.0f}%  {min_m*100:>7.0f}%")
    print(flush=True)
