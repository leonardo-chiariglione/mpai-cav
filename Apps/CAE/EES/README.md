# Emotion Enhanced Speech

Gives emotionless speech an emotion, by one of two pathways: the prosody of a model utterance, or the speech features of an emotion.

| Part | What |
|---|---|
| The App | `EesApp` (`App\`): the program you run; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `EES.orch`, gets each datum from where the App says, and talks to the Controller through the Controller API only; it knows no AIM and no use case |
| Controller | the Controller of the Module, in the App's process, holding no AIM (`Apps/CAE/Support`, `ModuleHost`) |
| Module | the AIMs of `EES`, on an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App |

The App is stand-alone: nothing here uses MPAI-MAS.

## Use

```
EesApp --speech neutral.wav --emotion SADNESS [--degree 0.5] [--language en] --out out.wav
EesApp --speech neutral.wav --model model.wav --out out.wav
```

The neural features are produced by a rule table (a stand-in for a neural network); see `MKG/pending-changes.md`.
