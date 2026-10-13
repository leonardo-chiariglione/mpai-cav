# CAE-ASM: where a User Command acts, and how one is revoked (2026/10/11, with the author's answers)

## 1. The rules of the AI Framework this rests on

- The Module receives inputs from the User Agent (UA) and gives outputs to it, and the UA reaches it only through the Controller API.
- So the UA does not act on the Module's contents: it **passes what the person asks to the Controller**, as data on the Module's boundary.
- **The AI Framework is not changed**, nor the CAE-ASM Module: **the only change is to the User Command** (CAE-UCM).

## 2. A User Command

- A User Command has an **ID** (`UserCommandID`), which its report returns. It is addressed, as now, to the port of the AIM that executes it; each AIM keeps its own.
- **"Play X"** is the existing `DeliveredObject`: a request for X, an Object or a Scene named by its ID, which may be one an AIM holds open in memory or one in Shared
  Storage. The Module outputs it; the UA's Loudspeaker Unit renders it spatially and plays it. The Module never plays; stopping the sound is the UA's own act on its device.
- After a command that modifies a scene, the Module outputs the modified scene, which the UA displays to the user.

## 3. Revocation: one new field of the User Command

- `UserCommandData.RevokedUserCommandID`: **the ID of an earlier User Command to revoke.** It goes to the AIM that executed that command, which **undoes it if it can**.
- The report of the revoking command says, under the action `RevokedUserCommandID` and the ID of the command revoked: **Done** (undone), **Failed**, or **NotSupported**
  (this AIM cannot undo it). An AIM that has versions makes it easy; the standard does not require them.
- Undo, as a person says it, is a revocation of the last command: the UA (or EDP, from voice) gives the ID of the last command.
- **Keeping** (saving) a version is not a command: it is the Controller's retention of what the Module holds (`SharedStorageKeep`).
- Done: the schema `schemas/CAE3/V1.0/data/UserCommand.json` (the field, and the action in the report), the C# `UserCommandData`, and `RevocationTests` (five tests).

## 4. Left to the AIM

- Revoking after later commands acted on the same Object, and revoking a translation (which replaces the Speech Object under the same ID in every Scene that holds it):
  the AIM may be able to undo it or not, and says so in its report.

## 5. Done, and still to do

Done (2026/10/11), in code: **every AIM that executes User Commands can undo** (the author: the AIMs involved in a User Command must be able to undo).
- `UndoJournal` (AIMs\Core\UndoJournal.cs): each AIM records a command that changed something, with how to put it back. **The last command first**: an earlier one is
  reported as Failed ("later User Commands were executed after it: revoke them first") while a later one stands; so is one this AIM never executed or has already undone.
- AOE, ASE, SSE and BME keep every edit as a new version: revoking puts the version before back as the open one (if the command created the Object or Scene, nothing is open).
- SOE edits in place: it keeps what the command overwrote (a change, a Point of View) and puts it back; a translation keeps the original and puts it back under its
  identifier, so every Scene that holds it holds the original again.
- Tests: `RevocationTests` (the schema) and `RevocationAimTests` (AOE, ASE, SOE) pass; `AsmTranslationTests.ATranslationIsRevoked` needs the translation and Italian voice
  models and runs in the night run.

Still to do:
1. EDP (voice): "undo" becomes a revocation of the last command - EDP does not know its ID; the UA that sent it does.
2. The CAE-ASM AIM Metadata is unchanged (no new AIM, no new port).
