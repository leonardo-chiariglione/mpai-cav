# Audio Scene Management (ASM) - by voice

**The window (`Ua\`, program `AsmUa`).** The User Agent for the demo: you speak or type to an audio scene and it changes; the avatar answers. It runs `ASM-VCI.orch` with the interpreter of the User Agent against ONE Module (Audio Scene Management, with the Voice Command Interface inside it) and supplies only devices: the microphone and the text box (a Speech Object, OSD-BSO #1; typed words are spoken by the machine's voice), a file chooser (a sound, sent in a User Command that acquires it, CAE-UCM #1), the screen (the words heard, the response in text; the scene drawn from above from the Audio Scene Descriptors), the avatar (the response in speech) and the loudspeakers (the scene, rendered). It knows no AIM and reads no storage. Start it with `AllApps\CAE\ASM.bat` (it starts Ollama, then the Controller, then the window; `AsmUa.exe --controller <url>` if the Controller is elsewhere). `Voice\` holds what builds the Controller: the L3s of the run with the Access of the JSON Interface read from the files of this software. Tests: `AsmVoiceWorkflowTests` (the real workflow and Module, fake speech and model) and the JSON Interface and Voice Command Interface classes.

| Part | Where | What it holds |
|---|---|---|
| User Agent | `Ua\` (`AsmUa`), on `AIF/UserAgent/Ua` (`Mpai.Ua`) | a program of its own. Reads `ASM-VCI.orch` and talks to the Controller through the Controller API only (`RemoteControllerApi`): Data Types and Port Numbers. It tells the Controller to start the execution of the L3; it holds no Controller, no Module, no AIM |
| Controller | `Controller\` (`AsmController`) | a program of its own, on `127.0.0.1:5057` (`--listen`). Starts the Module and its AIMs as the L3 says when the User Agent asks, and binds the Module's Shared Storage (`--storage`, `--keep-until`). `--live` and `--scene` are its settings for the language model and the scene |
| Module | `1CAE-ASM-V1.0-I01`, started by the Controller | AOE, ASE, SOE, SSE, MSE, MMC-TST and the Voice Command Interface; and, in the same Controller, the Module of the machine's voice (`1PAF-RSR-V1.6-I01`) |

The Controller API reaches the Controller over the routes of `Mpai.Mas.Server`. `Server\` (`AsmServer`) and `Host\` are the Controller and the folder of the AIMs for the case in which the Module runs apart; the tests of that case (`AsmServerTests`) use them.

The language of what the person says is the Application Context, compiled from `Grammar\` (see its README); the walls, the room and the acoustics are the Acoustic Profile (CAE-ACP) and the Closed Space (OSD-CSP).

The old combined App (`App\`, `AsmApp`: window, Controller and Module in one program) was retired on 2026-10-29: it did not keep the User Agent, the Controller and the Module apart as AIF does. Git has it.
