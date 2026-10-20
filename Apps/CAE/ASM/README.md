# Audio Scene Management (ASM)

ASM is run as its parts are: three programs that talk through APIs.

| Part | Program | What it holds |
|---|---|---|
| User Agent | ASM Studio (`AIF/UserAgent/Clients/AsmStudio`) | the window, the library of names, the workflow `ASM.orch`; it knows only the Controller API and where its Controller is (`MPAI_MAS_SERVER`, default https://localhost:5109/) |
| Controller | `Server` (AsmServer) | the Controller; no AIM. It places the Module's AIMs on the AIM host named in its configuration and keeps the Module's Shared Storage where its configuration says |
| Module | an AIM host (`AIF/AimHost`) run on the folder `Host` builds | the six AIMs of the Module `1CAE-ASM-V1.0-I01`: AOE, ASE, SOE, SSE, BME and MMC-TST |

`Start-Asm.ps1` starts the three (AllApps, 10), and ends the Controller and the AIM host when the Studio is closed. `-NoStudio` starts the AIM host and the Controller, checks that
they run, and ends them.

The Module runs continuously (Execution Continuous in its L3), so the Controller's Input and Output routes relay `InputWrite` and `OutputRead` for it, and an Output read waits for
the time the reader states (`?timeout=`, in milliseconds). The Objects, Scenes and User Commands cross as the JSON instances their schemas describe (`JsonObjectCodec`).

The Module has no vocal interface now (recognition, understanding, synthesis): that is an extension to be specified. The Studio understands typed words by its own grammar only.

Tests: `AsmOnAnAimHostTests` (the AIMs on an AIM host), `AsmServerTests` (User Agent, Controller and AIM host), `AsmStudioSelfTests` (the Studio's own self-test, 103 checks, against the deployed Module).