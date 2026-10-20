# Enhanced Audioconference server - the protocol between the browser client and the server

Server: `Apps/CAE/EAE/Server` (ASP.NET, Kestrel), derived from the Avatar Videoconference Server of ABV (`Apps/ABV/AVS`) without its avatars, chat and minutes. It serves `wwwroot` at `/`, `GET /api/info`
(`{"codeRequired":false,"channels":6,"rate":16000,"segment":3}`) and a WebSocket at `/ws`. Text frames are JSON; binary frames are audio.

Two kinds of participant: **the room**, whose microphone array is captured (at most one), and **listeners**.

## Client to server
* `{"t":"join","name":"anna","role":"listener"|"room","code":"..."}` - the first message.
* binary, the room only: byte `0x01`, then 16-bit little-endian PCM, the channels of the array interleaved, 16 kHz, any number of whole frames (send about 100 ms: 1600 frames).

## Server to client
* `{"t":"welcome","id":"anna","role":"listener","channels":6,"rate":16000,"segment":3,"status":"..."}`
* `{"t":"scene","speakers":[{"id":"A","azimuth":60,"elevation":20,"speaking":true},...],"seconds":3,"processing":2400}` - for each segment the Module has processed: the speakers found (azimuth anticlockwise from the room's x axis, elevation above the horizontal, degrees), and whether each speaks in the segment. A speaker keeps its letter from one segment to the next while it stays within 25 degrees.
* binary, listeners only: byte `0x02`, byte *n* (the length of the id), the id (UTF-8), then 16-bit PCM mono 16 kHz - what a speaker says, separated from the others and with the noise reduced. The room never hears itself.
* `{"t":"error","text":"name taken"}`

## What the server does
The room's audio is cut into segments (`EAE_SEGMENT`, 3 s); each goes through the Module of the Enhanced Audioconference Experience (`1CAE-EAE-V2.5-I01`), whose AIMs run on an AIM host of their own, by the User Agent of the EAE App. If the Module is still busy with the last segment, the new one is dropped.
`EAE_DEMO=1` makes the server a virtual room (two speakers, in turn and together, in a noisy room), so that the listeners can be tried without a microphone array.

## What the client does
A listener plays each speaker's audio through a Web Audio `PannerNode` (HRTF) placed in the speaker's direction (room x ahead, y to the left, z up), the listener at the centre facing x, chunks scheduled back to back per speaker. The map shows the array at the centre and a point in each speaker's direction, lit while heard. The room captures the multichannel microphone (`getUserMedia`, `channelCount` = the array's, no echo cancellation, noise suppression or gain control), brings it to 16 kHz and sends it; a browser that gives fewer channels than the array has sends nothing and says so.