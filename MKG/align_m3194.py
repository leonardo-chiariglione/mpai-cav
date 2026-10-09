"""Aligns the Topology of composite L2s to M3194: the ends of a flow are an AIM, a Data Type and a PortNumber, not a port name.

For every flow of a composite whose ends still carry port names, the Data Type and the PortNumber are taken from the
port of that name in the L2 of the Sub-AIM (D:\\DI first, then D:\\AI), or in the ExternalPorts of the composite for the
boundary.  A flow whose end cannot be resolved is left alone and reported.

    python align_m3194.py             dry run: the report
    python align_m3194.py --write     writes the L2s
    python align_m3194.py --write PAF-RSR-V1.6 ...   only these
"""
import glob, json, os, re, sys

DI = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOTS = [os.path.join(DI, 'schemas'), r'D:\AI\schemas']


def strip_comments(s):
    return re.sub(r'"(?:\\.|[^"\\])*"|/\*.*?\*/|//[^\n]*', lambda m: m.group(0) if m.group(0).startswith('"') else '', s, flags=re.S)


def load(path):
    return json.loads(strip_comments(open(path, encoding='utf-8-sig').read()))


def vkey(v):
    return tuple(int(x) for x in re.findall(r'\d+', v))


# ---- every L2 by AIM name, D:\DI first
L2 = {}
for root in ROOTS:
    for folder in sorted(os.listdir(root)):
        fdir = os.path.join(root, folder)
        vs = sorted((v for v in os.listdir(fdir) if re.fullmatch(r'V\d+\.\d+', v)), key=vkey) if os.path.isdir(fdir) else []
        if not vs:
            continue
        for f in glob.glob(os.path.join(fdir, vs[-1], 'AIMs', '*.json')):
            try:
                d = load(f)
            except Exception:
                continue
            name = (d.get('Identifier') or {}).get('AIMName')
            if name and name not in L2:
                L2[name] = (f, d)


def types_of(p):
    t = p.get('DataType')
    return [x for x in (t if isinstance(t, list) else [t]) if x]


def norm(n):
    n = (n or '').lower()
    for suf in ('out', 'in'):
        if n.endswith(suf) and len(n) > len(suf) + 2:
            n = n[:-len(suf)]
    return n


def score(port, name):
    n, q = norm(name), norm(port.get('Name'))
    if n == q:
        return 3
    if n.startswith(q) or q.startswith(n):
        return 2
    return 0


def candidates(aim, composite_ports, role):
    """role 'out': the ports that emit (Output of a Sub-AIM, Input of the composite); role 'in': the ports that take."""
    if aim == '':
        ports, want = composite_ports, ('Input' if role == 'out' else 'Output')
    else:
        sub = L2.get(aim)
        ports, want = ((sub[1].get('ExternalPorts') or []) if sub else []), ('Output' if role == 'out' else 'Input')
    return [p for p in ports if p.get('Direction') == want]


def find_label_port(ports, name, direction):
    hit = [p for p in ports if p.get('Direction') == direction and p.get('Name') == name]
    return hit[0] if len(hit) == 1 else None


def align(name, f, d):
    """Returns (new topology or None, problems, assumptions).  The Data Type of a flow is the one the two ports share;
    the names only choose between ports when there are several; where several ports are equal in everything but their
    PortNumber, the first not yet used is taken (reported as an assumption)."""
    problems, assumed = [], []
    topo = d.get('Topology') or []
    if not any('PortName' in (t.get('Output') or {}) or 'PortName' in (t.get('Input') or {}) for t in topo):
        return None, problems, assumed
    own = d.get('ExternalPorts') or []
    used = set()
    out = []
    for i, t in enumerate(topo, 1):
        o, ie = t.get('Output') or {}, t.get('Input') or {}
        if 'PortName' not in o and 'PortName' not in ie and 'DataType' in o and 'DataType' in ie:
            out.append(t)
            continue
        A, B = o.get('AIMName') or '', ie.get('AIMName') or ''
        label = next((x for x in (d.get('InternalTypes') or []) if A and x.get('Name') == o.get('PortName') and x.get('Output') is not None), None)
        if label is not None:
            # L1: the Output group of a supply from a Sub-AIM into a Sub-AIM that groups its Input Ports is declared on the
            # InternalType; the flow keeps the label that finds it
            out.append(t)
            assumed.append('flow %d: kept with its InternalType label %s (Output %s): it feeds an Input group of %s' % (i, label['Name'], label['Output'], B))
            continue
        ca, cb = candidates(A, own, 'out'), candidates(B, own, 'in')
        if (A and A not in L2) or (B and B not in L2):
            problems.append('flow %d: the L2 of %s is not in the repository' % (i, A if A not in L2 and A else B))
            out.append(t); continue
        pairs = []
        for pa in ca:
            for pb in cb:
                for ty in types_of(pa):
                    if ty in types_of(pb):
                        pairs.append((score(pa, o.get('PortName')) + score(pb, ie.get('PortName')), pa, pb, ty))
        if not pairs:
            problems.append('flow %d: %s.%s -> %s.%s: no Data Type in common' % (i, A or '(c)', o.get('PortName'), B or '(c)', ie.get('PortName')))
            out.append(t); continue
        best = max(x[0] for x in pairs)
        top = [x for x in pairs if x[0] == best]
        key = lambda x: (x[3], x[1].get('PortNumber') or 0, x[2].get('PortNumber') or 0)
        if len({x[3] for x in top}) > 1:
            # M3194 and the rule of the author: Data Types are the Basic ones (BTO, not TXO; BSO, not SPO)
            basic = [x for x in top if re.match(r'OSD-B[A-Z0-9]{2}-', x[3])]
            if basic and len({x[3] for x in basic}) == 1:
                top = basic
        if len({x[3] for x in top}) > 1:
            problems.append('flow %d: %s.%s -> %s.%s: several Data Types fit %s' % (i, A or '(c)', o.get('PortName'), B or '(c)', ie.get('PortName'), sorted({x[3] for x in top})))
            out.append(t); continue
        if len(top) > 1:
            free = [x for x in top if (A, 'o', x[3], x[1].get('PortNumber') or 0) not in used and (B, 'i', x[3], x[2].get('PortNumber') or 0) not in used]
            choice = (free or top)[0]
            assumed.append('flow %d: %s.%s -> %s.%s: %s #%s/#%s taken in order among %d' % (i, A or '(c)', o.get('PortName'), B or '(c)', ie.get('PortName'), choice[3], choice[1].get('PortNumber', 0), choice[2].get('PortNumber', 0), len(top)))
        else:
            choice = top[0]
        _, pa, pb, ty = choice
        if A and pb.get('Input') is not None:
            # a supply from a Sub-AIM into an Input group of a Sub-AIM (L1: its Output group is declared by the label): kept
            out.append(t)
            assumed.append('flow %d: kept as it was (labels): it feeds the Input group %s of %s' % (i, pb.get('Input'), B))
            continue
        used.add((A, 'o', ty, pa.get('PortNumber') or 0)); used.add((B, 'i', ty, pb.get('PortNumber') or 0))
        def end(aim, port):
            r = {'AIMName': aim, 'DataType': ty}
            if port.get('PortNumber'):
                r['PortNumber'] = port['PortNumber']
            return r
        endA = end(A, pa)
        out.append({'Output': endA, 'Input': end(B, pb)})
        # PAF-RSR takes its text twice (TextTTS #1 for the speech, TextSAF #2 for the avatar): the same text goes to both
        if B == 'PAF-RSR-V1.6' and ty == 'OSD-BTO-V1.5' and (pb.get('PortNumber') or 0) == 1:
            second = next((p for p in cb if ty in types_of(p) and p.get('PortNumber') == 2), None)
            if second is not None:
                out.append({'Output': endA, 'Input': end(B, second)})
                assumed.append('flow %d: the text also goes to PAF-RSR-V1.6 OSD-BTO-V1.5 #2 (TextSAF)' % i)
    return out, problems, assumed


def text_of(topology):
    pad = ' ' * 26
    lines = []
    for t in topology:
        lines.append(json.dumps(t, ensure_ascii=False))
    return '"Topology":             [' + (',\n' + pad).join(lines) + '],'


def main():
    write = '--write' in sys.argv
    only = [a for a in sys.argv[1:] if not a.startswith('--')]
    total = ok = 0
    for name in sorted(L2):
        f, d = L2[name]
        if not f.startswith(ROOTS[0]):
            continue                                 # only what is in D:\DI is changed
        if only and name not in only:
            continue
        new, problems, assumed = align(name, f, d)
        if new is None:
            continue
        total += 1
        print('%-14s %3d flows  %s%s' % (name, len(d['Topology']), 'OK' if not problems else '%d problem(s)' % len(problems), ('  + %d assumed' % len(assumed)) if assumed else ''))
        for p in problems:
            print('      PROBLEM', p)
        for p in assumed:
            print('      assumed', p)
        if write and not problems:
            s = open(f, encoding='utf-8-sig', newline='').read()
            m = re.search(r'"Topology":\s*\[.*?\n?\s*\],?(?=\s*\n\s*"|\s*\n\s*\})', s, flags=re.S)
            # the Topology array ends at the first ']' that is followed by the next key; find it by bracket matching
            a = s.index('"Topology"')
            b = s.index('[', a)
            depth, k = 0, b
            while True:
                c = s[k]
                if c == '"':
                    k += 1
                    while s[k] != '"':
                        k += 2 if s[k] == '\\' else 1
                elif c == '[':
                    depth += 1
                elif c == ']':
                    depth -= 1
                    if depth == 0:
                        break
                k += 1
            tail = s[k + 1:]
            comma = ',' if tail.lstrip().startswith(',') else ''
            end = k + 1 + (len(tail) - len(tail.lstrip(' ')) if False else 0)
            if comma:
                end = s.index(',', k) + 1
            else:
                end = k + 1
            nl = '\r\n' if '\r\n' in s else '\n'
            s2 = s[:a] + text_of(new).replace('\n', nl).rstrip(',') + (',' if comma else '') + s[end:]
            json.loads(strip_comments(s2))
            bom = open(f, 'rb').read(3) == b'\xef\xbb\xbf'
            open(f, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(s2)
            ok += 1
    print('\n%d composites with names in the Topology%s' % (total, '; %d written' % ok if write else ''))


if __name__ == '__main__':
    main()
