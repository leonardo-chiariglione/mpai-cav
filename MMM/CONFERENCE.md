# A conference in the MMM world

The MMM as a place that can be seen: the Metaverse Square, the Town Hall (a lobby and the meeting room), a library and a cafe, made of glTF buildings with doors. A human's User
brings a Persona (a Rocketbox avatar) into the world, walks it across the Square to the Town Hall, through the doors, and sits at the table: that is the meeting.

## What the standard calls what here

| In the world | In MMM-TEC |
|---|---|
| The Square | a Location of two dimensions (MVS) |
| The Town Hall and the meeting room | Locations of three dimensions: a Location Object (the building) with a Sub-Location (the room), rendered by a glTF 3D Model (`MMM/Viewer/world/*.glb`) |
| A door | a Right: the Right to move one's Persona to the Location behind it (MM-Move). Whoever has none is refused: 403 |
| The meeting | the AVS Service gives the Users it invites a seat, the right to the doors and the right to speak, **for the meeting**: the Rights carry its period (`RightsTime`) and are withdrawn when it ends |
| The acoustics of the meeting room | an Acoustic Profile (CAE3, Scene: reverberation by frequency, early reflections, absorption, diffusion) in the Attributes of the room's Location Qualifier; the client renders the speech of the room with it |
| The Persona walking | MM-Move; the client captures what it sees with MM-Capture |

`MMM/World/make_world.py` writes the buildings and `world.json` (where everything is, the walls, the doors, the seats, the acoustics). `MMM/Client/World.cs` makes the Locations from it.

## Running it

For people on this PC or this network only, no code:

```
dotnet run --project MMM/Server -- http://127.0.0.1:7099 --world --invite Leonardo,Anna,Bruno,Carla,Dario --hours 3
```
and open `http://127.0.0.1:7099/viewer/?world=1&mic=1&user=Leonardo`.

For guests outside the network (what tomorrow needs): `AllApps\42 MMM world (conference with people outside this network).bat`, or `MMM\Run-Conference.bat`.
It asks for the names (the convener first), starts the server closed with a **meeting code**, starts a Cloudflare quick tunnel (`cloudflared.exe`, no account) and prints
the address and the code to send to the guests, and opens the convener's own page. `-Until 19:00` or `-Hours 3` is when the meeting ends. Close both windows when it is over:
the address dies with them.

Each guest: Chrome or Edge on a PC, a headset (no loudspeakers: the room has its own echo), the address, **the name as it is in the list**, the code, *allow the microphone*.
W/S walk, A/D turn. The Town Hall is to the east (on the right when you start). The door opens by itself for the invited; the Persona sits at its seat in the meeting room.
Q stands up, E sits. Who is not in the list is told it is not invited, and the doors stay shut to it (403).

The convener has a button, bottom left: *End the meeting for everyone* (two clicks). The rights of everybody end, the Personae are sent out to the Square, nobody gets in again.
The meeting ends by itself at its time as well.

## What has been tried, and what has not

Tried: the doors and the rights (tests `MmmWorldTests`: the invited go through, the others get 403, the convener ends the meeting, the rights end with the meeting's time);
walking through the doors in a browser; two Users in the room seeing each other; a voice from one heard by the other through the room's reverberation (a test tone, 100-470 ms to
the first sound); the meeting code and the tunnel (a User joined through the public https address).

Not tried: five people with real microphones over the internet; Chrome on other people's PCs; the minutes (Whisper) in this world; phones.
The Secretary (a machine Process with the Persona of Persona2) takes a seat and takes the minutes as in the earlier meeting use case.

## Not in the standard

- `RightsTime` is used as the period the Rights are valid for; MMM-TEC does not say what it means.
- The room's Location Object (`OSD-BLC`) is a minimal one (not validated against the schema); a building's 3D shape is a glTF file named by the Location (`Model`), not yet a 3D Model Object Item.
- Acoustics are carried in the Attributes of the Location Qualifier (they are implementation-defined there).
