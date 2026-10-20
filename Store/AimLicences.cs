using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIF.Trust;

namespace AIF.Store;

// THE AIM LICENCE (AIF-LIC-V3.0, schemas/AIF/V3.0/data/AIMLicence.json) - issued by the MPAI Store and checked by a Controller.
//
// TWO LICENCES. The Implementer's OFFER is the AIMLicence field of the AIM Metadata: the Implementer's licence to the MPAI Store, which says what the Store may grant and, in
// Sublicensing, that it may sublicense. The AIM Licence issued here is the MPAI Store's SUBLICENCE, within the offer, and names the offer it is under (ParentLicence). When a Controller obtains the AIM the MPAI Store completes the offer for its licensee - the holder of a
// PTF Trust Anchor - the AIM and the files it covers with the fingerprints the Store approved, the Term from now, where revocation is asked and how long a status may go
// unconfirmed, the proof of payment - and signs it as MPAI-PTF signs its objects (PtfSignature: the canonical form PTF-JSON-CANON-V1 without the Signature, by the key the KeyID
// names). Every Controller under the licensee's Trust Anchor is covered. The Controller keeps the AIM Licence with the AIM and checks it before it builds the AIM.
public static class AimLicences
{
    public const string Header = "AIF-LIC-V3.0", Sha256 = "PTF-ALGO-HASH-SHA256";

    // What the MPAI Store knows of itself when it issues: its key, the identifier of the key and of the Store, and how long a status may go unconfirmed (the MaxAge it writes).
    // MarkupPercent: the MPAI Store's mark-up on the price the Implementer proposed (the rule is the Store's, outside AIF V3.0; 0 here means none).
    public sealed record Issuer(ECDsa Key, string KeyId, string StoreId, string? StatusMaxAge = null, string StatusBase = "https://store.example/MPAI/Store/Licence", decimal MarkupPercent = 0);

    // WHAT A CONTROLLER PROVES IT IS: its Instance Credential and the Cryptographic Instance Identity it is for, the links of its credential chain, and the keys of the Trust
    // Anchors it trusts (by the AnchorID). ControllerId is its identity, as in the credential.
    public sealed record ControllerIdentity(string ControllerId, JsonObject Credential, JsonObject Cii, IReadOnlyList<PtfCredential.Link> Chain, Func<string, ECDsa?> Anchors);

    // THE MPAI STORE ISSUES the offer of an AIM to a licensee: the AIM Licence it keeps and shows. controllerId: where only one Controller of the Trust Anchor is licensed.
    public static JsonObject Issue(JsonObject offer, string aimName, string version, IReadOnlyDictionary<string, string> approved, string trustAnchorId, string? controllerId,
                                   Issuer issuer, DateTimeOffset now, string implementerId)
    {
        // The MPAI Store sublicenses only what the Implementer's licence to it lets it sublicense.
        if (offer["Sublicensing"]?["Allowed"] is not JsonValue allowed || !allowed.TryGetValue<bool>(out var may) || !may)
            throw new InvalidOperationException($"{aimName}: the Implementer's offer does not allow the MPAI Store to sublicense the AIM.");
        var licence = offer.DeepClone().AsObject();
        licence["ParentLicence"] = new JsonObject { ["ImplementerID"] = implementerId, ["HashAlgorithm"] = Sha256, ["OfferHash"] = OfferHash(offer) };
        var id = "lic-" + Guid.NewGuid().ToString("N")[..12];
        licence["AIMLicenceID"] = id;
        licence["AIMLicenceTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id + "-t", ["Data"] = Text(now) };
        licence["AIM"] = new JsonObject
        {
            ["AIMName"] = aimName, ["Versions"] = new JsonObject { ["From"] = version },
            ["Implementations"] = new JsonArray(approved.Select(a => (JsonNode)new JsonObject { ["BinaryName"] = a.Key, ["HashAlgorithm"] = Sha256, ["Hash"] = a.Value.ToUpperInvariant() }).ToArray())
        };
        licence["Licensor"] = new JsonObject { ["LicensorID"] = issuer.StoreId };       // the MPAI Store grants; the Implementer's relation to it is outside the AIM Licence
        var licensee = new JsonObject { ["TrustAnchorID"] = trustAnchorId };
        if (controllerId is not null) licensee["ControllerID"] = controllerId;
        licence["Licensee"] = licensee;
        var term = new JsonObject { ["NotBefore"] = Text(now) };
        if ((string?)offer["Term"]?["Duration"] is { } duration) term["NotAfter"] = Text(Add(now, duration));
        licence["Term"] = term;
        // What is priced is paid for: the Service Pricing Model offered becomes Final, with the model chosen and the Settlement - the Transaction that is the proof of acquisition.
        // (The payment itself is outside the AIM Licence; the Store records what was settled.)
        if (licence["Payment"] is JsonObject payment)
        {
            Markup(payment, issuer.MarkupPercent);                                       // the price the Implementer proposed, with the MPAI Store's mark-up
            var transaction = "tx-" + Guid.NewGuid().ToString("N")[..10];
            payment["SPMStatus"] = "Final";
            payment["Chosen"] = new JsonObject { ["ModelType"] = payment["ModelType"]!.DeepClone(), ["Parameters"] = new JsonObject() };
            payment["Settlement"] = new JsonObject
            {
                ["SettlementTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = transaction + "-t", ["Data"] = Text(now) },
                ["TargetID"] = aimName,
                ["Transaction"] = new JsonObject
                {
                    ["Header"] = "MMM-TRA-V2.2", ["MInstanceID"] = "mpai-store", ["MEnvironmentID"] = "mpai-store", ["TransactionID"] = transaction,
                    ["SenderData"] = new JsonObject { ["SenderID"] = trustAnchorId, ["SenderWalletID"] = "wallet:" + trustAnchorId },
                    ["ReceiverData"] = new JsonObject { ["ReceiverID"] = issuer.StoreId, ["ReceiverWalletID"] = "wallet:" + issuer.StoreId },
                    ["TransactionStatus"] = "Final"
                }
            };
        }
        var status = new JsonObject { ["StatusURI"] = $"{issuer.StatusBase}/{id}/status" };
        if (issuer.StatusMaxAge is not null) status["MaxAge"] = issuer.StatusMaxAge;
        licence["Status"] = status;
        PtfSignature.Sign(licence, issuer.Key, issuer.KeyId);
        return licence;
    }

    // THE CONTROLLER CHECKS an AIM Licence before it builds the AIM: what is wrong with it, none when it is good. held: the files the Controller holds of the AIM, with the
    // fingerprints the MPAI Store approved; storeKey: the public key a KeyID names; revoked: whether the MPAI Store says it is revoked, null when it could not be asked;
    // lastStatusCheck: when the MPAI Store last confirmed that it is not (the time it was issued, if never asked since).
    public static IReadOnlyList<string> Check(JsonObject licence, ControllerIdentity identity, string aimName, IReadOnlyDictionary<string, string> held,
                                              Func<string, ECDsa?> storeKey, DateTimeOffset now, bool? revoked = null, bool keeping = false, DateTimeOffset? lastStatusCheck = null, bool executesApart = false, JsonObject? offer = null, string? implementerId = null)
    {
        var found = new List<string>();
        if ((string?)licence["Header"] != Header) found.Add($"Header {licence["Header"]}: not {Header}.");
        var outcome = PtfSignature.Verify(licence, storeKey);
        if (outcome != PtfSignature.Outcome.Valid) found.Add($"the signature is not the MPAI Store's ({outcome}).");
        if ((string?)licence["AIM"]?["AIMName"] != aimName) found.Add($"it is for {licence["AIM"]?["AIMName"]}, not {aimName}.");

        // It is a sublicence: it is under the Implementer's offer in the Metadata the Controller holds (the Implementer, and the hash of the offer).
        if (licence["ParentLicence"] is not JsonObject parent) found.Add("it names no Implementer's offer it is under (ParentLicence).");
        else
        {
            if (offer is not null && !string.Equals((string?)parent["OfferHash"], OfferHash(offer), StringComparison.OrdinalIgnoreCase)) found.Add("it is not under the offer in the AIM Metadata (other OfferHash).");
            if (implementerId is not null && (string?)parent["ImplementerID"] != implementerId) found.Add($"it is under the offer of {parent["ImplementerID"]}, not of {implementerId}.");
        }

        // The licensee is the holder of a Trust Anchor: this Controller is covered if its Instance Credential chains to that Trust Anchor.
        var anchorId = (string?)licence["Licensee"]?["TrustAnchorID"] ?? "";
        var chain = PtfCredential.CheckChain(identity.Credential, identity.Cii, identity.Chain, id => id == anchorId ? identity.Anchors(id) : null, now);
        // The identity here is that of what controls the AIM's execution - a Controller, or an AIM that executes apart from it. Under the licensee's Trust Anchor, the execution is the
        // licensee's and the right to Run covers it; under another's, the licence has to grant that the execution may be under the control of another party.
        if (executesApart && chain != PtfCredential.Outcome.Valid)
        {
            if (!(licence["Grant"]?["Rights"] as JsonArray ?? []).Any(r => (string?)r == "RunUnderOtherControl"))
                found.Add($"the AIM would execute under the control of {identity.ControllerId}, which is not under the Trust Anchor {anchorId} ({chain}), and the licence does not grant the right to run the AIM under the control of another party.");
        }
        else if (chain != PtfCredential.Outcome.Valid) found.Add($"this Controller ({identity.ControllerId}) is not under the Trust Anchor {anchorId} ({chain}).");
        if (!executesApart && (string?)licence["Licensee"]?["ControllerID"] is { } only && only != identity.ControllerId) found.Add($"it covers only the Controller {only}, not this Controller ({identity.ControllerId}).");

        var covered = (licence["AIM"]?["Implementations"] as JsonArray ?? []).OfType<JsonObject>()
                        .ToDictionary(i => (string)i["BinaryName"]!, i => ((string)i["Hash"]!).ToUpperInvariant(), StringComparer.Ordinal);
        foreach (var (file, hash) in held)
            if (!covered.TryGetValue(file, out var licensed)) found.Add($"{file} is not covered by it.");
            else if (!string.Equals(licensed, hash, StringComparison.OrdinalIgnoreCase)) found.Add($"{file} is not the file it covers (other fingerprint).");

        if (DateTimeOffset.TryParse((string?)licence["Term"]?["NotBefore"], out var from) && now < from) found.Add($"its Term begins at {from:O}.");
        if (licence["Term"]?["NotAfter"] is { } to && DateTimeOffset.TryParse((string?)to, out var until) && now >= until) found.Add($"its Term ended at {until:O}.");

        var rights = (licence["Grant"]?["Rights"] as JsonArray ?? []).Select(r => (string?)r).ToList();
        if (!rights.Contains("Run")) found.Add("it does not grant the right to run the AIM.");
        if (keeping && !rights.Contains("Keep")) found.Add("it does not grant the right to keep the AIM in a repository.");

        if (revoked == true) found.Add("it is revoked.");

        // A status goes unconfirmed no longer than the licence allows (Status.MaxAge): an offline Controller goes on until then, and asks after.
        if ((string?)licence["Status"]?["MaxAge"] is { } maxAge)
        {
            var since = lastStatusCheck ?? (DateTimeOffset.TryParse((string?)licence["AIMLicenceTime"]?["Data"], out var issued) ? issued : (DateTimeOffset?)null);
            if (since is { } confirmed && now > Add(confirmed, maxAge))
                found.Add($"its status was last confirmed at {confirmed:O}, more than {maxAge} ago: the MPAI Store has to be asked.");
        }
        return found;
    }

    // The hash of the Implementer's offer: the PTF-JSON-CANON-V1 form of the AIMLicence field, SHA-256, in capitals.
    public static string OfferHash(JsonObject offer) => Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(offer)));

    // The MPAI Store's mark-up on the price the Implementer proposed, where the pricing model has a price: percent of the amount, to the cent.
    private static void Markup(JsonObject payment, decimal percent)
    {
        if (percent == 0) return;
        void Raise(JsonObject? holder, string name)
        {
            if (holder?[name] is JsonValue v && v.TryGetValue<double>(out var amount)) holder[name] = (double)Math.Round((decimal)amount * (1 + percent / 100m), 2, MidpointRounding.AwayFromZero);
        }
        Raise(payment["OneTime"] as JsonObject, "Price");
        Raise(payment["Subscription"] as JsonObject, "RecurringPrice");
        Raise(payment["PayPerUse"] as JsonObject, "Rate");
        Raise(payment["PayPerTime"] as JsonObject, "Price");
    }

    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // An ISO 8601 duration added to a time: years and months by the calendar.
    public static DateTimeOffset Add(DateTimeOffset at, string duration)
    {
        var m = Regex.Match(duration, @"^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) throw new FormatException($"{duration}: not an ISO 8601 duration.");
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        return at.AddYears(N(1)).AddMonths(N(2)).AddDays(7 * N(3) + N(4)).AddHours(N(5)).AddMinutes(N(6)).AddSeconds(N(7));
    }
}
