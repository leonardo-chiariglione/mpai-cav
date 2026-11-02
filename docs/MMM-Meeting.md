# A meeting in the MMM

ABV's meeting - a room, seats, speech, the Secretary and its minutes - rebuilt as services of an MMM M-Instance (MMM-TEC V2.2), 2026-10-15. ABV, the PAF-ABV implementation, keeps its own life; this is a second implementation beside it.

## What is what

| Part | Where | Is |
|---|---|---|
| The room and its seats | `MMM/Client/Meeting.cs` (`Meeting.Setup`) | Locations: `MeetingRoom` on `MeetingParcel`, four seat Locations inside it. Nothing about a meeting is in the room itself |
| AVS Service | `MeetingServices` | A Process. Convening, invitations, the seats it assigns, the right to move a Persona to a seat, taking a User out |
| Secretary | `MeetingServices` | A machine Process with a Persona of its own. Takes a seat like a participant, takes the minutes, gives each participant a copy |
| ASR Service | `MeetingServices.PumpAsrAsync`, `Speech.cs` (`WhisperAsr`) | A Process. Recognises the words of the Speech Objects it is sent (Whisper) |
| Speech Object | `Speech.cs` | A Basic Speech Object (OSD-BSO-V1.5) with its own time. The client declares it at its User's seat when the speech starts (MM-Add, with its Speech Qualifier) and streams the data in as it captures; no request per chunk. It is what was said: the participants hear it, the Secretary hears it too |
| Client | `MMM/Viewer/index.html`, `/viewer/?user=Name&meet=1` | The human's side of a User: drives the devices (keys, screen, microphone) and renders. `&convene=1&invite=A,B` convenes and invites; `&mic=1` captures and cuts speech; `&persona=Id` brings one of the User's own Personae |
| God's-eye view | `/viewer/` (`?look=12,0` frames the room) | The operator's view of the M-Instance: it shows everything |

The services act on the M-Instance only through its MMM-API, as any Process does.

## Hearing: Speech Objects that are streams

A Speech Object carries its own duration, and that does not stop it being sent at once. Its time is declared when the speech starts and nothing is redefined until that time is over.

- **Captured, identified, placed (MMM-TEC V2.2, the Verification Use Cases).** When the speech starts, the client - the human's Device - **UM-Captures** it at the human's U-Location (`Nil` the name of the Data, `At` the U-Location, `With` the **Speech Qualifier**, TFA-SPQ-V1.5: raw PCM, 16 kHz, 16 bits, a microphone, one channel, and the **stream format `WebSocket`**, a parameter of the Qualifier - the Qualifier says what kind of data this is, not how long it lasts). Captured Data is not usable yet: the **Identify** Process Action makes an Item of it, a Speech Object (OSD-BSO-V1.5), which has **no duration** while the stream goes on (its Time, the duration, is the Speech Object's own and is put in it when the stream is over) and whose Identifier is the one the Data was named with; the M-Instance refuses (422 IncDQ) a Qualifier that names no stream format, and (403, 404) whoever is not the one who captured it, or Data that does not exist. The User then **MM-Adds** the Speech Object at its seat (with the right the AVS Service gave with the seat). Three Process Actions for each Speech Object: UM-Capture, Identify, MM-Add.
- **Data goes out as it is captured**, once the Speech Object is an Item. Only the User that owns the Speech Object sends it. In this implementation it goes through the server that hosts the M-Instance: `/ws/v2.2/speech/{id}?token=...`.
- **Hearing is the receiver's decision.** A User receives from Anna and not from Carla if it chooses to: the panel of the client lists who is in the room with a tick for each (`&hear=Anna,Bruno` starts with only those). For each Speech Object of a User it hears, the client opens the Speech Object's address (`/ws/v2.2/speech/{id}/listen?token=...`), a WebSocket - the format the Qualifier declares, so it understands what it receives - and receives the PCM from the start of the Speech Object and as it goes on, then the end. The M-Instance refuses only whoever may not hear at all: a User outside the room, or taken out of the meeting, gets 403.
- **Played** as it arrives, from the seat it is said at (a panner at the seat; the listener where the User sits and looks), with 0.12 s of buffer.
- **One Speech Object for a speech.** It starts when the person starts to speak and ends when they stop, however long that is: there are no 3-second units and no "next one takes over". When the client closes the stream (or nothing arrives for 30 s) the data becomes the Speech Object's, its Time is put in it, and it is no longer a stream. A listener that comes late is given what it was, then the end.
- **The Secretary** hears the room as any participant does, by capturing it. A Speech Object that is a stream is not waited for: it listens to the stream (the address of its data) and has what it hears recognised in pieces - at a pause of 0.6 s in the speech, or when a piece reaches 12 s - so the words of a sentence are in the minutes while the person is still speaking. A Speech Object that is already complete when the Secretary first sees it is recognised whole.
- **Measured** on one PC with headless browsers: the listener's first sound comes about 0.1 to 0.3 s after the Speech Object began once the pages are warm, and 0.8 to 1.8 s for the first objects of a session. Over a network add its round trip.
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
