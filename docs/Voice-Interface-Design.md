# A voice interface with a fall-back (draft, 2026/10/11, with the author's answers)

Nothing here is built except the chain tested in `Test/Mpai.Aif.Tests/AsmVoiceChainTests.cs`.

## 1. The idea

A user can issue a command **by voice**. If Speech Recognition (ASR) or Entity Dialogue Processing (EDP) do not do the right job - EDP is right about 80% of the time on the 59
requests of `Test/AsmVoice` - **the user can act by text, or by mouse or another modal interface.** Voice is one way in, never the only one.

## 2. Three ways in, one kind of command

| Way in | What the user gives | What makes the command |
|---|---|---|
| Voice | a Speech Object | ASR (Whisper) makes the text, EDP, given the scene, makes the User Command |
| Text | a Text Object (typed) | EDP makes the User Command; ASR is skipped |
| Direct (mouse, sliders, forms) | a User Command, built by the User Agent | nothing: the AIMs take it as it is |

The AIMs that execute (for audio scenes: AOE, ASE, SOE, SSE, BME) see only a User Command (CAE-UCM); they do not know which way it came. The User Agent moves the data between
AIMs through the Controller; AIMs do not talk to one another.

## 3. A composite of its own, separate from CAE-ASM

The voice interface is **general** - ASR, a dialogue AIM and the AIMs of whatever application follows - so it is a separate composite with its own L2 and L3, and the CAE-ASM
L2 stays as it is. Its boundary, as first drawn: speech in, text in, a User Command in (direct); out: the command with its confidences, the reply text, the Text For UA.
To check before the L2 is drawn: whether the Controller allows two writers (EDP's command and the UA's direct command) into one AIM port, or a small merging AIM is needed.

## 4. Confidence, both of them

- **Recognition:** the confidence of ASR in the text, from Whisper's token probabilities, on the Text Object it gives (the Data Exchange Metadata `Confidence`).
- **Understanding:** the User Command's own `Confidence` field (already in CAE-UCM, never filled). EDP's model gives no probabilities, so it is built from: the recognition
  confidence; the agreement of two or three samples of the same request; and checks against the scene (the target exists, the amount is sensible, no pronoun is left).
- **The policy is the User Agent's:** below a threshold it shows "Move the violin 1 m to the left?" and executes only on a yes; and EDP asks a question when the request is
  ambiguous ("Which one?"). The revocation of the last command (`RevokedUserCommandID`) is the last safety net.

## 4b. What a person can set, by any way in

Position and orientation of an Object - all three angles, roll, pitch and yaw, as the Spatial Attitude's Euler angles - and its Acoustic Profile (frequency range, loudness,
directional patterns), a Speech Object's Qualifier, where the scene is heard from, adding, removing, getting something to play. EDP's vocabulary reaches only part of this
(the yaw only); the User Command and the AIMs already take all of it. The MMM Property Change action sets further properties of an Item (perceptibility, size, sources).

## 5. To do, in this order

1. ASR: the confidence of the recognition on the Text Object.
2. EDP: the confidence of the understanding on the User Command; `turn` with pitch and roll; text as an input; "undo" as a revocation of the last command (the UA gives EDP the
   recent commands with their reports).
3. The composite's L2 and L3, once the fan-in question is answered; a test in which voice, text and a direct command reach the same AIM.
4. The User Agent's fall-back: the confirmation, and the mouse and form interface.
