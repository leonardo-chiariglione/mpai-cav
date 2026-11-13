using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;

namespace Mpai.Aih.Hdp;

// AIH DATA IN THE PRIVATE STORAGE OF THE MODULE (AIF V3.0 Storage 1-3; AIH-HSP: "keeps AIH Data in the Private Storage of the AIF, through the storage API of the AIF"). There is no
// Storage AIM: AIH Data Processing, which keeps AIH Data, uses the storage API directly - on a Front End in the Secure Data Vault of the End User, on the Back End in the Secure Storage
// (the location the User Agent gave: 'ask Controller to keep storage in'). Each AIH Data is written under the rules of a datum (STO-3): Category "AIH Data"; Readers none named,
// so that, in the Private Storage of the Module, AIH Data Processing - its writer - alone reads it (no other AIM of the Module, no User Agent, no other Module); Time none, so
// that it is kept until it is deleted. The Controller writes its Trace (writer, stamp, category, rule). There is no central control (StorageControl): the writer sets the rules.
//
// AIH Data is kept under its identifier, which is unique. Who may have it back is checked on the way out: its End User, and the Back End (its derived copies hold a pseudonym).
internal static class AihStore
{
    private const string Prefix = "aih/";
    public const string Category = "AIH Data";

    private static string Key(string id) => Prefix + Uri.EscapeDataString(id);

    // Keeps an AIH Data; returns the End User it is of.
    public static string Put(IRuledStorage storage, JsonObject data)
    {
        var user = data["UserID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data names no End User.");
        var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
        var done = storage.MPAI_AIFM_RuledStorage_Put(Key(id), Encoding.UTF8.GetBytes(data.ToJsonString()), Category);
        if (done != StorageOutcome.OK) throw new InvalidOperationException($"the AIH Data '{id}' was not kept: {done}.");
        return user;
    }

    // The AIH Data kept under an identifier; null where there is none.
    public static JsonObject? Get(IRuledStorage storage, string id) =>
        storage.MPAI_AIFM_RuledStorage_Get(Key(id), out var data) == StorageOutcome.OK ? JsonNode.Parse(Encoding.UTF8.GetString(data))?.AsObject() : null;

    // All the AIH Data kept (in the order of their identifiers).
    public static IEnumerable<JsonObject> All(IRuledStorage storage) =>
        storage.MPAI_AIFM_RuledStorage_List(Category, Prefix).Order(StringComparer.Ordinal)
               .Select(k => storage.MPAI_AIFM_RuledStorage_Get(k, out var data) == StorageOutcome.OK ? JsonNode.Parse(Encoding.UTF8.GetString(data))!.AsObject() : null).OfType<JsonObject>();

    // THE REGISTRATIONS (review finding A9, 2026-11-13): what AIH Data Processing knows of its users is kept as AIH Data is - in the Private Storage of the Module, Category
    // "Registration", no reader named (its writer alone reads it), no time - so that a Module started again knows who registered and what was granted. What is pending in a
    // run (an upload waiting for its Smart Contract, a processing for its Licence Confirm) is of that run (AIF V3.0 AIM-4) and is not kept.
    public const string RegistrationCategory = "Registration";
    private const string RegistrationPrefix = "reg/";

    public static void PutRegistration(IRuledStorage storage, string name, JsonObject record)
    {
        var done = storage.MPAI_AIFM_RuledStorage_Put(RegistrationPrefix + Uri.EscapeDataString(name), Encoding.UTF8.GetBytes(record.ToJsonString()), RegistrationCategory);
        if (done != StorageOutcome.OK) throw new InvalidOperationException($"the registration '{name}' was not kept: {done}.");
    }

    public static IEnumerable<JsonObject> Registrations(IRuledStorage storage) =>
        storage.MPAI_AIFM_RuledStorage_List(RegistrationCategory, RegistrationPrefix).Order(StringComparer.Ordinal)
               .Select(k => storage.MPAI_AIFM_RuledStorage_Get(k, out var data) == StorageOutcome.OK ? JsonNode.Parse(Encoding.UTF8.GetString(data))!.AsObject() : null).OfType<JsonObject>();

    // A request to retrieve: an AIH Data Process request of Process Type Store that names one AIH Data by its identifier. (One that holds AIH Data is an upload.)
    public static bool IsRetrieval(JsonObject process, out string user, out string id)
    {
        user = process["UserID"]?.GetValue<string>() ?? ""; id = "";
        var request = (process["AIHDataProcess"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null);
        if (request?["ProcessingType"]?.GetValue<string>() != "Store") return false;
        var items = (request["AIHDataOrAIHDataID"] as JsonArray)?.ToList() ?? [];
        if (items.Count != 1 || items[0] is not JsonValue value || !value.TryGetValue<string>(out var asked)) return false;
        id = asked;
        return true;
    }

    // The response to a retrieval: the AIH Data itself when it is kept and the requester may have it - its End User, and the Back End - else not done.
    public static JsonObject Answer(IRuledStorage storage, string user, string id, string backEndId, string? processId = null)
    {
        var kept = Get(storage, id);
        var owner = kept?["UserID"]?.GetValue<string>() ?? "";
        return kept is not null && (user == owner || (backEndId.Length > 0 && user == backEndId))
            ? AihJson.ProcessResponse(user, new JsonArray(kept), true, processId)
            : AihJson.ProcessResponse(user, new[] { id }, false, processId);
    }
}
