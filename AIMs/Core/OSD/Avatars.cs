using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Mpai.Core;

namespace Mpai.Core.OSD;

// ---------------------------------------------------------------------------
//  PAF-PAV-V1.6 - Portable Avatar. Mirrors schemas/PAF/V1.6/data/PortableAvatar.json.
//
//  The Portable Avatar is the one entity of the Avatar-Based Videoconference and of
//  the Modules that render an avatar (the author, 2026/10/14: PAF-AVT is merged into
//  it). It is an UPDATE of an avatar: the ID of the Portable Avatar and, of the rest,
//  only what it carries - the Model, the Speech or the Text, the Face and Body
//  Descriptors, the Personal Status (Entity State) and so on.
//
//  The Avatar is named in AvatarOrAvatarID: the Avatar's ID (a string) and, when the
//  Portable Avatar carries it, its Model (a Basic 3D Model Object). A Model is usually
//  referenced by ID (Basic3DModelObjectID - for instance a glTF file the User Agent
//  holds) rather than carried. The Speech is in SPOOrSPOID.
// ---------------------------------------------------------------------------
public sealed class PortableAvatar
{
    public string Header { get; init; } = "PAF-PAV-V1.6";
    public string? MInstanceID { get; init; }
    public string? UEnvironmentID { get; init; }
    public string PortableAvatarID { get; init; } = Guid.NewGuid().ToString();
    public SpaceTime? AvatarSpaceTime { get; init; }
    public JsonArray? AvatarOrAvatarID { get; init; }
    public JsonArray? SPOOrSPOID { get; init; }
    public FaceDescriptorsObject? FaceDescriptorsObject { get; init; }
    public BodyDescriptorsObject? BodyDescriptorsObject { get; init; }
    public JsonArray? Accessories { get; init; }
    public string? ProcessID { get; init; }
    public DataExchangeMetadata? DataXMData { get; init; }
    public string? DescrMetadata { get; init; }

    // A Portable Avatar that is only a Model, referenced by ID.
    public static PortableAvatar OfModel(string modelId, string? avatarId = null) => new()
    {
        AvatarOrAvatarID = new JsonArray(avatarId ?? Guid.NewGuid().ToString(),
            new JsonObject { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = modelId })
    };

    // The same Avatar, speaking: its Model, the Face and Body Descriptors that
    // animate it and, when given, the Speech (a Portable Avatar carries the Speech
    // itself, once).
    public PortableAvatar Speaking(BasicSpeechObject? speech, FaceDescriptorsObject? face, BodyDescriptorsObject? body) => new()
    {
        MInstanceID = MInstanceID, UEnvironmentID = UEnvironmentID, PortableAvatarID = PortableAvatarID,
        AvatarSpaceTime = AvatarSpaceTime, AvatarOrAvatarID = AvatarOrAvatarID,
        SPOOrSPOID = speech is null ? null : new JsonArray(JsonNode.Parse(MpaiJson.ToJson(speech))),
        FaceDescriptorsObject = face, BodyDescriptorsObject = body, Accessories = Accessories, ProcessID = ProcessID,
        DataXMData = DataXMData, DescrMetadata = DescrMetadata
    };

    // The Avatar's ID: the string of AvatarOrAvatarID.
    public string? AvatarId() => AvatarOrAvatarID?.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
    public string? ModelId() => AvatarOrAvatarID?.OfType<JsonObject>().Select(m => (string?)m["Basic3DModelObjectID"]).FirstOrDefault(s => s is not null);
    public BasicSpeechObject? Speech() => SPOOrSPOID?.OfType<JsonObject>().Select(s => MpaiJson.FromJson<BasicSpeechObject>(s.ToJsonString())).FirstOrDefault();
}
