using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Aih;

// The instances of the Data Types of AIH-HSP V1.1 are JSON objects (schemas/AIH1/V1.1/data); the AIMs read and write them as such.
public static class AihJson
{
    public const string AihData = "AIH-AHD-V1.1", AraData = "AIH-ARD-V1.1", Process = "AIH-AHP-V1.1", HealthData = "AIH-HLD-V1.1", ModelLicence = "AIH-MDL-V1.1",
                        Register = "AIH-REG-V1.1", Tokens = "AIH-TKN-V1.1", Blockchain = "AIH-BCL-V1.1", LicenceConfirm = "AIH-LCF-V1.1", FederatedLearn = "AIH-FDL-V1.1",
                        DeId = "AIH-DIA-V1.1", Audit = "AIH-ADT-V1.1";

    // The services a Token grants (ProcessTypes.json).
    public static readonly string[] Services = ["Store", "Auditing", "DeIDAndAnonym", "AIHDataProcessing", "LicenceChange"];

    public static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    // A Time (OSD-TIM-V1.5): the moment, in UTC.
    // A Simple Time (OSD-STM): an instant, in milliseconds - the Request Time of a Licence Confirm.
    public static JsonObject SimpleTime(DateTimeOffset? at = null)
    {
        var ms = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        return new()
        {
            ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = NewId("stm"),
            ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
        };
    }

    public static JsonObject Time(DateTimeOffset? at = null) => new()
    {
        ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = NewId("t"), ["Data"] = (at ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ")
    };

    public static JsonObject Parse(string json, string what) =>
        JsonNode.Parse(json) as JsonObject ?? throw new FormatException($"{what} is not a JSON object.");

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    // ---- AIH Data Process -------------------------------------------------------------------------------------------------------------------------------

    // A response of the AIH Data Process Data Type: the identifiers of the AIH Data it concerns and whether the process was done.
    public static JsonObject ProcessResponse(string userId, IEnumerable<string> aihDataIds, bool done) =>
        ProcessResponse(userId, Strings(aihDataIds), done);

    // The same, with items that are AIH Data or identifiers (JsonObject or string nodes).
    public static JsonObject ProcessResponse(string userId, JsonArray items, bool done) => new()
    {
        ["Header"] = Process, ["UserID"] = userId, ["AIHDataProcessID"] = NewId("ahp"), ["AIHDataProcessTime"] = Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataOrAIHDataID"] = items, ["ProcessingStatus"] = done } })
    };

    // The Request of an AIH Data Process (null if it holds none).
    public static JsonObject? RequestOf(JsonObject process) =>
        (process["AIHDataProcess"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null);

    public static JsonObject? ResponseOf(JsonObject process) =>
        (process["AIHDataProcess"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"] as JsonObject).FirstOrDefault(r => r is not null);

    // ---- Register and Tokens ----------------------------------------------------------------------------------------------------------------------------

    public static JsonObject TokensFor(string userId, IEnumerable<string> services) => new()
    {
        ["Header"] = Tokens, ["UserID"] = userId, ["TokensID"] = NewId("tk"), ["TokensTime"] = Time(),
        ["Tokens"] = new JsonArray(services.Select(s => (JsonNode)new JsonObject { ["ProcessType"] = s }).ToArray())
    };

    public static JsonObject RegisterResponse(string registerId, bool success, JsonObject? tokens) => new()
    {
        ["Header"] = Register, ["RegisterID"] = registerId, ["RegisterTime"] = Time(),
        ["RegisterData"] = new JsonArray(new JsonObject
        {
            ["Response"] = tokens is null ? new JsonObject { ["Success"] = success } : new JsonObject { ["Success"] = success, ["Tokens"] = tokens }
        })
    };

    public static JsonObject? RegisterRequestOf(JsonObject register) =>
        (register["RegisterData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null);

    public static JsonObject? RegisterResponseOf(JsonObject register) =>
        (register["RegisterData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"] as JsonObject).FirstOrDefault(r => r is not null);

    public static string[] ServicesOf(JsonObject? tokens) =>
        (tokens?["Tokens"] as JsonArray)?.OfType<JsonObject>().Select(t => t["ProcessType"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [];

    // ---- the Blockchain's Data Types ---------------------------------------------------------------------------------------------------------------------

    public static JsonObject BlockchainRequest(string userId, string aihDataId, JsonNode modelLicence) => new()
    {
        ["Header"] = Blockchain, ["UserID"] = userId, ["BlockchainLicenceID"] = NewId("bcr"), ["AIHDataID"] = aihDataId, ["BlockchainLicenceTime"] = Time(),
        ["BlockchainLicenceData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["ModelLicence"] = modelLicence.DeepClone() } })
    };

    public static JsonObject BlockchainResponse(string userId, string aihDataId, string smartContractId, bool approved) => new()
    {
        ["Header"] = Blockchain, ["UserID"] = userId, ["BlockchainLicenceID"] = NewId("bcp"), ["AIHDataID"] = aihDataId, ["BlockchainLicenceTime"] = Time(),
        ["BlockchainLicenceData"] = new JsonArray(new JsonObject
        {
            ["Response"] = new JsonObject { ["BlockchainLicenceID"] = smartContractId, ["LicenceStatus"] = approved, ["AIHDataID"] = aihDataId }
        })
    };

    // The Licence Confirm names the party it asks about (RequesterID): a licence of a Third-Party User names it.
    public static JsonObject LicenceConfirmRequest(string endUserId, string aihDataId, string processType, string requester) => new()
    {
        ["Header"] = LicenceConfirm, ["EndUserID"] = endUserId, ["LicenceConfirmID"] = NewId("lcr"), ["LicenceConfirmTime"] = Time(),
        ["LicenceConfirmData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = aihDataId, ["ProcessType"] = processType, ["RequesterID"] = requester, ["Time"] = SimpleTime() } })
    };

    public static JsonObject LicenceConfirmResponse(string endUserId, string confirmId, bool approved) => new()
    {
        ["Header"] = LicenceConfirm, ["EndUserID"] = endUserId, ["LicenceConfirmID"] = confirmId, ["LicenceConfirmTime"] = Time(),
        ["LicenceConfirmData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["Status"] = approved } })
    };
}
