using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Trust;

namespace AIF.Bout;

// THE INSTANCE CREDENTIALS A CONTROLLER ISSUES SO THAT A USER AGENT OR A REMOTE CLIENT APPLICATION CAN GIVE BOUTS on boundary Input Ports
// (MPAI-AIF V3.0, Basic API 3.1.3, COM-21; MPAI-MAS V1.1, API 2.1). The proof is the Cryptographic Instance Identity (PTF-CII) of the party, whose
// CryptographicInstanceTime is fresh: its Integrity, a Signature made with the key it names, shows that the party holds the key. The Controller, a
// Trust Anchor, then issues an Instance Credential (IssueCredential), valid until the Controller is destroyed, when every credential it issued is
// revoked and the Bouts given with it are closed.
public sealed class BoutCredentials
{
    // What the party is an instance of (Subject.Specification). Its InstanceType is ProcessInstance, the type PTF has for what is not an AIM Instance.
    public const string UserAgent = "MPAI-AIF User Agent";
    public const string RemoteClientApplication = "MPAI-MAS Remote Client Application";

    private readonly PtfIssuer issuer;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan freshness;
    private readonly ConcurrentDictionary<string, (BoutKey Key, JsonObject Credential)> issued = new(StringComparer.Ordinal);

    public BoutCredentials(PtfIssuer issuer, Func<DateTimeOffset>? clock = null, TimeSpan? freshness = null)
    {
        this.issuer = issuer;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.freshness = freshness ?? TimeSpan.FromMinutes(5);
    }

    // Called on revocation, with the identifier of the party: the Controller closes the Bouts given with that credential.
    public event Action<string>? Revoked;

    // The credential, or the reason the proof does not verify (MPAI_AIF_NOT_TRUSTED; 401 in MPAI-MAS).
    public (JsonObject? Credential, string? Reason) Issue(JsonObject cii, string specification)
    {
        if (PtfIdentity.Check(cii, out var key) != PtfIdentity.Outcome.Valid || key is null) return (null, "the Cryptographic Instance Identity does not verify");
        if ((string?)cii["CryptographicInstanceTime"] is not { } t ||
            !DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            return (null, "the Cryptographic Instance Identity states no CryptographicInstanceTime");
        var now = clock();
        if ((now - time).Duration() > freshness) return (null, "the CryptographicInstanceTime is not fresh");
        if ((string?)cii["CryptographicInstanceID"] is not { Length: > 0 } id) return (null, "the Cryptographic Instance Identity has no CryptographicInstanceID");
        var credential = issuer.IssueCredential(cii, "ProcessInstance", id, specification, now, TimeSpan.FromDays(3650));
        issued[id] = (key, credential);
        return (credential, null);
    }

    // The key of a party holding a credential not revoked: the KeyID of what it signs is its CryptographicInstanceID.
    public BoutKey? KeyFor(string keyId) => issued.TryGetValue(keyId, out var entry) ? entry.Key : null;

    // AcceptBout check 3 for a boundary Input Port: the signer holds an Instance Credential this Controller issued and has not revoked.
    public string? MayGive(JsonObject bout) =>
        BoutSecurity.KeyIdOf(bout) is { } keyId && issued.ContainsKey(keyId) ? null : "the signer holds no valid Instance Credential of this Controller";

    public bool Revoke(string keyId)
    {
        if (!issued.TryRemove(keyId, out _)) return false;
        Revoked?.Invoke(keyId);
        return true;
    }

    // When the Controller is destroyed (MPAI_AIFU_Controller_Destroy; DELETE of the Controller Instance in MPAI-MAS).
    public void RevokeAll()
    {
        foreach (var keyId in issued.Keys.ToList()) Revoke(keyId);
    }

    // The proof a User Agent or a client gives: its Cryptographic Instance Identity (PTF-CII), made now, signed with its key - as PtfIdentity makes
    // it, with a BoutKey, so that a browser, which has no ECDSA of its platform, makes the same.
    public static JsonObject Proof(BoutKey key, string instanceId, DateTimeOffset now)
    {
        var pending = ProofAsync(key, instanceId, now);
        return pending.IsCompleted ? pending.GetAwaiter().GetResult() : throw new InvalidOperationException("This key signs asynchronously: use ProofAsync.");
    }

    public static async ValueTask<JsonObject> ProofAsync(BoutKey key, string instanceId, DateTimeOffset now)
    {
        var spki = key.Spki();
        var cii = new JsonObject
        {
            ["Header"] = PtfIdentity.Header,
            ["CryptographicInstanceID"] = instanceId,
            ["CryptographicInstanceTime"] = PtfIdentity.Time(now),
            ["CryptographicBinding"] = new JsonObject
            {
                ["PublicKey"] = new JsonObject { ["Algorithm"] = PtfIdentity.SignatureAlgorithm, ["KeyEncoding"] = "spki", ["KeyValue"] = Convert.ToHexString(spki) },
                ["KeyDerivation"] = new JsonObject { ["Method"] = "direct" }
            },
            ["InstanceAttributes"] = new JsonObject { ["InstanceType"] = "software" },
            [PtfIdentity.Integrity] = new JsonObject
            {
                ["Fingerprint"] = new JsonObject { ["Algorithm"] = PtfIdentity.HashAlgorithm, ["Value"] = Convert.ToHexString(SHA256.HashData(spki)) },
                ["KeyID"] = instanceId
            }
        };
        cii[PtfIdentity.Integrity]!["Signature"] = Convert.ToHexString(await key.SignAsync(PtfCanonical.Bytes(cii)));
        return cii;
    }

    public static JsonObject Proof(ECDsa key, string instanceId, DateTimeOffset now) => Proof((BoutKey)key, instanceId, now);
}
