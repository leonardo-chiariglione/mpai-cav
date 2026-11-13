using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih.Chain;
using Mpai.Aih.Nnm;

namespace Mpai.Aih.Hsp;

// THE USER AGENT OF THE HEALTH BACK END. It runs HBE.orch, which binds a device to every Port of the Module's boundary, and it does what the Back End's operator does with them: it carries to
// the Blockchain, as the Module asks and all the time, what the Module asks of it and gives the Module the answer; it offers the Back End's own requests (an audit, a processing) at the
// Port of their Data Type and reads the response at the Port of its Data Type. It carries nothing between the AIMs of the Module (AIF V3.0 COM-1, UA-2, UA-4): AIH Data Processing checks the
// Tokens and gives De-Identification and Anonymisation, and Auditing, the AIH Data on Channels (2026-11-13). The Front Ends' registrations and requests, and the answers to them, do not
// pass through it: they are between the Controllers.
public sealed class HbeUserAgent : IDisposable
{
    // The Health Back End Module. The policy of its service is the workflow's (HBE.orch: 'ask Controller to admit by').
    // The Module the workflow runs: 1AIH-HBE-V1.1-I01 (Basic) or -I02 (Secure).
    public string Module => s.Module;

    private readonly IControllerApi api;
    private readonly ModuleSession s;
    private readonly IBlockchain chain;
    private readonly string me;
    private readonly object gate = new();

    private readonly ModelAimPublisher? models;
    private readonly string? frontEndL3;

    // The AIM that wraps the latest model the rounds of federated learning gave, in the MPAI Store; the next round's Requests name it (PreLearnAIMID).
    public string? ModelAimId { get; private set; }
    // The version of the Health Front End the Back End published with that AIM.
    public string? FrontEndVersion { get; private set; }

    // models: the MPAI Store, to which the Back End uploads the AIM that wraps each model it gets; without it the model is not uploaded. frontEndL3: the L3 of the Health Front End as the Back End has it, from which it publishes the new version that has the AIM for a Front End to take (the Controller of a Front End follows the Store, or not).
    public HbeUserAgent(IControllerApi api, IBlockchain chain, string workflowFile, string storageLocation, string backEndId = "hbe-1", ModelAimPublisher? models = null, string? frontEndL3 = null)
    {
        this.api = api; this.chain = chain; me = backEndId; this.models = models; this.frontEndL3 = frontEndL3;
        s = new ModuleSession(api, workflowFile, storageLocation, null,
            ("RegisterIn", "AIH-REG-V1.1"), ("RequestIn", "AIH-AHP-V1.1"), ("DeIdIn", "AIH-DIA-V1.1"), ("AuditIn", "AIH-ADT-V1.1"),
            ("LearnAnswerIn", "AIH-FDL-V1.1"), ("ContractIn", "AIH-BCL-V1.1"), ("ConfirmIn", "AIH-LCF-V1.1"), ("AuditConfirmIn", "AIH-LCF-V1.1"),
            ("RegisterOut", ""), ("ResponseOut", ""), ("AraOut", ""), ("DeIdOut", ""), ("AuditOut", ""), ("ModelOut", ""),
            ("ContractOut", ""), ("ConfirmOut", ""), ("AuditConfirmOut", ""));
        s.Start();
        Anchor = api.ExternalAnchor(Module); Rendezvous = api.ExternalRendezvous(Module);
        // What the Module asks of the Blockchain it asks when it needs to, not when this User Agent expects it: each is served as it comes.
        s.Serve("ContractOut", asked => s.Send("ContractIn", Contract(asked)));
        s.Serve("ConfirmOut", asked => { Confirm(asked, out var answer); s.Send("ConfirmIn", answer); });
        s.Serve("AuditConfirmOut", asked => { Confirm(asked, out var answer); s.Send("AuditConfirmIn", answer); });
    }

    public void Dispose() => s.Dispose();

    // What went wrong in what the Module asked of the Blockchain.
    public IReadOnlyCollection<string> Errors => s.Errors;

    // THE BACK END'S SERVICE: the workflow made the Controller the Trust Authority of the External links of the Module ('ask Controller to admit by'). What the User Agent publishes with
    // the service is the Controller's anchor, and the rendezvous where a Front End presents itself; null where the workflow did not admit.
    public string? Anchor { get; private set; }
    public string? Rendezvous { get; private set; }

    private string Answer(string device, string what, int ms = 60_000) =>
        s.Receive(device, ms) ?? throw new TimeoutException($"The Back End's Module gave no {what}.");

    // ---- what the Back End's operator starts: an audit, a processing ----
    // The Back End starts these itself (a command of its operator device; the Back End's operator is the trigger, simplest reading). It is not a user and has no Tokens: AIH Data Processing
    // answers its own requests, and the licence terms are checked where they are: the Blockchain, asked by AIH Data Processing and by Auditing, never by the workflow.

    // Audits the use of the AIH Data of one End User, for the period given: an Audit request with the Back End's Tokens. Returns the answer of Auditing.
    public JsonObject AuditOf(string endUserId, TimeSpan? period = null)
    {
        lock (gate)
        {
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

    // THE FIRST MODEL. A Front End answers a Federated Learn request with the model the AIM wrapping it holds, so the Back End publishes an AIM with its first model before the first
    // round, and the Front End version that has it; a Front End's Controller takes that version when it starts. Returns the AIM's identifier.
    public string PublishModel(JsonObject model)
    {
        if (models is null) throw new InvalidOperationException("The Back End has no MPAI Store to publish to.");
        ModelAimId = models.Upload(model);
        if (frontEndL3 is not null) FrontEndVersion = models.PublishFrontEnd(frontEndL3, ModelAimId);
        return ModelAimId;
    }

    // A round: this User Agent offers the Module ONE Federated Learn with a Request for each Front End, by the name it joined the service under. Health Federated Learning sends each
    // Front End its own Request on the External Port - the Controller sends it, signed, to the Controller it names - and each answer comes in on the External Port the same way; the
    // Module gives the new model when every answer is in. The User Agent carries nothing between them, and the Module is not paused. Returns the model (AIP-MLO-V1.0).
    public JsonObject FederatedLearning(string preLearnModelId, params string[] frontEndIds)
    {
        lock (gate)
        {
            s.Drain();
            s.Send("LearnAnswerIn", new JsonObject
            {
                ["Header"] = AihJson.FederatedLearn, ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(),
                ["FederatedLearnData"] = new JsonArray(frontEndIds.Select(id => (JsonNode)new JsonObject { ["Request"] = Request(id, preLearnModelId) }).ToArray())
            }.ToJsonString());
            var model = AihJson.Parse(Answer("ModelOut", "new NN Model", 60_000), "NN Model");
            // What the Back End puts in the Store is the AIM that wraps the model; its identifier is what the Front Ends are told next.
            if (models is not null) { ModelAimId = models.Upload(model); if (frontEndL3 is not null) FrontEndVersion = models.PublishFrontEnd(frontEndL3, ModelAimId); }
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

    private static string Request(string requester, JsonObject tokens, JsonNode item, string type = "Store") => new JsonObject
    {
        ["Header"] = AihJson.Process, ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(item.DeepClone()), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    }.ToJsonString();
}
