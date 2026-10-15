# Avatar-Based Videoconference - User Agent: a first design (draft, 2026/10/11, with the author's answers)

For the author to correct. Nothing here is built. It reuses what Thalia has: the browser page, the Controller API, the WDL workflows and the Units.

## 1. The parts

| Part | What it is | Where it runs |
|---|---|---|
| **AVS User Agent** | The server side of a meeting: it hosts the **AVS** Module (Avatar Videoconference Server) and the registration page, keeps the registry, and **places the avatars around the table** when the meeting starts, in the order the participants arrive. | the server |
| **VMS** | The Virtual Meeting Secretary Module: it listens to the meeting and writes the summary the AVS passes on. | the server, with the AVS |
| **Client** | One for each participant: the **VCT** and **VCR** Modules, run **in the client**, with the Units - microphone and camera in, scene and sound out - and the controls. | the participant's computer |
| **Registration page** | A web page where a participant registers. | served by the AVS User Agent |

The browser alone cannot run the Modules (they are .NET). The client would be Thalia's pattern turned round: a small local program that holds the Modules and
the Units, with the meeting page in the browser, or in a window of its own, as the Thalia desktop client was.

## 2. Registration

A participant opens the page of a meeting and:
1. gives a **nickname**: **the nickname is the Avatar ID**;
2. **adds an avatar**: one of the gallery, or a 3D model (glTF/GLB) of their own, with the licence notice Thalia's avatar has;
3. optionally enrols by voice and face, so that the AVS's Service Participant Authentication (SPA) can tell who is speaking;
4. is given a Participant ID, and waits for the meeting to start.

**Avatars are introduced once, at start time, and referenced afterwards**: the avatar model goes to the other clients when the meeting starts, with its ID;
a Module that has an ID has access to the data it stands for.

## 3. What travels

A participant's **Portable Avatar** that is transmitted is **the Avatar ID, the Face and Body Descriptors, and the Speech** (with the Personal Status that
makes the descriptors expressive). The Portable Avatar's schema now has those fields: **FaceDescriptorsObject**, **BodyDescriptorsObject** and
**EntityPersonalStatus**, in place of the Entity State, which is not used here. The avatar model is not sent again: the ID refers to it.

## 4. The meeting page

- **The scene**: the room with a table, each avatar at the seat the AVS User Agent gave it, its **nickname** above it, animated by its descriptors, speaking.
- **The sound**: each participant's speech comes from its avatar's mouth. The Speech Object Delivery Unit needs a **head model** (acronym **HDM**) to propagate it.
- **The view**: the Point of View (OSD-OPV) from the page's controls; it goes to the VCR, which passes it on.
- **Controls**: microphone and camera on and off, leave, the Output Text as subtitles, the list of nicknames.
- **Size**: as many participants as come; the design is for at least 3 or 4 avatars.

## 5. The data, end to end

microphone and camera (Units) -> **VCT** (in the client) -> Portable Avatar -> **AVS** (on the server: demultiplexing, authentication, summary with the VMS,
multiplexing) -> each client's **VCR** (PDX -> MSC -> RSR) -> Portable Avatars with their IDs, the Multimodal Scene, the Point of View, the Output Text
-> the Units (3OD, SOD).

## 6. The head model (HDM): there is none, so one is to be found

The SOD must make the speech of an avatar come from its mouth and be heard differently from in front of the head and from behind it. It needs: where the mouth
and the ears are with respect to the characteristic point of the face that gives the avatar's Spatial Attitude, and how the speech radiates. Candidates:
- **A parametric head** (the simplest, enough for a first meeting): a sphere of about 9 cm radius with the mouth in front of it, and a **dipole weight and power**
  for the radiation, which Steam Audio - the library the repository already uses for spatial audio - takes for a source (weight 0 omnidirectional, 0.5
  cardioid; power how focused). Nothing to download.
- **A measured one**: the speech directivity of human talkers, [BYU](https://scholarsarchive.byu.edu/directivity/1) (an average talker and a head-and-torso
  simulator), or the [artificial head and mouth of the TU Berlin](https://depositonce.tu-berlin.de/items/c6e0d4b7-afdf-4b1e-81d8-8665b2326c81/full),
  measured at 5 degrees. The licences are to be checked.

An HDM Data Type would hold: the characteristic point, the head dimensions, the mouth and ear positions, and the speech directivity (parametric, or a dataset).
I have written no schema: the author says there is no head model, so what it is comes first.

## 7. The User Agents of the Modules

| Module | Units | Workflow (WDL) |
|---|---|---|
| VCT (client) | SOA (microphone), VOA (camera), text in | acquire -> VCT -> send the Portable Avatar to the AVS |
| VCR (client) | 3OD (avatars and scene), SOD (speech, with the HDM), the page's point of view | receive -> VCR -> deliver |
| AVS and VMS (server) | none | receive the Portable Avatars -> AVS (with the VMS) -> give them to the clients |

The ABV is one App of the Service (an `Apps/ABV` folder: workflow, `app.json`, icon), as MAD, AMQ and the others are.

## 8. Still to decide

1. The head model: parametric to begin with, or a measured one; and the HDM schema.
2. Whether the client is a local program with the page in the browser (proposed), or a window of its own.
3. The seating: a round table, the order of arrival, and what happens when someone joins after the start.
4. The consent text for voice and face.

## 9. Build order

1. The code and L3s of PDX, VSC, MSC and the RSR for the Portable Avatars; the VCR composite L3; tests with synthetic Portable Avatars of four participants.
2. The VCT and AVS code that is missing (PMX, SPA, the VCT composite).
3. The ABV App, the AVS User Agent (registration, seating).
4. The client: the Units, the meeting page.
5. A meeting of four participants on one computer, then on four.
