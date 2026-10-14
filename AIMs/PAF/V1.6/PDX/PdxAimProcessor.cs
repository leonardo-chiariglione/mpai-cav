using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Pdx;

// PAF-PDX-V1.6 - Portable Avatar Demultiplexing, as an AIF IAimProcessor.
//
// Gives the components present in a Portable Avatar (PAF-PAV-V1.6), each on its
// Port: the Avatar ID, the Audio-Visual Scene, the Space-Time, the Avatar Model,
// the Language Selector, the Speech and Text Objects, the Personal Status, the Face
// and Body Descriptors Objects, the Spatial Attitude. A component the Portable
// Avatar does not carry is not given.
//
// TWO ARE ALWAYS GIVEN, because what follows a Portable Avatar needs them to know
// who spoke and how - in the Virtual Meeting Secretary, Summary Creation waits for
// both:
//   - the Avatar ID: the Avatar's ID where the Portable Avatar carries one, else the
//     Portable Avatar's own;
//   - the Personal Status: the Portable Avatar's, else the one its Entity State
//     describes (Cognitive State, Emotion, Social Attitude), else a neutral one.
//
// SPEECH OR TEXT, NOT BOTH. A Portable Avatar may carry both a Speech Object and its
// Text (the Videoconference Client Transmitter puts in what it recognised). The
// Text is then what was said, and the Speech is not given, so that the words are
// not recognised a second time and counted twice.
//
// The Portable Avatar holds each component in the form the component has inside
// this implementation (PortData converts the whole to and from the wire form).
public sealed class PdxAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _inPort;
    private readonly string _avatarIdPort, _scenePort, _spaceTimePort, _modelPort, _selectorPort,
                            _speechPort, _textPort, _psPort, _facePort, _bodyPort, _attitudePort;

    public PdxAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId    = instanceId;
        _inPort        = ports.Input("PAF-PAV-V1.6");
        _avatarIdPort  = ports.OutputOrDefault("OSD-IID-V1.5", "");
        _scenePort     = ports.OutputOrDefault("OSD-BMS-V1.5", "");
        _spaceTimePort = ports.OutputOrDefault("OSD-SPT-V1.5", "");
        _modelPort     = ports.OutputOrDefault("PAF-AVT-V1.6", "");      // the Avatar (PAF-AVT): its ID, its 3D model and its animation
        _selectorPort  = ports.OutputOrDefault("OSD-SEL-V1.5", "");
        _speechPort    = ports.OutputOrDefault("OSD-BSO-V1.5", "");
        _textPort      = ports.OutputOrDefault("OSD-BTO-V1.5", "");
        _psPort        = ports.OutputOrDefault("MMC-EPS-V2.5", "");
        _facePort      = ports.OutputOrDefault("PAF-FDO-V1.6", "");
        _bodyPort      = ports.OutputOrDefault("PAF-BDO-V1.6", "");
        _attitudePort  = ports.OutputOrDefault("OSD-OSA-V1.5", "");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Done(Message.Error(message.MessageId, _instanceId, "no Portable Avatar on input port"));
        if (JsonNode.Parse(json) is not JsonObject pav)
            return Done(Message.Error(message.MessageId, _instanceId, "the Portable Avatar is not a JSON object"));

        var outputs = new Dictionary<string, string>();
        void Give(string port, JsonNode? value)
        {
            if (port.Length > 0 && value is not null) outputs[port] = value.ToJsonString();
        }

        var avatars = pav["AvatarOrAvatarID"] as JsonArray ?? new JsonArray();
        var speeches = pav["SPOOrSPOID"] as JsonArray ?? new JsonArray();

        Give(_avatarIdPort, JsonNode.Parse(MpaiJson.ToJson(Identifier(IdOf(pav, avatars)))));
        Give(_psPort, JsonNode.Parse(MpaiJson.ToJson(PersonalStatusOf(pav))));
        Give(_scenePort, pav["AVSceneDescriptors"]);
        Give(_spaceTimePort, pav["AvatarSpaceTime"] ?? pav["PortableAvatarSpaceTime"]);
        // The Avatar is not the Avatar Model: it is the Avatar data type (PAF-AVT), made of the avatar's ID, its 3D model, and the Face and Body Descriptors - its animation.
        if (avatars.OfType<JsonObject>().FirstOrDefault() is { } model) Give(_modelPort, AvatarOf(IdOf(pav, avatars), model, pav, speeches.OfType<JsonObject>().FirstOrDefault()));
        Give(_selectorPort, pav["LanguageSelector"]);
        Give(_facePort, pav["FaceDescriptorsObject"]);
        Give(_bodyPort, pav["BodyDescriptorsObject"]);
        Give(_attitudePort, pav["AvatarSpatialAttitude"]);

        var text = pav["TextObject"];
        if (text is not null && !string.IsNullOrWhiteSpace(TextOf(text))) Give(_textPort, text);
        else Give(_speechPort, speeches.OfType<JsonObject>().FirstOrDefault());

        return Done(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = outputs });
    }

    private static JsonObject AvatarOf(string id, JsonObject model, JsonObject pav, JsonObject? speech)
    {
        var data = new JsonObject { ["ModelOrModelID"] = new JsonArray(new JsonObject { ["3DModel"] = model.DeepClone() }) };
        if (pav["FaceDescriptorsObject"] is JsonObject face) data["FaceDescriptorsObject"] = face.DeepClone();
        if (pav["BodyDescriptorsObject"] is JsonObject body) data["BodyDescriptorsObject"] = body.DeepClone();
        // The Avatar has its speech, so that what follows - Multimodal Scene Creation - needs no other way to tell whose speech it is.
        if (speech is not null) data["SpeechObjectOrSpeechObjectID"] = new JsonArray(new JsonObject { ["SpeechObject"] = speech.DeepClone() });
        return new JsonObject { ["Header"] = "PAF-AVT-V1.6", ["AvatarID"] = id, ["AvatarData"] = data };
    }

    private static System.Threading.Tasks.Task<Message> Done(Message m) => System.Threading.Tasks.Task.FromResult(m);

    private static string IdOf(JsonObject pav, JsonArray avatars) =>
        avatars.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
        ?? (string?)avatars.OfType<JsonObject>().FirstOrDefault()?["Basic3DModelObjectID"]
        ?? (string?)pav["PortableAvatarID"]
        ?? "";

    private static InstanceIdentifier Identifier(string id) => new()
    {
        InstanceIdentifier_ = id,
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = id,
                LabelConfidenceLevel = 1,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "entity", "avatar" } }
            }
        }
    };

    private static string TextOf(JsonNode text)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(text.ToJsonString())?.GetText() ?? ""; }
        catch { return ""; }
    }

    // The Personal Status of the Portable Avatar is its Entity State, as far as it is implemented: the Cognitive State, the Emotion and the Social Attitude (the
    // other elements of the Entity State are not used for the time being); else neutral.
    private static EntityPersonalStatus PersonalStatusOf(JsonObject pav)
    {
        if (pav["EntityState"] is JsonObject state)
        {
            T? Factor<T>(string name) where T : class =>
                state[name] is JsonObject f ? MpaiJson.FromJson<T>(f.ToJsonString()) : null;
            var emotion = Factor<Emotion>("Emotion");
            var cognitive = Factor<CognitiveState>("CognitiveState");
            var social = Factor<SocialAttitude>("SocialAttitude");
            if (emotion is not null || cognitive is not null || social is not null)
                return new EntityPersonalStatus
                {
                    TextPersonalStatus = new TextPersonalStatus
                    {
                        TextEmotion = emotion, TextCognitiveState = cognitive, TextSocialAttitude = social
                    }
                };
        }

        return new EntityPersonalStatus
        {
            TextPersonalStatus = new TextPersonalStatus { TextEmotion = Emotion.Of(FactorLabel.Of("NEUTRAL", "neutral", null, 0.5)) }
        };
    }
}
