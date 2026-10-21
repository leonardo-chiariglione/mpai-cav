"""The world of the MMM, as files: the buildings as glTF (.glb) and world.json, which says where everything is.

  python MMM/World/make_world.py

Writes MMM/Viewer/world/{townhall,library,cafe}.glb and MMM/Viewer/world/world.json. Nothing here is downloaded: the buildings are made of boxes, with walls that
have door and window openings, a floor, a ceiling, a table and chairs, and the Rocketbox avatars of the viewer are the people.

Axes: space - x east, y north, z up, metres (MMM/AXES.md). glTF is Y-up, so a point (x, y, z) of space is written as (x, z, -y).
A building's glTF has its origin at the centre of its meeting room (or of its one room): the building is placed in the world by the Location it is.
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


# ------------------------------------------------------------------ the Town Hall: a lobby and the meeting room
# Origin: the centre of the meeting room. The room is x in [-6, 6], y in [-5, 5]; the lobby is x in [-12, -6]. The main door is in the west wall, the meeting room's in the wall between them.
SEATS = []                                           # (id suffix, x, y, yaw), around the table
for i, (sx, sy) in enumerate([(-1.3, 1.7), (0, 1.7), (1.3, 1.7), (-1.3, -1.7), (0, -1.7), (1.3, -1.7), (-3.0, 0), (3.0, 0)], start=1):
    SEATS.append((f'S{i}', sx, sy, round(math.degrees(math.atan2(sx, -sy)) % 360, 1)))


def furnish_hall(m):
    m.box('carpet', -5.8, -4.8, FLOOR * 0.5, 5.8, 4.8, FLOOR * 0.5 + 0.01)
    m.box('table', -2.2, -0.8, 0.72, 2.2, 0.8, 0.78)                                # the table top
    for tx in (-1.9, 1.9):
        for ty in (-0.55, 0.55): m.box('table', tx - 0.06, ty - 0.06, 0, tx + 0.06, ty + 0.06, 0.72)
    for (_, sx, sy, yaw) in SEATS: chair(m, sx, sy, yaw)
    m.box('screen', -3.0, 4.82, 1.1, 3.0, 4.9, 2.7)                                 # a screen on the north wall
    m.box('desk', -11.4, -1.0, 0, -10.2, 1.0, 1.05)                                 # the reception desk in the lobby
    m.box('chair', -9.0, 3.4, 0, -7.4, 4.6, 0.45); m.box('chair', -9.0, -4.6, 0, -7.4, -3.4, 0.45)   # two sofas


HALL = dict(x0=-12, y0=-5, x1=6, y1=5, h=3.2)
hall, hall_solids = building('Town Hall', HALL['x0'], HALL['y0'], HALL['x1'], HALL['y1'], HALL['h'],
    outer_openings=dict(
        west=[(0, 1.8, 0, 2.3, 'door')],
        south=[(-9, 1.6, 0.9, 2.3, 'window'), (-3.5, 1.6, 0.9, 2.3, 'window'), (0, 1.6, 0.9, 2.3, 'window'), (3.5, 1.6, 0.9, 2.3, 'window')],
        north=[(-9, 1.6, 0.9, 2.3, 'window'), (-4.2, 1.6, 0.9, 2.3, 'window'), (4.2, 1.6, 0.9, 2.3, 'window')]),
    inner=[((-6, -5), (-6, 5), [(0, 1.8, 0, 2.3, 'door')])], furnish=furnish_hall)


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

hall.glb(os.path.join(OUT, 'townhall.glb')); library.glb(os.path.join(OUT, 'library.glb')); cafe.glb(os.path.join(OUT, 'cafe.glb'))

# ------------------------------------------------------------------ the world, as the viewer and the server read it
def at(x, y, rects):     # a building's rectangles, in the world
    return [[r[0] + x, r[1] + y, r[2] + x, r[3] + y] for r in rects]

HALL_AT = (28.0, 0.0)
ACOUSTICS = {      # CAE3 Acoustic Profile, Scene (CAE-ACP-V1.0): a meeting room with a table, curtains and a carpet - fairly dry, a little diffuse. RT60 by frequency: a Plot, (Hz, seconds)
    'Header': 'CAE-ACP-V1.0', 'AcousticProfileID': 'Acoustics-MeetingRoom',
    'Reflectivity': [{'EarlyReflectionTime': 0.012, 'LateReflectionTime': 0.08}],
    'Reverberation': [{'RT60': {'Header': 'OSD-PLT-V1.5', 'PlotID': 'RT60-MeetingRoom', 'Plot': {'2D': [[125, 0.70], [250, 0.62], [500, 0.55], [1000, 0.50], [2000, 0.45], [4000, 0.36]]}}, 'EDT': 0.35}],
    'Diffusion': 0.5, 'Absorption': 0.32}
world = {
    'axes': 'x east, y north, z up, metres',
    'ground': {'size': 200},
    'square': {'id': 'MVS', 'name': 'Metaverse Square', 'x': 0, 'y': 0, 'size': 30},
    'spawn': {'x': 3.0, 'y': 0.0, 'radius': 4.0, 'yaw': 270},
    'bounds': [-60, -60, 60, 60],
    'buildings': [
        {'id': 'MeetingParcel', 'name': 'Town Hall', 'model': 'world/townhall.glb', 'x': HALL_AT[0], 'y': HALL_AT[1], 'yaw': 0, 'height': HALL['h'],
         'footprint': [HALL['x0'], HALL['y0'], HALL['x1'], HALL['y1']],
         'solids': at(*HALL_AT, hall_solids),
         'doors': [
            {'id': 'TownHall-main', 'name': 'Town Hall, main door', 'from': 'MVS', 'to': 'MeetingParcel', 'x': HALL_AT[0] - 12, 'y': HALL_AT[1], 'axis': 'x', 'width': 1.8, 'outside': -1},
         ],
         'rooms': [
            {'id': 'MeetingRoom', 'name': 'Meeting room', 'x': 0, 'y': 0, 'footprint': [-6, -5, 6, 5], 'height': HALL['h'], 'acoustics': ACOUSTICS,
             'door': {'id': 'MeetingRoom-door', 'name': 'Meeting room door', 'from': 'MeetingParcel', 'to': 'MeetingRoom', 'x': HALL_AT[0] - 6, 'y': HALL_AT[1], 'axis': 'x', 'width': 1.8, 'outside': -1},
             'seats': [{'id': 'MeetingRoom-' + s, 'x': sx, 'y': sy, 'yaw': yaw} for (s, sx, sy, yaw) in SEATS]}]},
        {'id': 'Library', 'name': 'Library', 'model': 'world/library.glb', 'x': 0.0, 'y': 30.0, 'yaw': 0, 'height': 3.4, 'footprint': [-6, -4.5, 6, 4.5], 'solids': at(0, 30, library_solids),
         'doors': [{'id': 'Library-door', 'name': 'Library door', 'from': 'MVS', 'to': 'Library', 'x': 0.0, 'y': 30 - 4.5, 'axis': 'y', 'width': 1.6, 'outside': -1}], 'rooms': []},
        {'id': 'Cafe', 'name': 'Cafe', 'model': 'world/cafe.glb', 'x': -30.0, 'y': 0.0, 'yaw': 0, 'height': 3.2, 'footprint': [-4.5, -4, 4.5, 4], 'solids': at(-30, 0, cafe_solids),
         'doors': [{'id': 'Cafe-door', 'name': 'Cafe door', 'from': 'MVS', 'to': 'Cafe', 'x': -30 + 4.5, 'y': 0.0, 'axis': 'x', 'width': 1.6, 'outside': 1}], 'rooms': []},
    ],
}
json.dump(world, open(os.path.join(OUT, 'world.json'), 'w'), indent=1)
print('wrote', sorted(os.listdir(OUT)))
