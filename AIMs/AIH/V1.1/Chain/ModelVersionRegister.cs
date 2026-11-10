using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Aih.Chain;

// THE NN MODELS OF THE ROUNDS: LICENCE AND AUDIT (federated learning, step 6).
//
// Every NN Model a round of federated learning makes is a VERSION. The Back End registers it: the round that made it, the model it started from, how many updates and cases it
// took, which AIH Data fed it (the AIHDataID of each accepted update), and a Model Licence - made on the Blockchain like the licence of AIH Data, with the version's identifier
// where an AIH Data identifier would be - that says who may use the version. The Blockchain answers, when asked, whether a requester may use a version (the Back End, which
// owns it, and the End Users or Third-Party Users the licence names) and whether it may audit it (only the owner). An audit of a version lists its round, its sources and its
// licence, and the Blockchain's ledger entries about it.
//
// The Model Licence schema (AIH-MDL-V1.1) is written for AIH Data: it is used for a model version by naming the Back End as EndUserID (the owner) and the version as AIHDataID.
public sealed class ModelVersionRegister
{
    public sealed record Version(string ModelId, int Round, string? StartedFrom, int Updates, int Cases, string[] DataIds, string ContractId, string[] Users);

    private readonly IBlockchain _chain;
    private readonly string _backEndId;
    private readonly Dictionary<string, Version> _versions = new(StringComparer.Ordinal);
    private readonly List<string> _pendingData = new();

    public ModelVersionRegister(IBlockchain chain, string backEndId) { _chain = chain; _backEndId = backEndId; }

    public IReadOnlyCollection<Version> Versions => _versions.Values;

    // Notes the AIH Data a Federated Learn Response says the model was trained on; they belong to the version the round makes next.
    public void NoteResponse(JsonObject federatedLearn)
    {
        var response = (federatedLearn["FederatedLearnData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"] as JsonObject).FirstOrDefault(r => r is not null);
        if (response?["AIHDataID"]?.GetValue<string>() is { Length: > 0 } id) _pendingData.Add(id);
    }

    // Registers the version a round made, with the Model Licence that names who may use it. Returns the version, or null if the Blockchain refused the licence.
    public Version? Register(string modelId, int round, string? startedFrom, int updates, int cases, IEnumerable<string> usersAllowed)
    {
        var users = usersAllowed.Distinct().ToArray();
        var licence = LicenceOf(modelId, users);
        var contract = _chain.Make(licence, modelId);
        if (contract is null) return null;
        var version = new Version(modelId, round, startedFrom, updates, cases, _pendingData.ToArray(), contract, users);
        _pendingData.Clear();
        _versions[modelId] = version;
        return version;
    }

    // The licence is changed (a new Smart Contract replaces the old one, as for AIH Data): who may use the version now.
    public bool ChangeLicence(string modelId, IEnumerable<string> usersAllowed)
    {
        if (!_versions.TryGetValue(modelId, out var v)) return false;
        var users = usersAllowed.Distinct().ToArray();
        var contract = _chain.Make(LicenceOf(modelId, users), modelId);
        if (contract is null) return false;
        _versions[modelId] = v with { ContractId = contract, Users = users };
        return true;
    }

    // May the requester use that version? The Blockchain answers (and the question is in its ledger).
    public bool MayUse(string modelId, string requester) => _versions.ContainsKey(modelId) && _chain.Confirm(modelId, "AIHDataProcessing", requester);

    // The audit of a version, for a requester; null if the Blockchain does not allow the requester to audit it.
    public JsonObject? Audit(string modelId, string requester)
    {
        if (!_versions.TryGetValue(modelId, out var v) || !_chain.Confirm(modelId, "Auditing", requester)) return null;
        return new JsonObject
        {
            ["ModelId"] = v.ModelId, ["Round"] = v.Round, ["StartedFrom"] = v.StartedFrom, ["Updates"] = v.Updates, ["Cases"] = v.Cases,
            ["DataIds"] = new JsonArray(v.DataIds.Select(d => (JsonNode)d).ToArray()),
            ["Licence"] = new JsonObject
            {
                ["Owner"] = _backEndId, ["SmartContractID"] = v.ContractId, ["MayUse"] = new JsonArray(v.Users.Select(u => (JsonNode)u).ToArray())
            },
            ["Ledger"] = new JsonArray(_chain.Ledger.Where(e => e.AihDataId == modelId)
                .Select(e => (JsonNode)new JsonObject { ["Index"] = e.Index, ["Kind"] = e.Kind, ["Requester"] = e.Requester, ["Detail"] = e.Detail, ["Allowed"] = e.Allowed, ["Hash"] = e.Hash }).ToArray())
        };
    }

    private JsonObject LicenceOf(string modelId, string[] users) => new()
    {
        ["Header"] = "AIH-MDL-V1.1", ["ModelLicenceID"] = "ml-" + modelId, ["EndUserID"] = _backEndId, ["AIHDataID"] = modelId, ["AIHDataType"] = "NNModel",
        ["ModelLicenceData"] = new JsonObject
        {
            ["Common"] = new JsonObject { ["EndUserID"] = _backEndId, ["AIHDataID"] = modelId, ["AIHDataUsage"] = "Research", ["AIHDataProcessingType"] = "FederatedLearning" },
            ["Specific"] = new JsonArray(new JsonObject[] { new() { ["BackEndLicence"] = new JsonObject { ["EndUserID"] = _backEndId, ["BackEndID"] = _backEndId, ["AIHDataID"] = modelId, ["AIHDataStatus"] = "Identified" } } }
                .Concat(users.Select(u => new JsonObject
            {
                ["ThirdPartyUserLicence"] = new JsonObject { ["EndUserID"] = _backEndId, ["ThirdPartyUserID"] = u, ["AIHDataID"] = modelId }
            })).Select(o => (JsonNode)o).ToArray())
        }
    };
}
