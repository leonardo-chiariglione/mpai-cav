"""Builds mkg-viewer.html: one self-contained page (data embedded) to explore the MKG.

Usage: python build_viewer.py [mkg.ttl]
"""
import json, os, sys
from rdflib import Graph, URIRef
from rdflib.namespace import RDF

here = os.path.dirname(os.path.abspath(__file__))
ttl = sys.argv[1] if len(sys.argv) > 1 else 'mkg.ttl'
g = Graph()
g.parse(os.path.join(here, ttl), format='turtle')
V = 'https://mpai.community/kg/vocab#'
def p(n): return URIRef(V + n)

PREFIX = {'standard': 'STD:', 'part': 'PART:', 'aim': 'AIM:', 'datatype': 'DT:', 'action': 'ACT:', 'l3': 'L3:',
          'implementer': 'IMP:', 'mpaistore': 'STORE:', 'performanceassessor': 'ASSESS:', 'framework': 'AIF:',
          'useragent': 'UA:', 'unit': 'UNIT:', 'software': 'SW:'}
KINDS = ['Standard', 'Part', 'AIM', 'DataType', 'Action', 'L3', 'Implementer', 'MPAIStore', 'PerformanceAssessor', 'Framework', 'UserAgent', 'Unit', 'Software']

def nid(u):
    s = str(u)
    k = s.split('/kg/')[1].split('/')[0]
    return PREFIX[k] + s.rsplit('/', 1)[-1]
def lit(s, pred):
    v = g.value(s, p(pred))
    return str(v) if v is not None else ''

nodes = {}
for kind in KINDS:
    for s in g.subjects(RDF.type, p(kind)):
        i = nid(s)
        std = g.value(s, p('ofStandard'))
        nodes[i] = {'id': i, 'label': (lit(s, 'acronym') + ' (' + lit(s, 'partAcronym') + ')') if kind == 'Part' else (lit(s, 'acronym') or i.split(':', 1)[1]), 'kind': kind,
                    'std': nid(std).split(':', 1)[1] if std is not None else (lit(s, 'acronym') if kind == 'Standard' else ''),
                    'title': lit(s, 'title'), 'desc': lit(s, 'description'), 'api': lit(s, 'apiProfile'),
                    'schema': lit(s, 'schema'), 'web': lit(s, 'webSpec'), 'page': lit(s, 'webPage'), 'code': lit(s, 'code'), 'codePath': lit(s, 'codePath'),
                    'ports': [], 'subs': [], 'flows': [], 'uses': [], 'of': '',
                    'declaredOnly': kind == 'DataType' and not lit(s, 'schema'),
                    'noAcronym': bool(lit(s, 'noAcronym')), 'notAligned': (s, p('notAligned'), None) in g,
                    'webOnly': bool(lit(s, 'webOnly')), 'sourceAI': bool(lit(s, 'sourceAI')), 'notInM3260': bool(lit(s, 'notInM3260')),
                    'webCode': lit(s, 'webCode'), 'codeDiffers': bool(lit(s, 'codeDiffersFromWeb')),
                    'repository': lit(s, 'repository'), 'path': lit(s, 'path'), 'softwareKind': lit(s, 'softwareKind'), 'unitStatus': lit(s, 'unitStatus'),
                    'version': lit(s, 'version'), 'partAcronym': lit(s, 'partAcronym'), 'inM3260': bool(lit(s, 'inM3260'))}

for s in list(g.subjects(RDF.type, p('AIM'))) + list(g.subjects(RDF.type, p('Unit'))):
    a = nodes[nid(s)]
    for port in g.objects(s, p('port')):
        a['ports'].append({'dt': nid(g.value(port, p('dataType'))), 'dir': lit(port, 'direction'), 'n': int(lit(port, 'portNumber') or 0),
                           'opt': lit(port, 'optional') == 'true', 'label': lit(port, 'label')})
    a['ports'].sort(key=lambda x: (x['dir'] != 'Input', x['dt'], x['n']))
    a['subs'] = sorted(nid(x) for x in g.objects(s, p('subAIM')))
    for fl in g.objects(s, p('flow')):
        a['flows'].append({'dt': nid(g.value(fl, p('dataType'))),
                           'from': nid(g.value(fl, p('fromAIM'))) if g.value(fl, p('fromAIM')) is not None else '',
                           'fn': int(lit(fl, 'fromPortNumber') or 0),
                           'to': nid(g.value(fl, p('toAIM'))) if g.value(fl, p('toAIM')) is not None else '',
                           'tn': int(lit(fl, 'toPortNumber') or 0)})
for kind in ('DataType', 'Action'):
    for s in g.subjects(RDF.type, p(kind)):
        nodes[nid(s)]['uses'] = sorted(nid(x) for x in g.objects(s, p('uses')))
for s in g.subjects(RDF.type, p('L3')):
    nodes[nid(s)]['of'] = nid(g.value(s, p('instanceOf')))

# the simple relations between nodes (the Standard, Part, L3, participants), with a label
edges = []
for rel in ('partOf', 'specifies', 'produces', 'receives', 'holdsIDFrom', 'communicatesWith', 'usesUnit', 'specifiedBy', 'dependsOn', 'implements'):
    for a, b in g.subject_objects(p(rel)):
        if isinstance(b, URIRef) and '/kg/' in str(b) and '/kg/' in str(a):
            edges.append([nid(a), rel, nid(b)])
# a Standard specifies what its Parts specify: keep the Part as the owner (no duplicate)
data = {'nodes': list(nodes.values()), 'edges': edges}
html = open(os.path.join(here, 'viewer-template.html'), encoding='utf-8').read().replace('/*DATA*/null', json.dumps(data, ensure_ascii=False))
out = os.path.join(here, 'mkg-viewer.html')
open(out, 'w', encoding='utf-8').write(html)
print('written', out, '- %d nodes, %d relations' % (len(nodes), len(edges)))
