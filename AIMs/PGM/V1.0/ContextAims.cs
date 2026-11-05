using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Pgm;

// PGM-CXT-V1.0 - Context Description. Takes what the User Agent acquired (Text, Speech, Audio, Visual Objects) and the Request of A-User Control, and gives the Context
// Descriptors. Its Sub-AIMs (Audio and Visual Scene Description, ASI, ASR, salience, alignment, behaviour and emotion analysis ...) are the implementer's business: in this slice
// the speech is recognised (the recogniser is given), and the descriptors of the audio and visual scenes are minimal valid Data - STUBS.
//
// WHERE THE RECOGNISED TEXT GOES. The words the human said or typed travel in the RecognisedText property of the Context Descriptors (PGM-CXD), a Basic Text Object.
public sealed class CxtAim : PgmAim
{
    private readonly string _audio, _visual, _text, _speech, _request, _domainIn, _historyIn, _descriptors, _domainOut, _historyOut, _response;
    private readonly ISpeechRecogniser? _recogniser;
    private JsonObject? _pendingRequest;
    private string? _words;

    public CxtAim(string instanceId, AimPortReader ports, ISpeechRecogniser? recogniser = null) : base(instanceId, ports)
    {
        _recogniser = recogniser;
        _audio = In("OSD-BAO-V1.5"); _visual = In("OSD-BVO-V1.5"); _request = In(Auc.Cxt); _domainIn = In("PGM-DOM-V1.0"); _historyIn = In("PGM-IHI-V1.0");
        _text = In("OSD-BTO-V1.5"); _speech = In("OSD-BSO-V1.5");
        _descriptors = Out("PGM-CXD-V1.0"); _domainOut = Out("PGM-DOM-V1.0"); _historyOut = Out("PGM-IHI-V1.0"); _response = Out(Auc.Cxt);
    }

    public override async Task<Message> ProcessAsync(Message message)
    {
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) _pendingRequest = rq;
        if (Read(message, _text) is { } text) _words = TextOf(text);
        else if (Read(message, _speech) is { } speech)
            _words = _recogniser is not null ? await _recogniser.RecogniseAsync(speech) : "";

        if (_pendingRequest is null || _words is null) return Give(message, []);
        var request = _pendingRequest; var words = _words;
        _pendingRequest = null; _words = null;

        var session = Session.Of(request);
        var response = Auc.Make(Auc.Cxt, Auc.Response, session, Auc.IdOf(request));
        if (words.Length == 0)
        {
            response["Outcome"] = "not_achieved";
            response["Reason"] = new JsonObject { ["Code"] = "insufficient_data", ["Description"] = "no words could be recognised" };
            return Give(message, [(_response, response)]);
        }
        response["Outcome"] = "achieved";

        var cxd = new JsonObject { ["Header"] = "PGM-CXD-V1.0" };
        session.Stamp(cxd);
        cxd["ContextDescriptorsID"] = NewId("cxd");
        cxd["ContextDescriptorsTime"] = Time();
        cxd["EnhancedAudioSceneDescriptors"] = new JsonObject
        {
            ["Header"] = "OSD-EAD-V1.5", ["EnhancedAudioSceneDescriptorsID"] = NewId("ead"), ["BaseASDID"] = NewId("asd"),
            ["Entities"] = new JsonArray(new JsonObject { ["EntityID"] = "human", ["ASDItemID"] = "human-speech" })
        };
        cxd["EnhancedVisualSceneDescriptors"] = new JsonObject
        {
            ["Header"] = "OSD-EVD-V1.5", ["EnhancedVisualSceneDescriptorsID"] = NewId("evd"), ["BaseVSDID"] = NewId("vsd"), ["Entities"] = new JsonArray()
        };
        cxd["UserState"] = new JsonObject { ["Header"] = "MMC-EST-V2.5", ["EntityStateID"] = NewId("est"), ["Engagement"] = new JsonObject { ["Level"] = "medium", ["Mode"] = "interactive" } };
        cxd["RecognisedText"] = Text(words);
        return Give(message, [(_descriptors, cxd), (_response, response)]);
    }

    // The words the CXD carries, for the AIM that reads them.
    public static string? WordsOf(JsonObject cxd) => TextOf(cxd["RecognisedText"] as JsonObject);
}

// PGM-PRC-V1.0 - Prompt Creation. From the Context Descriptors and the Request of A-User Control it gives the PC Prompt for Basic Knowledge; to a query of Basic Knowledge it answers.
public sealed class PrcAim : PgmAim
{
    private readonly string _descriptors, _request, _historyIn, _query, _prompt, _queryOut, _response, _historyOut;
    private JsonObject? _cxd, _pendingRequest;

    public PrcAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _descriptors = In("PGM-CXD-V1.0"); _request = In(Auc.Prc); _historyIn = In("PGM-IHI-V1.0"); _query = In("PGM-KPR-V1.0");
        _prompt = Out("PGM-PCP-V1.0"); _queryOut = Out("PGM-KPR-V1.0"); _response = Out(Auc.Prc); _historyOut = Out("PGM-IHI-V1.0");
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _descriptors) is { } cxd) _cxd = cxd;
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) _pendingRequest = rq;

        if (Read(message, _query) is { } q && (string?)q["Mode"] == Auc.Request)
        {
            var answer = Knowledge.Answer("PGM-KPR-V1.0", q);
            answer["Kind"] = (string?)q["Kind"] ?? "action_context";
            answer["Representation"] = new JsonObject
            {
                ["ActionContext"] = new JsonObject { ["Repertoire"] = new JsonArray("MU-Actuate", "MM-Animate"), ["MaxSteps"] = 3 }
            };
            outputs.Add((_queryOut, answer));
        }

        if (_cxd is not null && _pendingRequest is not null)
        {
            var request = _pendingRequest; var context = _cxd;
            _pendingRequest = null; _cxd = null;
            var session = Session.Of(request);
            var prompt = new JsonObject { ["Header"] = "PGM-PCP-V1.0" };
            session.Stamp(prompt);
            prompt["PCPromptID"] = NewId("pcp");
            prompt["PCPromptTime"] = Time();
            prompt["DialogID"] = (string?)request["RequestID"] ?? Auc.IdOf(request);
            prompt["Prompt"] = CxtAim.WordsOf(context) ?? "";
            prompt["SceneContext"] = context;
            outputs.Add((_prompt, prompt));
            var response = Auc.Make(Auc.Prc, Auc.Response, session, Auc.IdOf(request));
            response["Outcome"] = "achieved";
            outputs.Add((_response, response));
        }
        return Done(Give(message, outputs));
    }
}

// The answer of a partner of Basic Knowledge to one of its queries (PGM-KPR, KDA, KUS, KPA): the Response mode of the same Data Type.
public static class Knowledge
{
    public static JsonObject Answer(string header, JsonObject query)
    {
        var a = new JsonObject { ["Header"] = header, ["Mode"] = "RS" };
        PgmAim.Session.Of(query).Stamp(a);
        a["DialogID"] = (string?)query["DialogID"] ?? "";
        a["TurnIndex"] = (int?)query["TurnIndex"] ?? 0;
        a["QueryID"] = PgmAim.NewId("ans");
        a["QueryTime"] = PgmAim.Time();
        a["RequestID"] = (string?)query["QueryID"] ?? "";
        a["Outcome"] = "answered";
        return a;
    }

    public static JsonObject Query(string header, PgmAim.Session session, string dialogId, int turn)
    {
        var q = new JsonObject { ["Header"] = header, ["Mode"] = "RQ" };
        session.Stamp(q);
        q["DialogID"] = dialogId;
        q["TurnIndex"] = turn;
        q["QueryID"] = PgmAim.NewId("qry");
        q["QueryTime"] = PgmAim.Time();
        return q;
    }
}

// PGM-DAC-V1.0 - Domain Access. A fixed small domain: the A-User may open doors of the building. It answers Basic Knowledge, A-User Control and the domain requests.
public sealed class DacAim : PgmAim
{
    private readonly string _auc, _domainIn, _query, _historyIn, _domainOut, _answer, _historyOut, _response;

    public DacAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _auc = In(Auc.Dac); _domainIn = In("PGM-DOM-V1.0"); _query = In("PGM-KDA-V1.0"); _historyIn = In("PGM-IHI-V1.0");
        _domainOut = Out("PGM-DOM-V1.0"); _answer = Out("PGM-KDA-V1.0"); _historyOut = Out("PGM-IHI-V1.0"); _response = Out(Auc.Dac);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _query) is { } q && (string?)q["Mode"] == Auc.Request)
            outputs.Add((_answer, Knowledge.Answer("PGM-KDA-V1.0", q)));
        if (Read(message, _auc) is { } rq && Auc.IsRequest(rq))
        {
            var r = Auc.Make(Auc.Dac, Auc.Response, Session.Of(rq), Auc.IdOf(rq));
            r["Outcome"] = "achieved";
            outputs.Add((_response, r));
        }
        if (Read(message, _domainIn) is { } d && (string?)d["Mode"] == Auc.Request)
        {
            var r = new JsonObject { ["Header"] = "PGM-DOM-V1.0", ["Mode"] = "RS" };
            Session.Of(d).Stamp(r);
            r["SourceAIM"] = "PGM-DAC-V1.0"; r["Subject"] = (string?)d["Subject"] ?? "context";
            r["QueryID"] = NewId("dom"); r["QueryTime"] = Time(); r["RequestID"] = (string?)d["QueryID"] ?? "";
            outputs.Add((_domainOut, r));
        }
        return Done(Give(message, outputs));
    }
}

// PGM-USR-V1.0 - User State Refinement. Passes the User State through (no refinement in this slice) and answers.
public sealed class UsrAim : PgmAim
{
    private readonly string _auc, _stateIn, _query, _historyIn, _stateOut, _response, _answer, _historyOut;
    private JsonObject? _state;

    public UsrAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _auc = In(Auc.Usr); _stateIn = In("MMC-EST-V2.5"); _query = In("PGM-KUS-V1.0"); _historyIn = In("PGM-IHI-V1.0");
        _stateOut = Out("MMC-EST-V2.5"); _response = Out(Auc.Usr); _answer = Out("PGM-KUS-V1.0"); _historyOut = Out("PGM-IHI-V1.0");
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _stateIn) is { } s) _state = s;
        if (Read(message, _query) is { } q && (string?)q["Mode"] == Auc.Request)
        {
            outputs.Add((_answer, Knowledge.Answer("PGM-KUS-V1.0", q)));
            outputs.Add((_stateOut, _state ?? new JsonObject { ["Header"] = "MMC-EST-V2.5", ["EntityStateID"] = NewId("est"), ["Engagement"] = new JsonObject { ["Level"] = "medium", ["Mode"] = "interactive" } }));
        }
        if (Read(message, _auc) is { } rq && Auc.IsRequest(rq))
        {
            var r = Auc.Make(Auc.Usr, Auc.Response, Session.Of(rq), Auc.IdOf(rq));
            r["Outcome"] = "achieved";
            outputs.Add((_response, r));
        }
        return Done(Give(message, outputs));
    }
}

// PGM-PAL-V1.0 - Personality Alignment. Gives the default A-User State (no alignment in this slice).
public sealed class PalAim : PgmAim
{
    private readonly string _auc, _stateIn, _query, _historyIn, _aUserState, _response, _answer, _historyOut;

    public PalAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _auc = In(Auc.Pal); _stateIn = In("MMC-EST-V2.5"); _query = In("PGM-KPA-V1.0"); _historyIn = In("PGM-IHI-V1.0");
        _aUserState = Out("MMC-EST-V2.5"); _response = Out(Auc.Pal); _answer = Out("PGM-KPA-V1.0"); _historyOut = Out("PGM-IHI-V1.0");
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();
        if (Read(message, _query) is { } q && (string?)q["Mode"] == Auc.Request)
        {
            outputs.Add((_answer, Knowledge.Answer("PGM-KPA-V1.0", q)));
            outputs.Add((_aUserState, new JsonObject { ["Header"] = "MMC-EST-V2.5", ["EntityStateID"] = NewId("a-user-state"), ["Engagement"] = new JsonObject { ["Level"] = "medium", ["Mode"] = "interactive" } }));
        }
        if (Read(message, _auc) is { } rq && Auc.IsRequest(rq))
        {
            var r = Auc.Make(Auc.Pal, Auc.Response, Session.Of(rq), Auc.IdOf(rq));
            r["Outcome"] = "achieved";
            outputs.Add((_response, r));
        }
        return Done(Give(message, outputs));
    }
}
