"""
Train the FINAL key-classifier on ALL 604 GiantSteps tracks (x12 transposition aug) and
export to ONNX for in-app inference (Microsoft.ML.OnnxRuntime). Also runs a quick torch
5-fold CV to confirm parity with the sklearn result (~0.74-0.75 MIREX).

Architecture: MLP 36 -> 128 -> 64 -> 24 (same as the validated sklearn MLP(128,64)).
Input: 36-bin L1-normalised HPCP chroma (HpcpKeyDetector.BuildFine36).
Output: 24 logits (0-11 major, 12-23 minor); key = argmax, confidence = softmax max.

Mode-bias fix (2026-06-03): GiantSteps is 85% minor, so an unweighted loss produces
~17% major-exact. We use CrossEntropyLoss(weight=w) with MAJOR_W upweighting the 12
major classes. CV sweep over {1,2,3,4} showed MAJOR_W=2.0 doubles major-exact (17%→34%)
at a MIREX cost of only −0.007 (0.751→0.744), which is within the ±0.01 tolerance.
Higher values (3,4) gave diminishing returns with >0.02 MIREX drop.

Usage: python train_final.py chroma36.csv model.onnx
"""
import sys, numpy as np, torch, torch.nn as nn

BINS = 36
# Upweight the 12 major-key output classes (indices 0-11) to counter the 85%-minor
# dataset imbalance. Value chosen by CV sweep: 2.0 doubles major-exact with <0.01
# MIREX loss vs the unweighted baseline (see ml/sweep_major_w.py for the sweep results).
MAJOR_W = 2.0

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

def make_class_weight(major_w=MAJOR_W):
    """24-class weight vector: major classes (0-11) get major_w, minor (12-23) get 1.0."""
    w = torch.ones(24)
    w[:12] = major_w
    return w

def train(Xa, ya, epochs=250, seed=0):
    torch.manual_seed(seed)
    net = Net(); opt = torch.optim.Adam(net.parameters(), lr=1e-3, weight_decay=1e-5)
    lossf = nn.CrossEntropyLoss(weight=make_class_weight())
    Xt, yt = torch.tensor(Xa), torch.tensor(ya)
    ds = torch.utils.data.TensorDataset(Xt, yt)
    dl = torch.utils.data.DataLoader(ds, batch_size=256, shuffle=True)
    net.train()
    for _ in range(epochs):
        for xb, yb in dl:
            opt.zero_grad(); loss = lossf(net(xb), yb); loss.backward(); opt.step()
    return net

def cv(pcs, modes, X, folds=5):
    n = len(pcs); perm = np.random.RandomState(0).permutation(n)
    fold = np.array([perm[i] % folds for i in range(n)])
    scores = []; maj_ok = maj_n = min_ok = min_n = 0
    for f in range(folds):
        tr = np.where(fold != f)[0]; te = np.where(fold == f)[0]
        Xa, ya = augment(tr, pcs, modes, X)
        net = train(Xa, ya); net.eval()
        with torch.no_grad():
            pred = net(torch.tensor(X[te])).argmax(1).numpy()
        for j, i in enumerate(te):
            pp = pred[j] % 12; pm = pred[j] // 12
            s = mirex(pp, pm, pcs[i], modes[i]); scores.append(s)
            exact = (pp == pcs[i] and pm == modes[i])
            if modes[i] == 0: maj_n += 1; maj_ok += exact
            else: min_n += 1; min_ok += exact
    return np.mean(scores), (maj_ok + min_ok) / n, maj_ok / maj_n, maj_ok, maj_n, min_ok / min_n

if __name__ == "__main__":
    csv = sys.argv[1] if len(sys.argv) > 1 else "chroma36.csv"
    out = sys.argv[2] if len(sys.argv) > 2 else "model.onnx"
    pcs, modes, X = load(csv)
    print(f"Loaded {len(pcs)} tracks", flush=True)

    m, ex, maj_r, maj_ok, maj_n, min_r = cv(pcs, modes, X)
    print(f"torch CV (MAJOR_W={MAJOR_W}): MIREX {m:.3f}  exact {ex*100:.1f}%  "
          f"MAJ {maj_r*100:.0f}% ({maj_ok}/{maj_n})  min {min_r*100:.0f}%  "
          f"(baseline unweighted: MIREX ~0.751, MAJ ~17%)", flush=True)

    # Final model on ALL data, export ONNX
    Xa, ya = augment(np.arange(len(pcs)), pcs, modes, X)
    net = train(Xa, ya); net.eval()
    dummy = torch.zeros(1, 36)
    torch.onnx.export(net, dummy, out, input_names=["chroma36"], output_names=["logits"],
                      dynamic_axes={"chroma36": {0: "batch"}, "logits": {0: "batch"}}, opset_version=13)
    print(f"Exported final model to {out}", flush=True)
