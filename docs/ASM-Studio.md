# ASM Studio

The application of Audio Scene Management, on the current AIMs: `AIF/UserAgent/Clients/AsmStudio` (start it with `Run-AsmStudio.bat`). It is a User Agent: Audio Object Editing and Audio Scene Editing do the editing, one User Command at a time; the Command to revoke is a User Command too, so **Undo** (Ctrl+Z, or "undo") revokes the last. Every version is kept in Shared Storage (`Documents\ASM Studio\storage`).

- **The editing space** fills the window: a view from above, you at the origin facing up the page, **x** (right) and **y** (ahead) numbered in metres along the edges. Drag a sound to move it; drag the empty space to pan; the wheel zooms; right-click a sound for its properties (x, y, z, yaw), to rename it or take it out.
- **Names.** A thing has the name the user gives it and its kind as an extension, as in a directory: `violin.auo` (an audio object), `welcome.bso` (speech), `concert.asd` (a saved scene). The identifiers of the AIMs (BAO000001) are not shown. F2 renames.
- **The library** (left, can be put away) lists them. Import WAV files, or make speech from a text (the synthesiser speaks it); drag a sound into the space, or double-click it.
- **Hearing.** Play scene renders it binaurally with Steam Audio (panned by position where it is not installed).
- **Thalia** (a small window) speaks the answers; her mouth follows her voice. The head is the one of the CAV viewer (Ready Player Me, CC BY-NC-SA 4.0: the licence is shown under her), drawn with three.js from a CDN, so it needs an internet connection. **Text** is the small text window: type there what you want ("move the violin two metres to the left") and read the answers. **Hold to talk** (bottom right) records you; Whisper writes down what you say.
- Sentences are understood by the dialogue AIM (Entity Dialogue Processing), which needs Ollama (`llama3.2:3b`). Without it: undo, play, stop and save are understood, and everything else is done with the mouse.
- `AsmStudio.exe --selftest` runs the library, the commands, the undo, the saving, the rendering, the speech and the dialogue without windows and writes `Test/Reports/AsmStudio/selftest.txt`.
