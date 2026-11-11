using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// ATTESTATION EVIDENCE (MPAI-PTF V1.0, PTF-ATE) of what an AIM Instance runs
// (M3223 3.2): an item for the binary that implements it (AIF-EVID-CODE-HASH) and
// one for each model its settings name (AIF-EVID-CONFIG-HASH - the Security Evidence
// Taxonomy has no type for a model), each with the SHA-256 of what was measured and
// what it was, and the party that measured it. Signed by that party: the Controller
// for an AIM it runs, the AIM's own key for an AIM measured on its host.
public static class PtfEvidence
{
    public const string Header = "PTF-ATE-V1.0";
    public const string CodeHash = "AIF-EVID-CODE-HASH";
    public const string ConfigHash = "AIF-EVID-CONFIG-HASH";

    // One measurement: of what (a binary's name, a model's setting and file), its hash.
    public sealed record Item(string Type, string What, string Hash);

    public static JsonObject Make(string instanceId, IEnumerable<Item> items, string verifier, ECDsa key, string keyId)
    {
        var evidence = new JsonObject
        {
            ["Header"] = Header,
            ["AttestationEvidenceID"] = $"{instanceId}#ATE-{Guid.NewGuid():N}",
            ["EvidenceItems"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
            {
                ["Type"] = i.Type,
                ["Value"] = Hex(i.What),
                ["Verifier"] = verifier,
                ["HashAlgorithm"] = PtfIdentity.HashAlgorithm,
                ["HashValue"] = i.Hash
            }).ToArray())
        };
        return PtfSignature.Sign(evidence, key, keyId);
    }

    public static IReadOnlyList<Item> Items(JsonObject evidence) =>
        (evidence["EvidenceItems"] as JsonArray ?? [])
            .Select(i => new Item((string?)i?["Type"] ?? "", FromHex((string?)i?["Value"] ?? ""), (string?)i?["HashValue"] ?? ""))
            .ToList();

    // The payload of an item, in Hexadecimal ASCII as PTF Data Conventions require for binary values (sections 1 and 3; the author corrected
    // section 2, which said Base64URL, 2026-11-11): here the name of what was measured.
    private static string Hex(string s) => Convert.ToHexString(Encoding.UTF8.GetBytes(s));

    private static string FromHex(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromHexString(s)); }
        catch (FormatException) { return ""; }
    }
}
