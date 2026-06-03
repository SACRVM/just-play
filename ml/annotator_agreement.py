"""
Honest label-quality probe: how much do two independent expert annotations of the
SAME audio disagree? Compares the original GiantSteps-604 key labels against the
GiantSteps+ (Camhi & Faraldo) re-annotation of the same Beatport tracks.

The number this prints is an ANNOTATOR CEILING: if two serious annotations of the
same track disagree at rate R, no model trained on one can score above ~(1-R-ish)
against the other. It tells us how much of our 0.73-0.75 is a label ceiling.

Usage: python annotator_agreement.py <604_key_dir> <giantsteps_plus_json_dir>
"""
import sys, os, json, glob

PC = {'C':0,'C#':1,'DB':1,'D':2,'D#':3,'EB':3,'E':4,'FB':4,'E#':5,'F':5,'F#':6,
      'GB':6,'G':7,'G#':8,'AB':8,'A':9,'A#':10,'BB':10,'B':11,'CB':11,'B#':0}

def parse_key(s):
    """'C minor' / 'F# major' -> (pc, mode) ; mode 0=major 1=minor. None on junk."""
    s = s.strip()
    if not s or s.lower() in ('none', 'silence', '-'): return None
    parts = s.replace('\t', ' ').split()
    if len(parts) < 2: return None
    note, mode = parts[0].upper(), parts[1].lower()
    if note not in PC: return None
    if mode.startswith('maj'): m = 0
    elif mode.startswith('min'): m = 1
    else: return None
    return (PC[note], m)

def mirex(pp, pm, tp, tm):
    if pp == tp and pm == tm: return 1.0
    d = (pp - tp) % 12; same = pm == tm
    if same and d in (5, 7): return 0.5            # perfect fifth
    if not same:
        if pm == 0 and tm == 1 and d == 3: return 0.3   # relative (pred major / true minor)
        if pm == 1 and tm == 0 and d == 9: return 0.3   # relative (pred minor / true major)
        if d == 0: return 0.2                            # parallel
    return 0.0

def reltype(pp, pm, tp, tm):
    if pp == tp and pm == tm: return 'exact'
    d = (pp - tp) % 12; same = pm == tm
    if same and d in (5, 7): return 'fifth'
    if not same and ((pm==0 and tm==1 and d==3) or (pm==1 and tm==0 and d==9)): return 'relative'
    if not same and d == 0: return 'parallel'
    return 'other'

def load_604(d):
    out = {}
    for f in glob.glob(os.path.join(d, '*.key')):
        tid = os.path.basename(f).split('.')[0]
        txt = open(f, encoding='utf-8', errors='ignore').read()
        # format is either bare 'C minor' or 'key 0 C minor'
        line = [l for l in txt.splitlines() if l.strip() and not l.startswith('#')]
        if not line: continue
        s = line[0]
        if s.lower().startswith('key'):
            s = ' '.join(s.split()[2:])
        k = parse_key(s)
        if k: out[tid] = k
    return out

def load_plus(d):
    out = {}
    for f in glob.glob(os.path.join(d, '*.json')):
        tid = os.path.basename(f).split('.')[0]
        j = json.load(open(f, encoding='utf-8'))
        keys = j.get('key') or []
        if isinstance(keys, str): keys = [keys]
        parsed = [parse_key(k) for k in keys]
        parsed = [k for k in parsed if k]
        if parsed:
            out[tid] = {'primary': parsed[0], 'all': parsed,
                        'multi': len(set(parsed)) > 1, 'conf': j.get('confidence')}
    return out

if __name__ == '__main__':
    a = load_604(sys.argv[1])
    b = load_plus(sys.argv[2])
    ids = sorted(set(a) & set(b))
    print(f"604 parsed={len(a)}  GS+ parsed={len(b)}  matched={len(ids)}")

    multi = sum(1 for t in ids if b[t]['multi'])
    print(f"GS+ tracks with >1 key (ambiguity/modulation): {multi} ({multi/len(ids)*100:.0f}%)")

    exact = lenient = 0
    scores = []
    buckets = {'exact':0,'fifth':0,'relative':0,'parallel':0,'other':0}
    examples = {'fifth':[],'relative':[],'parallel':[],'other':[]}
    for t in ids:
        tp, tm = a[t]              # 604 = reference
        pp, pm = b[t]['primary']   # GS+ primary = comparison
        scores.append(mirex(pp, pm, tp, tm))
        rt = reltype(pp, pm, tp, tm); buckets[rt] += 1
        if (pp, pm) == (tp, tm): exact += 1
        if (tp, tm) in b[t]['all']: lenient += 1
        if rt != 'exact' and len(examples[rt]) < 4:
            inv = {v:k for k,v in PC.items() if v in (0,2,4,5,7,9,11) or True}
            nm = {0:'C',1:'Db',2:'D',3:'Eb',4:'E',5:'F',6:'Gb',7:'G',8:'Ab',9:'A',10:'Bb',11:'B'}
            examples[rt].append(f"{t}: 604={nm[tp]} {'maj' if tm==0 else 'min'} | GS+={nm[pp]} {'maj' if pm==0 else 'min'}")

    n = len(ids)
    print(f"\n--- agreement (604 vs GS+ primary key) ---")
    print(f"  EXACT same key      : {exact:4d}/{n}  = {exact/n*100:5.1f}%")
    print(f"  lenient (604 key in GS+ key list): {lenient}/{n} = {lenient/n*100:5.1f}%")
    print(f"  MIREX score (treat GS+ as 'prediction'): {sum(scores)/n:.3f}")
    print(f"\n--- disagreement breakdown ---")
    for k in ('exact','fifth','relative','parallel','other'):
        print(f"  {k:9s}: {buckets[k]:4d}  ({buckets[k]/n*100:4.1f}%)")
    print(f"\n--- example disagreements ---")
    for k in ('relative','fifth','parallel','other'):
        for e in examples[k]:
            print(f"  [{k}] {e}")
