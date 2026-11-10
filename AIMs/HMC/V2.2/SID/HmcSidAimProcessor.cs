using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Osd.Mdx;

namespace Mpai.Hmc.Sid;

// HMC-SID-V2.2 - Multimodal Scene Integration and Description.
//
// Adds the Avatar of the Input Portable Avatar (PAF-PAV) to the Multimodal Scene Descriptors the Portable Avatar conveys, with an appropriate Spatial Attitude, and gives what the resulting scene holds: the Face, the Body
// and the other Visual Objects (Ports 1, 2 and 3 of OSD-BVO), the Visual Scene Descriptors (OSD-BVS), the Audio Objects (OSD-BAO), the Speech Objects (OSD-BSO) and the Audio Scene Descriptors (OSD-BAS), as in the Reference Model.
// The scene is made as a Basic Multimodal Scene Descriptors (OSD-BMS) and taken apart with the function of OSD-MDX. If the Portable Avatar conveys no Scene, a generic Scene is used: an empty one, with its frame at the origin.
//
// The Avatar is placed at the Spatial Attitude the Portable Avatar states for it (AvatarSpatialAttitude); if it states none, at the origin of the scene, facing the person looking at it (orientation 180: the
// convention of ASM and CAV: 0 faces away from the listener, 180 faces the listener). Only the Avatar is placed: the schema of the Basic Multimodal Scene Descriptors allows as members the Basic Objects (audio, visual,
// speech, text, 3D Model, LiDAR, RADAR, ultrasound, offline map) and Instance Identifiers, and not Face or Body Descriptors Objects, which stay in the Portable Avatar.
// A Scene the Portable Avatar conveys is taken as it is, and must be a Basic Multimodal Scene Descriptors: another kind is refused with the reason.
public sealed class HmcSidAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _in;
    private readonly AimPortReader _ports;

    public HmcSidAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _ports = ports;
        _in = ports.Input("PAF-PAV-V1.6");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_in, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Portable Avatar on input port"));
        JsonObject? avatar;
        try { avatar = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { avatar = null; }
        if (avatar is null || (string?)avatar["Header"] != "PAF-PAV-V1.6")
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "what was received is not a Portable Avatar (PAF-PAV-V1.6)"));

        JsonObject scene;
        if (avatar["MultimodalSceneDescriptors"] is { } conveyed)
        {
            var held = (conveyed as JsonArray)?.FirstOrDefault() ?? conveyed;
            if (held is not JsonObject o || (string?)o["Header"] != "OSD-BMS-V1.5")
                return Task.FromResult(Message.Error(message.MessageId, _instanceId, "the Portable Avatar conveys a Scene that is not Basic Multimodal Scene Descriptors (OSD-BMS-V1.5)"));
            scene = (JsonObject)o.DeepClone();
        }
        else
            scene = new JsonObject
            {
                ["Header"] = "OSD-BMS-V1.5",
                ["MInstanceID"] = (string?)avatar["MInstanceID"] ?? "HMC",
                ["BasicMultimodalSceneDescriptorsID"] = Guid.NewGuid().ToString(),
                ["BMSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = Guid.NewGuid().ToString() }
            };

        var objects = (scene["BasicObjectsData"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();     // the Objects the scene had, and then the Avatar's
        var placement = Placement(avatar);

        // The Avatar: its Model, when the Portable Avatar carries one, else its ID. The value of BXOOrBXOID is the Object or its ID (the schema of the Basic Multimodal Scene Descriptors).
        if (avatar["AvatarOrAvatarID"] is JsonArray { Count: > 0 } model && (model.OfType<JsonObject>().FirstOrDefault() ?? model[0]) is { } avatarModel)
            objects.Add(new JsonObject { ["BXOSpaceTime"] = placement.DeepClone(), ["BXOOrBXOID"] = avatarModel.DeepClone() });

        scene["BasicObjectsData"] = objects;
        scene["MultimodalObjectCount"] = objects.Count + ((scene["BasicMultimodalSceneDescriptorsData"] as JsonArray)?.Count ?? 0);
        var given = new Dictionary<string, string>();
        foreach (var (dataType, port, datum) in OsdMdxAimProcessor.Demultiplexed(scene.ToJsonString()))
        {
            // Visual Objects: the Face ones (the Visual Object Type of the qualifier) on Port 1, the Body ones on Port 2, the others on Port 3.
            var number = dataType == "OSD-BVO-V1.5" ? (JsonNode.Parse(datum)?["VisualQualifier"]?["Attributes"]?["VisualObjectType"]?.GetValue<string>() switch { "Face" => 1, "Body" => 2, _ => 3 }) : 1;
            if (dataType is "OSD-BVO-V1.5" or "OSD-BVS-V1.5" or "OSD-BAO-V1.5" or "OSD-BSO-V1.5" or "OSD-BAS-V1.5")
                given.TryAdd(_ports.Output(dataType, number), datum);
        }
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = given });
    }

    // Where the Avatar stands in the scene: as the Portable Avatar says, else at the origin facing the listener.
    private static JsonObject Placement(JsonObject avatar)
    {
        var attitude = avatar["AvatarSpatialAttitude"]?.DeepClone() ?? new JsonObject
        {
            ["Header"] = "OSD-OSA-V1.5",
            ["ObjectSpatialAttitudeID"] = Guid.NewGuid().ToString(),
            ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = Guid.NewGuid().ToString(), ["General"] = new JsonObject { ["CoordType"] = "Cartesian" }, ["CartPosition"] = new JsonArray(0, 0, 0) },
            ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = Guid.NewGuid().ToString(), ["Orientation"] = new JsonArray(0, 0, 180) }
        };
        return new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = Guid.NewGuid().ToString(), ["SpatialAttitude1"] = attitude };
    }
}

public sealed class SidPlugin : IAimPlugin
{
    public string AimName => "HMC-SID-V2.2";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new HmcSidAimProcessor(AimName, ports);
}
