using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Pgm;

// PGM-AUC-V1.0 - A-User Control. Takes the Human Command and drives the partners of the A-User with Requests (Mode RQ), taking their Responses (Mode RS):
//   Human Command RQ -> CXT Request -> CXT Response -> PRC Request -> PRC Response -> BKN Request -> BKN Response (the Final Response goes from BKN to AUF, not here)
//   -> Human Command Response (as soon as it has decided) + AUF Request -> the Portable Avatar and the Process Actions of AUF, which it authorises (or stops).
// An authorised Portable Avatar goes on (to Portable Avatar Demultiplexing, then to the Units); an authorised Process Action is requested of the M-Instance (Process Action Request),
// whose Process Action Response it takes. Nothing else leaves it.
//
// In this slice A-User Control authorises everything that arrives, except when the Human Command is of a kind that stops (pause, terminate): then it stops the Process Actions
// and the Portable Avatar. The partners DAC, USR, PAL and AUS (history) are not asked by A-User Control in this slice; the partners ask each other.
public sealed class AucAim : PgmAim
{
    private readonly string _humanIn, _paResponse, _paIn, _avatarIn, _cxtIn, _prcIn, _bknIn, _dacIn, _usrIn, _palIn, _aufIn, _ausIn;
    private readonly string _action, _avatarOut, _paRequest, _cxtOut, _prcOut, _bknOut, _dacOut, _usrOut, _palOut, _aufOut, _humanOut, _ausOut;

    private JsonObject? _command;
    private bool _authorised = true;

    // What the Controller of the test (or the Module) may ask: how the command ended.
    public string Outcome { get; private set; } = "";

    public AucAim(string instanceId, AimPortReader ports) : base(instanceId, ports)
    {
        _humanIn = In("PGM-HCM-V1.0"); _paResponse = In("MMM-PAC-V2.2"); _paIn = In("MMM-PAT-V2.2"); _avatarIn = In("PAF-PAV-V1.6");
        _cxtIn = In(Auc.Cxt); _prcIn = In(Auc.Prc); _bknIn = In(Auc.Bkn); _dacIn = In(Auc.Dac); _usrIn = In(Auc.Usr); _palIn = In(Auc.Pal); _aufIn = In(Auc.Auf); _ausIn = In(Auc.Aus);
        _action = Out("MMM-ACT-V2.2"); _avatarOut = Out("PAF-PAV-V1.6"); _paRequest = Out("MMM-PAC-V2.2");
        _cxtOut = Out(Auc.Cxt); _prcOut = Out(Auc.Prc); _bknOut = Out(Auc.Bkn); _dacOut = Out(Auc.Dac); _usrOut = Out(Auc.Usr); _palOut = Out(Auc.Pal); _aufOut = Out(Auc.Auf);
        _humanOut = Out("PGM-HCM-V1.0"); _ausOut = Out(Auc.Aus);
    }

    public override Task<Message> ProcessAsync(Message message)
    {
        var outputs = new List<(string, JsonNode?)>();

        if (Read(message, _humanIn) is { } hc && (string?)hc["Mode"] == "RQ")
        {
            _command = hc;
            var stops = (string?)hc["CommandType"] is "pause" or "terminate" or "override";
            _authorised = !stops;
            var session = Session.Of(hc);
            var rq = Auc.Make(Auc.Cxt, Auc.Request, session);
                        outputs.Add((_cxtOut, rq));
        }

        if (Read(message, _cxtIn) is { } cxt && !Auc.IsRequest(cxt))
        {
            if ((string?)cxt["Outcome"] == "not_achieved")
                outputs.Add((_humanOut, HumanResponse("rejected")));
            else
            {
                var rq = Auc.Make(Auc.Prc, Auc.Request, Session.Of(cxt));
                outputs.Add((_prcOut, rq));
            }
        }

        if (Read(message, _prcIn) is { } prc && !Auc.IsRequest(prc))
        {
            var rq = Auc.Make(Auc.Bkn, Auc.Request, Session.Of(prc));
            rq["Actions"] = new JsonObject
            {
                ["Requirement"] = "optional",
                ["Allowed"] = new JsonArray("MU-Actuate", "MM-Animate"),
                ["MaxSteps"] = 3,
                ["Syntax"] = "MMM-TEC-V2.2"
            };
            outputs.Add((_bknOut, rq));
        }

        if (Read(message, _bknIn) is { } bkn && !Auc.IsRequest(bkn))
        {
            // Decided: the Human Command is answered at once; A-User Formation is asked to form what BKN concluded.
            var concluded = (string?)bkn["DeliberationState"] == "concluded";
            outputs.Add((_humanOut, HumanResponse(concluded ? (_authorised ? "accepted" : "rejected") : "escalated")));
            if (concluded && _authorised)
            {
                var rq = Auc.Make(Auc.Auf, Auc.Request, Session.Of(bkn));
                rq["FormationControl"] = new JsonObject { ["Speech"] = "required", ["Face"] = "required", ["Body"] = "optional", ["Avatar"] = "required" };
                outputs.Add((_aufOut, rq));
            }
        }

        // The Portable Avatar: authorised, it goes on.
        if (Read(message, _avatarIn) is { } avatar && _authorised) outputs.Add((_avatarOut, avatar));

        // The Process Actions of A-User Formation: authorised, each is an Action and a Process Action Request to the M-Instance.
        if (Read(message, _paIn) is { } actions && actions["ProcessActions"] is JsonObject pas && _authorised)
            foreach (var (kind, pa) in pas)
            {
                var session = Session.Of(_command);
                outputs.Add((_action, new JsonObject { ["Header"] = "MMM-ACT-V2.2", ["Action"] = kind }));
                var item = (string?)pa?[kind.Replace("-", "") + "PARQ"]?["RQ1"]?["ItemID"] ?? "";
                outputs.Add((_paRequest, new JsonObject
                {
                    ["Header"] = "MMM-PAC-V2.2", ["MInstanceID"] = session.MInstance, ["UEnvironmentID"] = session.UEnvironment,
                    ["ProcessActionID"] = NewId("pac"), ["ProcessActionTime"] = Time(),
                    ["RQ-Complements"] = new JsonArray(new JsonObject { ["ToItem"] = item }),
                    ["PAStatus"] = new JsonArray()
                }));
            }

        // The M-Instance's answer to a Process Action Request.
        if (Read(message, _paResponse) is { } paResponse)
            Outcome = paResponse["PAStatus"] is JsonArray st && st.Any(s => s is JsonObject o && o.ContainsKey("Ack")) ? "done" : "failed";

        if (Read(message, _aufIn) is { } auf && !Auc.IsRequest(auf) && Outcome.Length == 0) Outcome = "formed";

        return Done(Give(message, Merge(outputs)));
    }

    // Several Data for the same Port in one firing: the Port carries the last (a Port is a Channel of one Message at a time in a firing).
    private static IEnumerable<(string, JsonNode?)> Merge(List<(string Key, JsonNode? Value)> outputs) =>
        outputs.GroupBy(o => o.Key).Select(g => (g.Key, g.Last().Value));

    private string? Command(string property) => (string?)_command?[property];

    private JsonObject HumanResponse(string result)
    {
        var rs = new JsonObject { ["Header"] = "PGM-HCM-V1.0", ["Mode"] = "RS" };
        Session.Of(_command).Stamp(rs);
        rs["HumanCommandID"] = Command("HumanCommandID") ?? NewId("hc");
        rs["HumanCommandTime"] = Time();
        rs["RequestID"] = Command("HumanCommandID") ?? "";
        rs["ExecutionResult"] = result;
        return rs;
    }
}
