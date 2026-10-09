"""MPAI-PRF V2.0: the corrections to the pages made from those of V1.0 (make_web_version.py PRF "" 1.0 2.0).

V1.0 stays as it was published. V2.0 corrects what the specification and its own JSON schema disagree on:
  - AVS (Audio-Visual Scene Descriptors) is used by the examples and by the tables of the AIMs but was not an Attribute: it
    is a row of Table 1 and in the schema;
  - the Text sub-attribute of the Personal Status is PST (the text; the schema had SPT);
  - the Gesture sub-attribute (PSG) is Body (PSB): gesture is a subset of the body;
  - ISD is not an Attribute (Table 1 has Speech Descriptors, SPD): out of the schema; the example uses SPD.
Edits the fragments in web/mpai-prf/v2-0/ and appends to its CHANGES.txt.
"""
import os, re

ROOT = os.path.dirname(os.path.abspath(__file__))
D = os.path.join(ROOT, 'web', 'mpai-prf', 'v2-0')
log = []

def edit(page, edits):
    p = os.path.join(D, page, 'index.html')
    s = open(p, encoding='utf-8').read()
    done = []
    for label, old, new, count in edits:
        n = s.count(old)
        if n != count:
            done.append('NOT DONE (%d found, %d expected): %s' % (n, count, label)); continue
        s = s.replace(old, new); done.append('%d x %s' % (n, label))
    open(p, 'w', encoding='utf-8').write(s)
    log.append(page + ': ' + '; '.join(done))
    return s

# Table 1: a new row for AVS, made from the last row
p = os.path.join(D, 'profile-signalling', 'index.html')
s = open(p, encoding='utf-8').read()
t0 = s.index('<table')
t1 = s.index('</table>', t0)
rows = list(re.finditer(r'<tr>.*?</tr>', s[t0:t1], flags=re.S))
last = rows[-1]
cells = re.findall(r'(<td[^>]*>)(.*?)(</td>)', last.group(0), flags=re.S)
assert len(cells) == 6, len(cells)
texts = ['Audio-Visual Scene Descriptors', 'AVS', '', '', '', '']
new_row = last.group(0)
for (open_, inner, close), text in zip(cells, texts):
    new_row = new_row.replace(open_ + inner + close, open_ + text + close, 1)
at = t0 + last.end()
s = s[:at] + '\n' + new_row + s[at:]
open(p, 'w', encoding='utf-8').write(s)
log.append('profile-signalling: a row for Audio-Visual Scene Descriptors (AVS) in Table 1')

edit('profile-signalling', [
    ('Gesture (PSG) is Body (PSB)', 'Gesture (PSG)', 'Body (PSB)', 1),
    ('#PSG is #PSB in the example', '#PSG', '#PSB', 1),
    ('Face and Gesture is Face and Body', 'Face and Gesture', 'Face and Body', 1),
    ('the example with ISD uses SPD', 'ALL-ISD@TRN', 'ALL-SPD@TRN', 1),
])
edit('json-syntax-and-semantics', [
    ('the Profile pattern: PST for SPT, PSB for PSG', 'SPT|PSS|PSF|PSG', 'PST|PSS|PSF|PSB', 1),
    ('the Profile pattern: AVS added', 'AVG|AVM', 'AVG|AVS|AVM', 1),
    ('the Profile pattern: ISD removed', 'FCD|EPS|ISD|', 'FCD|EPS|', 1),
])
edit('aim-profiles', [('Body Object receives Body, not Gesture', 'Receives Gesture', 'Receives Body', 1)])
with open(os.path.join(D, 'CHANGES.txt'), 'a', encoding='utf-8') as f:
    f.write('\nCorrections (fix_prf_pages.py):\n' + '\n'.join(log) + '\n')
print('\n'.join(log))
