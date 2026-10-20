# Third-party notices

The software in this repository is under the BSD 3-Clause License of `LICENSE`. The
following files, made by others, are included under their own licences.

| Files | What | Licence |
|---|---|---|
| `MMM/Viewer/personae/Persona1.glb`, `Persona2.glb` | The Personae of the MMM viewer: Microsoft Rocketbox avatars Female_Adult_01 and Male_Adult_01, converted to glTF (`MMM/Viewer/personae/README.md`) | MIT, Copyright (c) 2020 Microsoft - `MMM/Viewer/personae/LICENSE-Rocketbox.md` |
| `Apps/ABV/AVS/Server/avatars/Persona1.glb`, `Persona2.glb`, `Male03.glb`, `Male05.glb`, `Male08.glb`, `Thalia.glb` | The avatars of the Avatar Videoconference demo: Microsoft Rocketbox avatars converted to glTF (FBX2glTF; the Rocketbox `Bip01` skeleton). `Thalia.glb` is the Secretary's avatar of that demo and is not the Ready Player Me avatar of the Thalia client (`AIF/UserAgent/Assets/cav-avatar.glb`) | MIT, Copyright (c) 2020 Microsoft - `Apps/ABV/AVS/Server/avatars/LICENSE-Rocketbox.txt` |
| `AIF/UserAgent/Assets/cav-avatar.glb` | Thalia's avatar, made with Ready Player Me | Creative Commons Attribution-NonCommercial-ShareAlike 4.0 (CC BY-NC-SA 4.0), https://creativecommons.org/licenses/by-nc-sa/4.0/ |

**Thalia's avatar is used for demonstration.** CC BY-NC-SA 4.0 allows non-commercial use,
with attribution to Ready Player Me, and adaptations under the same licence; other uses
are restricted. The avatar page says so under the avatar. Whoever uses this software
otherwise must replace `cav-avatar.glb` by an avatar they may use: any glTF avatar with
the 52 ARKit blendshapes works with the avatar page - for example a Microsoft Rocketbox
avatar (MIT), whose ARKit blendshapes are named `AK_nn_<Name>` and need only that
mapping.

`AIF/UserAgent/Assets/studio.hdr`, the studio lighting of the avatar page, is an image
whose origin is not recorded; it can be replaced by any equirectangular HDR, such as
the CC0 studio HDRIs of Poly Haven.

**Obtained separately, not in the repository.** The spatial renderer of the User Agent
(`AIF/UserAgent/UaKit/SpatialAudio`) calls Steam Audio 4.8.1 by Valve Corporation, under the
Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0), whose native library
is put in `Models/SteamAudio` (`docs/models-provenance.md`). OCR uses RapidOcrNet
(Apache-2.0, a NuGet package) with PaddleOCR's PP-OCRv5 models (Apache-2.0).

**Obtained separately, not in the repository, used by the CAE-USC software.** The Speech Model of the Speech Restoration System uses the tone-colour converter of OpenVoice V2 (MIT licence; https://github.com/myshell-ai/OpenVoice and https://huggingface.co/myshell-ai/OpenVoiceV2), which the person who runs it downloads; the demonstrations of the CAE-USC Apps use recordings of LibriSpeech (CC BY 4.0) that are not included. The voices of the Piper Text To Speech and the other models are listed, with where they come from, in `docs/models-provenance.md`.
