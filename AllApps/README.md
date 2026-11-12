# All the Apps that run

Double-click the one you want: each one starts what it needs. The first start of a program builds it, which takes a little longer.
Each standard is a folder; the Apps of a standard are the files in it, named as the App. `_support` holds what the launchers share (settings, Ollama, the Service); the software is the folder above `AllApps`.

| Folder | App | What it does |
|---|---|---|
| `CAE` | `ASM` | Audio Scene Management by voice: speak or type to an audio scene; it starts Ollama itself (README: `Apps\CAE\ASM\README.md`) |
| | `EAE`, `SRS`, `EES`, `ARP` | the Apps of CAE-USC, with dialogs to pick the files: EAE the audio of a microphone array, SRS restores damaged speech, EES adds emotion to speech, ARP preserves an open reel tape (results in `Downloads\MPAI USC`) |
| | `EAE server (virtual room)` | a virtual room of real speech; open http://localhost:5190/ with headphones |
| | `SRS demo (one click)` | restores a damaged recording with the voice of another speaker; plays original, damaged, restored |
| `MMC` | `ACR`, `MAC`, `MCA`, `MTS`, `AMC`, `AMQ`, `NPR` | the standalone programs: access registration, access control, conversation with the avatar, translation, affective conversation, picture questions, newspaper reading |
| `CAV` | `CAV`, `HCI` | the connected autonomous vehicle; the passenger conversation with it |
| `ABV` | `ABV server (this PC only)`, `ABV server (open to the network)` | the avatar videoconference |
| `MMM` | `MMM demo`, `MMM world`, `MMM meeting` | the metaverse meeting: the demo, a conference with people outside this network, personal links |
| `AIH` | `HSP demo` | the first slice of AIH-HSP, narrated |
| `AIF` | `Store service`, `Store app` | the repository of AIMs; submit an AIM |
| `MAS` | `MAS App desktop client`, `MAS App browser server`, `Stop the MAS App and the Service`, `MAS App local apps` | The MAS App, the MAS launcher: you choose an App in its window; `MAS App local apps` lets its icons start the standalone MMC Apps |
| `_retired` | `ASM typed (old window)` | the first ASM window, typed commands; it cannot be built any more |

The address of the Service is in `_support\_settings.bat`.
