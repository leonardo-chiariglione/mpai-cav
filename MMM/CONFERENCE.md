# A meeting in the MMM world

The MMM as a place that can be seen: the Metaverse Square, the **MPAI building**, a library and a cafe, made of glTF buildings with doors. A human registers with the M-Instance and chooses
a Persona (a Rocketbox avatar of a catalogue of 113: it does not bring its own); its User walks the Persona across the Square, through the MPAI doors, and into the building.

## The MPAI building

- **The atrium** is a public space: anybody may enter, look, talk and ask. At the counter, a lady avatar (a Persona of the Reception Service, the Multimodal Conversation Avatar) welcomes the visitors. *She stands there; her conversation is the next step.*
- **The members' wing** (north): rooms for the member companies. **The offices of MPAI** (east): the President, the Vice-president, the Board members, the Secretariat. **The meeting rooms** (south): one for each group of the list (MMM, AIF, AIH, AIP, CAE, EVC, ISAC, MMC, NNW, PAF, XRV); the MMM meeting room is the first on the right.
- The wings and their rooms are not public: a door is a Right, and whoever has none is refused (403). The chair gives the Rights to the people it invites, for the meeting.

## What the standard calls what here

| In the world | In MMM-TEC |
|---|---|
| The Square | a Location of two dimensions (MVS) |
| The MPAI building, its atrium, its wings and rooms | Locations of three dimensions: a Location Object (the building) with Sub-Locations; the atrium is a public one; rendered by a glTF 3D Model (`MMM/Viewer/world/mpai.glb`) |
| A door | a Right: the Right to move one's Persona to the Location behind it (MM-Move). Whoever has none is refused: 403 |
| The meeting | the AVS Service gives the Users the chair invites a seat, the right to the doors (the wing, the room) and the right to speak, **for the meeting**: the Rights carry its period (`RightsTime`): after it the participants have no Right in the room |
| The registration | Register (RGSrvc), as the Register page of MMM-TEC V2.2 says: the human selects Registration and is shown the options (Account Levels *Visitor* and *Participant*, both free: no Service Pricing Model), selects a level, gives its Personal Data (first name, last name, email), and chooses its Persona among the Items the Account makes available (the catalogue; `PersonaChoice`). The Account carries `AccountLevel` and its Items. A Visitor Account has no seat in a meeting; a level not offered, or a Persona not in the catalogue, is refused (422). Nothing is uploaded |
| The acoustics of the meeting room | an Acoustic Profile (CAE3, Scene: reverberation by frequency, early reflections, absorption, diffusion) in the Attributes of the room's Location Qualifier; the client renders the speech of the room with it |
| The Persona walking | MM-Move; the client captures what it sees with MM-Capture |

`MMM/World/make_world.py` writes the building and `world.json` (where everything is, the walls, the doors, the seats, the acoustics). `MMM/World/make_catalogue.py` makes the catalogue of Personae
(`MMM/Viewer/personae/catalogue`, from the Rocketbox repository; not kept in git). `MMM/Client/World.cs` makes the Locations from `world.json`.

## Running it

**Through Tailscale (the way for the guests).** `AllApps\43 MMM meeting (through Tailscale).bat`, or `MMM\Run-Meeting.bat -Chair Leonardo -Guests "anna@example.com=Anna,..." -Until 19:00`.
Tailscale tells the server who is asking, so nothing is typed and there is no code: whoever browses on this PC is the chair; a guest the chair listed (their Tailscale login = their name in the
meeting) may go into the meeting room, for the meeting; any other login on the tailnet is a visitor, who may go into the atrium and no further. Nothing that does not come by Tailscale is answered.
Needs: Tailscale on this PC and on each guest's, https certificates enabled for the tailnet (the admin console: DNS, HTTPS certificates: the microphone needs https), each guest invited to the tailnet
(or this machine shared with them). `tailscale serve --bg 7099` puts the server on the tailnet; `tailscale serve reset` takes it off (the script does both).

Each guest: Chrome or Edge on a PC, a headset (no loudspeakers: the room has its own echo), the address; the first time, they choose their Persona; *allow the microphone*.
W/S walk, A/D turn. Across the atrium to the south wing (the meeting rooms); the door to the MMM meeting room opens by itself for the invited; the Persona sits at its seat. Q stands up, E sits.
Closing the browser does not lose the place: coming back, the Persona is where it was.

The chair has a button, bottom left: *End the meeting for everyone* (two clicks). The rights of everybody end, the Personae are sent out to the Square, nobody gets into the wing again.
The meeting ends by itself at its time as well.

**On this PC only**, no Tailscale: `dotnet run --project MMM/Server -- http://127.0.0.1:7099 --world --invite Leonardo,Anna,Bruno,Carla --hours 3`, and open
`http://127.0.0.1:7099/viewer/?world=1&mic=1&user=Leonardo`. (`MMM\Run-Conference.bat` is the older way for guests outside the network: a meeting code and a Cloudflare tunnel.)

## Sharing a screen

The chair has a button, bottom left: *Share my screen* (above *End the meeting for everyone*). The browser asks which screen or window; the chair's screen then fills everybody's window in the room, and the room is
reduced to a stamp at the bottom right (a click on the stamp changes them over). *Stop sharing* ends it, and the end of the meeting does too. For now only the chair may share: the Right to place a screen in the room is
given to the convener with the seat, and to nobody else. A shared screen is a Visual Object (OSD-BVO) that is a stream, placed in the room by MM-Add; whoever has an Item in the room sees it; it is sent as JPEG frames over
a WebSocket, only when the picture changes (a slide that stays costs nothing). The sound of what is shared is carried too (24 kHz mono, in order): when the browser asks, tick *share tab audio* (or *share system audio*). Share the tab or window with the presentation, not the MMM tab: the voices of the room would go back into the room as an echo.

## What has been tried, and what has not

Tried: the doors and the rights (tests `MmmWorldTests`: the atrium is open to all, the wing and the room to the invited, the convener ends the meeting, the rights end with the meeting's time);
the catalogue choice, the identity by Tailscale's headers and the return of a User (tests `MmmTailscaleTests`); walking through the doors in a browser; two Users in the room seeing each other;
a voice from one heard by the other through the room's reverberation (a test tone, 100-470 ms to the first sound).

Not tried: real Tailscale (this PC had none); four people with real microphones over the internet; Chrome on other people's PCs; the minutes (Whisper) in this world; phones.
The Secretary (a machine Process with the Persona of Persona2) takes a seat and takes the minutes as in the earlier meeting use case.

## Not in the standard

- A shared screen as a Visual Object stream, and the Right to place it (MMAdd at the room), are not in MMM-TEC; the frames are JPEG, and the sound raw PCM, over a WebSocket (a first byte says which).
- `RightsTime` is used as the period the Rights are valid for; MMM-TEC does not say what it means.
- `PersonaChoice` in the Personal Profile is not in MMM-TEC (Register lets a human name or bring a Persona; here it is chosen from the catalogue of the M-Instance).
- The room's Location Object (`OSD-BLC`) is a minimal one (not validated against the schema); a building's 3D shape is a glTF file named by the Location (`Model`), not yet a 3D Model Object Item.
- Acoustics are carried in the Attributes of the Location Qualifier (they are implementation-defined there).
