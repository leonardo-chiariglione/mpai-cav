using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;

namespace Mpai.Aih.Hdp;

// AIH DATA IN THE MODULE'S STORAGE. There is no Storage AIM: Private Storage is part of the AIF infrastructure, and AIH Data Processing, which keeps AIH Data, uses its API
// (MPAI_AIFM_SharedStorage_Put, _Get, _Exists, _List) directly - on a Front End the Secure Data Vault of the End User, on the Back End the Secure Storage.
//
// AIH Data is kept under its identifier, which is unique. Who may have it back is checked on the way out: its End User, and the Back End (its derived copies hold a pseudonym).
internal static class AihStore
{
    private const string Prefix = "aih/";

    private static string Key(string id) => Prefix + Uri.EscapeDataString(id);

    // Keeps an AIH Data; returns the End User it is of.
    public static string Put(ISharedStorage storage, JsonObject data)
    {
        var user = data["UserID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data names no End User.");
        var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
        storage.MPAI_AIFM_SharedStorage_Put(Key(id), Encoding.UTF8.GetBytes(data.ToJsonString()));
        return user;
    }

    // The AIH Data kept under an identifier; null where there is none.
    public static JsonObject? Get(ISharedStorage storage, string id) =>
        storage.MPAI_AIFM_SharedStorage_Exists(Key(id)) ? JsonNode.Parse(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(Key(id))))?.AsObject() : null;

    // All the AIH Data kept (in the order of their identifiers).
    public static IEnumerable<JsonObject> All(ISharedStorage storage) =>
        storage.MPAI_AIFM_SharedStorage_List(Prefix).Select(k => JsonNode.Parse(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(k)))!.AsObject());

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
    public static JsonObject Answer(ISharedStorage storage, string user, string id, string backEndId)
    {
        var kept = Get(storage, id);
        var owner = kept?["UserID"]?.GetValue<string>() ?? "";
        return kept is not null && (user == owner || (backEndId.Length > 0 && user == backEndId))
            ? AihJson.ProcessResponse(user, new JsonArray(kept), true)
            : AihJson.ProcessResponse(user, new[] { id }, false);
    }
}
