"""NNW-NNT V2.0: the corrections to the pages made from those of V1.1 (make_web_version.py NNW nnt 1.1 2.0 --aiw), and the
page of the Data Types, which V1.1 did not have.

  - the links to the JSON of the AIMs and of the Modules are those of V2.0 (schemas.mpai.community/NNW1/V2.0/AIMs/...);
  - "NNW-NNT V1.1" is V2.0;
  - the page of the AI Modules said it was about MPAI-OSD (a copy of that page): it is about NNW-NNT V2.0;
  - the four AI Workflows are Modules: rows of the table of the AI Modules, in bold (Composite);
  - a page of the Data Types: Payload, Parameters, Dataset, Inference, Count Error.
"""
import os, re

ROOT = os.path.dirname(os.path.abspath(__file__))
D = os.path.join(ROOT, 'web', 'mpai-nnw', 'nnt', 'v2-0')
BASE = 'https://mpai.community/standards/mpai-nnw/nnt/v2-0/'
SCH = 'https://schemas.mpai.community/NNW1/V2.0/'
log = []

# everywhere
for dp, dn, fn in os.walk(D):
    for f in fn:
        if f != 'index.html':
            continue
        p = os.path.join(dp, f)
        s = open(p, encoding='utf-8').read()
        o = s
        s = re.sub(r'https?://schemas\.mpai\.community/web/NNW1/(?:AIMs|AIWs)/', SCH + 'AIMs/', s)
        s = re.sub(r'(NNW-NNT\)? ?)V1\.1', r'\1V2.0', s)
        if s != o:
            open(p, 'w', encoding='utf-8').write(s)
            log.append('%s: JSON links of V2.0, NNW-NNT V2.0' % os.path.relpath(p, D).replace('\\', '/'))

# the page of the AI Modules
p = os.path.join(D, 'ai-modules', 'index.html')
s = open(p, encoding='utf-8').read()
for old, new in (
    ('Technical Specification: Object and Scene Description (MPAI-OSD) V1.4', 'Technical Specification: Neural Network Watermarking &#8211; Traceability (NNW-NNT) V2.0'),
    ('All previously specified MPAI-OSD AI-Modules are superseded by those specified by V1.4', 'All previously specified NNW-NNT AI Modules and AI Workflows are superseded by those specified by V2.0 (the AI Workflows of V1.1 are the Modules of V2.0)'),
    ('AIMs used by MPAI-OSD V1.3', 'AIMs and Modules of NNW-NNT V2.0'),
    ('MPAI-OSD V1.4', 'NNW-NNT V2.0'),
    ('MPAI-OSD Standard', 'NNW-NNT Standard'),
):
    n = s.count(old)
    s = s.replace(old, new)
    log.append('ai-modules: %d x %s' % (n, old[:60]))
t0 = s.index('<table style="width: 99.9157%;">')
t1 = s.index('</table>', t0)
rows = list(re.finditer(r'<tr>.*?</tr>', s[t0:t1], flags=re.S))
last = rows[-1].group(0)
cells = re.findall(r'(<td[^>]*>)(.*?)(</td>)', last, flags=re.S)
assert len(cells) == 6
def link(acr_slug, name):
    return '<a href="%sai-modules/%s/"><strong>%s</strong></a>' % (BASE, acr_slug, name)
def json_(fname):
    return '<a href="%sAIMs/%s.json">X</a>' % (SCH, fname)
mods = [('NNW-NIR', 'no-inference-robustness', 'No Inference Robustness', 'NoInferenceRobustness'),
        ('NNW-NTI', 'no-training-imperceptibility', 'No Training Imperceptibility', 'NoTrainingImperceptibility'),
        ('NNW-WIR', 'with-inference-robustness', 'With Inference Robustness', 'WithInferenceRobustness'),
        ('NNW-WTI', 'with-training-imperceptibility', 'With Training Imperceptibility', 'WithTrainingImperceptibility')]
new_rows = ''
for k in range(0, 4, 2):
    texts = []
    for acr, slug, name, fname in mods[k:k + 2]:
        texts += ['<strong>%s</strong>' % acr, link(slug, name), json_(fname)]
    row = last
    for (open_, inner, close), text in zip(cells, texts):
        row = row.replace(open_ + inner + close, open_ + text + close, 1)
    new_rows += '\n' + row
at = t0 + rows[-1].end()
s = s[:at] + new_rows + s[at:]
log.append('ai-modules: 4 Modules (NNW-NIR, NNW-NTI, NNW-WIR, NNW-WTI) as rows of Table 1, in bold')
open(p, 'w', encoding='utf-8').write(s)

# the page of the Data Types
types = [('NNW-PLD', 'Payload', 'Payload', 'The payload a watermark carries: a bitstring.'),
         ('NNW-PRM', 'Parameters', 'Parameters', 'The parameters of a neural network (a Module).'),
         ('NNW-DST', 'Dataset', 'Dataset', 'A dataset for the training or the testing of a Module.'),
         ('NNW-INF', 'Inference', 'Inference', 'What a Module produces from the testing dataset.'),
         ('NNW-CER', 'Count Error', 'CountError', 'The count of the errors between the original and the retrieved payload.')]
rows_html = ''.join('<tr><td>%s</td><td>%s</td><td>%s</td><td style="text-align: center;"><a href="%sdata/%s.json">X</a></td></tr>\n' % (a, n, d, SCH, f) for a, n, f, d in types)
dt = ('<!-- data-types : new in V2.0 (V1.1 had no page of Data Types; its ports were of raw types: uint8[], bitstring) -->\n<h1>NNW-NNT V2.0 Data Types</h1>\n'
      '<article class="post">\n<div class="post-content">\n'
      '<p style="text-align: center;"><a href="%sai-modules/">&lt;- AI Modules</a> <a href="%s">Go to ToC</a></p>\n'
      '<h3>1. Technical Specification</h3>\n'
      '<p>Table 1 provides the Data Types of the AI Modules of Technical Specification: Neural Network Watermarking &#8211; Traceability (NNW-NNT) V2.0 with the link to their JSON syntax. The role of a Data Type in a Module (the original and the retrieved payload; the untrained, unwatermarked, watermarked and modified parameters; the training and testing dataset) is the Port of the Module that receives or produces it.</p>\n'
      '<p style="text-align: center;">Table 1 &#8211; Data Types of NNW-NNT V2.0</p>\n'
      '<table style="width: 99.9157%%;">\n<tbody>\n<tr><td><strong>Acronym</strong></td><td><strong>Name</strong></td><td><strong>Description</strong></td><td style="text-align: center;"><strong>JSON</strong></td></tr>\n%s</tbody>\n</table>\n'
      '<h3>2. Conformance Testing</h3>\n<p>A Data instance of a Data Type conforms with NNW-NNT V2.0 if the JSON Data validate against the relevant NNW-NNT V2.0 JSON Schema.</p>\n'
      '</div>\n</article>\n') % (BASE, BASE, rows_html)
os.makedirs(os.path.join(D, 'data-types'), exist_ok=True)
open(os.path.join(D, 'data-types', 'index.html'), 'w', encoding='utf-8').write(dt)
log.append('data-types: a new page, the five Data Types')
with open(os.path.join(D, 'CHANGES.txt'), 'a', encoding='utf-8') as f:
    f.write('\nCorrections and additions (fix_nnt_pages.py):\n' + '\n'.join(log) + '\n')
print('\n'.join(log))
