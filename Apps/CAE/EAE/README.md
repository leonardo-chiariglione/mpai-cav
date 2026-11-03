# Enhanced Audioconference Experience

Finds the speakers in the audio of a microphone array, separates them, takes the noise away and describes the audio scene.

| Part | What |
|---|---|
| The App | `EaeApp` (`App\`): the program you run; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `EAE.orch`, gets each datum from where the App says, and talks to the Controller through the Controller API only; it knows no AIM and no use case |
| Controller | the Controller of the Module, in the App's process, holding no AIM (`Apps/CAE/Support`, `ModuleHost`) |
| Module | the AIMs of `EAE`, on an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App |

The App is stand-alone: nothing here uses MPAI-MAS.

## Use

```
EaeApp --demo <folder>
EaeApp --capture <folder>\room.wav --array <folder>\array.json --out <folder>\out
```
`--demo` writes a capture of two speakers in a noisy room to try the App with. The result is `scene.json` and a WAV for each speaker, with where each is and when it speaks.

The array must be small enough that the wave number times its radius is below about 1 at the frequencies of interest (see `MKG/pending-changes.md`).

## The server (a conference)

`Server\` is the Enhanced Audioconference server of the live demo, derived from the Avatar Videoconference Server of ABV (`Apps/ABV/AVS`) without its avatars, chat and minutes: a room whose microphone array is captured, and listeners who hear the speakers of the room, separated from each other and placed in the direction they are. It runs the Module by the same User Agent and Controller as the App. `PROTOCOL.md` says what the browser and the server say to each other.

| Script | What |
|---|---|
| `Run-Eae-Demo.bat` | a VIRTUAL room (two speakers in a noisy room); open http://localhost:5190/ as a listener, with headphones |
| `Run-Eae.bat` | the real room: it joins as "the room" and sends its microphone array (`EAE_ARRAY` = the array's geometry file; the browser must give as many channels as the array has) |
| `Run-Eae-Open.bat` | the same, answering on the network (read the firewall note in the script) |
| `Stop-Eae.bat` | stops the server and the AIM host |
