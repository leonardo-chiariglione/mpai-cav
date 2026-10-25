# MPAI reference software on MPAI-AIF

Reference **Human-CAV Interaction (HCI)** applications built on the
**MPAI-AIF** AI Framework. Each application is a genuine AIF **Module** (a graph
of AI Modules) executed by the AIF **Controller** and driven by a **User Agent**.
The applications share a common runtime, a common set of AI Modules (AIMs), and a
common Portable Avatar user-interface library.

Developed by MPAI - Moving Picture, Audio and Data Coding by Artificial
Intelligence (<https://mpai.community>). Contact: secretariat@mpai.community.

Relevant MPAI Technical Specifications:

- **MPAI-AIF** - AI Framework (Controller, AIMs, AIWs/Modules, User Agent, Store): <https://mpai.community/standards/mpai-aif/>
- **MPAI-MMC** - Multimodal Conversation (ASR, EDP dialogue, TTS, SIR/ESD speaker, SOA): <https://mpai.community/standards/mpai-mmc/>
- **MPAI-PAF** - Portable Avatar Format (RSR rendering, PSD, GFD, FIR/EFD face): <https://mpai.community/standards/mpai-paf/>
- **MPAI-OSD** - Object and Scene Description (IDR reconciliation, visual scene, time): <https://mpai.community/standards/mpai-osd/>

---

## The applications

| App | Name | What it does |
|---|---|---|
| **AMQ** | Multimodal Question Answering | Show a picture and ask a question about it; the Portable Avatar answers aloud. |
| **NPR** | Newspaper Reading | Choose a newspaper page, click at the top of a column: the avatar reads it aloud. |
| **MTS** | Multimodal Translation | Speak in one language and hear the avatar say it in another. |
| **MCA** | Multimodal Conversation | A spoken conversation; no identity is asked for or kept. |
| **AMC** | Affective Multimodal Conversation | The avatar reads your words, voice and face and answers with feeling. |
| **ACR** | Access Registration | Register a person's face and voice into the gallery that MAC reads. |
| **MAC** | Access Control | Recognise a person from face and voice; access is granted only when both agree. |

They are offered by the MAS Service and its launcher (`Apps/MAS`, the client is Thalia) and also run alone (`Standalone`). Beside them: ABV (virtual
avatar meetings), AIH (HSP, the Health Front End and Back End), CAE (ASM and the CAE-USC Apps), CAV (the Connected Autonomous Vehicle and its HCI), and MMM
(the Metaverse Meeting). Each has its folder under `Apps/`, `AIMs/` or `MMM/`.

Each application presents a 3-D **Portable Avatar** that guides the user by voice.

---

## Architecture

### AIF in brief

MPAI-AIF organises an application around a few standard notions:

- **AIM (AI Module)** - a unit of processing with typed input and output ports.
  At a port, **only the data type matters**, not the port name.
- **Module (AIW)** - a *composite* AIM: a graph of sub-AIMs wired by data type.
  A Module is defined by an **L3** descriptor (JSON, under `AIMs/AMDs/`) that
  lists its sub-AIMs, its boundary ports, and the internal topology.
- **Controller** - the runtime. Given a Module's L3 it instantiates each sub-AIM
  through a **provider**, wires them, and executes the graph. It exposes
  `MPAI_AIFU_MODULE_Start / RunAsync / ResumeAsync / Stop` to the User Agent.
- **User Agent (UA)** - the application "brain with I/O limbs". It acquires and
  delivers real-world data (camera, microphone, the avatar) and **orchestrates**
  the Controller. The UA is *not* part of the Module.
- **Data types & qualifiers** - every port carries a typed object with a
  qualifier (format, attributes). The JSON schemas of the data types are under
  `schemas/`.
- **Shared Storage** - a governed store; MAC/ACR use it as the enrolment gallery.

### The pattern used by every app

```
User Agent  --drives-->  Controller  --builds & runs-->  Module (sub-AIMs)
   |  (acquire face/voice/speech, present the avatar)          |
   \------------------ boundary inputs / outputs --------------/
```

- The UA drives the Module only through the Controller.
- A **provider** (a small switch class per app) constructs the leaf AIMs named by
  the Module's L3.
- The UA's behaviour is described by a **WDL** guidebook, the `.orch` file under
  `AIF/UserAgent/Orchestration/`, which the UA code realises.

### How some of the Apps are composed

| App | Module (L3) | Leaf AIMs (via the app's provider) |
|---|---|---|
| MAC | `MMC-MAC-V2.5` | `PAF-FIR` (SCRFD+ArcFace face), `MMC-SIR` (ECAPA voice), `OSD-IDR` (reconcile), `PAF-RSR` (the avatar) |
| ACR | `MMC-ACR-V2.5` | `PAF-EFD` (face descriptors), `MMC-ESD` (speech descriptors), `PAF-RSR` (avatar prompts) |
| MCA | `MMC-MCA-V2.5` | `MMC-ASR` (Whisper speech->text), `MMC-EDP` (local LLM via Ollama), `MMC-TTS` (Piper), `PAF-GFD`, `PAF-PMX` (the avatar's face and its mixing) |
| MTS | `MMC-MTS-V2.5` | `MMC-ASR` (Whisper speech->text), `MMC-TTT` (M2M100 translation), `PAF-RSR` (the avatar speaks the translation) |

The Portable Avatar (`PAF-PAV`) carries the speech and the face and body motion that go with it. Capture and delivery are the User Agent's (or the client's, for a Service), never an AIM's: the
Units of its Physical Layer (`AIF/UserAgent/PhysicalLayer`) - microphone with voice activity detection, camera, loudspeaker, the avatar page.

---

## Repository layout

```
AIF/              the AI Framework
  AimHost/ Communication/{Channels,GlobalStorage,SharedStorage}/ Controller/ ControllerApi/
  Metadata/ PortData/ Trust/ RootOfTrust/
  UserAgent/      the User Agent: Assets, Clients (AsmStudio, Browser, Desktop), Orchestration, Rca, RcaConsole,
                  Remote, Wdl, PhysicalLayer, UaKit (with SpatialAudio)
  Access/         local data of the access service (not published)
AIMs/             the AIMs: AMDs, Core, Providers, Onnx, AIH/V1.1, CAE3/V1.0, CAV/V2.0, CVE/V1.0, MMC/V2.5, OSD/V1.5, PAF/V1.6,
                  aim-settings.json, Build-Packages.ps1
Apps/             the applications, one folder per standard: ABV (VCT, AVS, VCR, VMS), AIH (HSP: the Health Front End and Back End), CAE (ASM), CAV (CAO, CAVViewer,
                  HCIApps), MAS (Server, Service, the launcher, MAS-App, Language), MMC (ACR, AMC, AMQ, MAC, MCA, MTS, NPR)
Demos/            recordings of the CAV simulation
docs/             the documents of the software
MKG/              the MPAI Knowledge Graph
MMM/              the Metaverse Meeting software: Api, Client, Instance, Server, Viewer
schemas/          the JSON schemas of the standards
Store/            the MPAI Store: the core, Service, App
MPAIStore/        what MPAI gives the Store: the L3s and the packages of the AIMs (L3s, Packages)
Support/          local tools that are not source (Ollama, Python, native libraries); no models
Test/             the tests: AsmApp, AsmVoice, CAV (recordings code), Data, Expected, legacy, Mpai.Aif.Tests, Reports, Results
```

---

## Prerequisites

- **.NET 10 SDK**, Windows (the UIs are WPF + WebView2).
- A **webcam** and **microphone** for MAC and ACR.
- For the dialogue (EDP, in MCA, AMC): a local **LLM served by Ollama** (`Support/Ollama`, or your own); Whisper and Piper are obtained through Access.
- **Model files are NOT included** in this repository (size and licensing). Each
  application's *Models & Prerequisites* section lists exactly which models it
  needs, with file names, sizes, **SHA-256** and sources; obtain them separately
  - and nothing is placed by hand: the Controller obtains what an AIM declares (address, SHA-256, terms) and keeps it in `AIF/Access`.

---

## Build & run

Each Application has a project (`dotnet build`) and, for the Standalone ones, a build script that produces a single-file executable:

```
Apps\MMC\MAC\Standalone\MacAppBuild.bat     ->  MacApp.exe
Apps\MMC\ACR\Standalone\AcrAppBuild.bat     ->  AcrApp.exe
```

The tests: `dotnet test Test\Mpai.Aif.Tests`. The first run obtains the models the tests need through Access (it takes time and disk); a test whose model cannot be had is skipped, not failed.

An application resolves its root (to find `AIMs/`, `AIF/UserAgent/`, `AIF/Access/`)
from the executable's location. Test data (pictures, speech, newspaper pages) is in `Test/Data`.

---

## Models

Model binaries are not in the repository and not in a `Models` folder. The per-application Developer guides
give a **Models & Prerequisites** table with, for each model: the setting that names it, file name, size, **SHA-256** (the authoritative identity -
verify a downloaded file with `Get-FileHash <file> -Algorithm SHA256`), and the
source. The Metadata of each AIM (`AccessSources`) names the public source of every model and datum it uses, with its SHA-256 and its terms, and the Controller obtains and checks it from there; [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) lists them all.
Families used: InsightFace (SCRFD detector, ArcFace recogniser),
ECAPA-TDNN of 3D-Speaker (speaker, converted by MPAI), Piper (text-to-speech voices),
whisper.cpp (speech-to-text), and an Ollama-served local LLM for dialogue.

---

## Status

These are reference implementations demonstrating MPAI-AIF end to end. Face and
speaker recognition quality depends on enrolment and on lighting/acoustic
conditions. The dialogue Apps require a running local LLM; response latency reflects local
inference. Biometric galleries are user data and are not part of this repository.

---

## Licence

**BSD 3-Clause License** - `Copyright (c) 2026 MPAI - Moving Picture, Audio and
Data Coding by Artificial Intelligence`. See [`LICENSE`](LICENSE). Each
application folder also carries a copy of the licence.

**The licence is that of the code. The models and the databases the code uses are not part of it, and each has its own licence;** some restrict use, for example to non-commercial research (the InsightFace models, the wav2vec2 speech emotion model, the Ready Player Me avatar), and the pictures and recordings of the databases are of real people. Read [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) before you use or pass on a model or a database. The source files that use them say so in their first lines.
