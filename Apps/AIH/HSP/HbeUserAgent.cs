using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Aif.Api;
using Mpai.Aih.Chain;
using Mpai.Aih.Nnm;

namespace Mpai.Aih.Hsp;

// WHAT THE HEALTH BACK END OFFERS ITS CLIENTS - the Front Ends and the Third-Party Users (the Front Ends never speak to each other). Each call is one request of
// one of the Data Types of the Back End's boundary and gives its answer; the Data Types are the JSON instances of schemas/AIH1/V1.1.
public interface IBackEnd
{
    string Register(string registerRequest);          // AIH-REG: an End User, or a Third-Party User
    string Process(string aihDataProcess);            // AIH-AHP to HDP: an upload (Store, the AIH Data inline), a retrieval (Store, by identifier), a processing (AIHDataProcessing, by identifier)
    string DeIdentify(string deIdAndAnonym);          // AIH-DIA
    string Audit(string audit);                       // AIH-ADT
}

// WHAT A HEALTH FRONT END OFFERS THE BACK END: to train its model on the AIH Data it holds when asked (the Back End never reaches into a Front End for anything else).
public interface IFrontEnd
{
    string Learn(string federatedLearnRequest);        // AIH-FDL: the request in, the response (the model it trained) out
}

// THE USER AGENT OF THE HEALTH BACK END. It runs HBE.orch, which binds a device to every Port of the Module's boundary, and it does what the Back End's operator
// does with them: it carries to the Blockchain what the Module asks of it and gives the Module the answer; it asks AIH Data Processing - which keeps the AIH Data in the Secure Storage
// through the storage API, there is no Storage AIM - for the AIH Data that De-Identification and Auditing work on and hands it to them, and gives AIH Data Processing the copy
// De-Identification made to keep; it hands each client the answer the Module gave.
public sealed class HbeUserAgent : IBackEnd, IDisposable
{
    private readonly ModuleSession s;
    private readonly IBlockchain chain;
    private readonly string me;
    private readonly object gate = new();
    private readonly Dictionary<string, string> owners = new();          // the End User of each AIH Data kept
    private readonly Dictionary<string, List<string>> copies = new();    // the copies made of each AIH Data

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
        this.chain = chain; me = backEndId; this.models = models; this.frontEndL3 = frontEndL3; this.modelLicence = modelLicence;
        s = new ModuleSession(api, workflowFile, storageLocation,
            ("RegisterIn", "AIH-REG-V1.1"), ("RequestIn", "AIH-AHP-V1.1"), ("DeIdIn", "AIH-DIA-V1.1"), ("AuditIn", "AIH-ADT-V1.1"),
            ("AhdAuditIn", "AIH-AHD-V1.1"), ("AhdDeIdIn", "AIH-AHD-V1.1"), ("AhdProcIn", "AIH-AHD-V1.1"),
            ("LearnAnswerIn", "AIH-FDL-V1.1"), ("ContractIn", "AIH-BCL-V1.1"), ("ConfirmIn", "AIH-LCF-V1.1"), ("AuditConfirmIn", "AIH-LCF-V1.1"),
            ("RegisterOut", ""), ("ResponseOut", ""), ("AraOut", ""), ("DeIdOut", ""), ("AhdDeIdOut", ""), ("AuditOut", ""), ("LearnOut", ""), ("ModelOut", ""),
            ("ContractOut", ""), ("ConfirmOut", ""), ("AuditConfirmOut", ""));
        s.Start();
    }

    public void Dispose() => s.Dispose();

    private string Answer(string device, string what, int ms = 20_000) =>
        s.Receive(device, ms) ?? throw new TimeoutException($"The Back End's Module gave no {what}.");

    // ---- registration ----
    public string Register(string registerRequest)
    {
        lock (gate)
        {
            s.Drain();
            s.Send("RegisterIn", registerRequest);
            return Answer("RegisterOut", "answer to the registration");
        }
    }

    // ---- HDP ----
    public string Process(string aihDataProcess)
    {
        lock (gate)
        {
            s.Drain();
            var process = AihJson.Parse(aihDataProcess, "AIH Data Process");
            var request = AihJson.RequestOf(process) ?? throw new FormatException("the request holds no Request.");
            var type = request["ProcessingType"]?.GetValue<string>();
            var items = (request["AIHDataOrAIHDataID"] as JsonArray)?.ToList() ?? [];
            s.Send("RequestIn", aihDataProcess);

            // AN UPLOAD: the Module asks for the Smart Contract; the Blockchain makes it; the AIH Data comes back with its identifier, kept in the Secure Storage.
            if (type == "Store" && items.Count == 1 && items[0] is JsonObject)
            {
                if (s.Receive("ContractOut", 3000) is { } asked) s.Send("ContractIn", Contract(asked));
                var response = Answer("ResponseOut", "answer to the upload");
                var result = AihJson.ResponseOf(AihJson.Parse(response, "AIH Data Process"));
                if (result?["ProcessingStatus"]?.GetValue<bool>() == true && result["AIHDataOrAIHDataID"]?[0] is JsonObject kept)
                {
                    owners[kept["AIHDataID"]!.GetValue<string>()] = kept["UserID"]?.GetValue<string>() ?? process["UserID"]?.GetValue<string>() ?? "";
                    // what the client is told is the response as it is (the AIH Data with its Smart Contract)
                }
                return response;
            }

            // A PROCESSING: the Module asks whether the licence allows it; if so, it fetches the AIH Data from the Secure Storage and processes it.
            if (type == "AIHDataProcessing" && items.Count == 1 && items[0] is JsonValue id)
            {
                if (s.Receive("ConfirmOut", 3000) is { } asked)
                {
                    Confirm(asked, out var answer);
                    s.Send("ConfirmIn", answer);
                }
                return Answer("ResponseOut", "answer to the processing");
            }

            return Answer("ResponseOut", "answer");
        }
    }

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
            if (!Authorised(user, "DeIDAndAnonym", asked["Tokens"] as JsonObject, JsonValue.Create(id)))
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
                if (!copies.TryGetValue(id, out var list)) copies[id] = list = [];
                list.Add(result["NewAIHDataID"]?.GetValue<string>() ?? AihJson.Parse(copy, "AIH Data")["AIHDataID"]!.GetValue<string>());      // the identifier the response gives the copy
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
            if (!Authorised(user, "Auditing", auditRequest["Audit"]?["Request"]?["Tokens"] as JsonObject))
                return new JsonObject
                {
                    ["Header"] = AihJson.Audit, ["UserID"] = user, ["AuditID"] = AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
                    ["Audit"] = new JsonObject { ["Response"] = new JsonObject { ["ProcessingState"] = false, ["AIHDataIDs"] = new JsonArray() } }
                }.ToJsonString();
            // Auditing sees the AIH Data of the End User and the copies made of it, which AIH Data Processing gives the User Agent to hand it.
            foreach (var id in owners.Where(o => o.Value == user).Select(o => o.Key).ToList())
            {
                s.Send("AhdAuditIn", Fetch(id) ?? throw new InvalidOperationException($"AIH Data Processing did not give the AIH Data {id} of {user} for the audit."));
                foreach (var copyId in copies.GetValueOrDefault(id) ?? [])
                    s.Send("AhdAuditIn", Fetch(copyId) ?? throw new InvalidOperationException($"AIH Data Processing did not give the copy {copyId} of {id} for the audit."));
            }
            s.Drain();
            s.Send("AuditIn", audit);
            // It asks one Licence Confirm at a time, of the Blockchain.
            while (s.Receive("AuditConfirmOut", 2000) is { } asked)
            {
                Confirm(asked, out var answer);
                s.Send("AuditConfirmIn", answer);
            }
            return Answer("AuditOut", "answer of the audit");
        }
    }

    // ---- Health Federated Learning ----

    private JsonObject Request(string frontEndId, string preLearnModelId)
    {
        var request = new JsonObject { ["HFEID"] = frontEndId, ["PreLearnNNModelID"] = preLearnModelId };
        if (ModelAimId is not null) request["PreLearnAIMID"] = ModelAimId;
        return request;
    }

    // A round: the Module asks each Front End for the model it trained (the User Agent carries the request there and the answer back), and gives the new model when
    // every answer is in. Returns the model (AIP-MLO-V1.0).
    public JsonObject FederatedLearning(string preLearnModelId, params (string FrontEndId, IFrontEnd Front)[] fronts)
    {
        lock (gate)
        {
            s.Drain();
            var forwarded = new List<(IFrontEnd Front, string Request)>();
            foreach (var (id, front) in fronts)
            {
                s.Send("LearnAnswerIn", new JsonObject
                {
                    ["Header"] = AihJson.FederatedLearn, ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(),
                    ["FederatedLearnData"] = new JsonArray(new JsonObject { ["Request"] = Request(id, preLearnModelId) })
                }.ToJsonString());
                forwarded.Add((front, Answer("LearnOut", "request for the Front End")));
            }
            foreach (var (front, request) in forwarded) s.Send("LearnAnswerIn", front.Learn(request));
            var model = AihJson.Parse(Answer("ModelOut", "new NN Model"), "NN Model");
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

    // WHETHER THE TOKENS GRANT A SERVICE: asked of AIH Data Processing, which registered the user and gave them the Tokens. No Tokens, no service.
    private bool Authorised(string user, string service, JsonObject? tokens, JsonNode? item = null)
    {
        if (tokens is null) return false;
        s.Drain();
        s.Send("RequestIn", new JsonObject
        {
            ["Header"] = AihJson.Process, ["UserID"] = user, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject
            {
                ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = item is null ? new JsonArray() : new JsonArray(item.DeepClone()), ["ProcessingType"] = service, ["Tokens"] = tokens.DeepClone() }
            })
        }.ToJsonString());
        return s.Receive("ResponseOut", 10_000) is { } answer && AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process"))?["ProcessingStatus"]?.GetValue<bool>() == true;
    }

    // The AIH Data AIH Data Processing keeps under an identifier, asked for as the Back End (which is not a user: it has no Tokens); null if it is not kept.
    private string? Fetch(string id)
    {
        s.Drain();
        s.Send("RequestIn", Request(me, AihJson.TokensFor(me, ["Store"]), JsonValue.Create(id)!));
        if (s.Receive("ResponseOut", 10_000) is not { } answer) return null;
        return AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process"))?["AIHDataOrAIHDataID"]?[0] is JsonObject data ? data.ToJsonString() : null;
    }

    private static string Request(string requester, JsonObject tokens, JsonNode item) => new JsonObject
    {
        ["Header"] = AihJson.Process, ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(item.DeepClone()), ["ProcessingType"] = "Store", ["Tokens"] = tokens.DeepClone() }
        })
    }.ToJsonString();
}
