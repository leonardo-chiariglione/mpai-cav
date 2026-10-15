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
// ONE IS ALWAYS GIVEN, because what follows a Portable Avatar needs it to know whose
// update it is: the Avatar ID, the Avatar's ID where the Portable Avatar carries one,
// else the Portable Avatar's own. Everything else is given only if the Portable Avatar
// carries it - no empty placeholders (author's ruling 2026/10/14: the Avatar Model is
// shown if it is there, a default avatar is placed if it is not).
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
    private readonly string _avatarIdPort, _avatarSpaceTimePort, _modelPort, _selectorPort, _speechPort, _textPort, _speechModelPort, _psPort, _sceneSpaceTimePort, _scenePort;

    public PdxAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId          = instanceId;
        _inPort              = ports.Input("PAF-PAV-V1.6");
        // The ten outputs of the figure of PAF-PDX (PAF Reference Models, slide 16).
        _avatarIdPort        = ports.OutputOrDefault("string", "");                 // the Avatar ID: an ID, declared, not an Instance Identifier
        _avatarSpaceTimePort = ports.OutputOrDefault("OSD-SPT-V1.5", 1, "");
        _modelPort           = ports.OutputOrDefault("OSD-B3O-V1.5", "");          // the Avatar Model: the 3D model of the avatar
        _selectorPort        = ports.OutputOrDefault("OSD-SEL-V1.5", "");
        _speechPort          = ports.OutputOrDefault("OSD-BSO-V1.5", "");
        _textPort            = ports.OutputOrDefault("OSD-BTO-V1.5", "");
        _speechModelPort     = ports.OutputOrDefault("AIP-MLO-V1.0", "");          // the ML Model for Speech Synthesis
        _psPort              = ports.OutputOrDefault("MMC-EPS-V2.5", "");
        _sceneSpaceTimePort  = ports.OutputOrDefault("OSD-SPT-V1.5", 2, "");
        _scenePort           = ports.OutputOrDefault("OSD-BMS-V1.5", "");
    }

    public string InstanceId => _instanceId;

    // A Portable Avatar is an UPDATE of an avatar: it names the avatar and carries what changed. The Avatar ID is always given, because what follows needs to know whose
    // update it is; every other item is given only when the Portable Avatar carries it.
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
        var models = pav["MLModelOrMLModelID"] as JsonArray ?? new JsonArray();

        Give(_avatarIdPort, JsonValue.Create(IdOf(pav, avatars)));
        Give(_avatarSpaceTimePort, pav["AvatarSpaceTime"] ?? pav["PortableAvatarSpaceTime"]);
        Give(_modelPort, avatars.OfType<JsonObject>().FirstOrDefault());
        Give(_selectorPort, pav["LanguageSelector"]);
        Give(_speechModelPort, models.OfType<JsonObject>().FirstOrDefault());
        if (PersonalStatusOf(pav) is { } status) Give(_psPort, JsonNode.Parse(MpaiJson.ToJson(status)));
        Give(_sceneSpaceTimePort, pav["AVSceneSpaceTime"]);
        Give(_scenePort, pav["AVSceneDescriptors"]);

        // Speech or text, not both: a Portable Avatar may carry both (the transmitter puts in what it recognised); the text is then what was said, and the speech is not
        // given, so that the words are not recognised a second time and counted twice.
        var text = pav["TextObject"];
        if (text is not null && !string.IsNullOrWhiteSpace(TextOf(text))) Give(_textPort, text);
        else Give(_speechPort, speeches.OfType<JsonObject>().FirstOrDefault());

        return Done(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = outputs });
    }

    private static System.Threading.Tasks.Task<Message> Done(Message m) => System.Threading.Tasks.Task.FromResult(m);

    // The ID of the avatar a Portable Avatar updates; also the Videoconference Client Receiver's, which demultiplexes what it receives.
    public static string IdOf(JsonObject pav, JsonArray avatars) =>
        avatars.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
        ?? (string?)avatars.OfType<JsonObject>().FirstOrDefault()?["Basic3DModelObjectID"]
        ?? (string?)pav["PortableAvatarID"]
        ?? "";

    private static string TextOf(JsonNode text)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(text.ToJsonString())?.GetText() ?? ""; }
        catch { return ""; }
    }

    // The Personal Status of the Portable Avatar is its Entity State, as far as it is implemented: the Cognitive State, the Emotion and the Social Attitude (the
    // other elements of the Entity State are not used for the time being); none where the Portable Avatar has no Entity State.
    private static EntityPersonalStatus? PersonalStatusOf(JsonObject pav)
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

        return null;
    }
}
