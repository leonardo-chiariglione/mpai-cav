using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  Speech Object - OSD/V1.5/data/SpeechObject.json, header OSD-SPO-V1.5.
//
//  A COLLECTION of Basic Speech Objects (and of Speech Objects), each with its own
//  Space-Time within it, as the Audio Object is of Basic Audio Objects. One that holds
//  a single Basic Speech Object is that Object, in the form the Data Type of a Port
//  asks for.
// ---------------------------------------------------------------------------
public sealed class SpeechObject
{
    public string Header { get; init; } = "OSD-SPO-V1.5";
    public string? MInstanceID { get; init; }
    public string SpeechObjectID { get; init; } = "";
    public SpaceTime? SpeechObjectSpaceTime { get; init; }
    public PointOfView? UserPoV { get; init; }

    public int? BasicSpeechObjectCount { get; init; }
    public List<BasicSpeechObjectEntry>? BasicSpeechObjects { get; init; }

    public int? SubSpeechObjectCount { get; init; }
    public List<SubSpeechObjectEntry>? SubSpeechObjects { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // Wrap a single Basic Speech Object as an Object of one, under a new identifier.
    public static SpeechObject FromBasic(BasicSpeechObject basic) => new()
    {
        SpeechObjectID = Guid.NewGuid().ToString(),
        SpeechObjectSpaceTime = new SpaceTime(),
        BasicSpeechObjectCount = 1,
        BasicSpeechObjects = [new BasicSpeechObjectEntry { BSObjectIDOrBSObject = basic }]
    };
}

public sealed class BasicSpeechObjectEntry
{
    public SpaceTime? BasicSpeechObjectSpaceTime { get; init; }
    // In C# the member itself; in JSON the schema's BSObjectIDOrBSObject, an array of one.
    [JsonIgnore]
    public BasicSpeechObject? BSObjectIDOrBSObject { get; init; }
    [JsonPropertyName("BSObjectIDOrBSObject")]
    public List<BasicSpeechObject>? BSObjectIDOrBSObjectArray
    {
        get => BSObjectIDOrBSObject is null ? null : [BSObjectIDOrBSObject];
        init => BSObjectIDOrBSObject = value?.FirstOrDefault();
    }
}

public sealed class SubSpeechObjectEntry
{
    public SpaceTime? SubSpeechObjectSpaceTime { get; init; }
    // In C# the member itself; in JSON the schema's SubSObjectIDOrSubSObject, an array of one.
    [JsonIgnore]
    public SpeechObject? SubSObjectIDOrSubSObject { get; init; }
    [JsonPropertyName("SubSObjectIDOrSubSObject")]
    public List<SpeechObject>? SubSObjectIDOrSubSObjectArray
    {
        get => SubSObjectIDOrSubSObject is null ? null : [SubSObjectIDOrSubSObject];
        init => SubSObjectIDOrSubSObject = value?.FirstOrDefault();
    }
}

// ---------------------------------------------------------------------------
//  Speech Scene Descriptors - OSD/V1.5/data/SpeechSceneDescriptors.json, header
//  OSD-SSD-V1.5: Basic Speech Objects and Speech Objects, and sub-scenes (a Basic Speech
//  Scene among them), each with its Space-Time in the Scene.
// ---------------------------------------------------------------------------
public sealed class SpeechSceneDescriptors
{
    public string Header { get; init; } = "OSD-SSD-V1.5";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string SpeechSceneDescriptorsID { get; init; } = "";
    public SpaceTime? SpeechSceneDescriptorsSpaceTime { get; init; }
    public PointOfView? UserPoV { get; init; }
    public JsonArray? ClosedSpace { get; init; }

    public int? ObjectCount { get; init; }
    public List<SpeechSceneObjectEntry>? Objects { get; init; }

    public int? SubSpeechSceneCount { get; init; }
    public List<SubSpeechSceneEntry>? SubSpeechScenes { get; init; }

    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }
}

public sealed class SpeechSceneObjectEntry
{
    public SpaceTime? ObjectSpaceTime { get; init; }
    // The member is a Basic Speech Object or a Speech Object, or the identifier of either:
    // in JSON the schema's ObjectIDOrObject, an array of one.
    [JsonIgnore]
    public SpeechObject? SpeechObject { get; init; }
    [JsonIgnore]
    public BasicSpeechObject? BasicObject { get; init; }
    [JsonIgnore]
    public string? ObjectId { get; init; }
    [JsonPropertyName("ObjectIDOrObject")]
    public JsonArray? ObjectIDOrObject
    {
        get => OneOf.Held(SpeechObject, BasicObject, ObjectId);
        init
        {
            SpeechObject = OneOf.Take<SpeechObject>(value, "OSD-SPO-V1.5");
            BasicObject = OneOf.Take<BasicSpeechObject>(value, "OSD-BSO-V1.5");
            ObjectId = OneOf.Id(value);
        }
    }
}

public sealed class SubSpeechSceneEntry
{
    public SpaceTime? SubSpeechSceneSpaceTime { get; init; }
    // The member is a Basic Speech Scene or a Speech Scene, or the identifier of either:
    // in JSON the schema's SubSSceneIDOrSubSScene, an array of one.
    [JsonIgnore]
    public SpeechSceneDescriptors? SubScene { get; init; }
    [JsonIgnore]
    public BasicSpeechSceneDescriptors? BasicScene { get; init; }
    [JsonIgnore]
    public string? SceneId { get; init; }
    [JsonPropertyName("SubSSceneIDOrSubSScene")]
    public JsonArray? SubSSceneIDOrSubSScene
    {
        get => OneOf.Held(SubScene, BasicScene, SceneId);
        init
        {
            SubScene = OneOf.Take<SpeechSceneDescriptors>(value, "OSD-SSD-V1.5");
            BasicScene = OneOf.Take<BasicSpeechSceneDescriptors>(value, "OSD-BSS-V1.5");
            SceneId = OneOf.Id(value);
        }
    }
}
