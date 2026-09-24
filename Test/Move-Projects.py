"""Moves folders of the repository with git mv and rewrites every relative
Include="..." path in every .csproj - of the moved projects and of the projects
that reference them - so that each still points at the same file (M3209).

    python Test/Move-Projects.py FROM=TO [FROM=TO ...]      (paths relative to the root)

Prints each rewritten path. Changes nothing else; building is the check."""
import os, re, subprocess, sys

root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
os.chdir(root)
moves = [tuple(a.replace('\\', '/').strip('/').split('=', 1)) for a in sys.argv[1:]]
if not moves or any(len(m) != 2 for m in moves):
    sys.exit(__doc__)

def tracked_projects():
    out = subprocess.run(['git', 'ls-files', '*.csproj'], capture_output=True, text=True, check=True).stdout
    return [p for p in out.split() if p]

def new_location(path):
    """Where a repository path (forward slashes) is after the moves."""
    for src, dst in moves:
        if path == src or path.startswith(src + '/'):
            return dst + path[len(src):]
    return path

INCLUDE = re.compile(r'(Include=")([^"]*\.\.[^"]*)(")')

# 1. Every relative Include, resolved to the repository path it points at, before moving.
plan = {}
for proj in tracked_projects():
    text = open(proj, encoding='utf-8-sig').read()
    targets = []
    for m in INCLUDE.finditer(text):
        rel = m.group(2)
        target = os.path.normpath(os.path.join(os.path.dirname(proj), rel.replace('\\', '/'))).replace('\\', '/')
        targets.append((rel, target))
    plan[proj] = targets

# 2. Move.
for src, dst in moves:
    os.makedirs(os.path.dirname(dst) or '.', exist_ok=True)
    subprocess.run(['git', 'mv', src, dst], check=True)
    print(f'moved {src} -> {dst}')

# 3. Rewrite the Includes of every project, from its new place to its targets' new places.
for proj, targets in plan.items():
    if not targets: continue
    newproj = new_location(proj)
    raw = open(newproj, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig')
    changed = False
    for rel, target in targets:
        newrel = os.path.relpath(new_location(target), os.path.dirname(newproj)).replace('/', '\\')
        if newrel != rel:
            text = text.replace(f'Include="{rel}"', f'Include="{newrel}"')
            changed = True
            print(f'  {newproj}: {rel} -> {newrel}')
    if changed:
        open(newproj, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))
