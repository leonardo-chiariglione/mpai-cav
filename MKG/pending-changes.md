# MKG: changes noted during the day, to be applied when the MKG is updated at the end of the day

The code and the schemas change all day. The MKG is not rebuilt for each change: the changes are noted here, and the MKG is
rebuilt and published once, at the end of the day (the author, 2026/10/11).

## 2026/10/11
- **PAF-CRX becomes PAF-VCR** (Videoconference Client Receiver, as VCT is the Transmitter), applied to the L2 from the author's
  figure of 2026/10/11 (fourth; "AV" became "MM"): four sub-AIMs, PDX -> VSC -> **MSC** -> RSR. **PAF-AVC (Audio-Visual Scene
  Creation) is now PAF-MSC (Multimodal Scene Creation)** - the acronym MSC is mine, to be confirmed; file renamed to
  MultimodalSceneCreation.json. VSC takes and gives the whole Multimodal Scene (OSD-BMS), because RSR needs the full MM scene, not
  only the visual part (its output was OSD-BVS). Inputs of VCR: Portable Avatar, Point of View (to RSR). Outputs: Point of View
  (RSR passes it on), Speaking Avatars (XRV-SAV), Multimodal Scene Descr. (OSD-BMS), Output Text (from PDX). PDX -> VSC: MM Scene
  Descr., Avatars, Spatial Attitudes. VSC -> MSC: MM Scene Descr., Avatars, Mouth Spatial Attitudes. MSC -> RSR: MM Scene
  Descriptors, Avatars. PDX -> RSR directly: Personal Statuses, Input Speeches. L2 changes: VSC lost its Personal Status input and
  output; MSC has no speech or personal-status input; RSR gained an optional OutputPointOfView (OSD-OPV); the port labels
  "AVSceneDescriptors"/"AudioVisualSceneDescriptors" became "MultimodalSceneDescriptors" (PDX, VSC, MSC)..
  Sixth figure (same day): "Avatars - IDs": the Avatar ID (OSD-IID) travels with the avatar PDX -> VSC -> MSC -> RSR (no longer PDX ->
  MSC directly) and comes out with the Speaking Avatars (new VCR output OutputAvatarID). VSC and MSC gain an OutputAvatarID output; RSR an
  optional AvatarID input and OutputAvatarID output. This closes the Avatar ID assumption.
  Fifth figure (same day): the avatars' Spatial Attitudes (OSD-OSA) also travel PDX -> VSC -> MSC -> RSR, beside the mouth's, so that RSR
  can put each into its Speaking Avatar. VSC has two OSA outputs (1 Mouth, 2 Spatial Attitude); MSC two OSA inputs (1 Mouth, 2 Spatial
  Attitude) and an OSA output; RSR an optional OSA input. Ports of the same Data Type are told by Port Number.
  Files: PAF/V1.6/AIMs VideoconferenceClientReceiver, VisualSceneCreation, MultimodalSceneCreation (was AudioVisualSceneCreation),
  PortableAvatarDemultiplexing, ResponseAndSceneRendering; Test/Expected/l2-topology.json, schema.json. MetadataTests Schema,
  L2Topology, Conformance and Connections pass. The old PAF-CRX V1.6 is published on schemas.mpai.community (2026/10/09-10): to be
  replaced, and PAF-AVC V1.6 (AudioVisualSceneCreation.json) removed there if it was published.
  Seventh figure (same day): the Input Speeches go PDX -> **MSC** (not RSR); MSC adds each avatar's speech at its face and gives the
  Speaking Avatars with their IDs (XRV-SAV + OSD-IID) instead of the avatar models; RSR takes the Speaking Avatars (new optional input
  InputSpeakingAvatars), adds the Personal Statuses (EPS) to the avatars' speech, face and body, and gives the Speaking Avatars - IDs, the
  MM Scene Descr. and the Point of View. VSC gives the Spatial Attitude of the avatar's mouth - of a characteristic point of its face, from which the model gives the mouth (port label MouthSpatialAttitude, the author's name).
  The Avatar is no longer only a model to animate: it is an animated and speaking avatar (a model with its animation information); RSR is
  substantially enhanced - its GSD adds the Speech PS to the speech, GFD the Face PS to the face, GBD the Body PS to the body. Its L2 and
  its sub-AIM topology are still those of the response-rendering RSR: to be reworked. No registration exists: an ABV User Agent is to be
  defined (a web page where participants register with a nickname and an avatar), and the User Agents of the ABV Modules.
  Author's answers (same day): the animation of an avatar is its Face and Body Descriptors; the Entity State is not used in the ABV, so the
  Portable Avatar (PAF-PAV) loses EntityState and gains FaceDescriptorsObject, BodyDescriptorsObject and EntityPersonalStatus (schema edited);
  a Module has access to the data of an ID (model-or-ID: no change); the nickname IS the Avatar ID; avatars are introduced at start time and then
  referenced - a transmitted Speaking Avatar is the Avatar ID + Face and Body Descriptors + Speech; the AVS User Agent places the avatars around the
  table at start time, in the order of arrival; the VCT and the VCR run in each client; the VMS is also part of the meeting; at least 3-4 avatars.
  "Avatars - IDs" on the VCR arrows = the PAF-AVT (the avatar model with its animation - Face and Body Descriptors - and its ID), not OSD-B3O (author):
  PDX output Avatar (was AvatarModel, OSD-B3O) is PAF-AVT; VSC input Avatar and output OutputAvatar, MSC input Avatar, and the four VCR edges
  carry PAF-AVT. PDX still gives FDO and BDO on their own ports.
  EIGHTH FIGURE (same day): **Visual Scene Creation (PAF-VSC) is removed** (author: its function was minimal): the VCR is PDX -> MSC -> RSR. MSC takes from PDX the
  MM Scene Descr., the Avatars - IDs (PAF-AVT, OSD-IID), the Spatial Attitudes and the Input Speeches; gives the MM Scene Descr., the Speaking Avatars - IDs (XRV-SAV, OSD-IID)
  and the Spatial Attitudes to RSR. The face/mouth point is a standard 1.6 m above the avatar's Spatial Attitude (author), no output for it. The avatar's ID is the nickname; its
  placement is its Spatial Attitude; the model is not in the scene because there is nobody yet to display it. VSC's L2 (VisualSceneCreation.json), L3 and code are deleted.
  NINTH FIGURE (same day): the MM Scene Descr. goes MSC -> RSR -> boundary (not MSC -> boundary); the Point of View goes to the RSR (and, as before, round it); MSC -> RSR
  carries "Speeches" (OSD-BSO). Author, same message: the Body Descriptors should simply be the OSA of the nodes between joints (a change to the content of PAF-BDO / its
  TFA qualifier: not made, to be confirmed).
  Body Descriptors Object (author: it is an Object, select a format and add a Qualifier): a new content format in TFA/V1.5/formats/BodyDescriptorsContentFormats.json -
  "OSA (Spatial Attitudes of the nodes between joints, OSD-OSA-V1.5)" - the Data a JSON array of OSD-OSA objects, the TFA-BDQ Qualifier naming the format; C#
  BodyDescriptorsObject.NodeAttitudes and FromNodeAttitudes. GBD still gives BVH (Thalia's avatar body uses it): not changed.
  CAE-UCM (User Command): new field UserCommandData.RevokedUserCommandID - the ID of an earlier User Command to revoke; the AIM that executed it undoes it if it can; the report
  says Done / Failed / NotSupported under the action RevokedUserCommandID. Nothing else changes (no new AIM, no AIF change); see docs/ASM-Commands-and-Revocation.md.
  AOE, ASE, SOE, SSE and OSD-BME now undo a revoked User Command (UndoJournal in AIMs/Core; the last command first); no AIM Metadata change.
  RSR and SAF (author's figures of 2026/10/11, same as the L2s: RSR = MMC-PDX + MMC-TTS + PAF-SAF; SAF = GFD + GBD + SAS): the pass-throughs (Point of View, Spatial
  Attitude, MM Scene Descr.) are external to the RSR - in the VCR the Point of View goes boundary to boundary and the scene from the MSC straight to the boundary. The VCR now feeds
  the RSR with the Avatar (PAF-AVT, its Avatar Model input), the speech (its Input Speech), the Avatar ID, the Spatial Attitude and the Personal Status; the RSR's SAS forms the
  Speaking Avatar. MSC gives therefore the Avatar, the speech, the ID and the Spatial Attitude, not an XRV-SAV. RSR L2: only AvatarID and SpatialAttitude inputs and an
  OutputAvatarID output added to the committed one.
  VCR code (AIMs/PAF/V1.6/VCR, Mpai.Paf.Vcr, continuous AIMs, each with an L3 and tests): PDX (1PAF-PDX-V1.6-I01) and MSC (1PAF-MSC-V1.6-I01: places each
  avatar in the BMS at its Spatial Attitude, adds its speech at the mouth, gives the Speaking Avatar = an Avatar (PAF-AVT) + speech, author 2026/10/11). The
  Spatial Attitude is not inside the SAV: it travels on its own port. Not yet: the RSR enhancement, the VCR composite L3, a Controller-level run.
  Head model HDM (OSD-HDM-V1.5, in OSD - the standard is to be confirmed): a parametric head to begin with (author) - a sphere (radius 8.75 cm), the mouth
  in front of the characteristic point of the face, the speech radiating as a dipole (weight, power): schema OSD/V1.5/data/HeadModel.json.
  PAF-PDX (the VCR's) is now code: AIMs/PAF/V1.6/PDX (Mpai.Paf.Pdx), a continuous AIM, L3 1PAF-PDX-V1.6-I01, six tests (PdxTests); the ID of each
  object of an avatar carries the Avatar ID before a '#'.
  To do: the 3OD/SOD Units: the SOD needs a head model for the propagation of each avatar's speech (author); each avatar must carry
  its nickname as given by the participant (where: open); the MKG's Units list (units.json) to be updated for the 3OD (Speaking Avatars, BMS) and SOD.
- **PAF-FPE is a leaf; PAF-BPE is new (a leaf)** (author, 2026/10/11): Face and Body Personal Status Extraction, used by MMC-PSE. FPE L2: no
  Sub-AIMs (it had EFD and PFI), FaceObject and optional FaceDescriptors in, FacePersonalStatus out. BPE L2 (BodyPersonalStatusExtraction.json):
  BodyObject (OSD-VIO) and optional BodyDescriptors in, BodyPersonalStatus (MMC-BPS) out; no L3 and no code (there is no body-emotion
  model). MMC-PSE lists FPE and BPE as Sub-AIMs in place of EFD, PFI, EBD and PBI (EFD, EBD, PFI and PBI stay as AIMs). The RSR needs GFD and
  GBD, not FPE and BPE. Test/Expected re-recorded; all 11 MetadataTests pass.
- **XRV-SAV (Speaking Avatar)** gains the Spatial Attitude of the avatar in the scene (author's edit of SpeakingAvatar.json,
  not yet committed): there is no longer one Speaking Avatar but several, each carrying its OSA, so that the 3OD places each in
  the scene. The 3OD Unit takes the Speaking Avatar (and the BMS): its ports in units.json to be updated accordingly.
  (The edit writes the property "Spatial Attitude" and the $ref ".../Spatial Attitude.json", with a space: SpatialAttitude?)

## 2026-10-12 XRV-LTP alignment (for the end-of-day MKG rebuild)
- 8 AIM L2s (D:\AI\schemas\XRV1\V1.0\AIMs) aligned to the web specs; ADG -> ACG; Implementations/ResourcePolicies filled in all 8.
- Data Type codes follow the schemas: SCR, ATB, VDI, VDO, VTS, SOC, SVA (new), GSA (Gesture Attributes, no Header).
- New schema ScriptVenueAssociations.json (draft). New web pages: audio-console, biometric-synchrony, cue-list, gesture-attributes, lighting-console, show-console, video-console. Data Types index rebuilt (38 types).
- All DT semantics tables rebuilt from the schemas (31 pages). Fixed schemas: REParticipantStatus (nesting), AudioBehaviour (typo), Event (AVSceneDescriptors).

- 2026-10-12: CAE-ASM-V1.0 L2 (schemas/CAE3/V1.0/AIMs/AudioSceneManager.json) now includes the voice interface: SubAIMs MMC-ASR, MMC-EDP, MMC-TTS; new boundary ports VoiceRequest (BSO:2 in), VoiceLanguage (SEL in), VoiceReply (BSO:2 out), VoiceCommand (UCM:6 out), VoiceReplyText (BTO out); existing BSO boundary ports now numbered 1.
- 2026-10-12: ASM L2/AMD: added TypedRequest (BTO:1), TextToSpeak (BTO:2), VoiceScene (BAS:2) inputs; AMD has no VoiceLanguage (the ASR AMD has no SEL port). WDL verb 'ask Controller to keep storage in "..."'. Stale UserAgent/Orchestration/MMC-*.orch removed.

## 2026-10-14 ASM, ABV and the AIF spec (for the end-of-day MKG rebuild; the entries of 2026-10-11 and 2026-10-12 on PDX, FPE and the ASM ports are SUPERSEDED by this one)
**ASM (pushed, mpai-cav 4446648)**
- CAE-ASM-V1.0 L2 and AMD: voice inside the Module (SubAIMs MMC-ASR, MMC-EDP, MMC-TTS). Boundary Ports: VoiceRequest BSO:2 in, TypedRequest BTO:1 in, TextToSpeak BTO:2 in, VoiceScene BAS:2 in, AudioObjectFromOutside AUO in; VoiceReply BSO:2 out, VoiceCommand UCM:6 out, HeardText BTO:1 out, VoiceReplyText BTO:2 out, TextForUA TFU out, MadeAudioObject AUO out. The existing BSO and BAS boundary Ports are numbered 1. VoiceLanguage (SEL in) is in the L2 only: the ASR AMD has no language input.
- CAE-AOE L3/AMD: AUO in and AUO out. User Command semantics: AddedObjects sent to Audio Object Editing composes an Audio Object (report ResultID); MovedObjects/RemovedObjects may name the Object by its PoV alone (no identifier). EDP remembers the last Scene it was told; ASE ignores a Scene id the storage does not hold.
- The ASM UA runs Apps/ASM/ASM.orch with the workflow interpreter (one device per Port); no AIM, no storage in the UA.
- **AIF spec - to be applied to mpai.community by the author:** Workflow Description Table 1 gains `ask Controller to keep storage in "<where>"` (calls MPAI_AIFU_SharedStorage_Init); one sentence each in Basic API 3.5 and Storage (Reaching Storage). Text and reasons: Downloads\Spec-Updates-WDL-keep-storage.html. The site is NOT yet changed.
- Open (author): the UA still holds Object ids taken from the Scene (library, object edits); how the UA gives/receives an Object without ids (PoV for gestures decided; library open).
**ABV (merged with the parallel work, pushed)**
- PAF-PDX (L2, AMD, code Mpai.Paf.Pdx): the Avatar is the Avatar data type PAF-AVT (author's ruling 2026/10/14), with its ID, 3D model, Face/Body Descriptors and, where it speaks, its speech; PMX still takes the Avatar Model (OSD-B3O). The '#' part IDs and Mpai.Paf.Vcr's own PDX are gone.
- PAF-VCR L2 as slide 27: PDX - MSC - RSR, Continuous, Portable Avatars and Summary External; PAF-AVT flows kept. PAF-MSC (new AIM, schema + AMD + code Mpai.Paf.Vcr): takes the data of one Portable Avatar together (ID, Avatar, Spatial Attitude). VSC and AVC no longer used; schemas AudioVisualSceneCreation.json and VisualSceneCreation.json deleted (slides 3 and 31 removed by the author).
- Portable Avatar: EntityState kept (it holds only the Personal Status: Cognitive State, Emotion, Social Attitude), FaceDescriptorsObject, BodyDescriptorsObject added; no PersonalStatus field. MMC EntityState.json: only Header and EntityStateID required, plus at least one element (anyOf) - a change to a data type schema.
- PAF-FPE stays a leaf (author's ruling); PAF-BPE new leaf; VCT needs Body/Face Personal Statuses.
- Open: PMX firing rule (text and/or speech vs the VMS waiting for speech, face and body: two L3s?), the PSD giving the Text to PMX, an empty Speech Object for a set with no speech, the corrected Summary returning to SCM, Figure 27 (plural Portable Avatars: done by the author).
**Rebuild the MKG for:** CAE-ASM, CAE-AOE (L2/L3 ports), PAF-VCR, PAF-PDX, PAF-MSC (new), PAF-PMX, MMC EntityState; units.json for the ASM UA if it lists Units.
**SPG-MDL:** left open; the author has no time for it (2026-10-14) - no reminder.
- 2026-10-14: MKG rebuilt (build_mkg.py, build_viewer.py, --public): 1203 nodes, 1671 relations; public 1105 nodes, 1354 relations. The entries above are in it. Upload MKG/public/index.html (FileZilla) - not done yet.
- 2026-10-14: AIF spec APPLIED to mpai.community (pages 44176 Workflow Description: new Table 1 row 'ask Controller to keep storage in'; 44174 Basic API 3.5.1: one sentence; 44171 Storage: one sentence). WordPress keeps the previous revisions. (The line above saying the site is not yet changed is out of date.)
- 2026-10-14 ABV: PMX updates the avatar (model once, then ID + changes); PDX->MSC packages with empty placeholders; NO edited-Summary return this version (architecture must afford it); not yet implemented.
- 2026-10-14: ABV Avatar/Participant/Machine IDs are 'string', not OSD-IID (L2: AVS MSC PSD PDX PMX RSR SPA VCR VCT VMS PSE SCM; L3: MSC PDX PMX PSD VMS SCM; code PDX PMX MSC SCM); AIF AIMMetadata AIFType gains string in DI (web Metadata 5 page still to update). SPA SubscriberID also string (borderline). Rebuild the MKG.
- 2026-10-14 AIF spec Metadata page: Type system BNF gains boolean_type and string_type, layout sentence (applied on the site, page 44173); AIMMetadata.json has string; second upload set in Downloads\schemas-publish-2026-10-14b (not yet uploaded).

## 2026-10-14 - PAF-AVT merged into PAF-PAV (ABV)
- PAF-AVT-V1.6 retired; Avatar.json deleted. PortableAvatar.json gains ProcessID; the model item may be a 3D Model Object or a Basic 3D Model Object; the speech item a Speech Object or a Basic Speech Object. XRV-SAV's Avatar refers to PortableAvatar.json.
- Every AVT port in the L2s, L3s, workflows (.orch), tests and expected files is now PAF-PAV-V1.6. C# class Avatar -> PortableAvatar (flat form).
- NOT done (architecture, the author reviews the C-AIMs): the VCR per slide 28 (MSC -> RSR, no PDX); MSC, VMS, PDX consequences; MKG rebuild.

## 2026-10-14 - CAV junction round
- CAV2/V2.0/data/BasicEnvironmentDescriptors.json: BasicEnvironmentObject gains TurnIndicator (Left | Right).
- CAV2/V2.0/data/FullEnvironmentDescriptors.json: FullEnvironmentObject gains PredictedPath (T, East, North).
- No change to the Ego-Remote AMS Message: the connected attribute is its Sender Info message (the Trajectory the CAV intends to adopt).
- Code and tests: docs/CAV-Junction-Round.md.

## 2026-10-14 - the Portable Avatar replaces the Speaking Avatar in PAF (slides of 2026-10-14)
- AIM renames: PAF-SAF "Portable Avatar Formation", PAF-SAS "Portable Avatar Synthesis" (acronyms kept); files PortableAvatarFormation.json, PortableAvatarSynthesis.json. XRV-SAV is no longer an Output or Input of any PAF/MMC/CAV/MAS AIM; the XRV standard's own SpeakingAvatar.json stays.
- Avatar Model input of SAS, SAF, RSR and of the composites that use them is OSD-B3O-V1.5 (was PAF-PAV); outputs are PAF-PAV-V1.6 (PortableAvatar). SAR takes a Portable Avatar. RSR composite per slide 17 (PoV through; Avatar Sp. Att. and MM Scene Descr. into SAF). SAF/SAS get optional Avatar Spatial Attitude (OSD-OSA) and AV Scene Descriptors (OSD-BMS) inputs (the RSR slide feeds them; the SAF slides do not show them). PAF-VCR is one basic AIM (slide 23). PAF-PMX: Speech Object optional. MMC-AMQ = ASR + TIQ + PMX (slide 3), output Portable Avatar.
- Decks: "Speaking Avatar" -> "Portable Avatar" in AIH, CUI, CVE, GME, MPAI AIWs and AIMs2, PGM (31 places; originals in the session scratchpad pptx-backup). Those decks still call PAF-SAR "Portable Avatar Rendering" (the PAF deck: Scene and Avatar Rendering).

## 2026-10-15 (night) - ABV aligned to the slides
- PAF-MSC retired; **PAF-VCR is one AIM** (L2 VideoconferenceClientReceiver.json, L3 1PAF-VCR-V1.6-I01, code Mpai.Paf.Vcr.VideoconferenceClientReceiver; the author: "VCR adds speech to Avatars, one AIM"). It holds each avatar's state by ID, merges updates, seats avatars at the Spatial Attitude of the Portable Avatar, adds the speech at the mouth (1.6 m), shows a default avatar (setting DefaultAvatar) when no model was ever sent; PoV and Text pass through. MultimodalSceneCreation.json, 1PAF-MSC-V1.6-I01 deleted.
- PAF-AVS L2 (slide 3): two Portable Avatar inputs, PAF-PAV-V1.6 Port 1 (straight to PMX, not acted upon) and Port 2 (to PDX, translation); the AVS adds room (AV Scene Descriptors) and seat (Spatial Attitude) to the participants' Portable Avatars.
- PAF-VMS L2/L3 (slide 25): boundary input MachineID (string) into PSD (the secretary's own ID, PMX requires the ID).
- MMC-SCM L2/L3: PersonalStatus input optional (a speaker without Personal Status is still minuted). L3 1MMC-EDP-V2.5-I02: PersonalStatus optional (as the L2).
- PAF-PDX: ID always, everything else only when the Portable Avatar has it - no placeholders, no neutral Personal Status. PAF-PMX: where it starts from an Input Portable Avatar, the Model already there is kept (the server's PMX adds the ID).
- Meaning (MMC-MEA) is discontinued: read Text Descriptors Object (MMC-TDO); Text Descriptors (MMC-TXD) discontinued; Intention is MMC-INT-V2.5 (D:\AI Intention.json: qtopic, qfocus, qLAT, qSAT, qdomain). The L2s of TTT, QAM, AQM, NLU, SCM still to align.
- WDL interpreter (UserAgent/Rca): a literal may also be an ID (`offer MachineID (string:1) = "Secretary"`).
- OPEN (author's ruling needed): PMX "updates on any arrival, ID the only required input" does not fit the Controller's firing (one Message per required input per firing): inside PSD the model, speech, face and body come from different AIMs without the ID. VmsTests.SecretarySpeaks and App are skipped for this reason (see the comment in VmsTests.cs). Options: PMX continuous (state by ID) or a retained ID input.
- Proposal for the author: Downloads\AIF-V3.0-Proposal-Link-on-Request.md (COM-9a, Basic API 3.10.4-6, WDL steps link/unlink) for the private message between participants.
- Access folder: D:\DI\Access\Avatars\RocketboxAvatars (moved from Models\Rocketbox), Access/ in .gitignore.
- Rebuild the MKG for: PAF-VCR, PAF-AVS, PAF-VMS, MMC-SCM, MMC-EDP L3.

## 2026-10-15 (day) - PMX/ID resolved, CAV launcher
- **PAF-PMX-V1.6-I02 (new L3, continuous)** resolves the OPEN firing point above: PMX I01 fired on the ID alone, before the content. I02 runs on its own Ports (IAimRunner, code Mpai.Paf.Pmx.ContinuousPmx), holds the Avatar ID (still required, as in the L2), sends an update on each arrival - the Model once, then what changed; a state unchanged is not sent again; what arrives before the ID waits for it; what arrives together goes out as one update. Input Ports Depth 8. The PSD L3 (1PAF-PSD-V1.6-I01) and the VMS provider use I02; the ABV demo server still uses I01.
- WDL/App: Apps/VMS/PAF-VMS.orch - the Secretary's ID comes from the Secretary device (`stream MachineID (string:1) from device "Secretary"`), once; `offer` only queues data until a `give`. Mpai.Rca SecretaryDevice gives the ID. VmsTests SecretarySpeaks and App pass.
- CAV: launcher CAV/Cav.bat (seven use cases live; animations page CAV/Demos); the live test gains the junction modes junction-route, junction-lights, junction-nothing; the viewer draws a crossing road, turned vehicles and indicators. Planned tonight: overtaking a car that turns off at a junction (Downloads\CAV-Overtaking-at-a-Junction.md).
- ABV demo (ABV/): server, browser client, protocol, avatars (Rocketbox, MIT) pushed; the Secretary types the minutes.
- Rebuild the MKG for: PAF-PMX I02, PAF-PSD L3, PAF-VMS (device ID).

## 2026-10-15 (evening) - MMM: a meeting as services (Phases 1-6)
- MMM implementation (D:\DI\MMM): axes x east, y north, z up; Orientation yaw, pitch, roll (yaw 0 north, provisional); MM-Capture (simplified scene); first-person client in the viewer; a meeting as services - room Location with seat Locations, AVS Service (Process: convening, invitations, seats, rights), Secretary (machine Process with its own Persona, minutes), ASR Service (Process, Whisper), Speech Objects with their own duration (OSD-BSO), minutes for the participants only. Doc: docs/MMM-Meeting.md.
- Rules from the author: a User may have many Personae, all its own (it brings one to an event); the places of a meeting are assigned by the AVS Service; who is in is a human decision; no meeting code.
- Not in MMM-TEC (flagged): GET processes/{id}/inbox; Discover not implemented; a removed participant's Persona stays in its seat.
- Rebuild the MKG for: nothing new in the standards; the MMM implementation notes only.

## 2026-10-16 - MMM: hearing (Phase 7)
- A User's speech is placed at its seat as a Speech Object (MM-Add; the AVS Service grants the right with the seat); MM-Capture returns it (kind Speech); the participants read its data and play it from the seat; the Secretary hears the room by capturing it (no second copy sent). MM-Add can make a Speech Object where it is added. MM-Capture is not in the Activity Data. Whisper noise-word filter of ABV ported. Doc: docs/MMM-Meeting.md.
- Rebuild the MKG for: nothing new in the standards.

## 2026-10-16 (later) - MMM: streamed Speech Objects (Phase 8)
- A Speech Object is declared when speech starts (time declared, not redefined until over) with its Speech Qualifier TFA-SPQ-V1.5 (PCM; StreamFormat WebSocket) and a DataURI (a WebSocket address); the data goes out as it is captured; whoever has chosen to hear that User (the receiver decides: from A, not from C) opens the address and receives it from the start, played from the seat. The Secretary takes it when it is over. First sound about 0.1-0.3 s after the Speech Object began (localhost, warm).
- Schema: "WebSocket" added to TFA/V1.5/formats/SpeechStreamFormats.json (the Speech Qualifier's stream formats) in D:\DI\schemas, to be published on schemas.mpai.community by the author.
- Rebuild the MKG for: nothing new in the standards.
## 2026-10-16 (XRV data types)
- XRV1 data schemas corrected in D:\AI\schemas\XRV1\V1.0 (28 data schemas, 5 examples); list in Downloads\XRV-Data-Types-Corrections.docx. Brainwavefreq in Hz with band ranges; FieldOfView in degrees.
- XRV Speaking Avatar (XRV-SAV, SpeakingAvatar.json) no longer exists: replaced by the PAF Portable Avatar in VE Performance Status and VE Participant Descriptors; schema deleted from D:\AI and D:\DI (uncommitted in D:\DI); web page moved to the WordPress trash, the Data Types table has 37 entries. Other specifications (PAF AIMs, MMC, CAV AIMs) still name XRV-SAV or the Speaking Avatar: not touched.
- 28 XRV-LTP data type pages regenerated on the web from the schemas (Semantics tables).
- Rebuild the MKG for: the XRV data types (no XRV-SAV; changed property names).
- XRV1 schemas: the five Functional Requirements added (Script Character/Set/Object IDs and Object Type; Show Descriptors Status; RE Venue Specification DMX/MIDI/seating chart; RE Data In Time Codes; Participant Data Participant ID) and on the web pages. PAF Speaking Avatar Formation renamed Portable Avatar Formation on the web; Speaking Avatar Synthesis and Scene and Avatar Rendering pages to be decided.
- PAF (web, 2026-10-16): Speaking Avatar Synthesis (PAF-SAS) and Scene and Avatar Rendering (PAF-SAR) pages trashed and removed from the AI Modules list (schemas already removed from D:\DI); Response and Scene Rendering kept. Portable Avatar Formation (PAF-SAF) page follows the new figures: inputs Speech, Text, Face Personal Status, Body Personal Status, Avatar Model, Machine ID, Avatar Spatial Attitude, AV Scene Descriptors; SubAIMs GFD, GBD, PMX. Figures to be added to the MKG AIM entries step by step, starting from PAF.
- Rebuild the MKG for: PAF AIMs (no SAS/SAR; SAF ports).
- PAF (web and decks, 2026-10-16, later): Audio-Visual Scene Creation (PAF-AVC) and Visual Scene Creation (PAF-VSC) pages trashed and removed from the AI Modules list; the old Avatar data-type page (PAF-AVT) trashed and its row removed from the V1.6 Data Types list; Avatar Model is represented by a Basic 3D Model Object (OSD-B3O) on the SAF, PMX, PDX and RSR pages; PMX figure now has Face Descriptors, Body Descriptors and Avatar Spatial Attitude; Videoconference Client Receiver links Portable Avatar. PGM-AUA pages and PAF About no longer say Speaking Avatar. Decks: slides for PAF-SAR (PAF REference Models, slide 18) and PAF-SAR/SAS (MPAI AIWs and AIMs2, slides 264-266) removed; PAF slides 13, 20, 21 updated (backups in Downloads).
- Rebuild the MKG for: PAF AIMs (no AVC, VSC, SAS, SAR; PMX inputs; no Avatar data type).

## 2026-10-16 (night) - OSD-AUS-V1.5 Audio Segmentation in the HCI composite (EI branch ei-restructure)
- New AIM OSD-AUS-V1.5 (schemas/OSD/V1.5/AIMs/AudioSegmentation.json, Header added; its input is a Basic Speech Object or a Speech Object, OSD-BSO-V1.5 | OSD-SPO-V1.5; outputs Speech Object of the turns, OSD-STM-V1.5 speaker times, OSD-SOL-V1.5 speech overlap) and new data type OSD-SOL-V1.5 (schemas/OSD/V1.5/data/SpeechOverlap.json). Code AIMs/OSD/V1.5/AUS (voice activity + ECAPA embeddings of windows, grouped by similarity; overlap is an estimate), AMD 1OSD-AUS-V1.5-I01.
- HCI composite (L2 schemas/CAV2/V2.0/AIMs/HumanCAVInteraction.json and the L3): CAE-ASI -> OSD-AUS (BSO), OSD-AUS -> MMC-SIR and MMC-PSE (SPO) replace CAE-ASI -> MMC-SIR and CAE-ASI -> MMC-PSE; CAE-ASI -> MMC-ASR is unchanged. The L3s of MMC-SIR, MMC-SPE and MMC-PSE now declare BSO|SPO as their standard schemas already did. SIR and SPE take the speech of the speaker who speaks longest (Mpai.Core.OSD.SpeechInputs).
- Rebuild the MKG for: the HCI composite, OSD-AUS-V1.5, OSD-SOL-V1.5. To upload on schemas.mpai.community: the two OSD schemas and HumanCAVInteraction.json.
