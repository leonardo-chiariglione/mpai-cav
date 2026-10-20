using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mpai.Aih.Chain;

// A BLOCKCHAIN FOR TESTS: a ledger in memory, each entry holding the hash of the one before, so that a change to what happened shows (Verify). The Smart Contract
// is the Model Licence itself, read as the rules it states:
//   - the End User who owns the AIH Data is not bound by the licence: all is allowed to them;
//   - the Back End (named by the Back End Licence) may store the AIH Data, and de-identify or anonymise it where the licence states a status other than Identified;
//   - a Third-Party User may have the AIH Data processed if a Third-Party User Licence of this AIH Data names it;
//   - auditing and changing the licence are the End User's.
public sealed class HashChainBlockchain : IBlockchain
{
    private sealed record Contract(string Id, string AihDataId, string Owner, string? BackEnd, string[] BackEndStatus, HashSet<string> ThirdParties);

    private readonly object gate = new();
    private readonly List<LedgerEntry> ledger = new();
    private readonly Dictionary<string, Contract> contracts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> clock;

    public HashChainBlockchain(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<LedgerEntry> Ledger { get { lock (gate) return ledger.ToList(); } }

    public string? ContractOf(string aihDataId) { lock (gate) return contracts.GetValueOrDefault(aihDataId)?.Id; }

    public string? Make(JsonObject modelLicence, string aihDataId)
    {
        var owner = modelLicence["EndUserID"]?.GetValue<string>() ?? "";
        var specific = (modelLicence["ModelLicenceData"]?["Specific"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var backEnd = specific.Select(s => s["BackEndLicence"] as JsonObject).FirstOrDefault(b => b is not null);
        var third = specific.Select(s => s["ThirdPartyUserLicence"] as JsonObject).Where(t => t is not null).Select(t => t!["ThirdPartyUserID"]?.GetValue<string>() ?? "").Where(i => i.Length > 0);
        if (owner.Length == 0 || (modelLicence["AIHDataID"]?.GetValue<string>() ?? aihDataId) != aihDataId || specific.Count == 0) return null;

        lock (gate)
        {
            var entry = Append("Contract", aihDataId, owner, "", true);
            var contract = new Contract("sc-" + entry.Hash[..12], aihDataId, owner, backEnd?["BackEndID"]?.GetValue<string>(),
                backEnd?["AIHDataStatus"] is JsonValue status ? [status.GetValue<string>()] : [], third.ToHashSet(StringComparer.Ordinal));
            contracts[aihDataId] = contract;                                           // a new licence replaces the one before: it is the licence change
            return contract.Id;
        }
    }

    public bool Confirm(string aihDataId, string processType, string requester)
    {
        lock (gate)
        {
            var allowed = contracts.TryGetValue(aihDataId, out var c) && Allows(c, processType, requester);
            Append("Confirm", aihDataId, requester, processType, allowed);
            return allowed;
        }
    }

    private static bool Allows(Contract c, string processType, string requester)
    {
        if (requester == c.Owner) return true;
        return processType switch
        {
            "Store" => c.BackEnd is not null && requester == c.BackEnd,
            "DeIDAndAnonym" => c.BackEnd is not null && requester == c.BackEnd && c.BackEndStatus.Any(s => s is "Anonymised" or "Pseudonymised"),
            "AIHDataProcessing" => c.ThirdParties.Contains(requester) || (c.BackEnd is not null && requester == c.BackEnd),
            _ => false                                                                    // auditing and changing the licence: the End User's
        };
    }

    private LedgerEntry Append(string kind, string aihDataId, string requester, string detail, bool allowed)
    {
        var previous = ledger.Count == 0 ? new string('0', 64) : ledger[^1].Hash;
        var time = clock().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var entry = new LedgerEntry(ledger.Count, kind, aihDataId, requester, detail, allowed, time, previous, "");
        entry = entry with { Hash = HashOf(entry) };
        ledger.Add(entry);
        return entry;
    }

    private static string HashOf(LedgerEntry e) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", e.Index, e.Kind, e.AihDataId, e.Requester, e.Detail, e.Allowed, e.Time, e.PreviousHash)))).ToLowerInvariant();

    // The chain holds together: every entry's hash is that of its content, and joins to the one before.
    public bool Verify()
    {
        lock (gate)
        {
            for (var i = 0; i < ledger.Count; i++)
            {
                var previous = i == 0 ? new string('0', 64) : ledger[i - 1].Hash;
                if (ledger[i].PreviousHash != previous || ledger[i].Hash != HashOf(ledger[i])) return false;
            }
            return true;
        }
    }


}
