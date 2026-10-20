# Speech Restoration System

Replaces the damaged parts of a recording of speech with speech made in the voice of the speaker. The voice is a Speech Model made from speech of the speaker (OpenVoice V2 tone-colour embedding), and Piper says the texts.

| Part | What |
|---|---|
| The App | `SrsApp` (`App\`): the program you run; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `SRS.orch`, gets each datum from where the App says, and talks to the Controller through the Controller API only; it knows no AIM and no use case |
| Controller | the Controller of the Module, in the App's process, holding no AIM (`Apps/CAE/Support`, `ModuleHost`) |
| Module | the AIMs of `SRS`, on an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App |

The App is stand-alone: nothing here uses MPAI-MAS.

## Use

```
SrsApp --voice a.wav [b.wav ...] --damaged d.wav --sections 0.5-1.0,2.0-2.5 --say "first text" --say "second text" --out restored.wav [--stand-in]
```
`--stand-in` speaks with a tone and needs no voice engine. Files are 16-bit PCM WAV; another rate is brought to 16 kHz.

Needs, for the real voice, `D:\AI\openvoice` and `D:\AI\pyannote-venv` (see `AIMs/CAE1/V2.5/Voice`). It changes the timbre only: prosody and language are Piper's.
