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
    public string RegOut = "", RegBackOut = "", AhpStoreOut = "", AhpResponseOut = "", AhdOut = "", LcfOut = "", BclOut = "", FdlOut = "";

    public HdpPorts(AimPortReader p)
    {
        RegIn = p.InputOrDefault(AihJson.Register, 1, "");           RegBackIn = p.InputOrDefault(AihJson.Register, 2, "");
        AhpResponseIn = p.InputOrDefault(AihJson.Process, 1, "");   AhpRequestIn = p.InputOrDefault(AihJson.Process, 2, "");
        AhdIn = p.InputOrDefault(AihJson.AihData, "");              ArdIn = p.InputOrDefault(AihJson.AraData, "");
        LcfIn = p.InputOrDefault(AihJson.LicenceConfirm, "");       BclIn = p.InputOrDefault(AihJson.Blockchain, "");     FdlIn = p.InputOrDefault(AihJson.FederatedLearn, "");
        RegOut = p.OutputOrDefault(AihJson.Register, 1, "");        RegBackOut = p.OutputOrDefault(AihJson.Register, 2, "");
        AhpStoreOut = p.OutputOrDefault(AihJson.Process, 1, "");    AhpResponseOut = p.OutputOrDefault(AihJson.Process, 2, "");
        AhdOut = p.OutputOrDefault(AihJson.AihData, "");            LcfOut = p.OutputOrDefault(AihJson.LicenceConfirm, "");
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
}

// ---- THE FRONT END's HDP: the End User's gateway ---------------------------------------------------------------------------------------------------------
// Registration and requests go on to the Back End and its answers come back; new AIH Data is kept in the Secure Data Vault - the Module's storage, used through its API, there
// is no Storage AIM - and goes to Anomaly and Risk Alerting, and what that finds is added to it
// a risk (the alert is the ARA Data: Anomaly and Risk Alerting gives it at the Front End's boundary). A request to retrieve AIH Data of the End User is answered here, from the Vault.
public sealed class FrontEndHdpAimProcessor(string instanceId, AimPortReader reader, ISharedStorage storage) : HdpAimProcessor(instanceId, reader)
{
    private readonly Dictionary<string, JsonObject> awaitingAra = new();

    // A Federated Learn request: the Front End trains on what it holds and answers with the model and the AIH Data it used (the last it made).
    private void Learn(JsonObject fdl, Dictionary<string, string> outputs)
    {
        var request = (fdl["FederatedLearnData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null)
                      ?? throw new FormatException("the Federated Learn holds no Request.");
        var held = AihStore.All(storage).ToList();
        if (held.Count == 0) throw new InvalidOperationException("this Front End holds no AIH Data to train on.");
        var model = FrontEndLearning.Model(request["PreLearnNNModelID"]?.GetValue<string>() ?? "model", request["HFEID"]?.GetValue<string>() ?? "hfe", held);
        Put(outputs, P.FdlOut, new JsonObject
        {
            ["Header"] = AihJson.FederatedLearn, ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(),
            ["FederatedLearnData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = held[^1]["AIHDataID"]!.GetValue<string>(), ["PostLearnNNModel"] = model } })
        });
    }
    private static int Rank(string risk) => risk switch { "Red" => 4, "Orange" => 3, "Yellow" => 2, "Green" => 1, _ => 0 };

    protected override void Handle(Message m, Dictionary<string, string> outputs)
    {
        if (Take(m, P.FdlIn) is { } learn) Learn(AihJson.Parse(learn, "Federated Learn"), outputs);
        if (Take(m, P.RegIn) is { } register) outputs[P.RegOut] = register;                       // to the Back End
        if (Take(m, P.RegBackIn) is { } registered) outputs[P.RegBackOut] = registered;           // to the End User
        if (Take(m, P.AhpRequestIn) is { } request)
        {
            var process = AihJson.Parse(request, "AIH Data Process");
            if (AihStore.IsRetrieval(process, out var user, out var id)) Put(outputs, P.AhpResponseOut, AihStore.Answer(storage, user, id, ""));   // from the Vault
            else outputs[P.AhpStoreOut] = request;                                                                                                      // to the Back End
        }
        if (Take(m, P.AhpResponseIn) is { } response) outputs[P.AhpResponseOut] = response;       // to the End User

        if (Take(m, P.AhdIn) is { } dataJson)
        {
            var data = AihJson.Parse(dataJson, "AIH Data");
            if (data["AIHData"]?["ARAData"] is null && data["AIHDataID"]?.GetValue<string>() is { } id) awaitingAra[id] = data;
            AihStore.Put(storage, data);                                                            // kept in the Vault
            Put(outputs, P.AhdOut, data);                                                           // to Anomaly and Risk Alerting
        }

        if (Take(m, P.ArdIn) is { } ardJson)
        {
            var ard = AihJson.Parse(ardJson, "ARA Data");
            var id = ard["ARAData"]?["AIHDataID"]?.GetValue<string>() ?? "";
            // The first ARA Data for an AIH Data is taken; another that comes later - from another AIM that judges it, such as one wrapping a Neural Network Model - is taken
            // only if it finds the risk graver.
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
// (The Front End's HDP, continued.) THE LOCAL TRAINING that a Federated Learn request asks for. The specification does not say what a model is or how it is
// trained; this is a stand-in that makes the round testable: the model of this Front End is two 32-bit floats - the mean of the risk scores of the AIH Data it holds
// (Red 1, Orange 0.75, Yellow 0.5, Green 0.25, Blue 0) and their number - as an ML Model Object; the Back End's Health Federated Learning averages them.
internal static class FrontEndLearning
{
    internal static JsonObject Model(string preLearnId, string hfe, IEnumerable<JsonObject> held)
    {
        var scores = held.Select(d => (d["AIHData"]?["ARAData"]?["ARAData"]?["Risk"]?.GetValue<string>() ?? "Blue") switch { "Red" => 1f, "Orange" => 0.75f, "Yellow" => 0.5f, "Green" => 0.25f, _ => 0f }).ToList();
        var weights = new[] { scores.Count == 0 ? 0f : scores.Average(), (float)scores.Count };
        var bytes = weights.SelectMany(BitConverter.GetBytes).ToArray();
        return new JsonObject
        {
            ["Header"] = "AIP-MLO-V1.0", ["MLModelObjectID"] = $"{preLearnId}-{hfe}", ["MLModelData"] = new JsonArray(new JsonObject { ["Data"] = Convert.ToBase64String(bytes) }),
            ["DescrMetadata"] = "Local training stand-in: mean risk score and number of AIH Data"
        };
    }
}

// ---- THE BACK END's HDP -------------------------------------------------------------------------------------------------------------------------------
// It registers End Users and Third-Party Users and gives them Tokens; it makes the Smart Contract of AIH Data that is uploaded and keeps the AIH Data in the Secure Storage - the
// Module's storage, through its API: there is no Storage AIM; it gives it back to its End User and to the Back End; it processes AIH Data for those whose Tokens allow it and
// whose licence confirms it.
public sealed class BackEndHdpAimProcessor(string instanceId, AimPortReader reader, ISharedStorage storage, string backEndId = "") : HdpAimProcessor(instanceId, reader)
{
    // What each class of user may ask the Back End for.
    private static readonly string[] EndUserServices = AihJson.Services;
    private static readonly string[] ThirdPartyServices = ["AIHDataProcessing"];

    private readonly Dictionary<string, string[]> registered = new();
    private readonly Dictionary<string, Upload> uploads = new();
    private readonly Dictionary<string, Processing> processing = new();

    private sealed record Upload(string Requester, JsonObject Data);
    private sealed class Processing(string requester, string confirmId)
    {
        public string Requester { get; } = requester;
        public string ConfirmId { get; } = confirmId;
        public bool? Licensed { get; set; }
        public JsonObject? Data { get; set; }
        public bool SentToAra { get; set; }
    }

    protected override void Handle(Message m, Dictionary<string, string> outputs)
    {
        if (Take(m, P.RegIn) is { } register) Register(AihJson.Parse(register, "Register"), outputs);
        if (Take(m, P.BclIn) is { } bcl) Contract(AihJson.Parse(bcl, "Blockchain Licence"), outputs);
        if (Take(m, P.AhpRequestIn) is { } request) Request(AihJson.Parse(request, "AIH Data Process"), outputs);
        if (Take(m, P.LcfIn) is { } lcf) Licence(AihJson.Parse(lcf, "Licence Confirm"), outputs);
        if (Take(m, P.AhdIn) is { } data) Data(AihJson.Parse(data, "AIH Data"), outputs);
        if (Take(m, P.ArdIn) is { } ard) Found(AihJson.Parse(ard, "ARA Data"), outputs);
    }

    // ---- registration ----
    private void Register(JsonObject register, Dictionary<string, string> outputs)
    {
        var id = register["RegisterID"]?.GetValue<string>() ?? AihJson.NewId("reg");
        var request = AihJson.RegisterRequestOf(register);
        var (user, allowed) =
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
        Put(outputs, P.RegBackOut, AihJson.RegisterResponse(id, true, AihJson.TokensFor(user, granted)));
    }

    // ---- requests ----
    private void Request(JsonObject process, Dictionary<string, string> outputs)
    {
        var requester = process["UserID"]?.GetValue<string>() ?? "";
        // The Back End itself retrieves what it keeps, for De-Identification and Auditing: it is not a user, and has no Tokens.
        if (backEndId.Length > 0 && requester == backEndId && AihStore.IsRetrieval(process, out _, out var wanted))
        {
            Put(outputs, P.AhpResponseOut, AihStore.Answer(storage, requester, wanted, backEndId));
            return;
        }
        var request = AihJson.RequestOf(process) ?? throw new FormatException("the request holds no Request.");
        var type = request["ProcessingType"]?.GetValue<string>() ?? "";
        var presented = AihJson.ServicesOf(request["Tokens"] as JsonObject);
        var items = (request["AIHDataOrAIHDataID"] as JsonArray)?.ToList() ?? [];

        // THE TOKENS: the user is registered, was granted this service, and presents it. A request to audit names no AIH Data; the others name one.
        if (!registered.TryGetValue(requester, out var granted) || !granted.Contains(type) || !presented.Contains(type) || (type != "Auditing" && items.Count != 1))
        {
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, Array.Empty<string>(), false));
            return;
        }

        switch (type)
        {
            case "Store" when items[0] is JsonObject data:
            {
                var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
                var licence = data["AIHData"]?["ModelLicence"] ?? throw new FormatException("the AIH Data has no Model Licence.");
                if (licence is JsonValue) throw new FormatException("the Model Licence is an identifier: the Back End needs the licence itself.");
                uploads[id] = new Upload(requester, data);
                Put(outputs, P.BclOut, AihJson.BlockchainRequest(data["UserID"]?.GetValue<string>() ?? requester, id, licence));
                break;
            }
            // The Tokens of a request to De-Identify and Anonymise, or to audit, were checked here - HDP is where the End Users are registered and given them: the answer says whether
            // the User Agent may go on and have De-Identification and Anonymisation, or Auditing, serve it.
            case "DeIDAndAnonym" or "Auditing":
                Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, items.Select(i => i?.ToString() ?? "").ToArray(), true));
                break;
            case "Store" when items[0] is JsonValue retrieveId:                                            // retrieval: the AIH Data of this End User
                Put(outputs, P.AhpResponseOut, AihStore.Answer(storage, requester, retrieveId.GetValue<string>(), backEndId));
                break;
            case "AIHDataProcessing" when items[0] is JsonValue idNode:
            {
                var id = idNode.GetValue<string>();
                var confirm = AihJson.LicenceConfirmRequest("", id, type, requester);
                processing[id] = new Processing(requester, confirm["LicenceConfirmID"]!.GetValue<string>());
                Put(outputs, P.LcfOut, confirm);
                break;
            }
            default:
                Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(requester, Array.Empty<string>(), false));        // the others are of other AIMs
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
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(upload.Requester, new[] { id }, false));
            return;
        }
        // The Smart Contract's identifier goes into the AIH Data, which is kept in the Secure Storage.
        ((JsonObject)upload.Data["AIHData"]!)["SmartContractID"] = response!["BlockchainLicenceID"]!.GetValue<string>();
        AihStore.Put(storage, upload.Data);
        Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(upload.Requester, new JsonArray(upload.Data.DeepClone()), true));
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
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Value.Requester, new[] { entry.Key }, false));
            return;
        }
        entry.Value.Licensed = true;
        // The AIH Data is fetched from the Secure Storage; one that is not there is not processed.
        entry.Value.Data = AihStore.Get(storage, entry.Key);
        if (entry.Value.Data is null)
        {
            processing.Remove(entry.Key);
            Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Value.Requester, new[] { entry.Key }, false));
            return;
        }
        Run(entry.Key, entry.Value, outputs);
    }

    // ---- AIH Data given to be kept (the copy De-Identification made): kept, and a response says so (a request that follows is answered after it) ----
    private void Data(JsonObject data, Dictionary<string, string> outputs)
    {
        var user = AihStore.Put(storage, data);
        Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(user, new[] { data["AIHDataID"]!.GetValue<string>() }, true));
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
        Put(outputs, P.AhpResponseOut, AihJson.ProcessResponse(entry.Requester, new JsonArray(entry.Data.DeepClone()), true));
    }
}
