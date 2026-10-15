using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Pmx;

// PAF-PMX-V1.6 - Portable Avatar Multiplexing, as an AIF IAimProcessor.
//
// The inverse of Portable Avatar Demultiplexing: puts what it is given into one
// Portable Avatar (PAF-PAV-V1.6) - the Avatar ID and Model, the Language Selector,
// the Speech and Text Objects, the Face and Body Descriptors Objects, the Personal
// Status, the Audio-Visual Scene and its Space-Time, the Avatar's Space-Time and
// Spatial Attitude. Where an Input Portable Avatar is given, it starts from it and
// replaces what it is given anew. It multiplexes; it does not compute.
//
// The Portable Avatar holds each component in the form the component has inside
// this implementation (PortData converts the whole to and from the wire form).
public sealed class PmxAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _avatarIdPort, _avatarSpaceTimePort, _modelPort, _selectorPort, _speechPort,
                            _recognisedTextPort, _inputTextPort, _facePort, _bodyPort, _psPort,
                            _sceneSpaceTimePort, _scenePort, _inputPavPort, _attitudePort;
    private readonly string _outPort;

    public PmxAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId          = instanceId;
        _avatarIdPort        = ports.InputOrDefault("string", "");              // the Avatar ID: an ID (a string), or the Instance Identifier of what Service Participant Authentication identified
        _avatarSpaceTimePort = ports.InputOrDefault("OSD-SPT-V1.5", 1, "");
        _sceneSpaceTimePort  = ports.InputOrDefault("OSD-SPT-V1.5", 2, "");
        _modelPort           = ports.InputOrDefault("OSD-B3O-V1.5", "");
        _selectorPort        = ports.InputOrDefault("OSD-SEL-V1.5", "");
        _speechPort          = ports.InputOrDefault("OSD-BSO-V1.5", "");
        _recognisedTextPort  = ports.InputOrDefault("OSD-BTO-V1.5", 1, "");
        _inputTextPort       = ports.InputOrDefault("OSD-BTO-V1.5", 2, "");
        _facePort            = ports.InputOrDefault("PAF-FDO-V1.6", "");
        _bodyPort            = ports.InputOrDefault("PAF-BDO-V1.6", "");
        _psPort              = ports.InputOrDefault("MMC-EPS-V2.5", "");
        _scenePort           = ports.InputOrDefault("OSD-BMS-V1.5", "");
        _inputPavPort        = ports.InputOrDefault("PAF-PAV-V1.6", "");
        _attitudePort        = ports.InputOrDefault("OSD-OSA-V1.5", "");
        _outPort             = ports.Output("PAF-PAV-V1.6");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        JsonNode? In(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? JsonNode.Parse(json) : null;

        var pav = In(_inputPavPort) as JsonObject ?? new JsonObject();
        pav["Header"] = "PAF-PAV-V1.6";
        pav["PortableAvatarID"] = Guid.NewGuid().ToString();

        void Put(string field, JsonNode? value)
        {
            if (value is not null) pav[field] = value;
        }

        // The Avatar: its Model, and its ID - each an item of AvatarOrAvatarID.
        // Where it starts from an Input Portable Avatar, what the Avatar already has stays unless it is given anew: the Model the participant sent at the introduction is not lost
        // when the server's PMX puts in the ID.
        var model = In(_modelPort);
        var id = In(_avatarIdPort) is { } given ? NameOf(given) : null;
        var held = pav["AvatarOrAvatarID"] as JsonArray;
        model ??= held?.OfType<JsonObject>().FirstOrDefault()?.DeepClone();
        if (string.IsNullOrWhiteSpace(id)) id = held?.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (model is not null || !string.IsNullOrWhiteSpace(id))
        {
            var avatar = new JsonArray();
            if (model is not null) avatar.Add(model);
            if (!string.IsNullOrWhiteSpace(id)) avatar.Add(JsonValue.Create(id));
            pav["AvatarOrAvatarID"] = avatar;
        }

        if (In(_speechPort) is { } speech) pav["SPOOrSPOID"] = new JsonArray(speech);
        // The text typed wins over the text recognised: it is what was meant.
        Put("TextObject", In(_inputTextPort) ?? (_recognisedTextPort != _inputTextPort ? In(_recognisedTextPort) : null));
        Put("LanguageSelector", In(_selectorPort));
        Put("FaceDescriptorsObject", In(_facePort));
        Put("BodyDescriptorsObject", In(_bodyPort));
        // The Personal Status goes into the Entity State of the Portable Avatar - the Cognitive State, the Emotion and the Social Attitude of it, the other elements of
        // the Entity State not being used for the time being.
        if (In(_psPort) is { } eps) Put("EntityState", EntityStates.OfPersonalStatus(eps.ToJsonString()));
        Put("AVSceneDescriptors", In(_scenePort));
        Put("AvatarSpatialAttitude", In(_attitudePort));
        Put("AvatarSpaceTime", In(_avatarSpaceTimePort));
        Put("AVSceneSpaceTime", _sceneSpaceTimePort != _avatarSpaceTimePort ? In(_sceneSpaceTimePort) : null);

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_outPort] = pav.ToJsonString() }
        });
    }

    // The name of an avatar: the ID as it is, or the label of the Instance Identifier.
    public static string? NameOf(JsonNode given)
    {
        if (given is JsonValue v) return v.TryGetValue<string>(out var id) ? id : null;
        try { return MpaiJson.FromJson<InstanceIdentifier>(given.ToJsonString())?.InstanceIdentifierData.FirstOrDefault()?.InstanceLabel; }
        catch { return null; }
    }
}
