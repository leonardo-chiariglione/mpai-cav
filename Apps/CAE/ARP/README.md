# Audio Recording Preservation

Preserves the digitised audio of an open reel tape: finds the irregularities, classifies them, restores the access copy where a speed or an equalisation was wrong, and packages the Preservation Master.

| Part | What |
|---|---|
| The App | `ArpApp` (`App\`): the program you run; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `ARP.orch`, gets each datum from where the App says, and talks to the Controller through the Controller API only; it knows no AIM and no use case |
| Controller | the Controller of the Module, in the App's process, holding no AIM (`Apps/CAE/Support`, `ModuleHost`) |
| Module | the AIMs of `ARP`, on an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App |

The App is stand-alone: nothing here uses MPAI-MAS.

## Use

```
ArpApp --tape tape.wav [--film-audio film.wav --film-frames <folder of grey PNG frames> --fps 10] --out <folder>
```
It writes `master\` (every file of the Preservation Master, the tape as it is among them) and `access-copy.wav`.

The Module runs continuously and settles as the audio and video analyses exchange their files; the App reads the Master until it has not changed for 15 seconds. The video analysis is a stand-in that reads frames, not a film; a tape file other than 16-bit PCM WAV is refused by the audio analysis.
