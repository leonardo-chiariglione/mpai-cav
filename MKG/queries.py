"""Example SPARQL queries on the MKG built by build_mkg.py."""
import sys, os
from rdflib import Graph

here = os.path.dirname(os.path.abspath(__file__))
g = Graph()
g.parse(os.path.join(here, sys.argv[1] if len(sys.argv) > 1 else 'mkg-paf-mmc-osd.ttl'), format='turtle')
P = 'PREFIX mkg: <https://mpai.community/kg/vocab#>\nPREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>\n'

def q(title, text, limit=25):
    rows = list(g.query(P + text))
    print('\n== %s  (%d)' % (title, len(rows)))
    for r in rows[:limit]:
        print('  ', ' | '.join(str(x).rsplit('/', 1)[-1] if x is not None else '' for x in r))
    if len(rows) > limit:
        print('   ...')

q('AIMs, data types and actions per standard (standards without any are not yet in the repository)',
  'SELECT ?s (COUNT(DISTINCT ?a) AS ?aims) (COUNT(DISTINCT ?d) AS ?types) (COUNT(DISTINCT ?x) AS ?actions) WHERE { ?s a mkg:Standard . OPTIONAL { ?a a mkg:AIM ; mkg:ofStandard ?s } OPTIONAL { ?d a mkg:DataType ; mkg:ofStandard ?s } OPTIONAL { ?x a mkg:Action ; mkg:ofStandard ?s } } GROUP BY ?s ORDER BY ?s', 30)

q('Composite AIMs and how many Sub-AIMs',
  'SELECT ?a (COUNT(?s) AS ?n) WHERE { ?a mkg:subAIM ?s } GROUP BY ?a ORDER BY DESC(?n)')

q('Data types of another standard used by PAF AIMs (cross-standard edges)',
  'SELECT ?ds (COUNT(DISTINCT ?t) AS ?types) WHERE { ?a mkg:ofStandard <https://mpai.community/kg/standard/PAF> ; mkg:port ?p . ?p mkg:dataType ?t . ?t mkg:ofStandard ?ds . FILTER(?ds != <https://mpai.community/kg/standard/PAF>) } GROUP BY ?ds ORDER BY DESC(?types)')

q('Who produces and who consumes Basic Multimodal Scene (OSD-BMS-V1.5)',
  'SELECT ?a ?dir WHERE { ?a mkg:port ?p . ?p mkg:dataType <https://mpai.community/kg/datatype/OSD-BMS-V1.5> ; mkg:direction ?dir } ORDER BY ?dir ?a')

q('Composites not yet aligned to M3194 (topology still by port names)',
  'SELECT ?a WHERE { ?a mkg:notAligned true } ORDER BY ?a')

q('Data types used by ports or flows with no schema in these standards (declared only)',
  'SELECT DISTINCT ?t WHERE { { ?p mkg:dataType ?t } UNION { ?f mkg:dataType ?t } ?t a mkg:DataType . FILTER NOT EXISTS { ?t mkg:schema ?x } } ORDER BY ?t', 40)

q('AIMs with an L3 in the repository',
  'SELECT ?a (COUNT(?l) AS ?l3s) WHERE { ?l a mkg:L3 ; mkg:instanceOf ?a } GROUP BY ?a ORDER BY DESC(?l3s)', 12)

q('Data types that depend on other data types (most used first)',
  'SELECT ?t (COUNT(?u) AS ?usedBy) WHERE { ?u mkg:uses ?t } GROUP BY ?t ORDER BY DESC(?usedBy)', 10)
