# The Personae of the MMM viewer

| File | From | Persona in UC2 |
|---|---|---|
| `Persona1.glb` | Microsoft Rocketbox, `Assets/Avatars/Adults/Female_Adult_01/Export/Female_Adult_01_facial.fbx` | Persona1ID (Friend1) |
| `Persona2.glb` | Microsoft Rocketbox, `Assets/Avatars/Adults/Male_Adult_01/Export/Male_Adult_01_facial.fbx` | Persona2ID (Friend2) |

Microsoft Rocketbox, https://github.com/microsoft/Microsoft-Rocketbox, MIT licence
(`LICENSE-Rocketbox.md`).

Each has its skeleton (161 joints) and 175 facial blendshapes: the 52 of ARKit
(`AK_01_BrowDownLeft` ... `AK_52_TongueOut`), 15 visemes (`AA_VI_...`) and the FACS Action
Units (`AU_...`).

How they were made (2026/10/01), from the `_facial.fbx` and the colour, normal and opacity
textures of each avatar:

1. the TGA textures to 1024 x 1024 - colour and normal maps JPEG (quality 85), the opacity
   map PNG with its alpha - next to the FBX, under the names the FBX gives them;
2. FBX2glTF v0.13.1 (https://github.com/godotengine/FBX2glTF):
   `FBX2glTF --binary -i <avatar>_facial.fbx -o PersonaN`.

When loading them:

- the opacity material (hair, eyelashes, eyebrows) must be made transparent with an alpha
  test: glTF does not keep the FBX's TransparentColor;
- they are measured as posed by their skeleton (the raw geometry lies along Z);
- they come in the T-pose.
