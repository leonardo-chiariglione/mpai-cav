# A meeting in the MMM

ABV's meeting - a room, seats, speech, the Secretary and its minutes - rebuilt as services of an MMM M-Instance (MMM-TEC V2.2), 2026-10-15. ABV, the PAF-ABV implementation, keeps its own life; this is a second implementation beside it.

## What is what

| Part | Where | Is |
|---|---|---|
| The room and its seats | `MMM/Client/Meeting.cs` (`Meeting.Setup`) | Locations: `MeetingRoom` on `MeetingParcel`, four seat Locations inside it. Nothing about a meeting is in the room itself |
| AVS Service | `MeetingServices` | A Process. Convening, invitations, the seats it assigns, the right to move a Persona to a seat, taking a User out |
| Secretary | `MeetingServices` | A machine Process with a Persona of its own. Takes a seat like a participant, takes the minutes, gives each participant a copy |
| ASR Service | `MeetingServices.PumpAsrAsync`, `Speech.cs` (`WhisperAsr`) | A Process. Recognises the words of the Speech Objects it is sent (Whisper) |
| Speech Object | `Speech.cs` | A Basic Speech Object (OSD-BSO-V1.5) with its own time: sent once, with MM-Send; no request per chunk |
| Client | `MMM/Viewer/index.html`, `/viewer/?user=Name&meet=1` | The human's side of a User: drives the devices (keys, screen, microphone) and renders. `&convene=1&invite=A,B` convenes and invites; `&mic=1` captures and cuts speech; `&persona=Id` brings one of the User's own Personae |
| God's-eye view | `/viewer/` (`?look=12,0` frames the room) | The operator's view of the M-Instance: it shows everything |

The services act on the M-Instance only through its MMM-API, as any Process does.

## The rules

- A User may have several Personae, all its own. It brings one of them to an event; nobody else moves it.
- Who is in the meeting is a human decision, the convener's (the first User to convene it). There is no meeting code: the AVS Service seats only the invited, and the invitation is a Message of the AVS Service.
- The places of a meeting are the seats the AVS Service assigns; the User moves its own Persona there with the right the AVS Service gave it.
- The minutes belong to the room (the AVS Service keeps them). Each participant is given a copy of its own; a User taken out keeps what it had and gets no more.
- Axes: x east, y north, z up (MMM/AXES.md). Yaw 0 looks north (provisional convention).

## What is not in the standard

- `GET /api/v2.2/processes/{id}/inbox`: how a Process learns which Messages reached it (MMM-TEC does not say).
- MM-Capture returns a simplified scene (the perceptible Things of the Location), not the OSD Scene Descriptors.
- Discover and Basic Discovery are not implemented: the Users address the services by their identifiers.
- The AVS Service cannot move a Persona it does not own: a User taken out of the meeting keeps its Persona standing in its seat (the seat stays reserved).

## Run it

```
dotnet run --project MMM/Server -- http://127.0.0.1:7099
```

The server starts Whisper for the ASR Service if `Models\Whisper` is there. Open `/viewer/?user=Anna&meet=1&convene=1&invite=Bruno&mic=1` for the convener and `/viewer/?user=Bruno&meet=1&mic=1` for the guest.

The scripted use case (three bots and the Secretary), a step at a time, for the god's-eye view:

```
dotnet run --project MMM/Server -- http://127.0.0.1:7099 --meeting-demo 5
```

and open `/viewer/?look=12,0`. As a test: `MmmMeetingUseCaseTests`, whose record is `Test/Expected/mmm-meeting-usecase.json`. The other tests: `MmmCaptureTests`, `MmmMeetingTests`, `MmmSpeechTests` (the Whisper test needs `Models\Whisper`), `MmmTrustTests`.
