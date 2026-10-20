# ABV live demo — protocol between the browser client and the server (AVS)

Server: `D:\DI\Apps\ABV\AVS\Server` (ASP.NET, Kestrel). It serves `D:\DI\Apps\ABV\AVS\Server\wwwroot` as static files at `/`, the avatar files at `/avatars/<file>.glb`, `GET /api/avatars`, and a WebSocket at `/ws` (same origin). Text frames are JSON; binary frames are audio.

## The room (metres; x east, y north, z up; yaw in radians, 0 = facing +x, counter-clockwise positive)
A round table of radius 0.9 at the origin; 5 seats on a circle of radius 1.5, seat k at angle `k * 72°` (x = 1.5 cos, y = 1.5 sin); an avatar at a seat faces the origin (yaw = angle + π). The server assigns the seat; the client never computes seats. Avatars are modelled standing (Rocketbox T-pose → arms-down rest pose as in `AIF/UserAgent/Assets/cav-webview.html`, 1.7 m tall).

## HTTP
`GET /api/avatars` → `[{"id":"Persona1","file":"/avatars/Persona1.glb","label":"Anna's kind"}, ...]` (4 entries).

## Client → server
* `{"t":"join","name":"anna","avatar":"Persona1"}` — first message. `name` is the participant's nickname (its Avatar ID).
* `{"t":"chat","text":"hello"}`
* binary: byte 0x01 then Int16 little-endian PCM, mono, 16000 Hz, any length (send ~100 ms chunks = 1600 samples) — the microphone.

## Server → client
* `{"t":"welcome","id":"anna","seat":2,"fov":90,"avatars":[...same as /api/avatars...],"minutes":""}`
* `{"t":"scene","you":"anna","seats":[{"id":"anna","avatar":"Persona1","x":-1.2,"y":0.88,"z":0,"yaw":0.62,"speaking":false}, ...]}` — every avatar of the meeting (the viewer's own included), sent at each change. `speaking` is true while that participant's speech arrives.
* `{"t":"chat","from":"anna","text":"hello"}`
* `{"t":"minutes","text":"Anna: ...\nBruno: ..."}` — the whole Summary of the meeting so far, replaces the previous one.
* `{"t":"error","text":"name taken"}`
* binary: byte 0x02, byte n (length of the id), n bytes UTF-8 id of the speaker, then Int16 LE PCM mono 16000 Hz — what another participant says (never the listener's own).

## What the client does
1. Join screen: a name box and the 4 avatars as selectable pictures (live small 3D previews or any picture of each). Join sends `join`.
2. The room as seen from the participant's own seat: a first-person camera at the own avatar's head (z ≈ 1.55), looking at the table centre, horizontal FOV = `welcome.fov` (90°; three.js `fov` is vertical, convert with the aspect ratio). The own avatar is not drawn. The other avatars are drawn at their `x,y,yaw`. Those at the sides are simply out of view. Dragging with the mouse (or arrow keys) turns the view left/right by up to ±70°; releasing returns it slowly to the centre.
3. Each avatar's name floats above its head; a speaking avatar shows it (jaw: blendshape `jawOpen`, driven by the level of its audio, as `cav-webview.html` does with its visemes/jaw) and a small speaking mark.
4. Audio: capture the microphone (getUserMedia, echoCancellation on), resample to 16 kHz mono Int16 with an AudioWorklet (or ScriptProcessor fallback), send ~100 ms chunks as binary frames type 0x01. A mute button. Play each incoming type-0x02 chunk through a Web Audio graph with a PannerNode (HRTF) at the speaker's position, the listener at the own seat facing the own yaw; schedule chunks back to back with ~120 ms of jitter buffer per speaker.
5. A side panel: roster, the minutes (`minutes.text`, preformatted), a chat box.
6. Test aids: `?bot=NAME&avatar=ID` joins at once without microphone and sends a synthetic speech-like signal (bursts of band-limited noise, ~1 s on, ~2 s off); `?autojoin=NAME&avatar=ID` joins at once with the microphone.
7. No external network resources: three.js from `/lib/three/...` (copy `D:\DI\MMM\Viewer\lib\three` into `wwwroot/lib/three`); glTFLoader and SkeletonUtils from its `addons`.

The client is one `index.html` (module script inline or `app.js`) in `D:\DI\Apps\ABV\AVS\Server\wwwroot`. Nothing in it depends on how the server computes the scene.
