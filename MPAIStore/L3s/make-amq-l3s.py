"""The L3s of AMQ for the MPAI Store: the Instances whose code is a package at an address.

AMQ (MMC-AMQ-V2.5) is a Composite AIM of ASR, TIQ, TTS and PMX. The L3 of a Composite AIM carries no code: it lists its sub-AIMs and
how they are connected. Each leaf AIM has an L3 of its own whose ImplementationURI is where its package is - the zip made by
AIMs/Build-Packages.ps1 for the platform the L3 names - and the Controller fetches the packages one by one.

These are new Instances of what AIMs/AMDs holds as -I01 and are kept in MPAIStore/L3s, beside the packages in MPAIStore/Packages: -I03 for the four leaf AIMs and -I02 for AMQ, which names them. Nothing else
in them differs from the -I01 L3s (the Access they declare, the ports, the resources).

    python make-amq-l3s.py <base> [<ImplementerID>]     e.g. https://github.com/leonardo-chiariglione/packages/releases/download/amq-v2.5
                                             or   file:///C:/Users/Thor/Downloads/mpai-packages/upload
The package of an AIM is <base>/<ImplementationID>-win-x64.zip. ImplementerID is "1" for the tests; the Implementer ID that the IIDRA assigns to MPAI
replaces it in the Identifiers and as the first character of every AIMName (1MMC-ASR-V2.5-I03).
"""
import os, re, sys

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(os.path.dirname(os.path.dirname(here)), 'AIMs', 'AMDs')   # the -I01 L3s the new ones are made from
out = os.path.join(here, 'AMQ')
os.makedirs(out, exist_ok=True)
if len(sys.argv) not in (2, 3):
    sys.exit(__doc__)
base = sys.argv[1].rstrip('/')
implementer = sys.argv[2] if len(sys.argv) == 3 else '1'

LEAVES = ['MMC-ASR-V2.5', 'MMC-TIQ-V2.5', 'MMC-TTS-V2.5', 'PAF-PMX-V1.6']

def read(name):
    raw = open(os.path.join(src, name), 'rb').read()
    return raw.startswith(b'\xef\xbb\xbf'), raw.decode('utf-8-sig')

def write(name, bom, text):
    if implementer != '1':
        text = text.replace('"ImplementerID": "1"', '"ImplementerID": "%s"' % implementer)
        text = re.sub(r'"1(MMC|PAF|OSD)-', lambda m: '"%s%s-' % (implementer, m.group(1)), text)
    open(os.path.join(out, name), 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(text)

uri = re.compile(r'("ImplementationURI"\s*:\s*)"[^"]*"')

for leaf in LEAVES:
    bom, t = read(f'1{leaf}-I01.json')
    t = t.replace(f'{leaf}-I01', f'{leaf}-I03')
    new = f'{base}/{leaf}-I03-win-x64.zip'
    t, n = uri.subn(lambda m: m.group(1) + '"' + new + '"', t)
    assert n >= 1, leaf
    write(f'1{leaf}-I03.json', bom, t)
    print(f'1{leaf}-I03  ->  {new}')

bom, t = read('1MMC-AMQ-V2.5-I01.json')
t = t.replace('MMC-AMQ-V2.5-I01', 'MMC-AMQ-V2.5-I02')
for leaf in LEAVES:
    t = t.replace(f'{leaf}-I01', f'{leaf}-I03')
# the Composite AIM has no package: its ImplementationURI is where its L3 is
t = uri.sub(lambda m: m.group(1) + '"https://github.com/leonardo-chiariglione/packages"', t)
write('1MMC-AMQ-V2.5-I02.json', bom, t)
print('1MMC-AMQ-V2.5-I02  (composite)')

# The App of AMQ: its workflow (Workflow Description Language) and its descriptor, which names the Module. The workflow is what a User Agent runs to drive the Module through the
# Controller; the Service hands it to the client that chooses the App. It names the Module by the Instance of the L3 above.
apps = os.path.join(os.path.dirname(here), 'Apps', 'AMQ')
os.makedirs(apps, exist_ok=True)
for name in ('app.json', 'MMC-AMQ.orch', 'icon.svg'):
    raw = open(os.path.join(os.path.dirname(os.path.dirname(here)), 'Apps', 'MMC', 'AMQ', name), 'rb').read()
    bom = raw[:3] == bytes([0xEF, 0xBB, 0xBF])
    text = raw.decode('utf-8-sig').replace('MMC-AMQ-V2.5-I01', 'MMC-AMQ-V2.5-I02')
    if implementer != '1':
        text = text.replace('1MMC-AMQ-V2.5-I02', implementer + 'MMC-AMQ-V2.5-I02')
    open(os.path.join(apps, name), 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(text)
print('Apps/AMQ  (the workflow of the App)')

