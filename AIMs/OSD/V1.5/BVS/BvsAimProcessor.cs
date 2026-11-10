using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Osd.Bvs;

// OSD-BVS-V1.5 - Basic Visual Scene Description, as an AIF IAimProcessor.
//
// Front-end describer of MMC-HCI. A structural transform: it takes the acquired
// Basic Visual Object (OSD-BVO) and packages it into a Basic Visual Scene
// Descriptors (OSD-BVS) - a one-object scene whose entry carries the object and
// its point of view. The object's spatial attitude is its SpaceTime; the visual
// object's real point of view (depth-resolved) is supplied later by Audio-Visual
// Alignment fusing the LiDAR scene. This AIM describes; it does not localise.
public sealed class BvsAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;    // OSD-BVO
    private readonly string _outPort;   // OSD-BVS

    public BvsAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId = instanceId;
        _inPort     = ports.Input("OSD-BVO-V1.5");
        _outPort    = ports.Output("OSD-BVS-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Visual Object on input port"));

        var obj = Mpai.Aif.PortData.PortObjects.ReadVisual(json);
        if (obj is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic Visual Object"));

        var scene = new BasicVisualSceneDescriptors
        {
            Header                          = "OSD-BVS-V1.5",
            MInstanceID                     = (string?)System.Text.Json.Nodes.JsonNode.Parse(json)?["MInstanceID"] ?? _instanceId,
            // When and where the Scene is: when and where its picture was acquired (the schema requires it).
            BVSDescriptorsSpaceTime         = obj.BasicVisualObjectTime ?? new SpaceTime { SpaceTimeID = System.Guid.NewGuid().ToString() },
            BasicVisualSceneDescriptorsID   = System.Guid.NewGuid().ToString(),
            VisualObjectCount               = 1,
            // The camera's place is not known here: at the origin, looking ahead.
            UserPoV                         = new PointOfView { PointOfViewID = "camera" },
            BasicVisualSceneDescriptorsEntries = new List<BasicVisualSceneEntry>
            {
                new BasicVisualSceneEntry
                {
                    VObjectIDOrVObject = obj   // placed downstream (MAL + LiDAR)
                }
            }
        };

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = new Dictionary<string, string> { [_outPort] = AsObjects(scene) }
        });
    }

    // The Scene for the Port: the Basic Visual Objects it holds are Objects of the schema's form (Data and Qualifier), as they cross any Port.
    private static string AsObjects(BasicVisualSceneDescriptors scene)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(MpaiJson.ToJson(scene))!.AsObject();
        if (root["BasicVisualSceneDescriptors"] is System.Text.Json.Nodes.JsonArray entries)
            foreach (var entry in entries.OfType<System.Text.Json.Nodes.JsonObject>())
                if (entry["VObjectIDOrVObject"] is System.Text.Json.Nodes.JsonArray held)
                    for (var i = 0; i < held.Count; i++)
                        held[i] = System.Text.Json.Nodes.JsonNode.Parse(Mpai.Aif.PortData.PortObjects.WriteVisual(MpaiJson.FromJson<BasicVisualObject>(held[i]!.ToJsonString())));
        return root.ToJsonString();
    }
}
