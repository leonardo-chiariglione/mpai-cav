import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { CSS2DRenderer, CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js';
import { clone as cloneSkinned } from 'three/addons/utils/SkeletonUtils.js';

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const BOT = params.get('bot'), AUTO = params.get('autojoin');
const D2R = Math.PI / 180;

// ------------------------------------------------------------------ avatars (loading and preparing)
const loader = new GLTFLoader();
const gltfs = new Map();   // file -> Promise<gltf>
const loadGltf = file => { if (!gltfs.has(file)) gltfs.set(file, loader.loadAsync(file)); return gltfs.get(file); };

// A prepared avatar: opacity material (hair, lashes) transparent with an alpha test; arms brought down
// from the T-pose; scaled to 1.7 m as posed by the skeleton and standing on y = 0.
function lowerArms(root) {            // each upper arm turned until it points down (Biped and Ready Player Me names)
  const found = {};
  root.traverse(o => {
    if (!o.isBone) return;
    const n = o.name;
    if (/^(Left|Right)Arm$/.test(n)) found[n[0] + 'a'] = o;
    else if (/^(Left|Right)ForeArm$/.test(n)) found[n[0] + 'f'] = o;
    else if (/L.?UpperArm$/.test(n)) found.La = o; else if (/R.?UpperArm$/.test(n)) found.Ra = o;
    else if (/L.?Forearm$/i.test(n)) found.Lf = o; else if (/R.?Forearm$/i.test(n)) found.Rf = o;
  });
  root.updateMatrixWorld(true);
  for (const side of ['L', 'R']) {
    const arm = found[side + 'a'], fore = found[side + 'f'];
    if (!arm || !fore) continue;
    const a = arm.getWorldPosition(new THREE.Vector3()), f = fore.getWorldPosition(new THREE.Vector3());
    const turn = new THREE.Quaternion().setFromUnitVectors(f.sub(a).normalize(), new THREE.Vector3(0, -1, 0));
    const world = turn.multiply(arm.getWorldQuaternion(new THREE.Quaternion()));
    const parent = arm.parent.getWorldQuaternion(new THREE.Quaternion()).invert();
    arm.quaternion.copy(parent.multiply(world));
    root.updateMatrixWorld(true);
  }
}
// Seated pose: each bone is turned so that it points to its child along a wanted direction (world = the
// avatar's own frame before it is placed: Y up, Z ahead, X to its left). Parents first.
const SEAT_H = 0.46, SEAT_FWD = 0.25, EYE_H = 1.22, HEAD_LABEL = 1.68;   // m; SEAT_FWD: how far in front of the seat's point the avatar sits
const BONES = {
  thigh: /(Thigh|UpLeg)$/, calf: /(Calf|Leg)$/, foot: /Foot$/, arm: /(UpperArm|Arm)$/, fore: /Fore[aA]rm$/, hand: /Hand$/, pelvis: /(Pelvis|Hips)$/
};
function sideBone(root, kind, side) {
  let found = null;
  root.traverse(o => {
    if (found || !o.isBone) return;
    const n = o.name.replace(/^mixamorig:?/, '');
    const isL = /(^|[ _.])L([ _.]|eft)/.test(n) || /^Left/.test(n), isR = /(^|[ _.])R([ _.]|ight)/.test(n) || /^Right/.test(n);
    if ((side === 'L' ? isL : isR) && BONES[kind].test(n) && !(kind === 'calf' && /UpLeg|Thigh/.test(n)) && !(kind === 'arm' && /Fore/i.test(n))) found = o;
  });
  return found;
}
function aim(root, bone, child, dir) {
  root.updateMatrixWorld(true);
  const a = bone.getWorldPosition(new THREE.Vector3()), c = child.getWorldPosition(new THREE.Vector3());
  const q = new THREE.Quaternion().setFromUnitVectors(c.sub(a).normalize(), dir.clone().normalize());
  const wq = q.multiply(bone.getWorldQuaternion(new THREE.Quaternion()));
  bone.quaternion.copy(bone.parent.getWorldQuaternion(new THREE.Quaternion()).invert().multiply(wq));
  root.updateMatrixWorld(true);
}
const ARM_DIR = new THREE.Vector3(0, -1, 0.28), FORE_DIR = new THREE.Vector3(0, -0.2, 1);
function sit(root) {
  for (const side of ['L', 'R']) {
    const th = sideBone(root, 'thigh', side), ca = sideBone(root, 'calf', side), ft = sideBone(root, 'foot', side);
    const ar = sideBone(root, 'arm', side), fo = sideBone(root, 'fore', side), ha = sideBone(root, 'hand', side);
    const out = Math.sign(th ? th.getWorldPosition(new THREE.Vector3()).x : 1) * 0.12;
    if (th && ca) aim(root, th, ca, new THREE.Vector3(out, 0, 1));
    if (ca && ft) aim(root, ca, ft, new THREE.Vector3(out * 0.5, -1, -0.02));
    const o2 = Math.sign(ar ? ar.getWorldPosition(new THREE.Vector3()).x : 1) * 0.12;
    if (ar && fo) aim(root, ar, fo, new THREE.Vector3(ARM_DIR.x + o2, ARM_DIR.y, ARM_DIR.z));
    if (fo && ha) aim(root, fo, ha, new THREE.Vector3(-o2 * 0.8, FORE_DIR.y, FORE_DIR.z));
  }
}
// The secretary types the minutes: the arms come forward to a laptop on the table; the forearms and hands tap while a minute is being written.
function typistPose(root) {
  const arms = [];
  const axisOf = b => new THREE.Vector3(1, 0, 0).applyQuaternion(b.getWorldQuaternion(new THREE.Quaternion()).invert());   // the side axis of the avatar, in the bone's own frame: fingers and wrists bend about it
  for (const side of ['L', 'R']) {
    const ar = sideBone(root, 'arm', side), fo = sideBone(root, 'fore', side), ha = sideBone(root, 'hand', side);
    if (!ar || !fo) continue;
    const o = Math.sign(ar.getWorldPosition(new THREE.Vector3()).x) * 0.1;
    aim(root, ar, fo, new THREE.Vector3(o, -0.5, 0.75));                 // the upper arm hangs, the elbow near the body
    if (ha) { aim(root, fo, ha, new THREE.Vector3(-o * 0.6, 0.5, 1));        // the forearm forward and a little up to the wrist, a little in toward the keys
      ha.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(axisOf(ha), 0.35)); }    // and the wrist bent down, so that the hand lies flat over the keys
    // The fingers: bones Finger0 (thumb) to Finger4, each of three joints (Finger1, Finger11, Finger12). They rest a little curled, as on keys.
    const fingers = [];
    const prefix = new RegExp('_' + side + '_Finger(\\d)(\\d)?$');
    root.traverse(o2 => {
      const m = o2.isBone && prefix.exec(o2.name);
      if (m) fingers.push({ bone: o2, finger: +m[1], joint: m[2] ? +m[2] : 0 });
    });
    root.updateMatrixWorld(true);
    for (const f of fingers) { f.base = f.bone.quaternion.clone(); f.axis = axisOf(f.bone); }
    arms.push({ fo, ha, fingers, foBase: fo.quaternion.clone(), haBase: ha?.quaternion.clone(), foAxis: axisOf(fo), haAxis: ha && axisOf(ha), side: side === 'L' ? 0 : 1 });
  }
  for (const [re, ang] of [[/(^|[ _.:])Neck$/, 0.14], [/(^|[ _.:])Head$/, 0.3]]) {          // the head bent toward the keys
    let b = null; root.traverse(o => { if (!b && o.isBone && re.test(o.name.replace(/^mixamorig:?/, ''))) b = o; });
    if (!b) continue;
    root.updateMatrixWorld(true);
    b.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(axisOf(b), ang));
  }
  return arms;
}
// One keystroke of one finger: a quick press and release, at random moments (a hash of the slot, the finger and the hand); the more it types, the more strokes.
function stroke(t, id, amp) {
  const x = t * 8 + id * 0.37, k = Math.floor(x), u = x - k;
  const h = Math.sin(k * 12.9898 + id * 78.233) * 43758.5453, r = h - Math.floor(h);
  return r < 0.3 * amp ? Math.sin(Math.PI * u) ** 2 : 0;
}
function typistTap(arms, t, amp) {
  const q = new THREE.Quaternion(), REST = [0.22, 0.3, 0.18], TAP = [0.3, 0.4, 0.25];            // radians for the three joints of a finger
  for (const a of arms) {
    let press = 0;
    for (const f of a.fingers) {
      const id = a.side * 5 + f.finger, s = f.finger === 0 ? 0.25 * stroke(t, id, amp) : stroke(t, id, amp);
      press += s;
      const rest = f.finger === 0 ? 0.35 : 1;                                                    // the thumb rests nearly straight
      f.bone.quaternion.copy(f.base).multiply(q.setFromAxisAngle(f.axis, REST[f.joint] * rest + TAP[f.joint] * s));
    }
    // the hand and forearm: calm, a slow drift and a small dip with the keys pressed
    a.fo.quaternion.copy(a.foBase).multiply(q.setFromAxisAngle(a.foAxis, 0.012 * Math.sin(t * 1.3 + a.side * 2) + 0.01 * amp * Math.sin(t * 3.1 + a.side)));
    if (a.ha) a.ha.quaternion.copy(a.haBase).multiply(q.setFromAxisAngle(a.haAxis, 0.03 * Math.sin(t * 0.9 + a.side) + 0.018 * Math.min(press, 2)));
  }
}function makeLaptop() {                    // a keyboard on the table in front of the seat (a screen would hide the secretary from the others); origin on the floor at the seat's point
  const g = new THREE.Group(), body = new THREE.MeshStandardMaterial({ color: 0x1b1f25, roughness: 0.5, metalness: 0.3 }), keys = new THREE.MeshStandardMaterial({ color: 0xc9ced6, roughness: 0.6 });
  const kb = bx(0.44, 0.02, 0.15, body); kb.position.set(0, 0.79, 0.67); g.add(kb);
  const kk = bx(0.41, 0.006, 0.12, keys); kk.position.set(0, 0.803, 0.67); g.add(kk);
  return g;
}
async function makeAvatar(file, seated = false, typist = false) {
  const gltf = await loadGltf(file);
  const root = cloneSkinned(gltf.scene);
  const jaws = [];
  root.traverse(o => {
    if (o.isMesh) {
      o.frustumCulled = false;
      for (const m of [].concat(o.material)) if (/opacity/i.test(m.name)) { m.transparent = true; m.alphaTest = 0.35; m.side = THREE.DoubleSide; m.depthWrite = true; }
      if (o.morphTargetDictionary) for (const [k, i] of Object.entries(o.morphTargetDictionary)) if (/jawopen$/i.test(k)) jaws.push([o, i]);
    }
  });
  lowerArms(root);
  root.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(root, true);
  root.scale.setScalar(1.7 / (box.max.y - box.min.y)); root.updateMatrixWorld(true);
  box.setFromObject(root, true); root.position.y -= box.min.y;
  const group = new THREE.Group(); group.add(root);
  if (seated) {
    group.updateMatrixWorld(true);
    sit(root);
    if (typist) group.userData.typing = typistPose(root);
    const pel = sideBone(root, 'pelvis', 'L') || (() => { let f; root.traverse(o => { if (!f && o.isBone && BONES.pelvis.test(o.name)) f = o; }); return f; })();
    const ft = sideBone(root, 'foot', 'L');
    const fy = ft.getWorldPosition(new THREE.Vector3()).y, pp = pel.getWorldPosition(new THREE.Vector3());
    root.position.y -= fy - 0.075;                 // feet (ankles) on the floor
    root.position.z += SEAT_FWD - pp.z; root.position.x -= pp.x;   // the pelvis over the seat
    root.updateMatrixWorld(true);
    group.userData.hipY = pel.getWorldPosition(new THREE.Vector3()).y;
  }
  return { group, jaws };
}

// ------------------------------------------------------------------ state
let ws = null, myName = '', myAvatar = '', mySeat = -1, FOV = 90, avatarList = [];
let seats = [], you = '';
const people = new Map();     // id -> { id, avatar, group, jaws, label, jaw, speaking, token }
const chosen = { avatar: null };

// ------------------------------------------------------------------ join screen
let prevR = null, prevSc = null, prevCam = null, prevChain = Promise.resolve();
function renderPreview(a, tile) {          // fallback: render it, then give the PNG to the server
  prevChain = prevChain.then(async () => {
    try {
      if (!prevR) {
        prevR = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
        prevR.setSize(300, 380, false); prevR.outputColorSpace = THREE.SRGBColorSpace; prevR.toneMapping = THREE.ACESFilmicToneMapping;
        prevSc = new THREE.Scene();
        prevSc.environment = new THREE.PMREMGenerator(prevR).fromScene(new RoomEnvironment(), 0.04).texture;
        prevSc.add(new THREE.HemisphereLight(0xffffff, 0x555555, 1.0));
        const dl = new THREE.DirectionalLight(0xffffff, 1.5); dl.position.set(2, 3, 4); prevSc.add(dl);
        prevCam = new THREE.PerspectiveCamera(24, 300 / 380, 0.1, 20);
        prevCam.position.set(0, 1.5, 1.7); prevCam.lookAt(0, 1.38, 0);
      }
      const av = await makeAvatar(a.file);
      prevSc.add(av.group); prevR.render(prevSc, prevCam);
      const url = prevR.domElement.toDataURL('image/png');
      prevSc.remove(av.group);
      const img = document.createElement('img'); img.src = url;
      tile.querySelector('.ph, img').replaceWith(img);
      prevR.domElement.toBlob(blob => { if (blob) fetch('/api/thumb/' + encodeURIComponent(a.id), { method: 'POST', body: blob }).catch(() => {}); }, 'image/png');
    } catch (e) { console.warn('preview failed', a.id, e); }
  });
}
function previews(list) {
  for (const a of list) {
    const tile = document.querySelector(`.tile[data-id="${a.id}"]`);
    const img = document.createElement('img');
    img.onerror = () => renderPreview(a, tile);     // no thumbnail yet (404)
    img.src = '/thumbs/' + encodeURIComponent(a.id) + '.png';
    tile.querySelector('.ph').replaceWith(img);
  }
}
async function showJoin() {
  avatarList = await (await fetch('/api/avatars')).json();
  try { if ((await (await fetch('/api/info')).json()).codeRequired && $('code')) $('code').style.display = ''; } catch {}
  const tiles = $('tiles');
  for (const a of avatarList) {
    const t = document.createElement('div'); t.className = 'tile'; t.dataset.id = a.id;
    t.innerHTML = `<div class="ph"></div><div></div>`; t.lastChild.textContent = a.label || a.id;
    t.onclick = () => { chosen.avatar = a.id; document.querySelectorAll('.tile').forEach(x => x.classList.toggle('sel', x === t)); $('err').textContent = 'You will be: ' + (a.label || a.id); };
    tiles.appendChild(t);
  }
  chosen.avatar = null;                                  // nobody is chosen for you: the avatar is picked by a click
  previews(avatarList);
}
$('joinBtn').onclick = () => {
  const n = $('name').value.trim();
  if (!n) { $('err').textContent = 'Type your name.'; return; }
  if (!chosen.avatar) { $('err').textContent = 'Click the avatar you want to be.'; return; }
  $('err').textContent = ''; audio(); start(n, chosen.avatar, true);
};
$('name').addEventListener('keydown', e => { if (e.key === 'Enter') $('joinBtn').click(); });

// ------------------------------------------------------------------ the room (three.js: x = x, y = z, z = -y)
let renderer, labels, scene3, camera, view;
function initStage() {
  view = $('view');
  renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.outputColorSpace = THREE.SRGBColorSpace; renderer.toneMapping = THREE.ACESFilmicToneMapping;
  view.appendChild(renderer.domElement);
  labels = new CSS2DRenderer();
  Object.assign(labels.domElement.style, { position: 'absolute', top: '0', left: '0', pointerEvents: 'none' });
  view.appendChild(labels.domElement);
  scene3 = new THREE.Scene(); scene3.background = new THREE.Color(0x2b323b);
  scene3.environment = new THREE.PMREMGenerator(renderer).fromScene(new RoomEnvironment(), 0.04).texture;
  scene3.add(new THREE.HemisphereLight(0xffffff, 0x444444, 0.8));
  const sun = new THREE.DirectionalLight(0xffffff, 1.2); sun.position.set(2, 5, 3); scene3.add(sun);
  const floor = new THREE.Mesh(new THREE.CircleGeometry(4.2, 64), new THREE.MeshStandardMaterial({ color: 0x59534b, roughness: 0.9 }));
  floor.rotation.x = -Math.PI / 2; scene3.add(floor);
  const rug = new THREE.Mesh(new THREE.RingGeometry(0.6, 1.9, 64), new THREE.MeshStandardMaterial({ color: 0x3d4a5c, roughness: 1 }));
  rug.rotation.x = -Math.PI / 2; rug.position.y = 0.004; scene3.add(rug);
  const wall = new THREE.Mesh(new THREE.CylinderGeometry(4.2, 4.2, 3.2, 64, 1, true), new THREE.MeshStandardMaterial({ color: 0x38414d, roughness: 1, side: THREE.BackSide }));
  wall.position.y = 1.6; scene3.add(wall);
  const table = new THREE.Mesh(new THREE.CylinderGeometry(0.6, 0.6, 0.06, 64), new THREE.MeshStandardMaterial({ color: 0x8a6a48, roughness: 0.5 }));
  table.position.y = 0.75; scene3.add(table);
  const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.2, 0.72, 24), new THREE.MeshStandardMaterial({ color: 0x4a3a2a, roughness: 0.7 }));
  leg.position.y = 0.36; scene3.add(leg);
  camera = new THREE.PerspectiveCamera(60, 1, 0.05, 60);
  camera.rotation.order = 'YXZ';
  fit(); addEventListener('resize', fit);
}
function fit() {
  const w = view.clientWidth, h = view.clientHeight;
  renderer.setSize(w, h); labels.setSize(w, h);
  camera.aspect = w / h;
  camera.fov = 2 * Math.atan(Math.tan(FOV * D2R / 2) / camera.aspect) / D2R;   // FOV is horizontal
  camera.updateProjectionMatrix();
}
const toThree = (x, y, z) => new THREE.Vector3(x, z, -y);
const eyePos = s => toThree(s.x + Math.cos(s.yaw) * SEAT_FWD, s.y + Math.sin(s.yaw) * SEAT_FWD, EYE_H);   // seated eyes, a little in front of the seat's point
const yawToRotY = yaw => Math.atan2(Math.cos(yaw), -Math.sin(yaw));     // avatar faces +Z at 0

// ------------------------------------------------------------------ view turning
let turn = 0, drag = null, keyDir = 0;   // turn > 0: to the right, radians
const MAXTURN = 70 * D2R;
function wireTurn() {
  const el = renderer.domElement;
  addEventListener('pointerdown', e => { if (view.contains(e.target)) { drag = { x: e.clientX, turn }; } });
  addEventListener('pointermove', e => { if (drag) turn = Math.max(-MAXTURN, Math.min(MAXTURN, drag.turn + (e.clientX - drag.x) * 0.25 * D2R)); });
  addEventListener('pointerup', () => { drag = null; });
  addEventListener('keydown', e => { if (document.activeElement?.tagName === 'INPUT') return; if (e.key === 'ArrowLeft') keyDir = -1; if (e.key === 'ArrowRight') keyDir = 1; });
  addEventListener('keyup', e => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') keyDir = 0; });
}

// ------------------------------------------------------------------ scene updates
function setSpeakingUi(p) {
  p.label?.element.classList.toggle('sp', !!p.speaking);
}
const chairMat = new THREE.MeshStandardMaterial({ color: 0x2f3a4a, roughness: 0.8 }), legMat = new THREE.MeshStandardMaterial({ color: 0x1c1f24, roughness: 0.6 });
const bx = (w, h, d, m) => new THREE.Mesh(new THREE.BoxGeometry(w, h, d), m);
function makeChair() {                      // origin on the floor at the seat's point, facing +Z (the table)
  const g = new THREE.Group(), c = new THREE.Group(); c.position.z = SEAT_FWD - 0.02; g.add(c);
  const seat = bx(0.46, 0.05, 0.46, chairMat); seat.position.y = SEAT_H - 0.025; c.add(seat);
  const back = bx(0.44, 0.5, 0.04, chairMat); back.position.set(0, SEAT_H + 0.27, -0.22); c.add(back);
  for (const [x, z] of [[-0.2, -0.2], [0.2, -0.2], [-0.2, 0.2], [0.2, 0.2]]) { const l = bx(0.04, SEAT_H - 0.05, 0.04, legMat); l.position.set(x, (SEAT_H - 0.05) / 2, z); c.add(l); }
  return g;
}
async function addPerson(s) {
  const p = { id: s.id, avatar: s.avatar, speaking: false, jaw: 0, group: null, jaws: [], label: null, token: 0, chair: makeChair() };
  people.set(s.id, p); scene3.add(p.chair);
  if (s.id === you) { placePerson(p); return; }      // the own avatar is never drawn: only its chair
  try {
    const typist = s.id === 'Secretary';
    const av = await makeAvatar((avatarList.find(a => a.id === s.avatar) || {}).file || `/avatars/${s.avatar}.glb`, true, typist);
    if (people.get(s.id) !== p) return;
    p.group = av.group; p.jaws = av.jaws; p.typing = av.group.userData.typing; p.typeAmp = 0;
    if (typist) av.group.add(makeLaptop());
    const el = document.createElement('div'); el.className = 'label'; el.textContent = s.id;
    p.label = new CSS2DObject(el); p.label.position.set(0, HEAD_LABEL, SEAT_FWD); av.group.add(p.label);
    p.group.visible = s.id !== you;
    scene3.add(av.group);
    placePerson(p);
  } catch (e) { console.warn('avatar not loaded', s.avatar, e); }
}
function placePerson(p) {
  const s = seats.find(x => x.id === p.id); if (!s) return;
  p.chair.position.copy(toThree(s.x, s.y, s.z || 0)); p.chair.rotation.y = yawToRotY(s.yaw);
  if (!p.group) return;
  p.group.position.copy(toThree(s.x, s.y, s.z || 0));
  p.group.rotation.y = yawToRotY(s.yaw);
  p.group.visible = s.id !== you;
  p.speaking = !!s.speaking; setSpeakingUi(p);
}
function onScene(m) {
  you = m.you; seats = m.seats;
  const ids = new Set(seats.map(s => s.id));
  for (const [id, p] of people) if (!ids.has(id)) { if (p.group) scene3.remove(p.group); scene3.remove(p.chair); p.label?.element.remove(); people.delete(id); speakers.delete(id); }
  for (const s of seats) { if (!people.has(s.id)) addPerson(s); else placePerson(people.get(s.id)); }
  const me = seats.find(s => s.id === you);
  if (me) { mySeatInfo = me; }
  updateListener();
  $('roster').innerHTML = '';
  for (const s of seats) {
    const c = document.createElement('span'); c.className = 'chip' + (s.speaking ? ' sp' : '') + (s.id === you ? ' me' : ''); c.textContent = s.id;
    $('roster').appendChild(c);
  }
  for (const s of seats) updatePanner(s);
}
let mySeatInfo = null, typingUntil = 0;     // typingUntil: the secretary types until then (ms, performance.now)

// ------------------------------------------------------------------ audio: playback (HRTF) and capture
let actx = null; const speakers = new Map();   // id -> { panner, analyser, next, buf }
function audio() { if (!actx) actx = new (window.AudioContext || window.webkitAudioContext)(); if (actx.state === 'suspended') actx.resume().catch(() => {}); return actx; }
for (const ev of ['pointerdown', 'keydown', 'touchstart']) addEventListener(ev, () => { if (actx && actx.state === 'suspended') actx.resume().catch(() => {}); }, { passive: true });
function updateListener() {
  if (!actx || !mySeatInfo) return;
  const L = actx.listener, p = eyePos(mySeatInfo);
  const th = Math.atan2(-Math.cos(mySeatInfo.yaw), Math.sin(mySeatInfo.yaw)) - turn;   // same as the camera
  const f = new THREE.Vector3(-Math.sin(th), 0, -Math.cos(th));
  if (L.positionX) {
    L.positionX.value = p.x; L.positionY.value = p.y; L.positionZ.value = p.z;
    L.forwardX.value = f.x; L.forwardY.value = 0; L.forwardZ.value = f.z; L.upX.value = 0; L.upY.value = 1; L.upZ.value = 0;
  } else { L.setPosition(p.x, p.y, p.z); L.setOrientation(f.x, 0, f.z, 0, 1, 0); }
}
function updatePanner(s) {
  const sp = speakers.get(s.id); if (!sp) return;
  const p = toThree(s.x + Math.cos(s.yaw) * SEAT_FWD, s.y + Math.sin(s.yaw) * SEAT_FWD, 1.3);
  if (sp.panner.positionX) { sp.panner.positionX.value = p.x; sp.panner.positionY.value = p.y; sp.panner.positionZ.value = p.z; }
  else sp.panner.setPosition(p.x, p.y, p.z);
}
function speaker(id) {
  let sp = speakers.get(id); if (sp) return sp;
  const ctx = audio();
  const panner = ctx.createPanner(); panner.panningModel = 'HRTF'; panner.distanceModel = 'inverse'; panner.refDistance = 1; panner.rolloffFactor = 0.6;
  const analyser = ctx.createAnalyser(); analyser.fftSize = 512;
  const gain = ctx.createGain(); gain.gain.value = 1;
  gain.connect(analyser); gain.connect(panner); panner.connect(ctx.destination);
  sp = { panner, analyser, gain, next: 0, td: new Float32Array(analyser.fftSize), lastData: 0 };
  speakers.set(id, sp);
  const s = seats.find(x => x.id === id); if (s) updatePanner(s);
  return sp;
}
const stats = { played: 0, chunks: 0 };
const diagInfo = { mic: 'not started', sent: 0, got: 0, worklet: '-' };
function playChunk(id, dv, off) {
  const ctx = audio(); const sp = speaker(id);
  const n = (dv.byteLength - off) >> 1; if (n <= 0) return;
  const buf = ctx.createBuffer(1, n, 16000), ch = buf.getChannelData(0);
  for (let i = 0; i < n; i++) ch[i] = dv.getInt16(off + 2 * i, true) / 32768;
  const src = ctx.createBufferSource(); src.buffer = buf; src.connect(sp.gain);
  const now = ctx.currentTime;
  if (sp.next < now + 0.01) sp.next = now + 0.12;       // jitter buffer
  src.start(sp.next); sp.next += buf.duration; sp.lastData = performance.now();
  stats.chunks++; stats.played += buf.duration;
}
function levelOf(id) {
  const sp = speakers.get(id); if (!sp || !actx || actx.state !== 'running') return 0;
  sp.analyser.getFloatTimeDomainData(sp.td);
  let s = 0; for (let i = 0; i < sp.td.length; i++) s += sp.td[i] * sp.td[i];
  return Math.sqrt(s / sp.td.length);
}


// A browser keeps a page silent until the person has clicked on it: say so, and turn the sound on at any click.
const soundBar = document.createElement('div');
soundBar.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:50;background:#b3261e;color:#fff;padding:10px 14px;font:600 15px system-ui,sans-serif;text-align:center;cursor:pointer;display:none';
soundBar.textContent = 'No sound yet - click here (or anywhere) to turn the sound on';
soundBar.onclick = () => audio();
document.body.appendChild(soundBar);
setInterval(() => { soundBar.style.display = (joined && actx && actx.state !== 'running') ? 'block' : 'none'; if (actx && actx.state === 'suspended') actx.resume().catch(() => {}); }, 500);

// WHAT THE PAGE KNOWS ABOUT SOUND, for whoever has to tell what is wrong: the sound of the page, the microphone, what was sent and what came.
const diagEl = document.createElement('div');
diagEl.style.cssText = 'position:fixed;left:0;bottom:0;z-index:40;background:#000a;color:#cfd6df;padding:3px 8px;font:12px ui-monospace,Consolas,monospace;max-width:70vw';
document.body.appendChild(diagEl);
setInterval(() => { diagEl.style.display = joined ? 'block' : 'none'; diagEl.textContent = `sound: ${actx ? actx.state : 'none'} | mic: ${diagInfo.mic} | sent ${diagInfo.sent} | received ${diagInfo.got}`; }, 700);
let muted = false, micLevel = 0;
function sendPcm(f32) {
  if (!ws || ws.readyState !== 1) return;
  const out = new DataView(new ArrayBuffer(1 + 2 * f32.length)); out.setUint8(0, 1);
  let peak = 0;
  for (let i = 0; i < f32.length; i++) { const v = Math.max(-1, Math.min(1, f32[i])); peak = Math.max(peak, Math.abs(v)); out.setInt16(1 + 2 * i, v < 0 ? v * 32768 : v * 32767, true); }
  micLevel = peak; diagInfo.sent++;
  ws.send(out.buffer);
}
const WORKLET = `class P extends AudioWorkletProcessor{constructor(){super();this.b=new Float32Array(1600);this.n=0;this.pos=0;this.last=0;}
process(inp){const c=inp[0]&&inp[0][0];if(!c)return true;const r=sampleRate/16000;
for(;;){const i=Math.floor(this.pos);if(i+1>=c.length)break;const f=this.pos-i;const a=i<0?this.last:c[i];this.b[this.n++]=a*(1-f)+c[i+1]*f;this.pos+=r;
if(this.n===1600){this.port.postMessage(this.b.slice());this.n=0;}}
this.pos-=c.length;this.last=c[c.length-1];return true;}}
registerProcessor('p16',P);`;
let micStream = null;
async function startMic() {
  const ctx = audio();
  diagInfo.mic = 'asking the browser...';
  try { micStream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, channelCount: 1 } }); }
  catch (e) { diagInfo.mic = 'REFUSED: ' + (e.name || '') + ' ' + (e.message || ''); throw e; }
  diagInfo.mic = 'ok: ' + (micStream.getAudioTracks()[0]?.label || 'device') + ' (' + ctx.sampleRate + ' Hz)';
  const src = ctx.createMediaStreamSource(micStream);
  if (ctx.audioWorklet) {
    const url = URL.createObjectURL(new Blob([WORKLET], { type: 'text/javascript' }));
    try { await ctx.audioWorklet.addModule(url); diagInfo.worklet = 'ok'; } catch (e) { diagInfo.worklet = 'FAILED: ' + e.message; diagInfo.mic += ' - worklet failed'; throw e; }
    const node = new AudioWorkletNode(ctx, 'p16', { numberOfOutputs: 0 });
    node.port.onmessage = e => { if (!muted) sendPcm(e.data); };
    src.connect(node);
  } else {                                    // fallback
    const sp = ctx.createScriptProcessor(4096, 1, 1), ratio = ctx.sampleRate / 16000; let pos = 0, buf = [];
    sp.onaudioprocess = e => {
      const c = e.inputBuffer.getChannelData(0);
      for (; pos + 1 < c.length; pos += ratio) { const i = Math.floor(pos), f = pos - i; buf.push(c[i] * (1 - f) + c[i + 1] * f); }
      pos -= c.length;
      while (buf.length >= 1600 && !muted) sendPcm(Float32Array.from(buf.splice(0, 1600)));
      if (muted) buf = [];
    };
    src.connect(sp); sp.connect(ctx.destination);
  }
}
// synthetic speech-like signal for ?bot=: bursts of band-limited noise, ~1 s on, ~2 s off
function startBot() {
  let t = Math.random() * 3, lp1 = 0, lp2 = 0, ph = 0;
  setInterval(() => {
    const f = new Float32Array(1600);
    for (let i = 0; i < 1600; i++) {
      t += 1 / 16000;
      const cyc = t % 3, on = cyc < 1;
      if (!on) continue;
      const w = (Math.random() * 2 - 1);
      lp1 += 0.35 * (w - lp1); lp2 += 0.05 * (lp1 - lp2);       // noise, band-limited
      const syl = 0.5 + 0.5 * Math.sin(2 * Math.PI * 4 * t);     // syllable rhythm
      ph += 2 * Math.PI * 140 / 16000;
      f[i] = (0.9 * (lp1 - lp2) + 0.15 * Math.sin(ph)) * syl * Math.min(1, cyc * 20, (1 - cyc) * 20 + 0.0) * 0.8;
    }
    let any = false; for (let i = 0; i < 1600; i += 40) if (Math.abs(f[i]) > 1e-4) { any = true; break; }
    if (any && !muted) sendPcm(f);
  }, 100);
}

// ------------------------------------------------------------------ connection
function log(who, text) {
  const d = document.createElement('div'); const b = document.createElement('b'); b.textContent = who + ': ';
  d.appendChild(b); d.appendChild(document.createTextNode(text)); $('chatlog').appendChild(d); $('chatlog').scrollTop = 1e9;
}
function status(t) { const s = $('status'); if (s) s.textContent = t; }
let joined = false;
function start(name, avatar, withMic) {
  myName = name; myAvatar = avatar;
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  ws = new WebSocket(`${proto}//${location.host}/ws`); ws.binaryType = 'arraybuffer';
  ws.onopen = () => ws.send(JSON.stringify({ t: 'join', name, avatar, code: params.get('code') || ($('code') ? $('code').value.trim() : ''), lang: params.get('lang') || ($('lang') ? $('lang').value : 'auto') }));
  ws.onclose = () => { status('disconnected'); if (!joined) $('err').textContent = $('err').textContent || 'Could not connect.'; };
  ws.onmessage = async ev => {
    if (typeof ev.data !== 'string') {
      const dv = new DataView(ev.data);
      if (dv.getUint8(0) === 2) {
        const n = dv.getUint8(1); const id = new TextDecoder().decode(new Uint8Array(ev.data, 2, n));
        diagInfo.got++; if (id !== you) playChunk(id, dv, 2 + n);
      }
      return;
    }
    const m = JSON.parse(ev.data);
    if (m.t === 'welcome') {
      joined = true; mySeat = m.seat; FOV = m.fov || 90; if (m.avatars?.length) avatarList = m.avatars;
      $('join').style.display = 'none'; $('app').style.display = 'block';
      if (!renderer) { initStage(); wireTurn(); renderer.setAnimationLoop(frame); }
      fit(); status(`${name} - ${avatar} - seat ${m.seat}`);
      if (m.minutes) $('minutes').textContent = m.minutes;
      if (BOT) { audio(); startBot(); $('muteBtn').textContent = 'Mute'; }
      else if (withMic) startMic().catch(e => { console.warn('microphone', e); status('no microphone: ' + e.message); });
    } else if (m.t === 'scene') onScene(m);
    else if (m.t === 'chat') log(m.from, m.text);
    else if (m.t === 'minutes') { const el = $('minutes'); const stick = el.scrollTop + el.clientHeight >= el.scrollHeight - 8; typingUntil = Math.max(performance.now(), typingUntil) + Math.min(6000, Math.max(0, m.text.length - el.textContent.length) * 45); el.textContent = m.text; if (stick) el.scrollTop = 1e9; }
    else if (m.t === 'error') { $('err').textContent = m.text; status(m.text); if (!joined) { try { ws.close(); } catch {} } }
  };
}
function sendChat() { const v = $('chatIn').value.trim(); if (v && ws?.readyState === 1) { ws.send(JSON.stringify({ t: 'chat', text: v })); $('chatIn').value = ''; } }
$('chatBtn').onclick = sendChat;
$('chatIn').addEventListener('keydown', e => { if (e.key === 'Enter') sendChat(); });
$('leaveBtn').onclick = () => { try { ws?.close(); } catch {} try { micStream?.getTracks().forEach(t => t.stop()); } catch {} location.href = location.pathname; };
$('muteBtn').onclick = () => { muted = !muted; $('muteBtn').textContent = muted ? 'Unmute' : 'Mute'; micStream?.getAudioTracks().forEach(t => t.enabled = !muted); };

// ------------------------------------------------------------------ the frame
const clock = new THREE.Clock();
function frame() {
  const dt = Math.min(clock.getDelta(), 0.1);
  if (keyDir) turn = Math.max(-MAXTURN, Math.min(MAXTURN, turn + keyDir * 1.2 * dt));
  else if (!drag) turn *= Math.exp(-dt * 0.8);                 // slowly back to the centre
  const me = mySeatInfo;
  if (me) {
  if (!window.abv.free) {
    camera.position.copy(eyePos(me));
    camera.rotation.set(-6 * D2R, Math.atan2(-Math.cos(me.yaw), Math.sin(me.yaw)) - turn, 0);
    updateListener();
  }
  }
  for (const p of people.values()) {
    if (!p.group) continue;
    if (p.typing) { p.typeAmp += ((performance.now() < typingUntil ? 1 : 0) - p.typeAmp) * Math.min(1, dt * 8); typistTap(p.typing, clock.elapsedTime, p.typeAmp); }
    const lvl = levelOf(p.id);
    const target = Math.min(1, lvl * 9);
    p.jaw += (target - p.jaw) * Math.min(1, dt * 18);
    for (const [mesh, i] of p.jaws) mesh.morphTargetInfluences[i] = p.jaw * 0.8;
  }
  $('meter').firstChild.style.width = Math.round(Math.min(1, micLevel * 1.5) * 100) + '%'; micLevel *= 0.85;
  renderer.render(scene3, camera); labels.render(scene3, camera);
}
window.abv = { free: false, people, speakers, stats, get turn() { return turn; }, set turn(v) { turn = v; }, get camera() { return camera; }, get scene3() { return scene3; } };

if (BOT || AUTO) { start(BOT || AUTO, params.get('avatar') || 'Persona1', !BOT); }
else showJoin();








