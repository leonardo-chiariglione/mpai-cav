"""MPAI Knowledge Graph (MKG), first instance: built from the schemas in D:\\DI.

Reads the L2s (AIMs), the data types and the L3s of the given standards and writes
an RDF graph (Turtle and JSON-LD).  Kinds, relations and the identity rule are in
the vocabulary below: a node is identified by its kind and its acronym (and version),
so the same acronym can be a Standard, an AIM and a Data Type.

Usage:  python build_mkg.py [PAF MMC OSD ...]
"""
import json, re, sys, glob, os
from rdflib import Graph, Namespace, URIRef, Literal, BNode
from rdflib.namespace import RDF, RDFS, XSD

ROOT = os.path.dirname(os.path.abspath(__file__))
DI = os.path.dirname(ROOT)
SCH = os.path.join(DI, 'schemas')
SCHEMA_BASE = 'https://schemas.mpai.community/'

MKG = Namespace('https://mpai.community/kg/vocab#')
STD = Namespace('https://mpai.community/kg/standard/')
PRT = Namespace('https://mpai.community/kg/part/')
AIM = Namespace('https://mpai.community/kg/aim/')
DT = Namespace('https://mpai.community/kg/datatype/')
L3 = Namespace('https://mpai.community/kg/l3/')


def load(path):
    s = open(path, encoding='utf-8-sig').read()
    s = re.sub(r'"(?:\\.|[^"\\])*"|/\*.*?\*/|//[^\n]*',
               lambda m: m.group(0) if m.group(0).startswith('"') else '', s, flags=re.S)
    return json.loads(s)


def refs(node, out):
    if isinstance(node, dict):
        for k, v in node.items():
            if k == '$ref' and isinstance(v, str):
                out.add(v)
            else:
                refs(v, out)
    elif isinstance(node, list):
        for x in node:
            refs(x, out)
    return out


def folder_parts(folder):
    """CAE3 -> (CAE, 3); PAF -> (PAF, None): a part number is appended to the acronym."""
    m = re.fullmatch(r'([A-Z]+?)(\d+)?', folder)
    return m.group(1), m.group(2)


def as_list(x):
    return x if isinstance(x, list) else [x]


def build(folders):
    g = Graph()
    for p, n in (('mkg', MKG), ('std', STD), ('part', PRT), ('aim', AIM), ('dt', DT), ('l3', L3)):
        g.bind(p, n)

    # ---- pass 1: the identifiers of data types, by their schema URL
    dt_by_id = {}
    for folder in folders:
        for f in glob.glob(os.path.join(SCH, folder, '*', 'data', '*.json')):
            d = load(f)
            hdr = (d.get('properties', {}).get('Header', {}) or {}).get('const')
            if hdr:
                dt_by_id[d.get('$id')] = hdr

    declared_dt = set()

    def datatype(acr):
        node = DT[acr]
        if (node, RDF.type, MKG.DataType) not in g:
            g.add((node, RDF.type, MKG.DataType))
            g.add((node, MKG.acronym, Literal(acr)))
            m = re.match(r'([A-Z0-9]+)-[A-Z0-9]+-V(\d+\.\d+)', acr)
            if m:
                g.add((node, MKG.ofStandard, STD[m.group(1)]))
                g.add((STD[m.group(1)], RDF.type, MKG.Standard))
        return node

    def aim(acr):
        node = AIM[acr]
        if (node, RDF.type, MKG.AIM) not in g:
            g.add((node, RDF.type, MKG.AIM))
            g.add((node, MKG.acronym, Literal(acr)))
            m = re.match(r'([A-Z0-9]+)-[A-Z0-9]+-V(\d+\.\d+)', acr)
            if m:
                g.add((node, MKG.ofStandard, STD[m.group(1)]))
        return node

    stats = {'aims': 0, 'datatypes': 0, 'ports': 0, 'subaims': 0, 'flows': 0, 'flows_by_name': 0, 'l3': 0}

    for folder in folders:
        acr, part = folder_parts(folder)
        std = STD[acr]
        g.add((std, RDF.type, MKG.Standard))
        g.add((std, MKG.acronym, Literal(acr)))
        for vdir in sorted(glob.glob(os.path.join(SCH, folder, 'V*'))):
            version = os.path.basename(vdir)

            # ---- data types
            for f in sorted(glob.glob(os.path.join(vdir, 'data', '*.json'))):
                d = load(f)
                hdr = (d.get('properties', {}).get('Header', {}) or {}).get('const')
                if not hdr:
                    continue
                n = datatype(hdr)
                g.add((std, MKG.specifies, n))
                g.add((n, MKG.title, Literal(d.get('title', ''))))
                g.add((n, MKG.schema, URIRef(d.get('$id') or SCHEMA_BASE + os.path.relpath(f, SCH).replace(os.sep, '/'))))
                stats['datatypes'] += 1
                for r in refs(d, set()):
                    target = dt_by_id.get(r.split('#')[0])
                    if target and target != hdr:
                        g.add((n, MKG.uses, datatype(target)))

            # ---- AIMs (L2)
            for f in sorted(glob.glob(os.path.join(vdir, 'AIMs', '*.json'))):
                if os.sep + 'AMDs' + os.sep in f:
                    continue
                d = load(f)
                name = d.get('Identifier', {}).get('AIMName') or d.get('Header')
                if not name:
                    continue
                a = aim(name)
                stats['aims'] += 1
                g.add((std, MKG.specifies, a))
                if part:
                    g.add((PRT[acr + part], RDF.type, MKG.Part))
                    g.add((PRT[acr + part], MKG.partOf, std))
                    g.add((PRT[acr + part], MKG.specifies, a))
                g.add((a, MKG.title, Literal(d.get('title', ''))))
                g.add((a, MKG.description, Literal(d.get('Description', ''))))
                g.add((a, MKG.schema, URIRef(d.get('$id') or SCHEMA_BASE + os.path.relpath(f, SCH).replace(os.sep, '/'))))
                if not d.get('$id'):
                    stats['no_id'] = stats.get('no_id', 0) + 1
                    print('no $id:', os.path.relpath(f, SCH))
                g.add((a, MKG.apiProfile, Literal(d.get('APIProfile', ''))))
                for doc in d.get('Documentation', []) or []:
                    if doc.get('URI'):
                        g.add((a, MKG.webSpec, URIRef(doc['URI'])))

                # ports: an edge between AIM and Data Type, with its direction and numbers
                for p in d.get('ExternalPorts', []) or []:
                    for t in as_list(p.get('DataType', [])):
                        if not t:
                            continue
                        port = URIRef('%s/port/%s/%s/%s' % (AIM[name], p['Direction'], t, p.get('PortNumber', 0)))
                        g.add((a, MKG.port, port))
                        g.add((port, RDF.type, MKG.Port))
                        g.add((port, MKG.dataType, datatype(t)))
                        g.add((port, MKG.direction, Literal(p['Direction'])))
                        g.add((port, MKG.label, Literal(p.get('Name', ''))))
                        if p.get('PortNumber'):
                            g.add((port, MKG.portNumber, Literal(p['PortNumber'], datatype=XSD.integer)))
                        g.add((port, MKG.optional, Literal(bool(p.get('IsOptional')))))
                        stats['ports'] += 1

                # composites: Sub-AIMs and the flows of the Topology
                for s in d.get('SubAIMs', []) or []:
                    sn = s.get('Identifier', {}).get('AIMName')
                    if sn:
                        g.add((a, MKG.subAIM, aim(sn)))
                        stats['subaims'] += 1
                for i, t in enumerate(d.get('Topology', []) or []):
                    o, ie = t.get('Output', {}), t.get('Input', {})
                    if 'DataType' not in o or 'DataType' not in ie:
                        stats['flows_by_name'] += 1       # still addressed by port names: not yet aligned to M3194
                        g.add((a, MKG.notAligned, Literal(True)))
                        continue
                    fl = URIRef('%s/flow/%d' % (AIM[name], i + 1))
                    g.add((a, MKG.flow, fl))
                    g.add((fl, RDF.type, MKG.Flow))
                    g.add((fl, MKG.dataType, datatype(o['DataType'])))
                    for tag, end in (('from', o), ('to', ie)):
                        if end.get('AIMName'):
                            g.add((fl, MKG[tag + 'AIM'], aim(end['AIMName'])))
                        else:
                            g.add((fl, MKG[tag + 'AIM'], a))     # the boundary of the composite
                        g.add((fl, MKG[tag + 'Type'], datatype(end['DataType'])))
                        if end.get('PortNumber'):
                            g.add((fl, MKG[tag + 'PortNumber'], Literal(end['PortNumber'], datatype=XSD.integer)))
                    stats['flows'] += 1

    # ---- L3s: an instance of the L2 whose Header they carry
    for f in sorted(glob.glob(os.path.join(DI, 'AIMs', 'AMDs', '1*.json'))):
        try:
            d = load(f)
        except Exception:
            continue
        l2 = d.get('Header')
        name = d.get('Identifier', {}).get('AIMName')
        if l2 and name and (AIM[l2], RDF.type, MKG.AIM) in g:
            n = L3[name]
            g.add((n, RDF.type, MKG.L3))
            g.add((n, MKG.instanceOf, AIM[l2]))
            g.add((n, MKG.implementerID, Literal(d.get('Identifier', {}).get('ImplementerID', ''))))
            stats['l3'] += 1
    return g, stats


if __name__ == '__main__':
    folders = sys.argv[1:] or ['PAF', 'MMC', 'OSD']
    g, stats = build(folders)
    out = os.path.join(ROOT, 'mkg-' + '-'.join(f.lower() for f in folders))
    g.serialize(out + '.ttl', format='turtle')
    g.serialize(out + '.jsonld', format='json-ld', indent=1)
    print('triples:', len(g))
    print(stats)
    print('written:', out + '.ttl', out + '.jsonld')
