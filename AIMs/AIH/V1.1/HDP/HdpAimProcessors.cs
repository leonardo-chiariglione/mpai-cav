using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Aih.Hdp;

// AIH-HDP-V1.1 - AIH Data Processing: "performs one or more processing functions on AIH Data". The AIM of both ends: which end it serves is the Role of the
// AIF instance it runs in (the setting "Role" of its AIM Instance: FrontEnd or BackEnd), since the Front End and the Back End are different AIF instances.
//
// HDP is driven by whatever reaches it - the Ports are optional and it handles one request at a time if it can be executed; what it must wait for (the
// Blockchain's answer, the Licence Confirm's, the Anomaly and Risk Alerting's) it waits for between messages, in its own memory.

// ---- THE PORTS, read by Data Type and Port Number from the AIM Metadata -------------------------------------------------------------------------------------
internal sealed class HdpPorts
{
    public string RegIn = "", RegBackIn = "", AhpRequestIn = "", AhpResponseIn = "", AhdIn = "", ArdIn = "", LcfIn = "", BclIn = "", FdlIn = "";
    // The AIH Data resulting from the processing of an AIH Data by an AIM that wraps a Neural Network Model (Port 2, 2026-11-13): kept, not sent on.
    public string AhdProcessedIn = "";
    // The Neural Network Model the AIM that wraps it holds (Front End): kept, and returned to the Back End in the answer to a Federated Learn request.
    public string MloIn = "";
    public string RegOut = "", RegBackOut = "", AhpStoreOut = "", AhpResponseOut = "", AhdOut = "", LcfOut = "", BclOut = "", FdlOut = "";
    // The De-ID and Anonym and the Audit requests reach AIH Data Processing first (1) and their responses come back through it (2) (2026-11-13). In the Back End the AIH Data
    // goes to De-Identification and Anonymisation (2) and to Auditing (3) on Channels; in the Front End the requests go on to the Back End (1) and its answers come back (2) -
    // a User Agent carries nothing between the AIMs of its Module, nor to another User Agent.
    public string DiaReqIn = "", DiaRespIn = "", AdtReqIn = "", AdtRespIn = "";
    public string DiaReqOut = "", DiaRespOut = "", AdtReqOut = "", AdtRespOut = "", AhdDeIdOut = "", AhdAuditOut = "";

    public HdpPorts(AimPortReader p)
    {
        RegIn = p.InputOrDefault(AihJson.Register, 1, "");           RegBackIn = p.InputOrDefault(AihJson.Register, 2, "");
        AhpResponseIn = p.InputOrDefault(AihJson.Process, 1, "");   AhpRequestIn = p.InputOrDefault(AihJson.Process, 2, "");
        AhdIn = p.InputOrDefault(AihJson.AihData, 1, "");          AhdProcessedIn = p.InputOrDefault(AihJson.AihData, 2, "");
        MloIn = p.InputOrDefault("AIP-MLO-V1.0", "");              ArdIn = p.InputOrDefault(AihJson.AraData, "");
        LcfIn = p.InputOrDefault(AihJson.LicenceConfirm, "");       BclIn = p.InputOrDefault(AihJson.Blockchain, "");     FdlIn = p.InputOrDefault(AihJson.FederatedLearn, "");
        RegOut = p.OutputOrDefault(AihJson.Register, 1, "");        RegBackOut = p.OutputOrDefault(AihJson.Register, 2, "");
        AhpStoreOut = p.OutputOrDefault(AihJson.Process, 1, "");    AhpResponseOut = p.OutputOrDefault(AihJson.Process, 2, "");
        AhdOut = p.OutputOrDefault(AihJson.AihData, 1, "");         LcfOut = p.OutputOrDefault(AihJson.LicenceConfirm, "");
        AhdDeIdOut = p.OutputOrDefault(AihJson.AihData, 2, "");     AhdAuditOut = p.OutputOrDefault(AihJson.AihData, 3, "");
        DiaReqIn = p.InputOrDefault(AihJson.DeId, 1, "");           DiaRespIn = p.InputOrDefault(AihJson.DeId, 2, "");
        AdtReqIn = p.InputOrDefault(AihJson.Audit, 1, "");          AdtRespIn = p.InputOrDefault(AihJson.Audit, 2, "");
        DiaReqOut = p.OutputOrDefault(AihJson.DeId, 1, "");         DiaRespOut = p.OutputOrDefault(AihJson.DeId, 2, "");
        AdtReqOut = p.OutputOrDefault(AihJson.Audit, 1, "");        AdtRespOut = p.OutputOrDefault(AihJson.Audit, 2, "");
        BclOut = p.OutputOrDefault(AihJson.Blockchain, "");         FdlOut = p.OutputOrDefault(AihJson.FederatedLearn, "");
    }
}

public abstract class HdpAimProcessor(string instanceId, AimPortReader reader) : IAimProcessor
{
    internal readonly HdpPorts P = new(reader);
    public string InstanceId { get; } = instanceId;

    protected static string? Take(Message m, string key) =>
        key.Length > 0 && m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            var outputs = new Dictionary<string, string>();
            Handle(message, outputs);
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "AIHDataProcessing", DataType = AihJson.AihData,
                Payload = outputs.Values.FirstOrDefault() ?? "", Ports = outputs
            });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-HDP-V1.1: " + ex.Message)); }
    }

    protected abstract void Handle(Message message, Dictionary<string, string> outputs);

    protected static void Put(Dictionary<string, string> outputs, string key, JsonNode json)
    {
        if (key.Length > 0) outputs[key] = json.ToJsonString();
    }

    protected static JsonObject WithoutAra(JsonObject data)
    {
        var copy = (JsonObject)data.DeepClone();
        (copy["AIHData"] as JsonObject)?.Remove("ARAData");
        return copy;
    }

    protected static string Risk(JsonObject ard) => ard["ARAData"]?["Risk"]?.GetValue<string>() ?? "";

    // A De-ID and Anonym request, or an Audit request, refused: the answer carries the identifier of the request (by it the Controllers give the answer to whoever asked).
    protected static JsonObject DeIdRefused(string user, string id, string? requestId) => new()
    {
        ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = requestId ?? AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = user,
        ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = id, ["ProcessingState"] = false } })
    };

    protected static JsonObject AuditRefused(JsonObject request) => new()
    {
        ["Header"] = AihJson.Audit, ["UserID"] = request["UserID"]?.GetValue<string>() ?? "", ["AuditID"] = request["AuditID"]?.GetValue<string>() ?? AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
        ["Audit"] = new JsonObject { ["Response"] = new JsonObject { ["ProcessingState"] = false, ["AIHDataIDs"] = new JsonArray() } }
    };
}

// ---- THE FRONT END's HDP: the End User's gateway ---------------------------------------------------------------------------------------------------------
// Registration and requests go on to the Back End and its answers come back; new AIH Data is kept in the Secure Data Vault - the Module's storage, used through its API, there
// is no Storage AIM - and goes to Anomaly and Risk Alerting, and what that finds is added to it
// a risk (the alert is the ARA Data: Anomaly and Risk Alerting gives it at the Front End's boundary). A request to retrieve AIH Data of the End User is answered here, from the Vault.
public sealed class FrontEndHdpAimProcessor(string instanceId, AimPortReader reader, IRuledStorage storage) : HdpAimProcessor(instanceId, reader)
{
    private readonly Dictionary<string, JsonObject> awaitingAra = new();

    // WHAT THIS FRONT END DOES DEPENDS ON THE USER PROFILE IT INGESTED, with the registration it sends to the Back End. For an End User it holds the End User's AIH Data in the Vault, trains
    // on it and has the Back End keep and process it - on that AIH Data only. For a Third-Party User (a Third-Party User Profile, or a User Profile of an individual who is a member of an
    // organisation) it makes and keeps no AIH Data, trains nothing, and asks for AIH Data to be processed, as its licence prescribes - nothing else.
    private bool thirdParty;

    // What the Front End knew before it was started again: the profile it registered with (Private Storage of the Module).
    private bool restored;
    private void Restore()
    {
        if (restored) return;
        restored = true;
        foreach (var r in AihStore.Registrations(storage)) if (r["ThirdParty"] is JsonValue v) thirdParty = v.GetValue<bool>();
    }

    // The Neural Network Model that the AIM wrapping it holds, as it last gave it (null where the Front End has no such AIM, or it has processed nothing yet).
    private JsonObject? trained;

    // A Federated Learn request: the Front End answers with the Neural Network Model that the AIM wrapping it holds - trained on the AIH Data the AIM processed - and the AIH Data
    // it declares it used (the last it made). The training is the AIM's, not this one's. A Front End whose AIM has no model to give does not answer.
    private void Learn(JsonObject fdl, Dictionary<string, string> outputs)
    {
        var request = (fdl["FederatedLearnData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null)
                      ?? throw new FormatException("the Federated Learn holds no Request.");
        if (trained is null) throw new InvalidOperationException("this Front End has no Neural Network Model to return (it has no AIM that wraps one, or the AIM has processed no AIH Data).");
        // The End User's AIH Data, not the AIH Data an AIM made from it (ParentAIHDataID).
        var held = AihStore.All(storage).Where(d => d["AIHData"]?["ParentAIHDataID"] is null).ToList();
        if (held.Count == 0) throw new InvalidOperationException("this Front End holds no AIH Data.");
        var model = (JsonObject)trained.DeepClone();
        model["MLModelObjectID"] = $"{request["PreLearnNNModelID"]?.GetValue<string>() ?? "model"}-{request["HFEID"]?.GetValue<string>() ?? "hfe"}";
        Put(outputs, P.FdlOut, new JsonObject
        {
            ["Header"] = AihJson.FederatedLearn, ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(),
            ["FederatedLearnData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = held[^1]["AIHDataID"]!.GetValue<string>(), ["PostLearnNNModel"] = model } })
        });
    }
    private static int Rank(string risk) => risk switch { "Red" => 4, "Orange" => 3, "Yellow" => 2, "Green" => 1, _ => 0 };

    protected override void Handle(Message m, Dictionary<string, string> outputs)
    {
        Restore();
        if (Take(m, P.MloIn) is { } model) trained = AihJson.Parse(model, "ML Model Object");
        if (Take(m, P.FdlIn) is { } learn && !thirdParty) Learn(AihJson.Parse(learn, "Federated Learn"), outputs);
        if (Take(m, P.RegIn) is { } register)
        {
            thirdParty = Profiles.IsThirdParty(AihJson.RegisterRequestOf(AihJson.Parse(register, "Register")));
            AihStore.PutRegistration(storage, "profile", new JsonObject { ["ThirdParty"] = thirdParty });      // kept: a Front End started again does what its profile allows
            outputs[P.RegOut] = register;                                                           // to the Back End
        }
        if (Take(m, P.RegBackIn) is { } registered) outputs[P.RegBackOut] = registered;           // to the End User
        if (Take(m, P.AhpRequestIn) is { } request)
        {
            var process = AihJson.Parse(request, "AIH Data Process");
            var asked = AihJson.RequestOf(process)?["ProcessingType"]?.GetValue<string>() ?? "";
            if (thirdParty && asked != "AIHDataProcessing")                                                                                              // not what a Third-Party User may do
                Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(process["UserID"]?.GetValue<string>() ?? "", Array.Empty<string>(), false));
            else if (AihStore.IsRetrieval(process, out var user, out var id)) Put(outputs, P.AhpResponseOut, AihStore.Answer(storage, user, id, ""));   // from the Vault
            else outputs[P.AhpStoreOut] = request;                                                                                                      // to the Back End
        }
        if (Take(m, P.AhpResponseIn) is { } response) outputs[P.AhpResponseOut] = response;       // to the End User

        // De-identification, anonymisation and audit of the End User's AIH Data are done by the Back End: the request goes to it (De-ID and Anonym 1, Audit 1) and its answer comes
        // back (2), on External Ports, between the Controllers. A Third-Party User may ask for processing only: its request is answered here.
        if (Take(m, P.DiaReqIn) is { } deId)
        {
            if (thirdParty)
            {
                var deIdRequest = AihJson.Parse(deId, "De-ID and Anonym");
                var deIdAsked = (deIdRequest["DeIDAndAnonymData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null);
                Put(outputs, P.DiaRespOut, DeIdRefused(deIdRequest["EndUserID"]?.GetValue<string>() ?? "", deIdAsked?["AIHDataID"]?.GetValue<string>() ?? "", deIdRequest["DeIDAndAnonymID"]?.GetValue<string>()));
            }
            else outputs[P.DiaReqOut] = deId;                                                       // to the Back End
        }
        if (Take(m, P.DiaRespIn) is { } deIdDone) outputs[P.DiaRespOut] = deIdDone;               // to the End User
        if (Take(m, P.AdtReqIn) is { } audit)
        {
            if (thirdParty) Put(outputs, P.AdtRespOut, AuditRefused(AihJson.Parse(audit, "Audit")));
            else outputs[P.AdtReqOut] = audit;                                                      // to the Back End
        }
        if (Take(m, P.AdtRespIn) is { } audited) outputs[P.AdtRespOut] = audited;                 // to the End User

        if (Take(m, P.AhdIn) is { } dataJson && !thirdParty)
        {
            var data = AihJson.Parse(dataJson, "AIH Data");
            if (data["AIHData"]?["ARAData"] is null && data["AIHDataID"]?.GetValue<string>() is { } id) awaitingAra[id] = data;
            AihStore.Put(storage, data);                                                            // kept in the Vault
            Put(outputs, P.AhdOut, data);                                                           // to Anomaly and Risk Alerting
        }

        // The AIH Data an AIM wrapping a Neural Network Model made from an AIH Data (ParentAIHDataID names it): kept in the Vault, given to no AIM again.
        if (Take(m, P.AhdProcessedIn) is { } processedJson && !thirdParty)
            AihStore.Put(storage, AihJson.Parse(processedJson, "AIH Data"));

        if (Take(m, P.ArdIn) is { } ardJson)
        {
            var ard = AihJson.Parse(ardJson, "ARA Data");
            var id = ard["ARAData"]?["AIHDataID"]?.GetValue<string>() ?? "";
            // The first ARA Data for an AIH Data is taken; another that comes later is taken only if it finds the risk graver.
            var stored = AihStore.Get(storage, id);
            var data = awaitingAra.Remove(id, out var waiting) ? waiting
                     : stored is not null && Rank(Risk(ard)) > Rank(stored["AIHData"]?["ARAData"] is JsonObject before ? Risk(before) : "") ? stored : null;
            if (data is not null)
            {
                ((JsonObject)data["AIHData"]!)["ARAData"] = ard;
                AihStore.Put(storage, data);                                                        // the AIH Data with what was found, kept
                // The alert is not a response of this AIM: it is the ARA Data, which Anomaly and Risk Alerting gives at the boundary of the Front End too.
            }
        }
    }
}

// ---- THE BACK END's HDP -------------------------------------------------------------------------------------------------------------------------------
// It registers End Users and Third-Party Users and gives them Tokens; it makes the Smart Contract of AIH Data that is uploaded and keeps the AIH Data in the Secure Storage - the
// Module's storage, through its API: there is no Storage AIM; it gives it back to its End User; it processes AIH Data for those whose Tokens allow it and whose licence confirms it.
// THE OTHER AIMs OF THE BACK END ARE SERVED ON CHANNELS (2026-11-13; before, the User Agent carried the AIH Data between them, against AIF V3.0 COM-1 and UA-2): a request to
// de-identify or anonymise, or to audit, comes here first, and the Tokens are checked here, where the users are registered; a request they grant goes on to De-Identification
// and Anonymisation with the AIH Data it names, or to Auditing; one they do not grant is answered here. The copy De-Identification makes comes back here to be kept, and its
// response goes out after it. Every AIH Data kept here is given to Auditing as it is kept.
public sealed class BackEndHdpAimProcessor(string instanceId, AimPortReader reader, IRuledStorage storage, string backEndId = "") : HdpAimProcessor(instanceId, reader)
{
    // What each class of user may ask the Back End for.
    private static readonly string[] EndUserServices = AihJson.Services;
    private static readonly string[] ThirdPartyServices = ["AIHDataProcessing"];

    private readonly Dictionary<string, string[]> registered = new();
    private readonly HashSet<string> endUsers = new();                       // registered with the profile of an End User: it works on its own AIH Data only
    private readonly Dictionary<string, Upload> uploads = new();
    private readonly Dictionary<string, Processing> processing = new();
    private readonly Dictionary<string, string> deIdHeld = new();                 // a De-ID and Anonym response whose copy is not kept yet, by the identifier of the copy

    private sealed record Upload(string Requester, JsonObject Data, string? RequestId);
    private sealed class Processing(string requester, string confirmId, string? requestId)
    {
        public string Requester { get; } = requester;
        public string? RequestId { get; } = requestId;
        public string ConfirmId { get; } = confirmId;
        public bool? Licensed { get; set; }
        public JsonObject? Data { get; set; }
        public bool SentToAra { get; set; }
    }

    // Who registered and what was granted, as kept before the Module was started again (Private Storage of the Module).
    private bool restored;
    private void Restore()
    {
        if (restored) return;
        restored = true;
        foreach (var r in AihStore.Registrations(storage))
            if (r["UserID"]?.GetValue<string>() is { } user && r["Services"] is JsonArray services)
            {
                registered[user] = services.Select(x => x!.GetValue<string>()).ToArray();
                if (r["EndUser"]?.GetValue<bool>() == true) endUsers.Add(user); else endUsers.Remove(user);
            }
    }

    protected override void Handle(Message m, Dictionary<string, string> outputs)
    {
        Restore();
        if (Take(m, P.RegIn) is { } register) Register(AihJson.Parse(register, "Register"), outputs);
        if (Take(m, P.BclIn) is { } bcl) Contract(AihJson.Parse(bcl, "Blockchain Licence"), outputs);
        if (Take(m, P.AhpRequestIn) is { } request) Request(AihJson.Parse(request, "AIH Data Process"), outputs);
        if (Take(m, P.LcfIn) is { } lcf) Licence(AihJson.Parse(lcf, "Licence Confirm"), outputs);
        if (Take(m, P.AhdIn) is { } data) Data(AihJson.Parse(data, "AIH Data"), outputs);
        if (Take(m, P.ArdIn) is { } ard) Found(AihJson.Parse(ard, "ARA Data"), outputs);
        if (Take(m, P.DiaReqIn) is { } deId) DeIdRequest(AihJson.Parse(deId, "De-ID and Anonym"), outputs);
        if (Take(m, P.DiaRespIn) is { } deIdDone) DeIdResponse(deIdDone, outputs);
        if (Take(m, P.AdtReqIn) is { } audit) AuditRequest(AihJson.Parse(audit, "Audit"), outputs);
        if (Take(m, P.AdtRespIn) is { } audited) Put(outputs, P.AdtRespOut, AihJson.Parse(audited, "Audit"));      // Auditing's answer goes out
    }

    // ---- De-Identification and Anonymisation, and Auditing: served here, on Channels ----

    // Whether the Tokens presented grant the service: the Back End itself (its operator: it is not a user, and its Tokens name it), or a registered user who was granted the service.
    private bool Grants(string user, JsonObject? tokens, string service) =>
        tokens is not null && AihJson.ServicesOf(tokens).Contains(service) &&
        ((backEndId.Length > 0 && tokens["UserID"]?.GetValue<string>() == backEndId) ||
         (tokens["UserID"]?.GetValue<string>() == user && registered.TryGetValue(user, out var granted) && granted.Contains(service)));

    private void DeIdRequest(JsonObject request, Dictionary<string, string> outputs)
    {
        var user = request["EndUserID"]?.GetValue<string>() ?? "";
        var asked = (request["DeIDAndAnonymData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null)
                    ?? throw new FormatException("the De-ID and Anonym request holds no Request.");
        var id = asked["AIHDataID"]?.GetValue<string>() ?? "";
        var data = id.Length > 0 ? AihStore.Get(storage, id) : null;
        var tokens = asked["Tokens"] as JsonObject;
        // A user has its own AIH Data de-identified or anonymised, not somebody else's; the Back End any it keeps.
        var mine = data is not null && (data["UserID"]?.GetValue<string>() == user || (backEndId.Length > 0 && tokens?["UserID"]?.GetValue<string>() == backEndId));
        if (data is null || !mine || !Grants(user, tokens, "DeIDAndAnonym"))
        {
            Put(outputs, P.DiaRespOut, DeIdRefused(user, id, request["DeIDAndAnonymID"]?.GetValue<string>()));
            return;
        }
        Put(outputs, P.DiaReqOut, request);                                         // to De-Identification and Anonymisation
        Put(outputs, P.AhdDeIdOut, data);                                           // with the AIH Data it names
    }

    // The response of De-Identification and Anonymisation: it goes out once the copy it names is kept here.
    private void DeIdResponse(string json, Dictionary<string, string> outputs)
    {
        var response = AihJson.Parse(json, "De-ID and Anonym")["DeIDAndAnonymData"]?[0]?["Response"];
        var copy = response?["NewAIHDataID"]?.GetValue<string>();
        if (response?["ProcessingState"]?.GetValue<bool>() == true && copy is not null && AihStore.Get(storage, copy) is null) { deIdHeld[copy] = json; return; }
        outputs[P.DiaRespOut] = json;
    }

    private void AuditRequest(JsonObject request, Dictionary<string, string> outputs)
    {
        var user = request["UserID"]?.GetValue<string>() ?? "";
        if (Grants(user, request["Audit"]?["Request"]?["Tokens"] as JsonObject, "Auditing"))
        {
            Put(outputs, P.AdtReqOut, request);                                     // to Auditing, which has seen the AIH Data kept here
            return;
        }
        Put(outputs, P.AdtRespOut, AuditRefused(request));
    }

    // ---- registration ----
    private void Register(JsonObject register, Dictionary<string, string> outputs)
    {
        var id = register["RegisterID"]?.GetValue<string>() ?? AihJson.NewId("reg");
        var request = AihJson.RegisterRequestOf(register);
        // THE USER PROFILE DECIDES WHAT THE USER MAY ASK FOR: an individual who is a member of an organisation, like a Third-Party User Profile, is a Third-Party User; any other individual is an End User.
        var (user, allowed) =
            Profiles.IsThirdParty(request) && request?["UserProfile"] is JsonObject member ? (member["UserID"]?.GetValue<string>(), ThirdPartyServices) :
            request?["UserProfile"] is JsonObject end ? (end["UserID"]?.GetValue<string>(), EndUserServices) :
            request?["ThirdPartyUserProfile"] is JsonObject third ? (third["ThirdPartyUserID"]?.GetValue<string>(), ThirdPartyServices) : (null, []);
        var asked = (request?["RequestedServices"] as JsonArray)?.OfType<JsonObject>().Select(s => s["Service"]?.GetValue<string>() ?? "").ToArray() ?? [];
        var granted = asked.Where(allowed.Contains).Distinct().ToArray();
        if (string.IsNullOrEmpty(user) || granted.Length == 0)
        {
            Put(outputs, P.RegBackOut, AihJson.RegisterResponse(id, false, null));
            return;
        }
        registered[user] = granted;
        if (allowed == EndUserServices) endUsers.Add(user); else endUsers.Remove(user);
        AihStore.PutRegistration(storage, user, new JsonObject
        {
            ["UserID"] = user, ["Services"] = new JsonArray(granted.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()), ["EndUser"] = allowed == EndUserServices
        });
        Put(outputs, P.RegBackOut, AihJson.RegisterResponse(id, true, AihJson.TokensFor(user, granted)));
    }

    // ---- requests ----
    private void Request(JsonObject process, Dictionary<string, string> outputs)
    {
        var requester = process["UserID"]?.GetValue<string>() ?? "";
        var requestId = process["AIHDataProcessID"]?.GetValue<string>();
        var request = AihJson.RequestOf(process) ?? throw new FormatException("the request holds no Request.");
        var type = request["ProcessingType"]?.GetValue<string>() ?? "";
        // THE BACK END'S OWN PROCESSING (its operator starts it): the Back End is not a user and has no Tokens. It names one AIH Data and is subject to the licence terms, which the
        // Blockchain confirms when asked (the requester is the Back End). (Its audit is an Audit request: AuditRequest.)
        if (backEndId.Length > 0 && requester == backEndId && type == "AIHDataProcessing" && (request["AIHDataOrAIHDataID"] as JsonArray) is { Count: 1 } one && one[0] is JsonValue backEndId1)
        {
            var wanted1 = backEndId1.GetValue<string>();
            var ask = AihJson.LicenceConfirmRequest("", wanted1, type, requester);
            processing[wanted1] = new Processing(requester, ask["LicenceConfirmID"]!.GetValue<string>(), requestId);
            Put(outputs, P.LcfOut, ask);
            return;
        }
        var presented = AihJson.ServicesOf(request["Tokens"] as JsonObject);
        var items = (request["AIHDataOrAIHDataID"] as JsonArray)?.ToList() ?? [];

        // THE TOKENS: the user is registered, was granted this service, and presents it. A request names one AIH Data.
        if (!registered.TryGetValue(requester, out var granted) || !granted.Contains(type) || !presented.Contains(type) || items.Count != 1)
        {
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, Array.Empty<string>(), false, requestId));
            return;
        }

        switch (type)
        {
            case "Store" when items[0] is JsonObject data:
            {
                var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
                var licence = data["AIHData"]?["ModelLicence"] ?? throw new FormatException("the AIH Data has no Model Licence.");
                if (licence is JsonValue) throw new FormatException("the Model Licence is an identifier: the Back End needs the licence itself.");
                uploads[id] = new Upload(requester, data, requestId);
                Put(outputs, P.BclOut, AihJson.BlockchainRequest(data["UserID"]?.GetValue<string>() ?? requester, id, licence));
                break;
            }
            case "Store" when items[0] is JsonValue retrieveId:                                            // retrieval: the AIH Data of this End User
                Put(outputs, P.AhpResponseOut, AihStore.Answer(storage, requester, retrieveId.GetValue<string>(), backEndId, requestId));
                break;
            case "AIHDataProcessing" when items[0] is JsonValue idNode:
            {
                var id = idNode.GetValue<string>();
                // An End User works on its own AIH Data only; a Third-Party User on that of anybody whose licence allows it, which is checked next.
                if (endUsers.Contains(requester) && AihStore.Get(storage, id)?["UserID"]?.GetValue<string>() != requester)
                {
                    Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, new[] { id }, false, requestId));
                    break;
                }
                var confirm = AihJson.LicenceConfirmRequest("", id, type, requester);
                processing[id] = new Processing(requester, confirm["LicenceConfirmID"]!.GetValue<string>(), requestId);
                Put(outputs, P.LcfOut, confirm);
                break;
            }
            default:
                Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, Array.Empty<string>(), false, requestId));        // the others are of other AIMs
                break;
        }
    }

    // ---- the Smart Contract was made (or refused) ----
    private void Contract(JsonObject bcl, Dictionary<string, string> outputs)
    {
        var response = (bcl["BlockchainLicenceData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"] as JsonObject).FirstOrDefault(r => r is not null);
        var id = response?["AIHDataID"]?.GetValue<string>() ?? bcl["AIHDataID"]?.GetValue<string>() ?? "";
        if (!uploads.Remove(id, out var upload)) return;
        var approved = response?["LicenceStatus"]?.GetValue<bool>() == true;
        if (!approved)
        {
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(upload.Requester, new[] { id }, false, upload.RequestId));
            return;
        }
        // The Smart Contract's identifier goes into the AIH Data, which is kept in the Secure Storage.
        ((JsonObject)upload.Data["AIHData"]!)["SmartContractID"] = response!["BlockchainLicenceID"]!.GetValue<string>();
        AihStore.Put(storage, upload.Data);
        Put(outputs, P.AhdAuditOut, upload.Data);                                   // Auditing sees what is kept
        Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(upload.Requester, new JsonArray(upload.Data.DeepClone()), true, upload.RequestId));
    }

    // ---- the licence was confirmed (or not) ----
    private void Licence(JsonObject lcf, Dictionary<string, string> outputs)
    {
        var confirmId = lcf["LicenceConfirmID"]?.GetValue<string>() ?? "";
        var status = (lcf["LicenceConfirmData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"]?["Status"]?.GetValue<bool>()).FirstOrDefault(s => s is not null);
        var entry = processing.FirstOrDefault(p => p.Value.ConfirmId == confirmId);
        if (entry.Value is null) return;
        if (status != true)
        {
            processing.Remove(entry.Key);
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Value.Requester, new[] { entry.Key }, false, entry.Value.RequestId));
            return;
        }
        entry.Value.Licensed = true;
        // The AIH Data is fetched from the Secure Storage; one that is not there is not processed.
        entry.Value.Data = AihStore.Get(storage, entry.Key);
        if (entry.Value.Data is null)
        {
            processing.Remove(entry.Key);
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Value.Requester, new[] { entry.Key }, false, entry.Value.RequestId));
            return;
        }
        Run(entry.Key, entry.Value, outputs);
    }

    // ---- AIH Data given to be kept (the copy De-Identification and Anonymisation made): kept, given to Auditing, and the response of the request that made it goes out ----
    private void Data(JsonObject data, Dictionary<string, string> outputs)
    {
        AihStore.Put(storage, data);
        Put(outputs, P.AhdAuditOut, data);
        if (deIdHeld.Remove(data["AIHDataID"]!.GetValue<string>(), out var response)) outputs[P.DiaRespOut] = response;
    }

    // Licensed and in hand: it is checked by Anomaly and Risk Alerting, which is the processing this AIM asks for.
    private void Run(string id, Processing entry, Dictionary<string, string> outputs)
    {
        if (entry.Licensed != true || entry.Data is null || entry.SentToAra) return;
        entry.SentToAra = true;
        Put(outputs, P.AhdOut, WithoutAra(entry.Data));
    }

    // ---- Anomaly and Risk Alerting found ----
    private void Found(JsonObject ard, Dictionary<string, string> outputs)
    {
        var id = ard["ARAData"]?["AIHDataID"]?.GetValue<string>() ?? "";
        if (!processing.Remove(id, out var entry) || entry.Data is null) return;
        ((JsonObject)entry.Data["AIHData"]!)["ARAData"] = ard;
        Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Requester, new JsonArray(entry.Data.DeepClone()), true, entry.RequestId));
    }
}
