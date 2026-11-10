# Speak Model with Emotion

Gives emotionless speech the emotion of a model utterance, by its prosody (CAE-SME of CAE-USC V2.5).

| Part | What |
|---|---|
| The App | `SmeApp` (`App\`): the program you run; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `SME.orch`, gets each datum from where the App says, and talks to the Controller through the Controller API only; it knows no AIM and no use case |
| Controller | the Controller of the Module, in the App's process, holding no AIM (`Apps/CAE/Support`, `ModuleHost`) |
| Module | the AIMs of `SME` (Speech Feature Analysis 1, Prosodic Emotion Insertion), on an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App |

The App is stand-alone: nothing here uses MPAI-MAS.

## Use

```
SmeApp --speech neutral.wav --model model.wav --out out.wav
```
