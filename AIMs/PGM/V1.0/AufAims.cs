using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Pgm;

// The Sub-AIMs of PGM-AUF-V1.0 that this slice implements, each against the Ports of its own Metadata.

// PGM-PSD-V1.0 - Personal Status Demultiplexing: splits the A-User State into the Speech, Face and Body Personal Statuses.
public sealed class PsdAim : PgmAim
{
    private readonly string _state, _request, _speech, _face, _body, _response;

    public PsdAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _state = In("MMC-EST-V2.5"); _request = In(Auc.Auf);
        _speech = Out("MMC-SPS-V2.5"); _face = Out("MMC-FPS-V2.5"); _body = Out("MMC-BPS-V2.5"); _response = Out(Auc.Auf);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        var state = Read(message, _state);
        if (state is not null)
        {
            outputs.Add((_speech, new JsonObject { ["Header"] = "MMC-SPS-V2.5", ["SpeechPersonalStatusID"] = NewId("sps") }));
            outputs.Add((_face, new JsonObject { ["Header"] = "MMC-FPS-V2.5", ["FacePersonalStatusID"] = NewId("fps") }));
            outputs.Add((_body, new JsonObject { ["Header"] = "MMC-BPS-V2.5", ["BodyPersonalStatusID"] = NewId("bps") }));
        }
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) outputs.Add((_response, AufResponse.Make(rq)));
        return Done(Give(message, outputs));
    }
}

// PGM-FRD-V1.0 - Final Response Demultiplexing. Takes apart the Final Response of Basic Knowledge: the Text (for Text-To-Speech and the Portable Avatar), the Actions of MMM-TEC V2.2 that go
// with the text (for Animation Object Generation) and the Process Actions, more remote from the text (they leave A-User Formation unchanged).
// A Port carries one Message at a time: of the Actions the Port carries the first (in this slice Basic Knowledge gives at most one).
public sealed class FrdAim : PgmAim
{
    private readonly string _request, _final, _text, _action, _actions, _response;

    public FrdAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _request = In(Auc.Auf); _final = In("PGM-FRS-V1.0");
        _text = Out("OSD-BTO-V1.5"); _action = Out("MMM-ACT-V2.2"); _actions = Out("MMM-PAT-V2.2"); _response = Out(Auc.Auf);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _final) is { } fr)
        {
            if (fr["Text"] is JsonObject text) outputs.Add((_text, text.DeepClone()));
            if (fr["Actions"] is JsonArray { Count: > 0 } actions && actions[0] is JsonObject first) outputs.Add((_action, first.DeepClone()));
            if (fr["ProcessActions"] is JsonObject processActions) outputs.Add((_actions, processActions.DeepClone()));
            if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) outputs.Add((_response, AufResponse.Make(rq)));
        }
        return Done(Give(message, outputs));
    }
}

// PGM-AOG-V1.0 - Animation Object Generation. Generates the animation of the Persona of the A-User from an Action of MMM-TEC V2.2 that goes with the text: for MM-Animate, a Face Animation
// Object (PAF-AMO, Port 1) and a Body Animation Object (PAF-AMO, Port 2). A STUB of this slice: the Animation Qualifier names a symbolic pose (PoseToken) and the data is the Action itself;
// the frames of the face (FACS) and of the body (BAP, BVH), from the text and the speech, are for Entity Face Description and Entity Body Description to make.
public sealed class AogAim : PgmAim
{
    private readonly string _request, _action, _face, _body, _response;

    public AogAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _request = In(Auc.Auf); _action = In("MMM-ACT-V2.2");
        _face = Out("PAF-AMO-V1.6", 1); _body = Out("PAF-AMO-V1.6", 2); _response = Out(Auc.Auf);
    }

    private JsonObject Animation(string idPrefix, string action) => new()
    {
        ["Header"] = "PAF-AMO-V1.6", ["AnimationObjectID"] = NewId(idPrefix),
        ["AnimationObjectData"] = new JsonArray(new JsonObject { ["Data"] = action }),
        ["AnimationQualifier"] = new JsonObject { ["Header"] = "TFA-AMQ-V1.5", ["AnimationQualifierID"] = NewId("amq"), ["SubTypes"] = "Animation", ["Formats"] = new JsonObject { ["ContentFormat"] = "PoseToken" } }
    };

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _action) is { } action)
        {
            var keyword = (string?)action["Action"] ?? "";
            if (keyword == "MM-Animate")
            {
                outputs.Add((_face, Animation("fan", keyword)));
                outputs.Add((_body, Animation("ban", keyword)));
            }
            if (Read(message, _request) is { } rq && Auc.IsRequest(rq))
            {
                var r = AufResponse.Make(rq);
                r["ActionStatus"] = new JsonArray(new JsonObject { ["Action"] = keyword, ["Status"] = keyword == "MM-Animate" ? "completed" : "failed" });
                outputs.Add((_response, r));
            }
        }
        return Done(Give(message, outputs));
    }
}

// PGM-EFD-V1.0 - Entity Face Description. The Face Descriptors of the A-User: mostly from its speech, with the Face Animation Object and the Face Personal Status when there are.
// The descriptors are a STUB: a note of what the face is to do, in the Data property of the Face Descriptors Object.
public sealed class EfdAim : PgmAim
{
    private readonly string _request, _final, _speech, _motion, _status, _face, _response;

    public EfdAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _request = In(Auc.Auf); _final = In("PGM-FRS-V1.0"); _speech = In("OSD-BSO-V1.5"); _motion = In("PAF-AMO-V1.6"); _status = In("MMC-FPS-V2.5");
        _face = Out("PAF-FDO-V1.6"); _response = Out(Auc.Auf);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _speech) is not null)
        {
            var note = Read(message, _motion)?["AnimationObjectData"]?[0]?["Data"] is JsonValue g ? $"speaking; gesture {(string?)g}" : "speaking";
            outputs.Add((_face, new JsonObject
            {
                ["Header"] = "PAF-FDO-V1.6", ["FaceDescriptorsObjectID"] = NewId("fdo"),
                ["FaceDescriptorsData"] = new JsonArray(new JsonObject { ["Confidence"] = 1.0, ["Data"] = note })
            }));
        }
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) outputs.Add((_response, AufResponse.Make(rq)));
        return Done(Give(message, outputs));
    }
}

// PGM-EBD-V1.0 - Entity Body Description. The Body Descriptors of the A-User: from the Body Animation Object and the Body Personal Status. A STUB: a note of what the body is to do.
public sealed class EbdAim : PgmAim
{
    private readonly string _request, _final, _status, _motion, _body, _response;

    public EbdAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _request = In(Auc.Auf); _final = In("PGM-FRS-V1.0"); _status = In("MMC-BPS-V2.5"); _motion = In("PAF-AMO-V1.6");
        _body = Out("PAF-BDO-V1.6"); _response = Out(Auc.Auf);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _final) is not null)
        {
            var note = Read(message, _motion)?["AnimationObjectData"]?[0]?["Data"] is JsonValue g ? $"gesture {(string?)g}" : "at rest";
            outputs.Add((_body, new JsonObject
            {
                ["Header"] = "PAF-BDO-V1.6", ["BodyDescriptorsObjectID"] = NewId("bdo"),
                ["BodyDescriptorsData"] = new JsonArray(new JsonObject { ["Confidence"] = 1.0, ["Data"] = note })
            }));
        }
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) outputs.Add((_response, AufResponse.Make(rq)));
        return Done(Give(message, outputs));
    }
}

// The response of an AUF Sub-AIM to the Request of A-User Control (PGM-UCF, Mode RS).
public static class AufResponse
{
    public static JsonObject Make(JsonObject request)
    {
        var r = Auc.Make(Auc.Auf, Auc.Response, PgmAim.Session.Of(request), Auc.IdOf(request));
        r["Outcome"] = "achieved";
        return r;
    }
}

// PGM-AUF-V1.0 - A-User Formation. The composite AIM: from the Final Response of Basic Knowledge and the A-User State it forms the Portable Avatar - the speech, the face and the body of
// the A-User - and the Process Actions that are not the Persona's own. It gives them to A-User Control and to nothing else. Inside, in this order: Personal Status Demultiplexing,
// Final Response Demultiplexing, Animation Object Generation, Text-To-Speech (the synthesiser given), Entity Face Description, Entity Body Description, and the multiplexing of the Portable Avatar.
public sealed class AufAim : PgmAim
{
    private readonly string _request, _model, _speechModel, _final, _state, _avatar, _response, _actions, _avatarIdPort;
    private readonly PsdAim _psd; private readonly FrdAim _frd; private readonly AogAim _aog; private readonly EfdAim _efd; private readonly EbdAim _ebd;
    private readonly ISpeechSynthesiser _voice;
    private readonly string _avatarId;
    private JsonObject? _pendingRequest, _pendingFinal, _avatarModel, _aUserState, _speechModelObject;

    public AufAim(string instanceId, AimPortReader ports, PsdAim psd, FrdAim frd, AogAim aog, EfdAim efd, EbdAim ebd, ISpeechSynthesiser voice, string avatarId = "a-user-avatar") : base(instanceId, ports)
    {
        (_psd, _frd, _aog, _efd, _ebd, _voice, _avatarId) = (psd, frd, aog, efd, ebd, voice, avatarId);
        _request = In(Auc.Auf); _model = In("OSD-B3O-V1.5"); _speechModel = In("AIP-MLO-V1.0"); _final = In("PGM-FRS-V1.0"); _state = In("MMC-EST-V2.5"); _avatarIdPort = In("OSD-IID-V1.5");
        _avatar = Out("PAF-PAV-V1.6"); _response = Out(Auc.Auf); _actions = Out("MMM-PAT-V2.2");
    }

    public override async Task<Message> ProcessAsync(Message message)
    {
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) _pendingRequest = rq;
        if (Read(message, _final) is { } fr) _pendingFinal = fr;
        if (Read(message, _model) is { } m) _avatarModel = m;
        if (Read(message, _speechModel) is { } sm) _speechModelObject = sm;
        if (Read(message, _state) is { } st) _aUserState = st;
        if (_pendingRequest is null || _pendingFinal is null) return Give(message, []);
        var request = _pendingRequest; var final = _pendingFinal;
        _pendingRequest = null; _pendingFinal = null;

        Dictionary<string, string> With(PgmAim a, params (string Key, JsonNode? Value)[] ports) =>
            ports.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.ToJsonString());
        JsonObject? Take(Message m, PgmAim a, string dataType, int n = 1) => Read(m, a.OutputKey(dataType, n));

        // 1. Personal Status Demultiplexing
        var psd = await _psd.ProcessAsync(new Message
        {
            Ports = With(_psd, (_psd.InputKey("MMC-EST-V2.5"), _aUserState), (_psd.InputKey(Auc.Auf), request))
        });
        // 2. Final Response Demultiplexing, 3. Animation Object Generation
        var frd = await _frd.ProcessAsync(new Message { Ports = With(_frd, (_frd.InputKey(Auc.Auf), request), (_frd.InputKey("PGM-FRS-V1.0"), final)) });
        var others = Take(frd, _frd, "MMM-PAT-V2.2");
        var aog = await _aog.ProcessAsync(new Message { Ports = With(_aog, (_aog.InputKey(Auc.Auf), request), (_aog.InputKey("MMM-ACT-V2.2"), Take(frd, _frd, "MMM-ACT-V2.2"))) });
        var faceMotion = Take(aog, _aog, "PAF-AMO-V1.6", 1); var bodyMotion = Take(aog, _aog, "PAF-AMO-V1.6", 2);
        // 4. Text-To-Speech
        var words = TextOf(Take(frd, _frd, "OSD-BTO-V1.5") ?? final["Text"] as JsonObject) ?? "";
        var speech = words.Length > 0 ? await _voice.SynthesiseAsync(words) : null;
        // 5. Entity Face Description, 6. Entity Body Description
        var efd = await _efd.ProcessAsync(new Message
        {
            Ports = With(_efd, (_efd.InputKey(Auc.Auf), request), (_efd.InputKey("PGM-FRS-V1.0"), final), (_efd.InputKey("OSD-BSO-V1.5"), speech),
                (_efd.InputKey("PAF-AMO-V1.6"), faceMotion), (_efd.InputKey("MMC-FPS-V2.5"), Take(psd, _psd, "MMC-FPS-V2.5")))
        });
        var ebd = await _ebd.ProcessAsync(new Message
        {
            Ports = With(_ebd, (_ebd.InputKey(Auc.Auf), request), (_ebd.InputKey("PGM-FRS-V1.0"), final), (_ebd.InputKey("MMC-BPS-V2.5"), Take(psd, _psd, "MMC-BPS-V2.5")),
                (_ebd.InputKey("PAF-AMO-V1.6"), bodyMotion))
        });

        // 7. Portable Avatar Multiplexing
        var session = Session.Of(request);
        var pav = new JsonObject { ["Header"] = "PAF-PAV-V1.6" };
        pav["MInstanceID"] = session.MInstance;
        pav["PortableAvatarID"] = NewId("pav");
        pav["PortableAvatarTime"] = Time();
        pav["AvatarOrAvatarID"] = new JsonArray(_avatarModel is not null ? _avatarModel.DeepClone() : JsonValue.Create(_avatarId));
        if (_speechModelObject is not null) pav["MLModelOrMLModelID"] = new JsonArray(_speechModelObject.DeepClone());
        if (speech is not null) pav["SPOOrSPOID"] = new JsonArray(speech);
        if (Take(efd, _efd, "PAF-FDO-V1.6") is { } fdo) pav["FaceDescriptorsObject"] = fdo;
        if (Take(ebd, _ebd, "PAF-BDO-V1.6") is { } bdo) pav["BodyDescriptorsObject"] = bdo;

        var response = AufResponse.Make(request);
        response["RenderState"] = "rendered";
        response["PortableAvatarID"] = (string)pav["PortableAvatarID"]!;
        response["ElementOutcomes"] = new JsonObject
        {
            ["SpeechSynthesis"] = new JsonObject { ["Outcome"] = speech is null ? "not_requested" : "success" },
            ["FaceDescription"] = new JsonObject { ["Outcome"] = pav.ContainsKey("FaceDescriptorsObject") ? "success" : "not_requested" },
            ["BodyDescription"] = new JsonObject { ["Outcome"] = pav.ContainsKey("BodyDescriptorsObject") ? "success" : "not_requested" },
            ["AvatarSynthesis"] = new JsonObject { ["Outcome"] = "success" }
        };
        if (Read(aog, _aog.OutputKey(Auc.Auf)) is { } animated && animated["ActionStatus"] is JsonNode status) response["ActionStatus"] = status.DeepClone();

        return Give(message, [(_avatar, pav), (_response, response), (_actions, others)]);
    }
}
