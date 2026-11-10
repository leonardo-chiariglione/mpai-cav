// TODO [architecture, flagged]: OSD is a distinct MPAI standard (Object and
// Scene Description). These OSD data types currently live inside Mpai.Core
// (namespace Mpai.Core.OSD) for convenience, but should later be extracted
// into their own Mpai.Osd project - mirroring how MMC/CAE/CVE/PTF are
// separated - in a single deliberate refactor covering all OSD types
// (audio + visual + audio-visual + LiDAR) at once, not piecemeal.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Basic Multimodal Scene Descriptors - OSD/V1.5/data/
//  BasicMultimodalSceneDescriptors.json, header OSD-BMS-V1.5.
//
//  The multimodal (fused) Basic scene. A BMS is a COMPOSITION of an arbitrary
//  number of per-modality Basic Scene Descriptors (BAS, BVS, BLS, BSS, ...),
//  each carrying its own SpaceTime so the modality scenes stay SPATIALLY
//  ALIGNED in a common frame (the outer BMSDescriptorsSpaceTime). E.g. a BMS
//  might hold a BAS + a BVS + a BLS, aligned, so "this face" (visual) and
//  "this voice direction" (audio) are registered to the same space.
//
//  NOT RECURSIVE (the author, 2026/10/02): a BMS holds Basic Scenes only, of
//  any medium, none required but at least one; a BMS is not nested in another
//  - that is the Audio-Visual Scene Descriptors (MSD) - and a Basic Object is
//  not a member.
//
//  The schema's per-entry choice is anyOf[ the modality Basic Scenes | an ID
//  string ]. Following the same simplification used by the audio/visual
//  scene types, the C# entry carries a single nullable object reference to the
//  contained scene (its concrete BXS type checked at use), not a strict
//  discriminated union - since the modality Basic-scene types share no common
//  base in this codebase (there is no object hierarchy; they are all Basic).
// ---------------------------------------------------------------------------
public sealed class BasicMultimodalSceneDescriptors
{
    public string Header { get; init; } = "OSD-BMS-V1.5";
    public string MInstanceID { get; init; } = "";
    public string? UEnvironmentID { get; init; }
    public string BasicMultimodalSceneDescriptorsID { get; init; } = "";
    public SimpleTime? BMSDescriptorsTime { get; init; }

    // The common reference frame for all contained modality scenes.
    public SpaceTime? BMSDescriptorsSpaceTime { get; init; }

    // Where the Scene is heard and seen from, the Closed Space (OSD-CSP) it is
    // in, and the Scene part of the Acoustic Profile - optional, added 2026/10/02
    // so that a BMS can hold speech and audio together for CAE-ASM.
    public PointOfView? UserPoV { get; init; }
    public JsonArray? ClosedSpace { get; init; }
    public AcousticProfile? AcousticProfile { get; init; }

    public double? GravityValue { get; init; }

    public int MultimodalObjectCount { get; init; }

    // Named "...Data" to match the schema property BasicMultimodalSceneDescriptorsData. Null
    // when the Scene holds only Objects: the schema wants it absent rather than empty.
    public List<BasicMultimodalSceneEntry>? BasicMultimodalSceneDescriptorsData { get; init; } = new();

    // The Basic Objects placed in the Scene directly, of any medium (the author,
    // 2026/10/05: BME integrates objects and scenes). Null when there are none.
    public List<BasicMultimodalObjectEntry>? BasicObjectsData { get; init; }

    // OPTIONAL alignment layer (populated by the OSD-MAL AIM, absent in the
    // degenerate composition-only BMS). Each element combines the constituent
    // per-modality objects that MAL determined to be the SAME entity (matched by
    // Spatial Attitude / PointOfView), tied by a shared AlignmentCode. Additive
    // and non-destructive: the original BAS/BVS scenes above keep their objects
    // and PointOfViews exactly as measured; this is MAL's interpretation on top.
    public List<AlignedMMObject> AlignedMMObjects { get; init; } = new();

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class BasicMultimodalSceneEntry
{
    // SPATIAL ALIGNMENT: this modality scene's placement within the BMS's common
    // frame. Required - it is what keeps the fused modalities registered.
    public SpaceTime BXSSpaceTime { get; init; } = new();

    // Where this member is heard and seen from; absent, the Scene's UserPoV.
    public PointOfView? UserPoV { get; init; }

    // The contained Basic Scene Descriptors (BAS / BSS / BVS / B3S / BLS / BRS /
    // BUS / BOS), or its ID string. Typed as object because these share no
    // common base type (all Basic, no hierarchy); the concrete type is checked
    // at use (e.g. `entry.BXSOrBXSID is BasicAudioSceneDescriptors`).
    public object? BXSOrBXSID { get; init; }
}

// A Basic Object placed in a Basic Multimodal Scene directly: its placement in the
// common frame, where it is heard and seen from, and the Object (BAO / BVO / BSO /
// B3O / BLO / BRO / BUO / BOO) or its ID string.
public sealed class BasicMultimodalObjectEntry
{
    public SpaceTime BXOSpaceTime { get; init; } = new();
    public PointOfView? UserPoV { get; init; }
    public object? BXOOrBXOID { get; init; }
}

// ---------------------------------------------------------------------------
//  Aligned MultiModal Object - one element of BMS.AlignedMMObjects, produced by
//  OSD-MAL. Combines the constituent per-modality objects (a BasicAudioObject +
//  a BasicVisualObject, extensible to speech/3D) that MAL aligned as the same
//  entity, by a shared AlignmentCode, with an optional consensus PointOfView.
//
//  AlignedObjects holds the constituents (or their id strings). Typed as object
//  because the constituents are of different object types with no common base
//  (BasicAudioObject / BasicVisualObject / BasicSpeechObject / ... / id string);
//  the concrete type is checked at use.
// ---------------------------------------------------------------------------
public sealed class AlignedMMObject
{
    // Shared code marking the constituents as the same entity in this scene.
    public string AlignmentCode { get; init; } = "";

    // Optional consensus attitude of the fused entity. Does NOT replace the
    // constituents' own PointOfViews (those stay on the original scene entries).
    public PointOfView? AlignmentPointOfView { get; init; }

    // How likely it is that the constituents are the same entity, [0, 1]; e.g. a
    // horn heard by a microphone array of limited accuracy, aligned to a car. Two
    // alignments competing for one constituent each carry their own. Optional, as
    // every Confidence (the author, 2026/10/03).
    public double? Confidence { get; init; }

    // The constituent objects fused into this entity (audio + visual, extensible),
    // or their id-string references. At least one; typically two.
    public List<object> AlignedObjects { get; init; } = new();
}

// ---------------------------------------------------------------------------
//  Multimodal Scene Descriptors - OSD/V1.5/data/MultimodalSceneDescriptors.json,
//  header OSD-MSD-V1.5 (what Multimodal Scene Editing, OSD-MSE, gives).
//
//  The full Scene: Objects of every medium and sub-scenes of every medium, each with its
//  Space-Time in the Scene's common frame. A member is a Basic Object or an Object (a Basic
//  Scene or a Scene), or its identifier, which its Header says. The lists are kept as
//  the arrays of the schema - each entry its Space-Time and its member, the member an array
//  of one - so that a Multimodal Scene read is written back as it was.
// ---------------------------------------------------------------------------
public sealed class MultimodalSceneDescriptors
{
    public string Header { get; init; } = "OSD-MSD-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string MultimodalSceneDescriptorsID { get; init; } = "";
    public SimpleTime? MultimodalSceneDescriptorsTime { get; init; }
    public SpaceTime? MultimodalSceneDescriptorsSpaceTime { get; init; }
    public PointOfView? UserPoV { get; init; }
    public JsonArray? ClosedSpace { get; init; }

    public int? AudioObjectCount { get; init; }
    public JsonArray? AudioObjects { get; init; }
    public int? SpeechObjectCount { get; init; }
    public JsonArray? SpeechObjects { get; init; }
    public int? VisualObjectCount { get; init; }
    public JsonArray? VisualObjects { get; init; }
    [JsonPropertyName("3DModelObjectCount")] public int? Model3DObjectCount { get; init; }
    [JsonPropertyName("3DModelObjects")] public JsonArray? Model3DObjects { get; init; }
    public int? AudioVisualObjectCount { get; init; }
    public JsonArray? AudioVisualObjects { get; init; }

    public int? SubAudioSceneCount { get; init; }
    public JsonArray? SubAudioScenes { get; init; }
    public int? SubSpeechSceneCount { get; init; }
    public JsonArray? SubSpeechScenes { get; init; }
    public int? SubVisualSceneCount { get; init; }
    public JsonArray? SubVisualScenes { get; init; }
    [JsonPropertyName("Sub3DModelSceneCount")] public int? SubModel3DSceneCount { get; init; }
    [JsonPropertyName("Sub3DModelScenes")] public JsonArray? SubModel3DScenes { get; init; }
    public int? SubMultimodalSceneCount { get; init; }
    public JsonArray? SubMultimodalScenes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}
