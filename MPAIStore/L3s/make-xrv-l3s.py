"""The L3s of XRV-LTP for the MPAI Store: the Instances whose code is a package at an address (review finding X2, 2026-11-14).

The Live Theatrical Performance (XRV-LTP-V1.0) is a Composite AIM of seven AIMs. The L3 of a Composite AIM carries no code: it lists its Sub-AIMs and how
they are connected. Each leaf AIM has an L3 of its own whose ImplementationURI is where its package is - the zip made by AIMs/Build-Packages.ps1 for
the platform the L3 names - and the Controller fetches the packages one by one, has the Store verify each, and loads the plug-in each carries.

These are new Instances of what AIMs/AMDs holds as -I01 (whose ImplementationURI is the source) and are kept in MPAIStore/L3s/XRV: -I02 for the seven
leaf AIMs and for LTP, which names them. Nothing else in them differs from the -I01 L3s.

    python make-xrv-l3s.py <base> [<ImplementerID>]     e.g. https://github.com/leonardo-chiariglione/packages/releases/download/xrv-v1.0
                                             or   file:///D:/AI/MPAIStore/Packages
The package of an AIM is <base>/<ImplementationID>-win-x64.zip.
"""
import os, re, sys

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(os.path.dirname(os.path.dirname(here)), 'AIMs', 'AMDs')   # the -I01 L3s the new ones are made from
out = os.path.join(here, 'XRV')
os.makedirs(out, exist_ok=True)
if len(sys.argv) not in (2, 3):
    sys.exit(__doc__)
base = sys.argv[1].rstrip('/')
implementer = sys.argv[2] if len(sys.argv) == 3 else '1'

LEAVES = ['XRV-ACG-V1.0', 'XRV-END-V1.0', 'XRV-OSI-V1.0', 'XRV-PFI-V1.0', 'XRV-PTI-V1.0', 'XRV-REG-V1.0', 'XRV-VEG-V1.0']
COMPOSITE = 'XRV-LTP-V1.0'

def read(name):
    raw = open(os.path.join(src, name), 'rb').read()
    return raw.startswith(b'\xef\xbb\xbf'), raw.decode('utf-8-sig')

def write(name, bom, text):
    if implementer != '1':
        text = text.replace('"ImplementerID": "1"', '"ImplementerID": "%s"' % implementer)
        text = re.sub(r'"1XRV-', '"%sXRV-' % implementer, text)
    open(os.path.join(out, name), 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(text)

uri = re.compile(r'("ImplementationURI"\s*:\s*)"[^"]*"')

for leaf in LEAVES:
    bom, t = read(f'1{leaf}-I01.json')
    t = t.replace(f'{leaf}-I01', f'{leaf}-I02')
    new = f'{base}/{leaf}-I02-win-x64.zip'
    t, n = uri.subn(lambda m: m.group(1) + '"' + new + '"', t)
    assert n == 1, leaf
    write(f'1{leaf}-I02.json', bom, t)
    print(f'1{leaf}-I02  ->  {new}')

bom, t = read(f'1{COMPOSITE}-I01.json')
for name in [COMPOSITE] + LEAVES:
    t = t.replace(f'{name}-I01', f'{name}-I02')
# the Composite AIM has no package: its ImplementationURI is where its L3 is
t = uri.sub(lambda m: m.group(1) + '"https://github.com/leonardo-chiariglione/packages"', t)
assert '-I01' not in t, 'the Composite still names an -I01 Instance'
write(f'1{COMPOSITE}-I02.json', bom, t)
print(f'1{COMPOSITE}-I02  (composite)')
