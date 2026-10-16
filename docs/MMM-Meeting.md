# A meeting in the MMM

ABV's meeting - a room, seats, speech, the Secretary and its minutes - rebuilt as services of an MMM M-Instance (MMM-TEC V2.2), 2026-10-15. ABV, the PAF-ABV implementation, keeps its own life; this is a second implementation beside it.

## What is what

| Part | Where | Is |
|---|---|---|
| The room and its seats | `MMM/Client/Meeting.cs` (`Meeting.Setup`) | Locations: `MeetingRoom` on `MeetingParcel`, four seat Locations inside it. Nothing about a meeting is in the room itself |
| AVS Service | `MeetingServices` | A Process. Convening, invitations, the seats it assigns, the right to move a Persona to a seat, taking a User out |
| Secretary | `MeetingServices` | A machine Process with a Persona of its own. Takes a seat like a participant, takes the minutes, gives each participant a copy |
| ASR Service | `MeetingServices.PumpAsrAsync`, `Speech.cs` (`WhisperAsr`) | A Process. Recognises the words of the Speech Objects it is sent (Whisper) |
| Speech Object | `Speech.cs` | A Basic Speech Object (OSD-BSO-V1.5) with its own time. The client cuts it (at most 8 s) and places it once at its User's seat with MM-Add; no request per chunk. It is what was said: the participants hear it, the Secretary hears it too |
| Client | `MMM/Viewer/index.html`, `/viewer/?user=Name&meet=1` | The human's side of a User: drives the devices (keys, screen, microphone) and renders. `&convene=1&invite=A,B` convenes and invites; `&mic=1` captures and cuts speech; `&persona=Id` brings one of the User's own Personae |
| God's-eye view | `/viewer/` (`?look=12,0` frames the room) | The operator's view of the M-Instance: it shows everything |

The services act on the M-Instance only through its MMM-API, as any Process does.

## Hearing

- A User's speech is placed at its seat (MM-Add, with the right the AVS Service gave with the seat). MM-Capture of the room returns it as a Thing of kind `Speech` (owner, seat position, start and end time, no data) while it is recent (a minute).
- Whoever has an Item in the room - a Persona seated in it - may read its data (`GET items/{id}`), once, and plays it from the seat it was said at (a panner at the seat; the listener where the User sits and looks). A User taken out of the meeting, or outside the room, gets 403.
- The Secretary hears the room as any participant does, by capturing it; it sends what it hears to the ASR Service.
- The delay of hearing is the length of the Speech Object (it is sent when it ends) plus the polling (5 times a second): about the utterance, not the sentence. A long monologue is cut every 8 s.
- The ASR Service drops what Whisper writes for noise: a language that is not one of ABV_LANGS (default en, it, pt, es, fr, de), segments Whisper itself marks as no speech or doubtful, captions like [BLANK_AUDIO].

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
