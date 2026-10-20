# Audio Scene Management (ASM)

ASM is a stand-alone App: you build an audio scene from sounds and speech, place them in a room, change them, and hear the scene.

| Part | Where | What it holds |
|---|---|---|
| The App | `App\` (`AsmApp`) | the window, the library of names, the grammar of the typed commands, the scene as drawn, the undo, the spatial rendering; it starts the Module and runs the User Agent |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`) | reads `ASM.orch` and talks to the Controller through the Controller API only: Data Types and Port Numbers; it knows no AIM and no use case |
| Controller | in the App's process (`Apps/CAE/Support`, `ModuleHost`) | holds no AIM; keeps the Module's Shared Storage where the App says (`Downloads\ASM\module-storage`) |
| Module | an AIM host (`AIF/AimHost`), a process of its own, started and ended by the App | the six AIMs of `1CAE-ASM-V1.0-I01`: AOE, ASE, SOE, SSE, BME and MMC-TST |

Nothing here uses MPAI-MAS. `Server\` (`AsmServer`) and `Host\` are the Controller and the folder of the AIMs for the case in which the App is remotised; the tests of that case (`AsmServerTests`) use them.

The Module runs continuously (Execution Continuous in its L3). The Objects, Scenes and User Commands cross as the JSON instances their schemas describe.

The App has no vocal interface: recognition, understanding and synthesis are not part of the Module (the author's decision of 2026-10-17). It understands typed words by its own grammar. A vocal interface would be another Module that the App runs beside this one; it is not built.

Start it with `AllApps\10 ASM (audio scene management).bat`. Its library of sounds and scenes is in `Downloads\ASM`. Test: `AsmStudioSelfTests` runs the App's own self-test (103 checks, `AsmApp --selftest`); the report goes to `Downloads\ASM\self-test`.