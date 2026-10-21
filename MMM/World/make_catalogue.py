"""The Persona catalogue of the MMM: every Microsoft Rocketbox avatar (MIT licence) as a glTF binary, for the viewer.

  python MMM/World/make_catalogue.py [rocketbox folder] [output folder] [--only NAME ...]

Per avatar (Assets/Avatars/<Group>/<Name>): its textures to 1024 x 1024 (colour and normal maps JPEG, the opacity map PNG with its
alpha) next to a copy of the _facial.fbx, under the names the FBX gives them, then FBX2glTF --binary. The result is <output>/<Name>.glb
and catalogue.json (Name, Group, Gender, File). Already converted avatars are skipped.
"""
import json, os, shutil, subprocess, sys, tempfile
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
# The Rocketbox repository (git clone https://github.com/microsoft/Microsoft-Rocketbox, about 14 GB: not kept in the repository), looked for in MMM/tools, or in D:\DI while it exists.
ROOT = next((p for p in [os.path.join(HERE, '..', 'tools', 'Microsoft-Rocketbox', 'Assets', 'Avatars'), r'D:\DI\Access\Avatars\_download\Microsoft-Rocketbox\Assets\Avatars'] if os.path.isdir(p)), None)
TOOL = os.path.join(HERE, '..', 'tools', 'FBX2glTF-windows-x86_64', 'FBX2glTF-windows-x86_64.exe')   # FBX2glTF v0.13.1, kept with the repository
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Viewer', 'personae', 'catalogue')
GROUPS = ['Adults', 'Professions']


def convert_texture(src, dst):
    im = Image.open(src)
    if max(im.size) > 1024:
        im = im.resize((1024, 1024), Image.LANCZOS)
    name = os.path.basename(src).lower()
    if 'opacity' in name:
        im.convert('RGBA').save(dst, 'PNG')
    else:
        im.convert('RGB').save(dst, 'JPEG', quality=85)


def avatar(group, name, out):
    folder = os.path.join(ROOT, group, name)
    fbx = os.path.join(folder, 'Export', name + '_facial.fbx')
    if not os.path.exists(fbx):
        fbx = os.path.join(folder, 'Export', name + '.fbx')
    if not os.path.exists(fbx):
        return None
    glb = os.path.join(out, name + '.glb')
    if os.path.exists(glb):
        return glb
    with tempfile.TemporaryDirectory() as tmp:
        shutil.copy(fbx, os.path.join(tmp, 'a.fbx'))
        textures = os.path.join(folder, 'Textures')
        for t in os.listdir(textures) if os.path.isdir(textures) else []:
            if t.lower().endswith(('.tga', '.png', '.jpg')):
                base = os.path.splitext(t)[0]                                               # FBX2glTF finds f001_body_color.tga as f001_body_color.jpg or .png next to the FBX
                convert_texture(os.path.join(textures, t), os.path.join(tmp, base + ('.png' if 'opacity' in t.lower() else '.jpg')))
        r = subprocess.run([TOOL, '--binary', '-i', os.path.join(tmp, 'a.fbx'), '-o', os.path.join(tmp, 'a')], capture_output=True, text=True)
        built = os.path.join(tmp, 'a.glb')
        if not os.path.exists(built):
            print('FAILED', name, r.stdout[-300:], r.stderr[-300:])
            return None
        shutil.copy(built, glb)
    return glb


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    only = sys.argv[sys.argv.index('--only') + 1:] if '--only' in sys.argv else None
    out = os.path.abspath(args[1] if len(args) > 1 else OUT)
    os.makedirs(out, exist_ok=True)
    entries = []
    for group in GROUPS:
        for name in sorted(os.listdir(os.path.join(ROOT, group))):
            if only and name not in only:
                continue
            glb = avatar(group, name, out)
            if glb:
                gender = 'Female' if name.lower().startswith('female') or name.lower().startswith('f') and not name.lower().startswith('fire') else 'Male'
                entries.append({'Name': name, 'Group': group, 'Gender': gender, 'File': 'personae/catalogue/' + name + '.glb', 'Bytes': os.path.getsize(glb)})
                print('ok', name, os.path.getsize(glb) // 1024, 'KB', flush=True)
    json.dump(entries, open(os.path.join(out, 'catalogue.json'), 'w', encoding='utf8'), indent=1)
