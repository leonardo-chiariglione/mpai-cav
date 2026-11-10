// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // ScrfdFaceDetector

namespace Mpai.Cve.Vsi;

// CVE-VSI-V1.0 - Visual Scene object Identification (the scanner).
//
// Reads the Basic Visual Scene Descriptors and, for each visual object, decides
// its type and TAGS the object's Visual Qualifier with VisualObjectType (Face |
// Object). It runs the SCRFD face detector on the object's image data: a face
// found -> "Face", otherwise "Object". It then emits the (re-qualified) Basic
// Visual Objects on TWO OSD-BVO outputs (the author's reference model, 2026-11-08):
// the Face Visual Objects on Port 1 and the Other Visual Objects on Port 3 (Port 2 is the Body ones, which this implementation does not detect). Face
// Recognition takes the first, Visual Instance Identification the second. The type
// also stays in the data (the qualifier).
public sealed class CveVsiAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;       // OSD-BVS
    private readonly string _facePort;     // OSD-BVO, Port 1: the Face Visual Objects
    private readonly string _bodyPort;     // OSD-BVO, Port 2: the Body Visual Objects (the L2 has them; this implementation does not detect a body, so none is given)
    private readonly string _otherPort;    // OSD-BVO, Port 3: the Other Visual Objects
    private readonly ScrfdFaceDetector _scrfd;

    public CveVsiAimProcessor(string instanceId, ScrfdFaceDetector scrfd, AimPortReader ports)
    {
        _instanceId = instanceId;
        _scrfd      = scrfd;
        _inPort     = ports.Input("OSD-BVS-V1.5");
        _facePort   = ports.Output("OSD-BVO-V1.5", 1);
        _bodyPort   = ports.OutputOrDefault("OSD-BVO-V1.5", 2, "");
        _otherPort  = ports.Output("OSD-BVO-V1.5", 3);
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Visual Scene Descriptors on input port"));

        // The scene as it came, and each Object in it READ as the schema defines it (PortObjects), also where the scene is in the form the repository used before that rule.
        JsonArray? entries;
        try { entries = (JsonNode.Parse(json) as JsonObject)?["BasicVisualSceneDescriptors"] as JsonArray; }
        catch (System.Text.Json.JsonException) { entries = null; }
        if (entries is null)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "could not parse Basic Visual Scene Descriptors"));

        // A scene has one or more visual objects. Each is classified by SCRFD and its qualifier tagged. A message carries one object per Port: the first Face
        // object goes to the Face Port, the first of the others to the Other Port (a scene of more objects of one type is a limit of this implementation).
        var ports = new Dictionary<string, string>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var held = (entry["VObjectIDOrVObject"] as JsonArray)?.FirstOrDefault() ?? entry["VObjectIDOrVObject"];
            if (held is not JsonObject) continue;                                    // an Object ID only: nothing to look at
            BasicVisualObject obj;
            try { obj = PortObjects.ReadVisual(held.ToJsonString()); }
            catch (Exception) { continue; }

            string type = "Object";
            if (obj.Data is { Length: > 0 })
            {
                try { type = _scrfd.Detect(obj.Data).Count > 0 ? "Face" : "Object"; }
                catch { type = "Object"; }
            }

            var port = type == "Face" ? _facePort : _otherPort;
            if (!ports.ContainsKey(port)) ports[port] = PortObjects.WriteVisual(ReQualify(obj, type));     // written as an Object of the schema
        }

        if (ports.Count == 0)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no visual object in scene"));

        return Task.FromResult(new Message
        {
            MessageId   = message.MessageId,
            MessageType = message.MessageType,
            Ports       = ports
        });
    }

    // Return the object with its Visual Qualifier's VisualObjectType set.
    private static BasicVisualObject ReQualify(BasicVisualObject obj, string visualObjectType)
    {
        var q  = obj.VisualQualifier;
        var at = q?.Attributes;
        var newAttrs = new VisualAttributes
        {
            Source               = at?.Source,
            VisualObjectType     = visualObjectType,
            Metadata             = at?.Metadata,
            ObjectID             = at?.ObjectID,
            EntityInternalStatus = at?.EntityInternalStatus,
            Device               = at?.Device
        };
        var newQual = new VisualQualifier
        {
            Header              = q?.Header ?? "TFA-VIQ-V1.5",
            MInstanceID         = q?.MInstanceID,
            UEnvironmentID      = q?.UEnvironmentID,
            VisualQualifierID   = q?.VisualQualifierID ?? "",
            VisualQualifierTime = q?.VisualQualifierTime,
            SubType             = q?.SubType,
            Format              = q?.Format,
            Attributes          = newAttrs,
            DataXMData          = q?.DataXMData,
            DescrMetadata       = q?.DescrMetadata
        };
        return new BasicVisualObject
        {
            Header              = obj.Header,
            BasicVisualObjectID = obj.BasicVisualObjectID,
            FileName            = obj.FileName,
            Data                = obj.Data,
            VisualQualifier     = newQual
        };
    }
}
