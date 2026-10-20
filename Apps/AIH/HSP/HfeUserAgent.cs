using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Aif.Api;

namespace Mpai.Aih.Hsp;

// THE USER AGENT OF A HEALTH FRONT END - the End User's. It runs HFE.orch, which binds a device to every Port of the Module's boundary, and does what the End User
// does with them: registers with the Back End, gives the Front End the Health Data it acquired from the End User's devices and the Model Licence the End User
// provides, hears the alert, asks for the AIH Data of the Secure Data Vault, and sends it to the Back End. The Front End talks to the Back End only.
public sealed class HfeUserAgent : IFrontEnd, IDisposable
{
    private readonly ModuleSession s;
    private readonly IBackEnd backEnd;
    private JsonObject? tokens;

    public string UserId { get; }

    public HfeUserAgent(IControllerApi api, IBackEnd backEnd, string userId, string workflowFile, string vault)
    {
        this.backEnd = backEnd; UserId = userId;
        s = new ModuleSession(api, workflowFile, vault,
            ("RegisterIn", "AIH-REG-V1.1"), ("RegisterAnswerIn", "AIH-REG-V1.1"), ("LearnIn", "AIH-FDL-V1.1"), ("RequestIn", "AIH-AHP-V1.1"), ("ResponseIn", "AIH-AHP-V1.1"),
            ("HealthIn", "AIH-HLD-V1.1"), ("LicenceIn", "AIH-MDL-V1.1"),
            ("RegisterOut", ""), ("LearnOut", ""), ("RequestOut", ""), ("ResponseOut", ""), ("AraOut", ""));
        s.Start();
    }

    public void Dispose() => s.Dispose();

    // The Tokens the Back End gave (null before the End User registered).
    public JsonObject? Tokens => tokens;

    // Registers with the Back End: the request goes out through the Front End, the Back End answers, and the answer goes in.
    public JsonObject Register(JsonObject request)
    {
        s.Drain();
        s.Send("RegisterIn", request.ToJsonString());
        var forwarded = s.Receive("RegisterOut") ?? throw new TimeoutException("The Front End sent no registration.");
        var answer = AihJson.Parse(backEnd.Register(forwarded), "Register");
        s.Send("RegisterAnswerIn", answer.ToJsonString());
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
        for (var wait = 6000; s.Receive("AraOut", wait) is { } json; wait = 1500)
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

    // Sends the AIH Data to the Back End to be kept: the Front End forwards the request, the Back End answers, and the answer goes in. Returns the Back End's answer.
    public JsonObject Upload(string aihDataId)
    {
        var data = Retrieve(aihDataId) ?? throw new InvalidOperationException($"No AIH Data '{aihDataId}' in the Secure Data Vault.");
        s.Drain();
        s.Send("RequestIn", Request("Store", [data]).ToJsonString());
        var forwarded = s.Receive("RequestOut") ?? throw new TimeoutException("The Front End sent no request.");
        var answer = backEnd.Process(forwarded);
        s.Send("ResponseIn", answer);
        var back = s.Receive("ResponseOut", 5000);                                  // the Front End gives the End User the Back End's answer
        return AihJson.Parse(back ?? answer, "AIH Data Process");
    }

    // The Back End asks for the model this Front End trained: the request goes into the Front End, the response comes out.
    public string Learn(string federatedLearnRequest)
    {
        s.Drain();
        s.Send("LearnIn", federatedLearnRequest);
        return s.Receive("LearnOut") ?? throw new TimeoutException("The Front End gave no model.");
    }

    // A request for the Back End to de-identify or anonymise an AIH Data of this End User.
    public JsonObject DeIdentify(string aihDataId, string type, string algorithm) =>
        AihJson.Parse(backEnd.DeIdentify(new JsonObject
        {
            ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = UserId,
            ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = aihDataId, ["DeIDAndAnonymType"] = type, ["DeIDAndAnonymAlgoID"] = algorithm, ["Tokens"] = (tokens ?? AihJson.TokensFor(UserId, [])).DeepClone() } })
        }.ToJsonString()), "De-Identification and Anonymisation");

    // A request for the Back End to audit the use of this End User's AIH Data.
    public JsonObject Audit() =>
        AihJson.Parse(backEnd.Audit(new JsonObject
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
        }.ToJsonString()), "Audit");

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
