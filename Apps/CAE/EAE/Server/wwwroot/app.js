// The browser client of the Enhanced Audioconference. A listener hears each speaker of the room, separated from the others and placed in the direction they are; the room
// sends the audio of its microphone array. PROTOCOL.md says what is exchanged. Nothing here depends on how the server finds the speakers.
"use strict";
const $ = id => document.getElementById(id);
let ws = null, ctx = null, info = null, role = "", speakers = new Map(), seconds = 3;
const voices = new Map();                                 // id -> { panner, next }

(async () => {
  info = await (await fetch("/api/info")).json();
  $("codeRow").hidden = !info.codeRequired;
  $("where").textContent = location.host;
  const q = new URLSearchParams(location.search);
  if (q.get("name")) $("name").value = q.get("name");
  if (q.get("role")) $("role").value = q.get("role");
  if (q.get("autojoin") !== null) join();
})();
$("go").onclick = join;
$("leave").onclick = () => location.reload();

async function join() {
  const name = $("name").value.trim(); role = $("role").value;
  if (!name) { $("error").textContent = "choose a name"; return; }
  ctx = new (window.AudioContext || window.webkitAudioContext)();     // made on the click: the browser allows sound only after one
  await ctx.resume();
  ws = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws");
  ws.binaryType = "arraybuffer";
  ws.onopen = () => ws.send(JSON.stringify({ t: "join", name, role, code: $("code").value }));
  ws.onmessage = e => typeof e.data === "string" ? text(JSON.parse(e.data)) : audio(new Uint8Array(e.data));
  ws.onclose = () => { if ($("stage").style.display === "block") $("status").textContent = "disconnected"; };
}

function text(m) {
  if (m.t === "error") { $("error").textContent = m.text; ws.close(); return; }
  if (m.t === "welcome") {
    $("join").style.display = "none"; $("stage").style.display = "block"; seconds = m.segment;
    $("status").textContent = role === "room" ? "You are the room." : "You are listening.";
    if (role === "room") capture(m.channels, m.rate);
    draw();
  }
  if (m.t === "scene") {
    const now = performance.now();
    for (const s of m.speakers) {
      const old = speakers.get(s.id) || {};
      speakers.set(s.id, { ...s, until: s.speaking ? now + m.seconds * 1000 : (old.until || 0) });
      const v = voice(s.id); place(v.panner, s.azimuth, s.elevation);
    }
    $("status").textContent = `${m.speakers.length} speaker(s) found; the Module took ${m.processing} ms for ${m.seconds} s of the room.`;
  }
}

// A voice: a panner HRTF in the direction of the speaker. Room: x ahead, y to the left, z up; Web Audio: -z ahead, -x to the left, y up.
function voice(id) {
  let v = voices.get(id);
  if (!v) { const panner = new PannerNode(ctx, { panningModel: "HRTF", distanceModel: "inverse", refDistance: 1 }); panner.connect(ctx.destination); v = { panner, next: 0 }; voices.set(id, v); }
  return v;
}
function place(p, az, el) {
  const a = az * Math.PI / 180, e = el * Math.PI / 180, r = 2;
  p.positionX.value = -r * Math.cos(e) * Math.sin(a); p.positionY.value = r * Math.sin(e); p.positionZ.value = -r * Math.cos(e) * Math.cos(a);
}
function audio(b) {                                       // 0x02, length of the id, the id, 16-bit PCM mono 16 kHz
  if (role !== "listener" || b[0] !== 2) return;
  const id = new TextDecoder().decode(b.subarray(2, 2 + b[1]));
  const pcm = new Int16Array(b.buffer.slice(b.byteOffset + 2 + b[1], b.byteOffset + b.byteLength));
  const buffer = ctx.createBuffer(1, pcm.length, 16000), data = buffer.getChannelData(0);
  for (let i = 0; i < pcm.length; i++) data[i] = pcm[i] / 32768;
  const v = voice(id), source = ctx.createBufferSource(); source.buffer = buffer; source.connect(v.panner);
  v.next = Math.max(v.next, ctx.currentTime + 0.15); source.start(v.next); v.next += buffer.duration;
}

// The room: the channels of the microphone array, brought to 16 kHz, interleaved, in chunks of 100 ms.
async function capture(channels, rate) {
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: { ideal: channels }, echoCancellation: false, noiseSuppression: false, autoGainControl: false } }); }
  catch (e) { $("status").textContent = "no microphone: " + e.message; return; }
  const got = stream.getAudioTracks()[0].getSettings().channelCount || 1;
  if (got !== channels) { $("status").textContent = `The microphone gives ${got} channel(s) and the room's array has ${channels}: nothing is sent.`; return; }
  const code = `class P extends AudioWorkletProcessor {
    constructor() { super(); this.n = ${channels}; this.ratio = sampleRate / ${rate}; this.pos = 0; this.acc = new Float32Array(${channels}); this.cnt = 0; this.out = []; }
    process(inputs) {
      const inp = inputs[0]; if (!inp || inp.length < this.n) return true;
      for (let i = 0; i < inp[0].length; i++) {
        for (let c = 0; c < this.n; c++) this.acc[c] += inp[c][i];
        this.cnt++; this.pos++;
        if (this.pos >= this.ratio) { this.pos -= this.ratio; for (let c = 0; c < this.n; c++) this.out.push(Math.max(-1, Math.min(1, this.acc[c] / this.cnt)) * 32767); this.acc.fill(0); this.cnt = 0; }
      }
      if (this.out.length >= 1600 * this.n) { const a = Int16Array.from(this.out.splice(0, 1600 * this.n)); this.port.postMessage(a.buffer, [a.buffer]); }
      return true;
    } }
    registerProcessor("array-capture", P);`;
  await ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([code], { type: "text/javascript" })));
  const node = new AudioWorkletNode(ctx, "array-capture", { channelCount: channels, channelCountMode: "explicit", numberOfOutputs: 0 });
  node.port.onmessage = e => { if (ws.readyState === 1) { const f = new Uint8Array(1 + e.data.byteLength); f[0] = 1; f.set(new Uint8Array(e.data), 1); ws.send(f); } };
  ctx.createMediaStreamSource(stream).connect(node);
  $("status").textContent = `Capturing ${channels} channels.`;
}

// The map: the room from above, the array at the centre, the speakers in their directions (x to the right, y up); a speaker lights while heard.
function draw() {
  const c = $("map"), g = c.getContext("2d"), w = c.width, cx = w / 2, R = w * 0.38, now = performance.now();
  g.clearRect(0, 0, w, w);
  g.strokeStyle = "#2c4a3d"; g.lineWidth = 1;
  for (const k of [0.33, 0.66, 1]) { g.beginPath(); g.arc(cx, cx, R * k, 0, 7); g.stroke(); }
  g.beginPath(); g.moveTo(cx - R, cx); g.lineTo(cx + R, cx); g.moveTo(cx, cx - R); g.lineTo(cx, cx + R); g.stroke();
  g.fillStyle = "#8fa89c"; g.beginPath(); g.arc(cx, cx, 7, 0, 7); g.fill();
  const list = $("speakers"); list.innerHTML = "";
  for (const [id, s] of speakers) {
    const on = now < s.until, a = s.azimuth * Math.PI / 180, e = Math.cos(s.elevation * Math.PI / 180);
    const x = cx + R * 0.9 * e * Math.cos(a), y = cx - R * 0.9 * e * Math.sin(a);
    g.fillStyle = on ? "#f2c14e" : "#4d6b5d"; g.beginPath(); g.arc(x, y, on ? 14 : 10, 0, 7); g.fill();
    g.fillStyle = "#10201a"; g.font = "bold 13px system-ui"; g.textAlign = "center"; g.textBaseline = "middle"; g.fillText(id, x, y + 1);
    const li = document.createElement("li"); li.textContent = `${id}  ${Math.round(s.azimuth)}°`; if (on) li.className = "on"; list.appendChild(li);
  }
  requestAnimationFrame(draw);
}