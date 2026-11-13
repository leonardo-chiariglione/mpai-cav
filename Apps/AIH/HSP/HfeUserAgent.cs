using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aih.Hsp;

// THE USER AGENT OF A HEALTH FRONT END - the End User's. It runs HFE.orch, which binds a device to every Port of the Module's boundary, and does what the End User
// does with them: registers with the Back End, gives the Front End the Health Data it acquired from the End User's devices and the Model Licence the End User
// provides, hears the alert, asks for the AIH Data of the Secure Data Vault, and sends it to the Back End. The Front End talks to the Back End only.
public sealed class HfeUserAgent : IDisposable
{
    public const string Module = "1AIH-HFE-V1.1-I01";

    private readonly IControllerApi api;
    private readonly ModuleSession s;
    private JsonObject? tokens;

    public string UserId { get; }

    // service: the Back End's Trust Authority this End User decided to trust - the name of its service, its anchor, where it is reached - and the name the Back End calls this Front End by (its
    // HFEID). The workflow joins the service by name before anything is sent.
    public HfeUserAgent(IControllerApi api, string userId, string workflowFile, string vault, (string Service, string Anchor, string Rendezvous, string KnownAs)? service = null)
    {
        this.api = api; UserId = userId;
        s = new ModuleSession(api, workflowFile, vault, service,
            ("RegisterIn", "AIH-REG-V1.1"), ("RequestIn", "AIH-AHP-V1.1"),
            ("HealthIn", "AIH-HLD-V1.1"), ("LicenceIn", "AIH-MDL-V1.1"), ("DeIdIn", "AIH-DIA-V1.1"), ("AuditIn", "AIH-ADT-V1.1"),
            ("RegisterAnswerOut", ""), ("ResponseOut", ""), ("AraOut", ""), ("DeIdOut", ""), ("AuditOut", ""));
        s.Start();
    }

    public void Dispose() => s.Dispose();

    // The Tokens the Back End gave (null before the End User registered).
    public JsonObject? Tokens => tokens;

    // Registers with the Back End: the request goes into the Front End, which sends it to the Back End; the Back End's answer comes back to the Front End, which gives it out. Between the
    // Controllers, which joined the service first (the workflow did that); this User Agent carries nothing.
    public JsonObject Register(JsonObject request)
    {
        s.Drain();
        s.Send("RegisterIn", request.ToJsonString());
        var answer = AihJson.Parse(s.Receive("RegisterAnswerOut", 60_000) ?? throw new TimeoutException("The Back End gave no answer to the registration."), "Register");
        tokens = AihJson.RegisterResponseOf(answer)?["Tokens"] as JsonObject;
        return answer;
    }

    // Gives the Front End the Health Data and the Model Licence: AIH Data is made, checked, and kept in the Secure Data Vault. The alert is the ARA Data the Front End gives
    // (Anomaly and Risk Alerting's and, where the Front End has one, the AIM that wraps a model - the graver of them is the alert); the ARA Data is returned if it is a risk (its
    // Risk Yellow, Orange or Red), and null if it is not. It is in LastAra either way.
    public JsonObject? Acquire(JsonObject healthData, JsonObject modelLicence)
    {
        s.Drain();
        s.Send("HealthIn", healthData.ToJsonString());
        s.Send("LicenceIn", modelLicence.ToJsonString());
        JsonObject? graver = null;
        // The first ARA Data is waited for; more may follow from the other AIMs that judge the AIH Data, and a moment is given them.
        for (var wait = 20_000; s.Receive("AraOut", wait) is { } json; wait = 3000)
        {
            var ara = AihJson.Parse(json, "ARA Data");
            if (graver is null || Rank(Risk(ara)) > Rank(Risk(graver))) graver = ara;
        }
        LastAra = graver;
        return graver is not null && Risk(graver) is "Red" or "Orange" or "Yellow" ? graver : null;
    }

    // The ARA Data of the last AIH Data the End User gave the Front End (the graver, where several AIMs judged it).
    public JsonObject? LastAra { get; private set; }

    private static string Risk(JsonObject ara) => ara["ARAData"]?["Risk"]?.GetValue<string>() ?? "";
    private static int Rank(string risk) => risk switch { "Red" => 4, "Orange" => 3, "Yellow" => 2, "Green" => 1, _ => 0 };
    // The AIH Data of the Secure Data Vault, by identifier (null if it is not there): asked of AIH Data Processing, which keeps it in the Vault through the storage API.
    public JsonObject? Retrieve(string aihDataId)
    {
        s.Drain();
        s.Send("RequestIn", Request("Store", [JsonValue.Create(aihDataId)!]).ToJsonString());
        while (s.Receive("ResponseOut", 10_000) is { } answer)
        {
            var response = AihJson.ResponseOf(AihJson.Parse(answer, "AIH Data Process"));
            if (response?["AIHDataOrAIHDataID"]?[0] is JsonObject data) return data;
            if (response?["ProcessingStatus"]?.GetValue<bool>() == false) return null;
        }
        return null;
    }

    // Sends the AIH Data to the Back End to be kept: the request goes into the Front End, which sends it to the Back End; the Back End makes the Smart Contract, keeps the AIH Data and answers, and
    // the Front End gives the answer out. Between the Controllers; this User Agent carries nothing. Returns the Back End's answer.
    public JsonObject Upload(string aihDataId)
    {
        var data = Retrieve(aihDataId) ?? throw new InvalidOperationException($"No AIH Data '{aihDataId}' in the Secure Data Vault.");
        s.Drain();
        s.Send("RequestIn", Request("Store", [data]).ToJsonString());
        return AihJson.Parse(s.Receive("ResponseOut", 60_000) ?? throw new TimeoutException("The Back End gave no answer to the upload."), "AIH Data Process");
    }

    // A request for the Back End to process an AIH Data, as its licence prescribes (a Third-Party User's): answered with the AIH Data and what was found, or refused.
    public JsonObject Process(string aihDataId)
    {
        s.Drain();
        s.Send("RequestIn", Request("AIHDataProcessing", [JsonValue.Create(aihDataId)!]).ToJsonString());
        return AihJson.Parse(s.Receive("ResponseOut", 60_000) ?? throw new TimeoutException("The Back End gave no answer to the processing."), "AIH Data Process");
    }

    // A request for the Back End to de-identify or anonymise an AIH Data of this End User: offered at the Port of its Data Type; the Front End sends it to the Back End and gives the answer
    // out, between the Controllers. This User Agent carries nothing to the Back End.
    public JsonObject DeIdentify(string aihDataId, string type, string algorithm) =>
        Ask("DeIdIn", "DeIdOut", "DeIDAndAnonymID", "De-Identification and Anonymisation", new JsonObject
        {
            ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = UserId,
            ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = aihDataId, ["DeIDAndAnonymType"] = type, ["DeIDAndAnonymAlgoID"] = algorithm, ["Tokens"] = (tokens ?? AihJson.TokensFor(UserId, [])).DeepClone() } })
        });

    // A request for the Back End to audit the use of this End User's AIH Data: the same way.
    public JsonObject Audit() =>
        Ask("AuditIn", "AuditOut", "AuditID", "Audit", new JsonObject
        {
            ["Header"] = AihJson.Audit, ["UserID"] = UserId, ["AuditID"] = AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
            ["Audit"] = new JsonObject
            {
                ["Request"] = new JsonObject
                {
                    ["AuditPeriod"] = AihJson.Time(DateTimeOffset.UtcNow.AddDays(-1)),
                    ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction", ["DataClass"] = "AIH-ECO" }),
                    ["Tokens"] = (tokens ?? AihJson.TokensFor(UserId, [])).DeepClone()
                }
            }
        });

    // Offers a request and reads its answer: the one that carries the request's identifier.
    private JsonObject Ask(string inDevice, string outDevice, string idName, string what, JsonObject request)
    {
        var id = request[idName]!.GetValue<string>();
        s.Drain();
        s.Send(inDevice, request.ToJsonString());
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && s.Receive(outDevice, (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds)) is { } json)
        {
            var answer = AihJson.Parse(json, what);
            if (answer[idName]?.GetValue<string>() == id) return answer;
        }
        throw new TimeoutException($"The Back End gave no answer to the {what} request.");
    }

    private JsonObject Request(string type, JsonNode[] items) => new()
    {
        ["Header"] = AihJson.Process, ["UserID"] = UserId, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["AIHDataOrAIHDataID"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()), ["ProcessingType"] = type,
                ["Tokens"] = (tokens ?? AihJson.TokensFor(UserId, [type])).DeepClone()
            }
        })
    };
}
