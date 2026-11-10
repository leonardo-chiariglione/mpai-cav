using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih.Chain;
using Mpai.Aih.Nnm;

namespace Mpai.Aih.Hsp;

// WHAT THE HEALTH BACK END OFFERS ITS CLIENTS - the Front Ends and the Third-Party Users (the Front Ends never speak to each other). Each call is one request of
// one of the Data Types of the Back End's boundary and gives its answer; the Data Types are the JSON instances of schemas/AIH1/V1.1.
public interface IBackEnd
{
    // Registration and the AIH Data Process requests (an upload, a retrieval, a processing) are the Front End's: they reach the Back End Module on its External Ports, between the Controllers
    // (the Health Back End's User Agent serves neither; a Third-Party User asks through a Health Front End too). The Register and Process calls of a door are for the trust protocol of the
    // User Agents' exchanges, which tests use; the Back End's User Agent refuses them.
    string Register(string registerRequest);
    string Process(string aihDataProcess);
    string DeIdentify(string deIdAndAnonym);          // AIH-DIA
    string Audit(string audit);                       // AIH-ADT
}

// THE USER AGENT OF THE HEALTH BACK END. It runs HBE.orch, which binds a device to every Port of the Module's boundary, and it does what the Back End's operator does with them: it carries to
// the Blockchain, as the Module asks and all the time, what the Module asks of it and gives the Module the answer; it asks AIH Data Processing - which keeps the AIH Data in the Secure
// Storage through the storage API, there is no Storage AIM, and knows whose each AIH Data and each copy is - for the AIH Data that De-Identification and Auditing work on and hands it to them,
// and gives AIH Data Processing the copy De-Identification made to keep. The Front Ends' registrations and requests, and the answers to them, do not pass through it: they are between the
// Controllers.
public sealed class HbeUserAgent : IBackEnd, IDisposable
{
    // The Health Back End Module, and the policy of its service: the Back End is the Trust Authority of its Front Ends, which exchange with it and with no other (a Front End
    // never speaks to another), and its answers go to the client they answer.
    public const string Module = "1AIH-HBE-V1.1-I01";
    public const string ServicePolicy = """{ "Roles": ["BackEnd", "FrontEnd"], "DefaultRole": "FrontEnd", "AuthorityRole": "BackEnd", "Exchange": [["BackEnd", "FrontEnd"]], "Directed": true, "Correlate": ["RegisterID", "AIHDataProcessID", "FederatedLearnID", "DeIDAndAnonymID", "AuditID"], "Address": ["HFEID"] }""";

    private readonly IControllerApi api;
    private readonly ModuleSession s;
    private readonly IBlockchain chain;
    private readonly string me;
    private readonly object gate = new();

    private readonly ModelAimPublisher? models;
    private readonly string? frontEndL3;
    private readonly JsonObject? modelLicence;      // the AIM Licence the Back End offers (AIF-LIC-V3.0 as an offer) for the AIM that wraps each model

    // The AIM that wraps the latest model the rounds of federated learning gave, in the MPAI Store; the next round's Requests name it (PreLearnAIMID).
    public string? ModelAimId { get; private set; }
    // The version of the Health Front End the Back End published with that AIM.
    public string? FrontEndVersion { get; private set; }

    // models: the MPAI Store, to which the Back End uploads the AIM that wraps each model it gets; without it the model is not uploaded. frontEndL3: the L3 of the Health Front End as the Back End has it, from which it publishes the new version that has the AIM for a Front End to take (the Controller of a Front End follows the Store, or not).
    public HbeUserAgent(IControllerApi api, IBlockchain chain, string workflowFile, string storageLocation, string backEndId = "hbe-1", ModelAimPublisher? models = null, string? frontEndL3 = null, JsonObject? modelLicence = null)
    {
        this.api = api; this.chain = chain; me = backEndId; this.models = models; this.frontEndL3 = frontEndL3; this.modelLicence = modelLicence;
        s = new ModuleSession(api, workflowFile, storageLocation, null,
            ("RegisterIn", "AIH-REG-V1.1"), ("RequestIn", "AIH-AHP-V1.1"), ("DeIdIn", "AIH-DIA-V1.1"), ("AuditIn", "AIH-ADT-V1.1"),
            ("AhdAuditIn", "AIH-AHD-V1.1"), ("AhdDeIdIn", "AIH-AHD-V1.1"), ("AhdProcIn", "AIH-AHD-V1.1"),
            ("LearnAnswerIn", "AIH-FDL-V1.1"), ("ContractIn", "AIH-BCL-V1.1"), ("ConfirmIn", "AIH-LCF-V1.1"), ("AuditConfirmIn", "AIH-LCF-V1.1"),
            ("RegisterOut", ""), ("ResponseOut", ""), ("AraOut", ""), ("DeIdOut", ""), ("AhdDeIdOut", ""), ("AuditOut", ""), ("ModelOut", ""),
            ("ContractOut", ""), ("ConfirmOut", ""), ("AuditConfirmOut", ""));
        s.Start();
        // What the Module asks of the Blockchain it asks when it needs to, not when this User Agent expects it: each is served as it comes.
        s.Serve("ContractOut", asked => s.Send("ContractIn", Contract(asked)));
        s.Serve("ConfirmOut", asked => { Confirm(asked, out var answer); s.Send("ConfirmIn", answer); });
        s.Serve("AuditConfirmOut", asked => { Confirm(asked, out var answer); s.Send("AuditConfirmIn", answer); });
    }

    public void Dispose() => s.Dispose();

    // What went wrong in what the Module asked of the Blockchain.
    public IReadOnlyCollection<string> Errors => s.Errors;

    // THE BACK END'S SERVICE: this User Agent asks its Controller to be the Trust Authority of the External links of the Module (MPAI_AIFU_External_Authority). The Controller admits the Front
    // Ends that ask to join, and exchanges with them what the Module's External Ports give (the Federated Learn requests and answers), signed. The anchor is what the User Agent publishes with
    // the service, and the rendezvous is where a Front End presents itself.
    public AifError StartService(string policy = ServicePolicy)
    {
        var error = api.ExternalAuthority(Module, policy);
        Anchor = api.ExternalAnchor(Module);
        Rendezvous = api.ExternalRendezvous(Module);
        return error;
    }

    public string? Anchor { get; private set; }
    public string? Rendezvous { get; private set; }

    private string Answer(string device, string what, int ms = 60_000) =>
        s.Receive(device, ms) ?? throw new TimeoutException($"The Back End's Module gave no {what}.");

    // ---- registration and requests: the Front Ends' (the Controllers carry them), not this User Agent's ----
    public string Register(string registerRequest) =>
        throw new NotSupportedException("Registration reaches the Back End Module on its External Ports, from the Front End's Controller.");

    public string Process(string aihDataProcess) =>
        throw new NotSupportedException("An AIH Data Process request reaches the Back End Module on its External Ports, from the Front End's Controller.");

    // ---- De-Identification and Anonymisation ----
    public string DeIdentify(string deIdAndAnonym)
    {
        lock (gate)
        {
            s.Drain();
            var request = AihJson.Parse(deIdAndAnonym, "De-Identification and Anonymisation");
            var asked = (request["DeIDAndAnonymData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null)
                        ?? throw new FormatException("the request holds no Request.");
            var id = asked["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the request names no AIH Data.");
            // The Tokens are checked where the End Users are registered, by AIH Data Processing; a request they do not grant is not served.
            var user = request["EndUserID"]?.GetValue<string>() ?? "";
            if (Authorised(user, "DeIDAndAnonym", asked["Tokens"] as JsonObject, JsonValue.Create(id)) is null)
                return new JsonObject
                {
                    ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = user,
                    ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = id, ["ProcessingState"] = false } })
                }.ToJsonString();
            s.Send("DeIdIn", deIdAndAnonym);
            s.Send("AhdDeIdIn", Fetch(id) ?? throw new InvalidOperationException($"AIH Data Processing did not give the AIH Data {id} to de-identify."));   // De-Identification is given the AIH Data AIH Data Processing keeps
            var response = Answer("DeIdOut", "answer to the de-identification");
            var result = AihJson.Parse(response, "De-Identification and Anonymisation")["DeIDAndAnonymData"]?[0]?["Response"];
            if (result?["ProcessingState"]?.GetValue<bool>() == true &&
                (s.Receive("AhdDeIdOut", 5000) ?? throw new InvalidOperationException($"De-Identification gave no copy of {id}.")) is { } copy)   // the copy it made: AIH Data Processing keeps it
            {
                s.Send("AhdProcIn", copy);
                Answer("ResponseOut", "receipt of the copy kept");                      // kept before anything is asked of it
            }
            return response;
        }
    }

    // ---- Auditing ----
    public string Audit(string audit)
    {
        lock (gate)
        {
            s.Drain();
            var auditRequest = AihJson.Parse(audit, "Audit");
            var user = auditRequest["UserID"]?.GetValue<string>() ?? "";
            var theirs = Authorised(user, "Auditing", auditRequest["Audit"]?["Request"]?["Tokens"] as JsonObject);
            if (theirs is null)
                return new JsonObject
                {
                    ["Header"] = AihJson.Audit, ["UserID"] = user, ["AuditID"] = AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
                    ["Audit"] = new JsonObject { ["Response"] = new JsonObject { ["ProcessingState"] = false, ["AIHDataIDs"] = new JsonArray() } }
                }.ToJsonString();
            // Auditing sees the AIH Data of the End User and the copies made of it: AIH Data Processing, which keeps them and whose each is, said which with its authorisation; it gives them now.
            foreach (var id in theirs)
                s.Send("AhdAuditIn", Fetch(id) ?? throw new InvalidOperationException($"AIH Data Processing did not give the AIH Data {id} of {user} for the audit."));
            s.Drain();
            s.Send("AuditIn", audit);
            // Its Licence Confirms, one at a time, are asked of the Blockchain as they come (AuditConfirmOut is served).
            return Answer("AuditOut", "answer of the audit");
        }
    }

    // ---- what the Back End's operator starts: an audit, a processing ----
    // The Back End starts these itself (a command of its operator device; the Back End's operator is the trigger, simplest reading). It is not a user and has no Tokens: AIH Data Processing
    // answers its own requests, and the licence terms are checked where they are: the Blockchain, asked by AIH Data Processing and by Auditing, never by the workflow.

    // Audits the use of the AIH Data of one End User, for the period given. Returns the answer of Auditing.
    public JsonObject AuditOf(string endUserId, TimeSpan? period = null)
    {
        lock (gate)
        {
            s.Drain();
            s.Send("RequestIn", Request(me, AihJson.TokensFor(me, ["Auditing"]), JsonValue.Create(endUserId)!, "Auditing"));
            var held = s.Receive("ResponseOut", 10_000) is { } answer && AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process")) is { } r && r["ProcessingStatus"]?.GetValue<bool>() == true
                ? (r["AIHDataOrAIHDataID"] as JsonArray)?.Select(i => i?.GetValue<string>() ?? "").ToList() ?? []
                : throw new InvalidOperationException($"AIH Data Processing did not give the AIH Data of {endUserId} for the audit.");
            foreach (var id in held)
                s.Send("AhdAuditIn", Fetch(id) ?? throw new InvalidOperationException($"AIH Data Processing did not give the AIH Data {id} of {endUserId} for the audit."));
            s.Drain();
            s.Send("AuditIn", new JsonObject
            {
                ["Header"] = AihJson.Audit, ["UserID"] = endUserId, ["AuditID"] = AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
                ["Audit"] = new JsonObject
                {
                    ["Request"] = new JsonObject
                    {
                        ["AuditPeriod"] = AihJson.Time(DateTimeOffset.UtcNow - (period ?? TimeSpan.FromDays(1))),
                        ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction", ["DataClass"] = "AIH-ECO" }),
                        ["Tokens"] = AihJson.TokensFor(me, ["Auditing"])
                    }
                }
            }.ToJsonString());
            return AihJson.Parse(Answer("AuditOut", "answer of the audit"), "Audit");
        }
    }

    // Processes one AIH Data, as the licence terms allow the Back End: AIH Data Processing asks the Blockchain; the answer holds the AIH Data and what was found, or is a refusal.
    public JsonObject ProcessData(string aihDataId)
    {
        lock (gate)
        {
            s.Drain();
            s.Send("RequestIn", Request(me, AihJson.TokensFor(me, ["AIHDataProcessing"]), JsonValue.Create(aihDataId)!, "AIHDataProcessing"));
            return AihJson.Parse(Answer("ResponseOut", "answer to the processing"), "AIH Data Process");
        }
    }

    // ---- Health Federated Learning ----

    private JsonObject Request(string frontEndId, string preLearnModelId)
    {
        var request = new JsonObject { ["HFEID"] = frontEndId, ["PreLearnNNModelID"] = preLearnModelId };
        if (ModelAimId is not null) request["PreLearnAIMID"] = ModelAimId;
        return request;
    }

    // A round: this User Agent asks the Module to start it, for each Front End by the name it joined the service under. The Module's request goes out on its External Port - the Controller
    // sends it, signed, to the Controller it names - and each Front End's answer comes in on the External Port the same way; the Module gives the new model when every answer is in. The
    // User Agent carries nothing between them. Returns the model (AIP-MLO-V1.0).
    public JsonObject FederatedLearning(string preLearnModelId, params string[] frontEndIds)
    {
        lock (gate)
        {
            s.Drain();
            // The Module is paused while the requests are given, so that it has them all before the first answer can come: a round is complete when it has as many answers as requests.
            api.Pause(Module);
            try
            {
                foreach (var id in frontEndIds)
                    s.Send("LearnAnswerIn", new JsonObject
                    {
                        ["Header"] = AihJson.FederatedLearn, ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(),
                        ["FederatedLearnData"] = new JsonArray(new JsonObject { ["Request"] = Request(id, preLearnModelId) })
                    }.ToJsonString());
            }
            finally { api.Resume(Module); }
            var model = AihJson.Parse(Answer("ModelOut", "new NN Model", 60_000), "NN Model");
            // What the Back End puts in the Store is the AIM that wraps the model; its identifier is what the Front Ends are told next.
            if (models is not null) { ModelAimId = models.Upload(model, aimLicence: modelLicence); if (frontEndL3 is not null) FrontEndVersion = models.PublishFrontEnd(frontEndL3, ModelAimId); }
            return model;
        }
    }
    // ---- what the Back End's User Agent does for the Module ----

    // The Smart Contract of the Model Licence the Module sent; the answer for the Module.
    private string Contract(string asked)
    {
        var request = AihJson.Parse(asked, "Blockchain Licence");
        var data = request["AIHDataID"]!.GetValue<string>();
        var licence = request["BlockchainLicenceData"]?[0]?["Request"]?["ModelLicence"] as JsonObject ?? throw new FormatException("the Blockchain Licence holds no Model Licence.");
        var id = chain.Make(licence, data);
        return AihJson.BlockchainResponse(request["UserID"]?.GetValue<string>() ?? "", data, id ?? "", id is not null).ToJsonString();
    }

    // Whether the Blockchain confirms the licence asked about; the answer for the Module.
    private bool Confirm(string asked, out string answer)
    {
        var request = AihJson.Parse(asked, "Licence Confirm");
        var ask = request["LicenceConfirmData"]![0]!["Request"]!;
        var requester = ask["RequesterID"]?.GetValue<string>() ?? "";
        var allowed = chain.Confirm(ask["AIHDataID"]!.GetValue<string>(), ask["ProcessType"]!.GetValue<string>(), requester);
        answer = AihJson.LicenceConfirmResponse(request["EndUserID"]?.GetValue<string>() ?? "", request["LicenceConfirmID"]!.GetValue<string>(), allowed).ToJsonString();
        return allowed;
    }

    // WHETHER THE TOKENS GRANT A SERVICE: asked of AIH Data Processing, which registered the user and gave them the Tokens. No Tokens, no service. Null where they do not; else the items
    // of its answer (for an audit, the AIH Data of the user).
    private List<string>? Authorised(string user, string service, JsonObject? tokens, JsonNode? item = null)
    {
        if (tokens is null) return null;
        s.Drain();
        s.Send("RequestIn", new JsonObject
        {
            ["Header"] = AihJson.Process, ["UserID"] = user, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject
            {
                ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = item is null ? new JsonArray() : new JsonArray(item.DeepClone()), ["ProcessingType"] = service, ["Tokens"] = tokens.DeepClone() }
            })
        }.ToJsonString());
        if (s.Receive("ResponseOut", 10_000) is not { } answer) return null;
        var response = AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process"));
        return response?["ProcessingStatus"]?.GetValue<bool>() == true
            ? (response["AIHDataOrAIHDataID"] as JsonArray)?.Select(i => i?.GetValue<string>() ?? "").ToList() ?? []
            : null;
    }

    // The AIH Data AIH Data Processing keeps under an identifier, asked for as the Back End (which is not a user: it has no Tokens); null if it is not kept.
    private string? Fetch(string id)
    {
        s.Drain();
        s.Send("RequestIn", Request(me, AihJson.TokensFor(me, ["Store"]), JsonValue.Create(id)!));
        if (s.Receive("ResponseOut", 10_000) is not { } answer) return null;
        return AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process"))?["AIHDataOrAIHDataID"]?[0] is JsonObject data ? data.ToJsonString() : null;
    }

    private static string Request(string requester, JsonObject tokens, JsonNode item, string type = "Store") => new JsonObject
    {
        ["Header"] = AihJson.Process, ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(item.DeepClone()), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    }.ToJsonString();
}
