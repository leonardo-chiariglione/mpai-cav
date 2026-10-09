"""The corrected pages of MPAI-PRF V1.0 (same version): what the specification says and its own JSON schema disagree on.

  - AVS (Audio-Visual Scene Descriptors) is used by the examples and by the tables of the AIMs but is not an Attribute of
    Table 1 nor of the schema: it takes the place of the second 'Speech Model' (SPM) of Table 1, which is a duplicate,
    and is added to the schema.
  - the Text sub-attribute of the Personal Status is PST in the text, SPT in the schema: PST.
  - the Gesture sub-attribute (PSG) is Body (PSB): gesture is a subset of the body.
Writes web/mpai-prf/v1-0/<page>/index.html (HTML fragments, as make_web_version.py) and CHANGES.txt.
"""
import os, re, urllib.request

ROOT = os.path.dirname(os.path.abspath(__file__))
BASE = 'https://mpai.community/standards/mpai-prf/v1-0/'

def get(u):
    return urllib.request.urlopen(urllib.request.Request(u, headers={'User-Agent': 'Mozilla/5.0'}), timeout=30).read().decode('utf-8', 'ignore')

changes = []
def page(name, edits):
    h = get(BASE + name + '/')
    title = re.search(r'<h1[^>]*>(.*?)</h1>', h, flags=re.S).group(1).strip()
    j, k = h.find('<article'), h.find('</article>')
    body = h[j:k + len('</article>')]
    done = []
    for label, old, new, count in edits:
        n = body.count(old)
        if n != count:
            done.append('NOT DONE (%d found, %d expected): %s' % (n, count, label))
            continue
        if label.startswith('the second'):
            i = body.rfind(old)
            body = body[:i] + new + body[i + len(old):]
        else:
            body = body.replace(old, new)
        done.append('%d x %s' % (n, label))
    d = os.path.join(ROOT, 'web', 'mpai-prf', 'v1-0', name)
    os.makedirs(d, exist_ok=True)
    open(os.path.join(d, 'index.html'), 'w', encoding='utf-8').write('<!-- %s : from %s -->\n<h1>%s</h1>\n%s\n' % (name, BASE + name + '/', title, body))
    changes.append(name + ': ' + '; '.join(done))
    return body

# Table 1: the second Speech Model cell
h = get(BASE + 'profile-signalling/')
j, k = h.find('<article'), h.find('</article>')
a = h[j:k]
cells = [m.start() for m in re.finditer(r'Speech Model', a)]
print('Speech Model at', cells)
second = cells[1]
# the cell text and the code cell after it
m = re.compile(r'(Speech Model)(</[^>]+>\s*(?:</td>\s*<td[^>]*>)?\s*(?:<[^>]+>)*)SPM').search(a, second)
assert m, 'the second Speech Model cell'
old_pair = a[m.start():m.end()]
new_pair = old_pair.replace('Speech Model', 'Audio-Visual Scene Descriptors', 1)[:-3] + 'AVS'
page('profile-signalling', [
    ('the second Speech Model (SPM), a duplicate, is Audio-Visual Scene Descriptors (AVS)', old_pair, new_pair, 2),
    ('Gesture (PSG) is Body (PSB)', 'Gesture (PSG)', 'Body (PSB)', 1),
    ('#PSG is #PSB in the example', '#PSG', '#PSB', 1),
    ('Face and Gesture is Face and Body', 'Face and Gesture', 'Face and Body', 1),
])
page('json-syntax-and-semantics', [
    ('the Profile pattern: PST for SPT, PSB for PSG', 'SPT|PSS|PSF|PSG', 'PST|PSS|PSF|PSB', 1),
    ('the Profile pattern: AVS added', 'AVG|AVM', 'AVG|AVS|AVM', 1),
])
page('aim-profiles', [('Body Object receives Body, not Gesture', 'Receives Gesture', 'Receives Body', 1)])
with open(os.path.join(ROOT, 'web', 'mpai-prf', 'v1-0', 'CHANGES.txt'), 'w', encoding='utf-8') as f:
    f.write('MPAI-PRF V1.0, corrected pages (make by fix_prf_pages.py)\n\n' + '\n'.join(changes) + '\n')
print('\n'.join(changes))
