// THE BROWSER'S PHYSICAL LAYER. What the desktop RCA does with WASAPI, a webcam
// library and WebView2, the browser does itself: the microphone and camera
// through getUserMedia, sound through Web Audio, the avatar in its own page.
window.rca = (() => {
  let ctx = null;        // one AudioContext, opened by the Start click
  let mic = null;        // the microphone stream, asked for once
  let capture = null;    // the capture in progress, so it can be abandoned

  // THE CLICK THAT OPENS THE WAY: sound may play and the microphone may open
  // only after the person has done something.
  async function unlock() {
    ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
    if (ctx.state === 'suspended') await ctx.resume();
    mic = mic || await navigator.mediaDevices.getUserMedia(
      { audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
  }

  // ONE SPOKEN TURN. Waits for speech to start, keeps a moment from before it
  // so the first syllable is not lost, and ends after a pause. Returned as
  // 16 kHz, 16-bit mono PCM, base64 - what Speech Object Acquisition produces.
  function captureSpeech() {
    return new Promise(async resolve => {
      try { await unlock(); } catch (e) { resolve(null); return; }
      const source = ctx.createMediaStreamSource(mic);
      const node = ctx.createScriptProcessor(4096, 1, 1);
      const rate = ctx.sampleRate;
      const START = 0.020, QUIET = 0.012, PAUSE_MS = 900, MAX_MS = 20000, PREROLL = 3;
      const before = [], kept = [];
      let speaking = false, quietMs = 0, spokenMs = 0, done = false;

      function finish(keep) {
        if (done) return;
        done = true;
        node.onaudioprocess = null;
        try { node.disconnect(); source.disconnect(); } catch (e) {}
        capture = null;
        resolve(keep && speaking ? toPcm16k(kept, rate) : null);
      }
      capture = { abandon: () => finish(false) };

      node.onaudioprocess = e => {
        const x = e.inputBuffer.getChannelData(0);
        let sum = 0;
        for (let i = 0; i < x.length; i++) sum += x[i] * x[i];
        const rms = Math.sqrt(sum / x.length), ms = x.length * 1000 / rate;
        const copy = new Float32Array(x);
        if (!speaking) {
          before.push(copy);
          if (before.length > PREROLL) before.shift();
          if (rms > START) { speaking = true; kept.push(...before); }
          return;
        }
        kept.push(copy);
        spokenMs += ms;
        quietMs = rms < QUIET ? quietMs + ms : 0;
        if (quietMs >= PAUSE_MS || spokenMs >= MAX_MS) finish(true);
      };
      source.connect(node);
      node.connect(ctx.destination);   // a processor runs only when connected; its output is silence
    });
  }

  function abandonCapture() { if (capture) capture.abandon(); }

  function toPcm16k(chunks, rate) {
    const n = chunks.reduce((a, c) => a + c.length, 0);
    const all = new Float32Array(n);
    let o = 0;
    for (const c of chunks) { all.set(c, o); o += c.length; }
    const ratio = rate / 16000, m = Math.floor(n / ratio);
    const out = new Int16Array(m);
    for (let i = 0; i < m; i++) {                 // average over each step: a plain low-pass
      const a = Math.floor(i * ratio), b = Math.min(n, Math.floor((i + 1) * ratio));
      let s = 0;
      for (let j = a; j < b; j++) s += all[j];
      const v = b > a ? s / (b - a) : all[a];
      out[i] = Math.max(-1, Math.min(1, v)) * 32767;
    }
    return base64(new Uint8Array(out.buffer));
  }

  function base64(bytes) {
    let bin = '';
    for (let i = 0; i < bytes.length; i += 0x8000)
      bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }

  // A FACE, FROM THE CAMERA: one frame, as JPEG, base64.
  async function captureFrame() {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ video: true });
      const v = document.createElement('video');
      v.srcObject = s; v.muted = true; v.playsInline = true;
      await v.play();
      await new Promise(r => setTimeout(r, 400));   // let exposure settle
      const c = document.createElement('canvas');
      c.width = v.videoWidth; c.height = v.videoHeight;
      c.getContext('2d').drawImage(v, 0, 0);
      s.getTracks().forEach(t => t.stop());
      const url = c.toDataURL('image/jpeg', 0.9);
      return url.substring(url.indexOf(',') + 1);
    } catch (e) { console.error(e); return null; }
  }

  // THE AVATAR: the same message the desktop sends it through WebView2.
  function present(faceDescriptorsJson, speechWavBase64) {
    const frame = document.getElementById('avatar');
    if (!frame || !frame.contentWindow) return;
    frame.contentWindow.postMessage(
      { Kind: 'render', FaceDescriptors: faceDescriptorsJson || null, SpeechWavBase64: speechWavBase64 || '' },
      window.location.origin);
  }

  function focus(id) { const e = document.getElementById(id); if (e) e.focus(); }

  return { unlock, captureSpeech, abandonCapture, captureFrame, present, focus };
})();
