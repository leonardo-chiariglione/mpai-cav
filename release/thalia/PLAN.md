# Thalia: restructuring plan

Status: DRAFT for review. Nothing in `D:\DI` has been changed. Written from `origin/main` of
`mpai-cav` (2026-10-09). File names below are paths in that tree.

## 1. The principles agreed

1. The unit of reuse is the **basic AIM**. The collection of all MPAI AIMs, by standard (`AIMs/`), is the **MPAI MW**.
2. A **composite** is reused or kept with its application by a judgement about that AIM, not by a rule:
   RSR is reused as it is (shared); CAV-HCI is CAV's alone.
3. An **application** draws only on the MW AIMs it needs, and only the application sees its own AIMs.
4. **Thalia is a combination of applications** (MAD, AMQ, MAT, MPD, ACR, MAC today). She does not see their AIMs
   *in code*. Which apps she offers changes over time: some stay, some go, some are added.
5. She is **not blind at run time**: she knows what is loaded and may keep an expensive model (Whisper) loaded
   because it is likely to be used again.
6. Thalia and the CAV applications stay separate. The repository holds applications of any nature, each
   self-contained, over the MW. The structure is `D:\DI`'s own.
7. Resetting a model costs less than loading it.

## 2. What the code does today

| # | Finding | Where |
|---|---|---|
| 1 | The Service is compiled against the AIMs of all six apps: `MasService.csproj` references `AIMs/Providers` and ASR, TIQ, TTS, PSD, GFD, GBD, SAS, SAR. | `MAS/Service/src/MasService.csproj` |
| 2 | `Program.cs` hard-codes the seven Module ids, fetches those L3s from the Store by that list, builds the six providers, starts all seven Modules, and initialises the gallery for ACR and MAC by name. | `MAS/Service/src/Program.cs` |
| 3 | `WarmUp.cs` is written app by app (MAD typed and spoken, AMQ, MAT per language, MPD; MAC and ACR not warmed). | `MAS/Service/src/WarmUp.cs` |
| 4 | Only two projects reference `Mpai.Providers`: the Service and `Mpai.Aif.Tests`. Moving providers is local. | all `.csproj` |
| 5 | Each provider only builds **leaf** AIMs. "The Controller builds the composite from its L3; this provider supplies ONLY the leaf AIMs the topology names." So a composite is already data (an L3). | `AIMs/Providers/*Provider.cs` |
| 6 | The six providers repeat the same RSR leaf wiring (PDX, TTS, GFD, GBD, SAS, SAR). RSR is shared in fact, copied in code. | same |
| 7 | A package route exists: `PackageAimProvider` loads an AIM from a package, found by the L3's `ImplementationURI`, through its `IAimPlugin`; `Build-Packages.ps1` builds the packages; `"AimSource": "Packages"` turns it on. The compiled-in providers are a fallback and stay in the Service. | `AIF/Controller/PackageAimProvider.cs`, `AIMs/Build-Packages.ps1`, `Program.cs` |
| 8 | **17 of the 19** basic AIMs Thalia uses have an `IAimPlugin`. **EFD and ESD, the two ACR leaves, have none.** | `AIMs/**/*Plugin.cs` |
| 9 | What an app offers is already data: `Apps`, `Collections`, `StoreUrl` and `AppCatalogue.Scan` in the config. The L3 list and Module list in `Program.cs` are the exception. | `MasServerConfig.cs`, `Program.cs` |
| 10 | The Controller API runs **one instance of each Module**, shared by all clients, one exchange at a time; a Module is stopped when its last holder stops it. | `MAS/Service/src/ControllerApiRunner.cs` |
| 11 | `IAimProcessor` has `InstanceId` and `ProcessAsync` only. **No reset, no unload.** `AimLifecycle` is Idle, Running, Paused, Stopped: it drives Stop and Pause, not the model. | `AIF/Controller/IAimProcessor.cs`, `AimLifecycle.cs` |
| 12 | `ResourcePolicy` reads Name, Minimum, Request, Maximum from the L3 (memory in GB). I found no code that keeps or evicts an AIM by it. **To confirm.** | `AIF/Controller/ResourcePolicy.cs` |
| 13 | **Whisper is already shared.** `WhisperServer.For(program, model, threads, args)` returns one server per key, for every AIM instance that names them. **Piper too:** `ResidentPiper`, one process per program, voice and arguments; least recently used retired above 8. Both die with the Service (`ResidentProcesses`). | `AIMs/MMC/V2.5/ASR/WhisperServer.cs`, `TTS/ResidentPiper.cs`, `AIMs/Core/ResidentProcesses.cs` |
| 14 | **ONNX models are not shared.** `OnnxSessions.Create(path)` opens a new session each call, and each AIM (SIR, SPE, TIQ, TTT, FIR, FPE, EFD...) holds and disposes its own. A model used by two Modules is loaded twice, as far as I read. **To confirm by counting sessions.** | `AIMs/Onnx/OnnxSessions.cs` |
| 15 | Ollama (EDP) is outside the process. A conversation's memory is carried by the workflow, not the Service; `Session_End` clears Shared Storage only. | `Program.cs`, `UserAgent.cs` |
| 16 | Providers share an engine between leaves of a Module (ACR: one ArcFace, one ECAPA, one SCRFD for EFD and ESD). A plug-in "builds its own dependencies inside Create", so on the package route that sharing is lost unless engines are cached by model. | `AcrProvider.cs`, `IAimPlugin.cs` |

## 3. Structure: the one `D:\DI` already has, unchanged

No folder is renamed or moved. The structure the author described is the existing one:

| Principle | Where it is |
|---|---|
| All AIMs in one group, by standard (the "MPAI MW") | `AIMs/` (`MMC`, `PAF`, `OSD`, `CAE3`, `CAV`, `CVE`; plus `Core`, `Onnx`, `AMDs`) |
| The framework | `AIF/` |
| The User Agent stands on its own | `UserAgent/` |
| An app is a folder | `Apps/MAD`, `AMQ`, `MAT`, `MPD`, `ACR`, `MAC` |
| Thalia is a combination of apps | `Apps/MAS-App` (its `app.json` lists the apps) with the Service in `MAS/` |
| MKG depends on no standard | `MKG/` |
| Schemas by standard | `schemas/` |

What is wrong is not the structure but one coupling inside it: the Service is compiled against the AIMs of
every app (section 2, findings 1 to 4). Steps 0 to 5 remove it. Nothing moves.

- **Models are not in git.** They stay in `D:\CI` and are not published. Each app's Markdown lists, for every
  model, its file name, size, SHA-256 and where to download it. The models MPAI developed are already in
  `mpai-community`: link them there.
- A **release** is a selection of `D:\DI` that keeps its paths. Thalia's selection is `release/thalia`.

## 4. Steps, in order

Each step must build and pass the tests before the next. Do them in `D:\DI`, in a local session, on a branch.

**Step 0. Fix what is open in the tests.** Split `Test/Mpai.Aif.Tests` so Thalia's tests reference only Thalia's
projects (it now pulls in 19 more: MMM, CAV Recordings, CAE...). *Done when:* Thalia's tests build with the 46
projects of her closure and nothing else.

**Step 1. Plug-ins for EFD and ESD.** Write `EfdPlugin` and `EsdPlugin` like the other 17. *Done when:* ACR runs
with `"AimSource": "Packages"` and the compiled ACR provider disabled.

**Step 2. A shared engine cache for ONNX models.** Add to `Mpai.Onnx` a cache keyed by model path and device, the
way `WhisperServer` is keyed, with a count of users and a limit. Make the ONNX AIMs take their sessions from it.
*Done when:* two Modules using SIR hold one SIR session, and a test counts them. This also repairs finding 16.

**Step 3. Packages for all 19 leaves, and a check.** Run `Build-Packages.ps1` for the leaves Thalia uses. *Done
when:* every Thalia app runs from packages alone, `"AimSource": "Packages"`, with the compiled providers removed
from the config path in a test.

**Step 4. Remove the AIMs from the Service.** Delete the references to `AIMs/Providers` and the 8 AIM projects from
`MasService.csproj`, and the provider lines from `Program.cs`. Remove `Mpai.Providers` and `AimBinaries`; their job
(the binary that implements an AIM, measured under Zero Trust) comes from the package. *Done when:* `MasService`
builds with no AIM reference, starts with no apps, and starts again with one app added.

**Step 5. Apps as data.** Replace the hard-coded Module list, the Store fetch list, the gallery initialisation by
name and `WarmUp.cs` by what each app declares: its Module id, whether it uses the gallery, its warm-up turns
(in its own folder, e.g. `warmup.json`). Thalia reads the app list from the config, `Apps/MAS-App/app.json` or the
Store. *Done when:* adding an app is a folder, an L3 and a package, with no change to Thalia.

**Step 6. One manifest per app,** in the app's folder, listing the `AIMs/` projects and data it needs; Thalia's
selection becomes the union of her apps' manifests plus the Service and the User Agent. *Done when:* the manifest
walk (`Copy-Thalia.ps1`, adapted) gives the same tree as the 46-project closure minus the AIMs, plus the apps'.

**Step 7. The release.** CI, Markdown, the gallery without the author's descriptors, then `mpai-sw`.

## 5. Keeping a model warm, and reset

What I propose after reading the code.

- **Most engines are stateless per request.** Whisper and Piper take the audio or the text in the request and keep
  nothing between requests; conversation memory is carried by the workflow. So for them there is nothing to reset.
  Keeping them loaded is enough, and they are already shared.
- **The cache decides how long, and Thalia sets the policy.** Move the keep-warm decision to the engine cache
  (the `WhisperServer` and `ResidentPiper` pattern, now also for ONNX): one entry per model and settings, a limit,
  a time since last use. Thalia sets the limit and the idle time in the config. The MW code never learns which app
  asked. That is "not blind" without a reference to any AIM.
- **Reset only where an engine has state.** The candidates are EDP (the Ollama context) and the gallery. I'd add
  an optional `IResettable` to `AIF.Controller` (a method `Reset()`), called by the Controller when an AIM instance
  passes to another session, and tested: run app A, then app B on the same engine, and B shows no trace of A.
  No AIM implements it until a test shows it needs to.
- **Unload** is the cache's act, by policy, or on app removal for an engine only that app used.
- **To confirm before building:** findings 12 and 14, and whether Ollama holds a context across turns that the
  workflow does not clear.

## 6. What changes for the release

- Thalia's manifest shrinks to the Service, the clients, the avatar assets and the app list.
- Each app has a manifest of the MW AIMs it needs.
- A release has two parts: the Thalia program, and the app packages, which can be added or removed later.
- `release/thalia/Copy-Thalia.ps1` and `projects.txt` describe today's monolithic closure (46 projects). They
  become the check for Step 4: after it, Thalia's own closure must contain no AIM project.

## 7. Decisions

Settled by the author:
- Restructure first, then release. The structure is `D:\DI`'s own; no folder moves.
- **AIMs of whatever nature stay together** (`AIMs/`): basic or composite, RSR, SAF and PSE included. Apps are apps
  (`Apps/`).
- **`mpai-sw` holds only what Thalia requires.** CAV, MMM, MKG, `CAV/Recordings`, the HCI apps and `legacy/` are
  not published there. (Thalia's selection is `release/thalia`.)
- Models are not in git; the Markdown lists them with their sources. The list is
  `docs/MAS-App-Models.md` in `mpai-sw`.

What the existing `mpai-sw` models Markdown leaves to do (from reading it, 2026-10-10):
1. **It covers four apps** (MAD, AMQ, MAT, MPD). ACR and MAC need three more entries: `scrfd_10g_bnkps.onnx`,
   `glintr100.onnx` (InsightFace `buffalo_l`) and `ecapa-tdnn.onnx` (SpeechBrain `spkrec-ecapa-voxceleb`, converted
   to ONNX; medium confidence in `docs/models-provenance.md`). Sizes and SHA-256 from `D:\CI\Models`.
2. **BLIP is both bundled and listed.** The old `mpai-sw` carries the four BLIP ONNX files through Git LFS
   ("the one model MPAI built"), while the Markdown lists BLIP as a model to obtain. One of the two must change.
3. **The licence warning stays:** "Several are research or non-commercial licences, and the Piper voices each have
   their own." It must be resolved per model before the release says it is redistributable.

Still open: which models, besides BLIP, are the MPAI-developed ones in `mpai-community`.

## 8. Risks

- **Zero Trust.** `AimBinaries.Of` and the Store's approval measure the binary that implements an AIM. Moving to
  packages changes what is measured; check `M3223` handling before Step 4.
- **Lost sharing.** Finding 16: without Step 2, moving ACR and MAC to plug-ins loads models twice.
- **Startup time.** The Service starts and warms every Module before accepting anyone. With apps as data, the
  order and the cost of that start change; keep the warm-up (finding 3), made data-driven.
- **A refactor of this size in `D:\DI` can hide breakage** in the projects outside Thalia's closure
  (CAE, CAV, MMM) that reference `Core`. Build the whole solution at each step, not only Thalia.
