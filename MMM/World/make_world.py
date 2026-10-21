"""The world of the MMM, as files: the buildings as glTF (.glb) and world.json, which says where everything is.

  python MMM/World/make_world.py

Writes MMM/Viewer/world/{mpai,library,cafe}.glb and MMM/Viewer/world/world.json. Nothing here is downloaded: the buildings are made of boxes, with walls that
have door and window openings, a floor, a ceiling, a table and chairs, and the Rocketbox avatars of the viewer are the people.

Axes: space - x east, y north, z up, metres (MMM/AXES.md). glTF is Y-up, so a point (x, y, z) of space is written as (x, z, -y).
The MPAI building's glTF has its origin at the centre of its atrium; a small building's at the centre of its one room: the building is placed in the world by the Location it is.
"""
import json, math, os, struct

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Viewer', 'world')
os.makedirs(OUT, exist_ok=True)

WALL, FLOOR = 0.2, 0.1                       # thickness of a wall; thickness of the floor and ceiling slabs
COL = dict(wall=(0.93, 0.92, 0.88, 1), floor=(0.62, 0.45, 0.28, 1), ceiling=(0.97, 0.97, 0.95, 1), table=(0.33, 0.2, 0.12, 1),
           chair=(0.15, 0.25, 0.45, 1), glass=(0.7, 0.85, 1.0, 0.25), door=(0.45, 0.3, 0.18, 1), desk=(0.55, 0.38, 0.22, 1),
           screen=(0.05, 0.06, 0.08, 1), trim=(0.3, 0.3, 0.32, 1), carpet=(0.35, 0.18, 0.2, 1), brick=(0.7, 0.42, 0.32, 1), roof=(0.3, 0.3, 0.34, 1),
           shelf=(0.4, 0.28, 0.16, 1), counter=(0.8, 0.8, 0.82, 1))


class Mesh:
    """Boxes, grouped by material, written as one glTF mesh with one primitive per material."""
    def __init__(self): self.mats = {}; self.prims = {}

    def box(self, mat, x0, y0, z0, x1, y1, z1):
        if x1 - x0 <= 1e-6 or y1 - y0 <= 1e-6 or z1 - z0 <= 1e-6: return
        p = self.prims.setdefault(mat, ([], [], []))
        pos, nor, idx = p
        X0, X1, Y0, Y1, Z0, Z1 = x0, x1, z0, z1, -y1, -y0           # to glTF: (x, z, -y)
        faces = [((0, 0, 1), [(X0, Y0, Z1), (X1, Y0, Z1), (X1, Y1, Z1), (X0, Y1, Z1)]), ((0, 0, -1), [(X1, Y0, Z0), (X0, Y0, Z0), (X0, Y1, Z0), (X1, Y1, Z0)]),
                 ((1, 0, 0), [(X1, Y0, Z1), (X1, Y0, Z0), (X1, Y1, Z0), (X1, Y1, Z1)]), ((-1, 0, 0), [(X0, Y0, Z0), (X0, Y0, Z1), (X0, Y1, Z1), (X0, Y1, Z0)]),
                 ((0, 1, 0), [(X0, Y1, Z1), (X1, Y1, Z1), (X1, Y1, Z0), (X0, Y1, Z0)]), ((0, -1, 0), [(X0, Y0, Z0), (X1, Y0, Z0), (X1, Y0, Z1), (X0, Y0, Z1)])]
        for n, vs in faces:
            b = len(pos)
            pos.extend(vs); nor.extend([n] * 4); idx.extend([b, b + 1, b + 2, b, b + 2, b + 3])

    def glb(self, path):
        bin_ = bytearray(); views, accs, prims, mats = [], [], [], []
        def add(data, fmt, target, comp, typ, count, mn=None, mx=None):
            while len(bin_) % 4: bin_.append(0)
            views.append({'buffer': 0, 'byteOffset': len(bin_), 'byteLength': len(data), 'target': target}); bin_.extend(data)
            a = {'bufferView': len(views) - 1, 'componentType': comp, 'count': count, 'type': typ}
            if mn is not None: a['min'], a['max'] = mn, mx
            accs.append(a); return len(accs) - 1
        for mat, (pos, nor, idx) in self.prims.items():
            c = COL[mat]
            m = {'name': mat, 'pbrMetallicRoughness': {'baseColorFactor': list(c), 'metallicFactor': 0.0, 'roughnessFactor': 0.85}}
            if c[3] < 1: m['alphaMode'] = 'BLEND'; m['doubleSided'] = True
            mats.append(m)
            p = add(b''.join(struct.pack('<3f', *v) for v in pos), 'f', 34962, 5126, 'VEC3', len(pos), [min(v[i] for v in pos) for i in range(3)], [max(v[i] for v in pos) for i in range(3)])
            n = add(b''.join(struct.pack('<3f', *v) for v in nor), 'f', 34962, 5126, 'VEC3', len(nor))
            i = add(b''.join(struct.pack('<I', v) for v in idx), 'I', 34963, 5125, 'SCALAR', len(idx), [min(idx)], [max(idx)])
            prims.append({'attributes': {'POSITION': p, 'NORMAL': n}, 'indices': i, 'material': len(mats) - 1})
        doc = {'asset': {'version': '2.0', 'generator': 'MMM/World/make_world.py'}, 'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [{'mesh': 0}], 'meshes': [{'primitives': prims}],
               'materials': mats, 'buffers': [{'byteLength': len(bin_)}], 'bufferViews': views, 'accessors': accs}
        js = json.dumps(doc, separators=(',', ':')).encode()
        while len(js) % 4: js += b' '
        while len(bin_) % 4: bin_.append(0)
        data = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bin_)) + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(bin_), 0x004E4942) + bytes(bin_)
        open(path, 'wb').write(data)


def wall(mesh, mat, a, b, h, openings, solids, thick=WALL):
    """A wall from a=(x0,y0) to b=(x1,y1) (axis-aligned), h high. openings: (centre along the wall, width, sill, head, kind) with kind door or window; a window gets a glass pane.
    Appends to solids the rectangles that stop a walker: the wall's pieces, and no door opening."""
    (x0, y0), (x1, y1) = a, b
    horizontal = abs(y1 - y0) < 1e-9
    lo, hi = (min(x0, x1), max(x0, x1)) if horizontal else (min(y0, y1), max(y0, y1))
    def piece(u0, u1, z0, z1):
        if horizontal: mesh.box(mat, u0, y0 - thick / 2, z0, u1, y0 + thick / 2, z1)
        else: mesh.box(mat, x0 - thick / 2, u0, z0, x0 + thick / 2, u1, z1)
    def stop(u0, u1):
        if u1 - u0 <= 1e-6: return
        solids.append([u0, y0 - thick / 2, u1, y0 + thick / 2] if horizontal else [x0 - thick / 2, u0, x0 + thick / 2, u1])
    cur = lo
    for c, w, sill, head, kind in sorted(openings):
        o0, o1 = c - w / 2, c + w / 2
        piece(cur, o0, 0, h); stop(cur, o0)
        if sill > 0: piece(o0, o1, 0, sill)
        piece(o0, o1, head, h)
        if kind == 'window':
            stop(o0, o1)
            gp = 0.02
            if horizontal: mesh.box('glass', o0, y0 - gp, sill, o1, y0 + gp, head)
            else: mesh.box('glass', x0 - gp, o0, sill, x0 + gp, o1, head)
        cur = o1
    piece(cur, hi, 0, h); stop(cur, hi)


def building(name, x0, y0, x1, y1, h, outer_openings, inner=None, furnish=None, wallmat='wall', roof=None):
    """A building: a floor, a ceiling, the four outer walls with their openings, inner walls. Returns (mesh, solids)."""
    m, solids = Mesh(), []
    m.box('floor', x0, y0, 0, x1, y1, FLOOR * 0.5)
    m.box('ceiling', x0 - 0.1, y0 - 0.1, h, x1 + 0.1, y1 + 0.1, h + FLOOR)
    if roof: m.box('roof', x0 - 0.3, y0 - 0.3, h + FLOOR, x1 + 0.3, y1 + 0.3, h + FLOOR + 0.12)
    for side, (a, b) in dict(south=((x0, y0), (x1, y0)), north=((x0, y1), (x1, y1)), west=((x0, y0), (x0, y1)), east=((x1, y0), (x1, y1))).items():
        wall(m, wallmat, a, b, h, outer_openings.get(side, []), solids)
    for (a, b, ops) in (inner or []): wall(m, wallmat, a, b, h, ops, solids)
    if furnish: furnish(m)
    return m, solids


def chair(m, cx, cy, yaw_deg):
    """The back of a chair behind the Persona, away from the table: the seat of the Persona is at (cx, cy) and it looks the way yaw says (0 looks north, positive turns west)."""
    r = math.radians(yaw_deg)
    dx, dy = -math.sin(r), math.cos(r)                                              # the way the Persona looks
    px, py = cx - dx * 0.42, cy - dy * 0.42
    if abs(dx) < 0.5: m.box('chair', px - 0.25, py - 0.04, 0.0, px + 0.25, py + 0.04, 1.0)
    else: m.box('chair', px - 0.04, py - 0.25, 0.0, px + 0.04, py + 0.25, 1.0)


# ------------------------------------------------------------------ the MPAI building: an atrium (public) and three wings
# Origin: the centre of the atrium. The atrium is x in [-12, 12], y in [-9, 9], 5 m high; its main door is in the west wall. Three wings go out of it, each a corridor with rooms on both sides:
#   north - the rooms of the member companies; east - the offices of MPAI (President, Vice-president, Board members, Secretariat); south - the meeting rooms, one for each group.
CORR = 3.0                  # width of a corridor
RW, RD = 7.0, 8.0           # a room: 7 m along the corridor, 8 m deep
DOOR_W = 1.8
HW = CORR / 2
MEETING_ROOMS = [('MeetingRoom', 'MMM meeting room')] + [(f'{g}-Room', f'{g} meeting room') for g in ('AIF', 'AIH', 'AIP', 'CAE', 'EVC', 'ISAC', 'MMC', 'NNW', 'PAF', 'XRV')]   # one for each group of MMM/Registry/groups.json
MEMBER_ROOMS = [(f'MemberRoom{i}', f"Members' room {i}") for i in range(1, 5)]
OFFICES = [('Office-President', "President's office"), ('Office-VicePresident', "Vice-president's office"), ('Office-Board', "Board members' office"), ('Office-Secretariat', 'Secretariat')]

cells = {}       # id -> dict(x0, y0, x1, y1, h, kind, name)
doors = []       # dict(id, name, a, b, x, y, axis, outside): a, b the cells (or MVS) the door joins, a first; (x, y) in the building's frame; outside: on which side of the door a is (-1: lower coordinate)


def cell(id, x0, y0, x1, y1, h, kind, name):
    cells[id] = dict(x0=x0, y0=y0, x1=x1, y1=y1, h=h, kind=kind, name=name)


cell('Atrium', -12, -9, 12, 9, 5.0, 'atrium', 'Atrium')
doors.append(dict(id='MPAI-main', name='MPAI, main door', a='MVS', b='Atrium', x=-12, y=0, axis='x', outside=-1))


def wing(id, name, door_xy, axis, direction, rooms, kind):
    """A corridor out of the atrium through the door at door_xy, running in direction (a unit vector), with its rooms on both sides, RW along the corridor."""
    n = (len(rooms) + 1) // 2
    L = n * RW
    dx, dy = direction
    px, py = door_xy
    if dx == 0:   # north or south
        y0, y1 = (py, py + L) if dy > 0 else (py - L, py)
        cell(id, px - HW, y0, px + HW, y1, 3.2, 'corridor', name)
    else:
        x0, x1 = (px, px + L) if dx > 0 else (px - L, px)
        cell(id, x0, py - HW, x1, py + HW, 3.2, 'corridor', name)
    doors.append(dict(id=id + '-door', name=name + ' door', a='Atrium', b=id, x=px, y=py, axis=axis, outside=(-1 if (dx > 0 or dy > 0) else 1)))
    for i, (rid, rname) in enumerate(rooms):
        side, k = i % 2, i // 2            # side 0: west or north of the corridor; 1: east or south
        if dx == 0:
            ya = (py + k * RW, py + (k + 1) * RW) if dy > 0 else (py - (k + 1) * RW, py - k * RW)
            xa = (px - HW - RD, px - HW) if side == 0 else (px + HW, px + HW + RD)
            cell(rid, xa[0], ya[0], xa[1], ya[1], 3.2, kind, rname)
            doors.append(dict(id=rid + '-door', name=rname + ' door', a=id, b=rid, x=(px - HW) if side == 0 else (px + HW), y=(ya[0] + ya[1]) / 2, axis='x', outside=(1 if side == 0 else -1)))
        else:
            xa = (px + k * RW, px + (k + 1) * RW) if dx > 0 else (px - (k + 1) * RW, px - k * RW)
            ya = (py + HW, py + HW + RD) if side == 0 else (py - HW - RD, py - HW)
            cell(rid, xa[0], ya[0], xa[1], ya[1], 3.2, kind, rname)
            doors.append(dict(id=rid + '-door', name=rname + ' door', a=id, b=rid, x=(xa[0] + xa[1]) / 2, y=(py + HW) if side == 0 else (py - HW), axis='y', outside=(-1 if side == 0 else 1)))


wing('MembersWing', "Members' wing", (0, 9), 'y', (0, 1), MEMBER_ROOMS, 'member')
wing('OfficesWing', 'Offices of MPAI', (12, 0), 'x', (1, 0), OFFICES, 'office')
wing('MeetingsWing', 'Meeting rooms', (0, -9), 'y', (0, -1), MEETING_ROOMS, 'meeting')

SEATS = []                                           # (id suffix, x, y, yaw), around the table, relative to the centre of the room
for i, (sx, sy) in enumerate([(-1.3, 1.7), (0, 1.7), (1.3, 1.7), (-1.3, -1.7), (0, -1.7), (1.3, -1.7), (-3.0, 0), (3.0, 0)], start=1):
    SEATS.append((f'S{i}', sx, sy, round(math.degrees(math.atan2(sx, -sy)) % 360, 1)))

RECEPTION = dict(cx=5.0, persona='Reception-Persona', x=6.2, y=0.0, yaw=90, model='catalogue/Business_Female_01.glb')   # the lady at the counter: the Multimodal Conversation Avatar


def edges(c):
    return dict(south=((c['x0'], c['y0']), (c['x1'], c['y0'])), north=((c['x0'], c['y1']), (c['x1'], c['y1'])), west=((c['x0'], c['y0']), (c['x0'], c['y1'])), east=((c['x1'], c['y0']), (c['x1'], c['y1'])))


def inside_other(id, x, y):
    for k, o in cells.items():
        if k != id and o['x0'] - 0.01 <= x <= o['x1'] + 0.01 and o['y0'] - 0.01 <= y <= o['y1'] + 0.01: return True
    return False


def furnish_room(m, id, c):
    cx, cy = (c['x0'] + c['x1']) / 2, (c['y0'] + c['y1']) / 2
    m.box('carpet', c['x0'] + 0.2, c['y0'] + 0.2, FLOOR * 0.5, c['x1'] - 0.2, c['y1'] - 0.2, FLOOR * 0.5 + 0.01)
    if c['kind'] == 'meeting':
        m.box('table', cx - 2.2, cy - 0.8, 0.72, cx + 2.2, cy + 0.8, 0.78)
        for tx in (-1.9, 1.9):
            for ty in (-0.55, 0.55): m.box('table', cx + tx - 0.06, cy + ty - 0.06, 0, cx + tx + 0.06, cy + ty + 0.06, 0.72)
        for (_, sx, sy, yaw) in SEATS: chair(m, cx + sx, cy + sy, yaw)
        m.box('screen', cx - 3.0, c['y1'] - 0.14, 1.1, cx + 3.0, c['y1'] - 0.06, 2.7)           # a screen on the north wall
    elif c['kind'] == 'member':
        m.box('table', cx - 1.5, cy - 0.7, 0.72, cx + 1.5, cy + 0.7, 0.78)
        for sx, sy, yaw in [(-1.0, 1.0, 180), (1.0, 1.0, 180), (-1.0, -1.0, 0), (1.0, -1.0, 0)]: chair(m, cx + sx, cy + sy, yaw)
    elif c['kind'] == 'office':
        m.box('desk', cx - 1.2, cy - 0.5, 0, cx + 1.2, cy + 0.5, 0.76)
        chair(m, cx, cy - 1.3, 0)
        m.box('shelf', c['x0'] + 0.3, c['y1'] - 0.7, 0, c['x1'] - 0.3, c['y1'] - 0.3, 1.8)
    elif c['kind'] == 'atrium':
        m.box('carpet', c['x0'] + 0.6, c['y0'] + 0.6, FLOOR * 0.5, c['x1'] - 0.6, c['y1'] - 0.6, FLOOR * 0.5 + 0.02)
        m.box('counter', RECEPTION['cx'] - 0.9, -3.0, 0, RECEPTION['cx'] + 0.4, 3.0, 1.05)       # the counter of the reception, long across the way in
        m.box('screen', -11.9, 3.0, 1.2, -11.8, 7.0, 3.0)                                       # the board of the building, on the west wall
        m.box('chair', -9.0, 6.0, 0, -7.4, 7.6, 0.45); m.box('chair', -9.0, -7.6, 0, -7.4, -6.0, 0.45)   # two sofas


def make_building():
    m, solids = Mesh(), []
    for id, c in cells.items():
        h = c['h']
        m.box('floor', c['x0'], c['y0'], 0, c['x1'], c['y1'], FLOOR * 0.5)
        m.box('ceiling', c['x0'] - 0.1, c['y0'] - 0.1, h, c['x1'] + 0.1, c['y1'] + 0.1, h + FLOOR)
        for side, (a, b) in edges(c).items():
            ops = []
            for d in doors:
                if id not in (d['a'], d['b']): continue
                on = (d['axis'] == 'x' and side in ('west', 'east') and abs(d['x'] - a[0]) < 1e-6 and c['y0'] < d['y'] < c['y1']) or \
                     (d['axis'] == 'y' and side in ('south', 'north') and abs(d['y'] - a[1]) < 1e-6 and c['x0'] < d['x'] < c['x1'])
                if on: ops.append(((d['y'] if d['axis'] == 'x' else d['x']), DOOR_W, 0, 2.3, 'door'))
            # REGULAR WINDOWS on the outer walls: evenly spaced along the wall, 1.4 m wide (the atrium's are tall), none where a door is or where the wall is another room's
            horiz = side in ('south', 'north')
            lo, hi = (a[0], b[0]) if horiz else (a[1], b[1])
            length = hi - lo
            k = max(0, int((length - 2.4 + 1.8) // 3.2))
            out = {'west': (-0.5, 0), 'east': (0.5, 0), 'south': (0, -0.5), 'north': (0, 0.5)}[side]
            for i in range(k):
                centre = lo + length * (i + 0.5) / k
                if any(abs(centre - o[0]) < DOOR_W / 2 + 0.7 + 0.4 for o in ops): continue
                px, py = (centre + out[0], a[1] + out[1]) if horiz else (a[0] + out[0], centre + out[1])
                if inside_other(id, px, py): continue
                ops.append((centre, 1.6 if h > 4 else 1.4, 0.9, 4.2 if h > 4 else 2.3, 'window'))
            wall(m, 'wall', a, b, h, ops, solids)
        furnish_room(m, id, c)
    # the frame of the sign on the roof of the atrium: a backing board and fins behind it
    x, z0, z1, half = SIGN['x'], SIGN['z'] - 0.1, SIGN['z'] + SIGN['h'] + 0.15, SIGN['w'] / 2 + 0.15
    m.box('trim', x, -half, z0, x + 0.12, half, z1)                         # behind the picture (the picture is at x - 0.01, in front of it)
    for fy in (-3.4, 0, 3.4): m.box('trim', x + 0.12, fy - 0.08, z0, x + 1.0, fy + 0.08, z0 + SIGN['h'] * 0.7)
    return m, solids



# ------------------------------------------------------------------ the sign on top of the MPAI building: the logo and the name of MPAI on two lines
SIGN = dict(w=9.0, h=1.425, z=5.2, x=-12.25, text_px=98)       # metres, in the building's frame: on the roof edge of the atrium, facing the Square (west)


def make_sign(path):
    from PIL import Image, ImageDraw, ImageFont
    W, H = 2400, int(2400 * SIGN['h'] / SIGN['w'])
    im = Image.new('RGB', (W, H), 'white'); d = ImageDraw.Draw(im)
    d.rectangle([0, 0, W - 1, H - 1], outline=(51, 122, 168), width=16)                            # the blue of the logo
    logo = Image.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'assets', 'MPAI-logo.png')).convert('RGBA')
    lh = int(H * 0.86); lw = int(logo.width * lh / logo.height)
    logo = logo.resize((lw, lh), Image.LANCZOS)
    im.paste(logo, (70, (H - lh) // 2), logo)
    fonts = ['C:/Windows/Fonts/arialbd.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf', '/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf']
    font_file = next((p for p in fonts if os.path.exists(p)), None)
    lines = ['Moving Picture, Audio and Data', 'Coding by Artificial Intelligence']
    x0 = 70 + lw + 90; room = W - x0 - 80
    size = SIGN['text_px']                                       # the size of the text does not change with the height of the sign
    while size > 20:
        font = ImageFont.truetype(font_file, size) if font_file else ImageFont.load_default()
        if max(d.textlength(t, font=font) for t in lines) <= room: break
        size -= 2
    gap = int(size * 0.35); total = 2 * size + gap
    y = (H - total) // 2 - int(size * 0.12)
    for t in lines:
        d.text((x0, y), t, font=font, fill=(40, 52, 68)); y += size + gap
    im.save(path)


make_sign(os.path.join(OUT, 'mpai-sign.png'))

mpai, mpai_solids = make_building()


def small_building(w, d, h, door_side):
    openings = {door_side: [(0, 1.6, 0, 2.3, 'door')]}
    for s in ('south', 'north', 'east', 'west'):
        if s != door_side: openings.setdefault(s, [(-w / 4 if s in ('south', 'north') else -d / 4, 1.4, 0.9, 2.2, 'window')])
    def furnish(m):
        m.box('counter', -w / 2 + 0.4, -d / 2 + 0.4, 0, -w / 2 + 3.4, -d / 2 + 1.0, 1.0)
        m.box('shelf', w / 2 - 1.0, -d / 2 + 0.4, 0, w / 2 - 0.4, d / 2 - 0.4, 1.8)
    return building('small', -w / 2, -d / 2, w / 2, d / 2, h, openings, furnish=furnish, wallmat='brick', roof=True)


library, library_solids = small_building(12, 9, 3.4, 'south')
cafe, cafe_solids = small_building(9, 8, 3.2, 'east')

mpai.glb(os.path.join(OUT, 'mpai.glb')); library.glb(os.path.join(OUT, 'library.glb')); cafe.glb(os.path.join(OUT, 'cafe.glb'))

# ------------------------------------------------------------------ the world, as the viewer and the server read it
def at(x, y, rects):     # a building's rectangles, in the world
    return [[r[0] + x, r[1] + y, r[2] + x, r[3] + y] for r in rects]

ACOUSTICS = {      # CAE3 Acoustic Profile, Scene (CAE-ACP-V1.0): a meeting room with a table, curtains and a carpet - fairly dry, a little diffuse. RT60 by frequency: a Plot, (Hz, seconds)
    'Header': 'CAE-ACP-V1.0', 'AcousticProfileID': 'Acoustics-MeetingRoom',
    'Reflectivity': [{'EarlyReflectionTime': 0.012, 'LateReflectionTime': 0.08}],
    'Reverberation': [{'RT60': {'Header': 'OSD-PLT-V1.5', 'PlotID': 'RT60-MeetingRoom', 'Plot': {'2D': [[125, 0.70], [250, 0.62], [500, 0.55], [1000, 0.50], [2000, 0.45], [4000, 0.36]]}}, 'EDT': 0.35}],
    'Diffusion': 0.5, 'Absorption': 0.32}
BUILDING_AT = (28.0, 0.0)


def cell_json(id, c, with_seats=False):
    cx, cy = (c['x0'] + c['x1']) / 2, (c['y0'] + c['y1']) / 2
    j = {'id': id, 'name': c['name'], 'x': cx, 'y': cy, 'footprint': [c['x0'] - cx, c['y0'] - cy, c['x1'] - cx, c['y1'] - cy], 'height': c['h'], 'kind': c['kind'], 'acoustics': ACOUSTICS}
    d = next(d for d in doors if d['b'] == id)           # the door by which it is entered, in the world's frame
    j['door'] = {'id': d['id'], 'name': d['name'], 'from': d['a'], 'to': d['b'], 'x': BUILDING_AT[0] + d['x'], 'y': BUILDING_AT[1] + d['y'], 'axis': d['axis'], 'width': DOOR_W, 'outside': d['outside']}
    if c['kind'] == 'atrium': j['public'] = True          # the atrium is a public space: anybody may enter, look, talk, ask
    if id == 'MeetingRoom': j['seats'] = [{'id': 'MeetingRoom-' + s, 'x': sx, 'y': sy, 'yaw': yaw} for (s, sx, sy, yaw) in SEATS]
    return j


xs = [c['x0'] for c in cells.values()] + [c['x1'] for c in cells.values()]
ys = [c['y0'] for c in cells.values()] + [c['y1'] for c in cells.values()]
main = doors[0]
ACOUSTICS_CORRIDOR = dict(ACOUSTICS, AcousticProfileID='Acoustics-Corridor', Absorption=0.15,
                          Reverberation=[{'RT60': {'Header': 'OSD-PLT-V1.5', 'PlotID': 'RT60-Corridor', 'Plot': {'2D': [[125, 1.1], [250, 1.0], [500, 0.9], [1000, 0.85], [2000, 0.7], [4000, 0.5]]}}, 'EDT': 0.6}])

world = {
    'axes': 'x east, y north, z up, metres',
    'ground': {'size': 200},
    'square': {'id': 'MVS', 'name': 'Metaverse Square', 'x': 0, 'y': 0, 'size': 30},
    'spawn': {'x': 3.0, 'y': 0.0, 'radius': 4.0, 'yaw': 270},
    'bounds': [-60, -60, 60, 60],
    'buildings': [
        {'id': 'MPAI', 'name': 'MPAI', 'model': 'world/mpai.glb', 'x': BUILDING_AT[0], 'y': BUILDING_AT[1], 'yaw': 0, 'height': 5.0,
                  'footprint': [min(xs), min(ys), max(xs), max(ys)],
         'sign': {'image': 'world/mpai-sign.png', 'x': BUILDING_AT[0] + SIGN['x'] - 0.01, 'y': BUILDING_AT[1], 'z': SIGN['z'], 'width': SIGN['w'], 'height': SIGN['h'], 'facing': 'west'},
         'solids': at(*BUILDING_AT, mpai_solids),
         'reception': dict(RECEPTION, x=RECEPTION['x'], y=RECEPTION['y']),
         'doors': [],
         'rooms': [cell_json(id, c) for id, c in cells.items()]},
        {'id': 'Library', 'name': 'Library', 'model': 'world/library.glb', 'x': 0.0, 'y': 30.0, 'yaw': 0, 'height': 3.4, 'footprint': [-6, -4.5, 6, 4.5], 'solids': at(0, 30, library_solids),
         'doors': [{'id': 'Library-door', 'name': 'Library door', 'from': 'MVS', 'to': 'Library', 'x': 0.0, 'y': 30 - 4.5, 'axis': 'y', 'width': 1.6, 'outside': -1}], 'rooms': []},
        {'id': 'Cafe', 'name': 'Cafe', 'model': 'world/cafe.glb', 'x': -30.0, 'y': 0.0, 'yaw': 0, 'height': 3.2, 'footprint': [-4.5, -4, 4.5, 4], 'solids': at(-30, 0, cafe_solids),
         'doors': [{'id': 'Cafe-door', 'name': 'Cafe door', 'from': 'MVS', 'to': 'Cafe', 'x': -30 + 4.5, 'y': 0.0, 'axis': 'x', 'width': 1.6, 'outside': 1}], 'rooms': []},
    ],
}
json.dump(world, open(os.path.join(OUT, 'world.json'), 'w'), indent=1)
print('wrote', sorted(os.listdir(OUT)))
