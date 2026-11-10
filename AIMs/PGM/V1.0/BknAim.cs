using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Pgm;

// PGM-BKN-V1.0 - Basic Knowledge. The one AIM of the A-User that is not a composite: it consults its partners (Prompt Creation, Domain Access, User State Refinement, Personality
// Alignment) in a turn of queries and responses, then asks the language model, and gives its Final Response to A-User Formation ONLY (never to A-User Control, never to the
// M-Instance); to A-User Control it gives the response to its Request, which names the Final Response.
//
// The language model is the AIM's configuration, not its code: the model name is a setting of the implementation.
public sealed class BknAim : PgmAim
{
    public const string PersonaSetting = "Persona.ItemId";

    private readonly string _request, _prompt, _prcIn, _dacIn, _usrIn, _palIn, _final, _response, _prcOut, _dacOut, _usrOut, _palOut;
    private readonly ILanguageModel _model;
    private readonly string _persona;

    private JsonObject? _pendingRequest, _pendingPrompt;
    private bool _consulting;
    private string _dialog = "";
    private readonly Dictionary<string, JsonObject> _answers = new();

    public BknAim(string instanceId, AimPortReader ports, ILanguageModel model, string persona = "a-user-persona") : base(instanceId, ports)
    {
        _model = model; _persona = persona;
        _request = In(Auc.Bkn); _prompt = In("PGM-PCP-V1.0"); _prcIn = In("PGM-KPR-V1.0"); _dacIn = In("PGM-KDA-V1.0"); _usrIn = In("PGM-KUS-V1.0"); _palIn = In("PGM-KPA-V1.0");
        _final = Out("PGM-FRS-V1.0"); _response = Out(Auc.Bkn); _prcOut = Out("PGM-KPR-V1.0"); _dacOut = Out("PGM-KDA-V1.0"); _usrOut = Out("PGM-KUS-V1.0"); _palOut = Out("PGM-KPA-V1.0");
    }

    public override async Task<Message> ProcessAsync(Message message)
    {
        if (Read(message, _request) is { } rq && Auc.IsRequest(rq)) _pendingRequest = rq;
        if (Read(message, _prompt) is { } pcp) _pendingPrompt = pcp;
        foreach (var (key, header) in new[] { (_prcIn, "PGM-KPR-V1.0"), (_dacIn, "PGM-KDA-V1.0"), (_usrIn, "PGM-KUS-V1.0"), (_palIn, "PGM-KPA-V1.0") })
            if (Read(message, key) is { } a && (string?)a["Mode"] == "RS") _answers[header] = a;

        var outputs = new List<(string, JsonNode?)>();

        // Turn 1: the request and the prompt are here - ask the four partners.
        if (!_consulting && _pendingRequest is not null && _pendingPrompt is not null)
        {
            _consulting = true;
            var session = Session.Of(_pendingRequest);
            _dialog = (string?)_pendingPrompt["DialogID"] ?? Auc.IdOf(_pendingRequest);
            var q = Knowledge.Query("PGM-KPR-V1.0", session, _dialog, 1); q["Kind"] = "action_context"; q["Question"] = "What may the A-User do, and how many steps?";
            outputs.Add((_prcOut, q));
            var d = Knowledge.Query("PGM-KDA-V1.0", session, _dialog, 1); d["Kind"] = "feasibility"; d["Question"] = "What can the A-User do in this domain?";
            outputs.Add((_dacOut, d));
            outputs.Add((_usrOut, Knowledge.Query("PGM-KUS-V1.0", session, _dialog, 1)));
            outputs.Add((_palOut, Knowledge.Query("PGM-KPA-V1.0", session, _dialog, 1)));
        }

        // Turn 2: the four answers are here - the model answers.
        if (_consulting && _answers.Count == 4 && _pendingRequest is not null && _pendingPrompt is not null)
        {
            var request = _pendingRequest; var prompt = _pendingPrompt;
            _pendingRequest = null; _pendingPrompt = null; _consulting = false; _answers.Clear();
            outputs.AddRange(await Conclude(request, prompt));
        }
        return Give(message, outputs);
    }

    private async Task<List<(string, JsonNode?)>> Conclude(JsonObject request, JsonObject prompt)
    {
        var session = Session.Of(request);
        var allowed = (request["Actions"]?["Allowed"] as JsonArray)?.Select(x => (string?)x).Where(x => x is not null).Select(x => x!).ToArray();
        if (allowed is not { Length: > 0 }) allowed = ["MU-Actuate", "MM-Animate"];
        var said = (string?)prompt["Prompt"] ?? "";

        string reply; var worldActions = new List<(string Verb, string Target)>(); var personaActions = new List<string>();
        var failure = "";
        try
        {
            var system =
                "You are the A-User, an autonomous assistant of a responsible human in a metaverse building. " +
                "'reply' is what you say aloud to the human: one or two short natural sentences, never a command name. " +
                "If the human asks you to do something to an item of the building (for instance to open doors), add one entry to worldActions: verb 'MU-Actuate', target the item (for instance 'doors'). " +
                "If you want to make a gesture yourself, put its plain name in personaGestures (for instance 'nod' or 'wave'). Otherwise leave the lists empty. " +
                "Example. Human: 'please close the window'. You: {\"reply\":\"Closing the window now.\",\"worldActions\":[{\"verb\":\"MU-Actuate\",\"target\":\"window\"}],\"personaGestures\":[\"nod\"]}. " +
                "Example. Human: 'how are you'. You: {\"reply\":\"I am fine, thank you.\",\"worldActions\":[],\"personaGestures\":[]}.";
            var raw = await _model.AnswerAsync(system, said, Schema(allowed));
            var json = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("the model did not give a JSON object");
            reply = (string?)json["reply"] ?? "";
            foreach (var a in json["worldActions"] as JsonArray ?? [])
                if (a is JsonObject o && (string?)o["verb"] is { } v && allowed.Contains(v) && (string?)o["target"] is { Length: > 0 } t) worldActions.Add((v, t));
            foreach (var g in json["personaGestures"] as JsonArray ?? [])
                if ((string?)g is { Length: > 0 } name) personaActions.Add(name);
        }
        catch (Exception e)
        {
            failure = e.Message;
            reply = "";
        }

        var response = Auc.Make(Auc.Bkn, Auc.Response, session, Auc.IdOf(request));
        if (failure.Length > 0)
        {
            response["Outcome"] = "not_achieved";
            response["Reason"] = new JsonObject { ["Code"] = "partner_unavailable", ["Description"] = failure };
            response["DeliberationState"] = "blocked";
            return [(_response, response)];
        }

        var fr = new JsonObject { ["Header"] = "PGM-FRS-V1.0" };
        session.Stamp(fr);
        fr["FinalResponseID"] = NewId("frs");
        fr["FinalResponseTime"] = Time();
        fr["AUCBKNRequestID"] = Auc.IdOf(request);
        fr["DialogID"] = _dialog;
        fr["Text"] = Text(reply);

        var pas = new JsonObject();
        var n = 0;
        foreach (var (verb, target) in worldActions)
            if (verb == "MU-Actuate" && !pas.ContainsKey(verb))
                pas[verb] = new JsonObject
                {
                    ["Header"] = "MMM-3CP-V2.2", ["MUActuatePAID"] = NewId("pa" + ++n), ["Time"] = Time(),
                    ["MUActuatePARQ"] = new JsonObject { ["RQ1"] = new JsonObject { ["Complement"] = "Nil", ["ItemID"] = target } }
                };
        // What goes with the text is an Action of MMM-TEC V2.2, not a Process Action: when the model wants the Persona to gesture, the Action is MM-Animate.
        if (personaActions.Count > 0)
            fr["Actions"] = new JsonArray(new JsonObject { ["Header"] = "MMM-ACT-V2.2", ["Action"] = "MM-Animate" });
        if (pas.Count > 0)
            fr["ProcessActions"] = new JsonObject { ["Header"] = "MMM-PAT-V2.2", ["MEnvironmentTime"] = Time(), ["ProcessActions"] = pas };

        response["Outcome"] = "achieved";
        response["DeliberationState"] = "concluded";
        response["Conclusion"] = new JsonObject { ["Headline"] = reply, ["FinalResponseID"] = (string)fr["FinalResponseID"]! };
        response["ActionsOutcome"] = new JsonObject { ["Outcome"] = pas.Count + personaActions.Count > 0 ? "produced" : "not_requested", ["StepCount"] = pas.Count + personaActions.Count };
        return [(_final, fr), (_response, response)];
    }

    // What the model must give: constrained by a schema, so that a small model cannot invent fields.
    private static JsonObject Schema(string[] allowed) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("reply", "worldActions", "personaGestures"),
        ["properties"] = new JsonObject
        {
            ["reply"] = new JsonObject { ["type"] = "string" },
            ["worldActions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("verb", "target"),
                    ["properties"] = new JsonObject
                    {
                        ["verb"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray()) },
                        ["target"] = new JsonObject { ["type"] = "string" }
                    }
                }
            },
            ["personaGestures"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
        }
    };
}
