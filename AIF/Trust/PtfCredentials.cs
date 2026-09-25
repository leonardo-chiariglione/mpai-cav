using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// CREDENTIALS (MPAI-PTF V1.0): the Instance Credential (PTF-ICR) by which an issuer
// binds a Cryptographic Instance Identity to an instance - an AIM Instance or a
// Process Instance - for a time, and the Process Lifecycle Credential (PTF-PLC) by
// which it states the instance's lifecycle state. Both signed by the issuer, whose
// KeyID they carry.
public sealed class PtfIssuer
{
    public const string CredentialHeader = "PTF-ICR-V1.0";
    public const string LifecycleHeader = "PTF-PLC-V1.0";

    public string Name { get; }
    public string KeyId { get; }
    private readonly ECDsa key;

    public PtfIssuer(string name, ECDsa key, string keyId)
    {
        Name = name;
        this.key = key;
        KeyId = keyId;
    }

    // instanceType: AIMInstance or ProcessInstance, as PTF has them; specification:
    // what the instance is an instance of (for an AIM, its AIM Type).
    public JsonObject IssueCredential(JsonObject cii, string instanceType, string instanceId, string? specification,
                                      DateTimeOffset now, TimeSpan lifetime)
    {
        var subject = new JsonObject { ["InstanceType"] = instanceType, ["InstanceID"] = instanceId };
        if (specification is { Length: > 0 }) subject["Specification"] = specification;
        var credential = new JsonObject
        {
            ["Header"] = CredentialHeader,
            ["InstanceCredentialID"] = $"{instanceId}#ICR-{Guid.NewGuid():N}",
            ["Subject"] = subject,
            ["CII"] = new JsonObject { ["HashAlgorithm"] = PtfIdentity.HashAlgorithm, ["Hash"] = PtfIdentity.Hash(cii) },
            ["Issuer"] = new JsonObject { ["Name"] = Name, ["KeyID"] = KeyId },
            ["Validity"] = new JsonObject { ["NotBefore"] = PtfIdentity.Time(now), ["NotAfter"] = PtfIdentity.Time(now + lifetime) }
        };
        return PtfSignature.Sign(credential, key, KeyId);
    }

    public JsonObject IssueLifecycle(string instanceId, string state, DateTimeOffset now, TimeSpan lifetime)
    {
        var lifecycle = new JsonObject
        {
            ["Header"] = LifecycleHeader,
            ["ProcessLifecycleCredentialID"] = $"{instanceId}#PLC-{Guid.NewGuid():N}",
            ["ProcessInstanceID"] = instanceId,
            ["LifecycleState"] = state,
            ["Issuer"] = new JsonObject { ["Name"] = Name, ["KeyID"] = KeyId },
            ["Validity"] = new JsonObject { ["NotBefore"] = PtfIdentity.Time(now), ["NotAfter"] = PtfIdentity.Time(now + lifetime) }
        };
        return PtfSignature.Sign(lifecycle, key, KeyId);
    }
}

// What a verifier checks of a credential, as the Verification Pipeline has it
// (MPAI-PTF V1.0, Trust Establishment Protocol 2): the issuer's signature, by a key
// the verifier trusts; the identity it is for; its validity at the time.
public static class PtfCredential
{
    public enum Outcome { Valid, Malformed, UnknownIssuer, InvalidSignature, CiiMismatch, NotYetValid, Expired, WrongState }

    public static Outcome CheckCredential(JsonObject credential, JsonObject cii, Func<string, ECDsa?> keys, DateTimeOffset now)
    {
        if ((string?)credential["Header"] != PtfIssuer.CredentialHeader) return Outcome.Malformed;
        var signed = Signed(credential, keys);
        if (signed != Outcome.Valid) return signed;
        if (!string.Equals((string?)credential["CII"]?["Hash"], PtfIdentity.Hash(cii), StringComparison.OrdinalIgnoreCase))
            return Outcome.CiiMismatch;
        return Current(credential, now);
    }

    // THE CHAIN: a verifier that trusts only an anchor trusts the key of an issuer
    // the anchor credentialed - here the Controller - once that credential checks.
    // The keys a verifier may then use, by KeyID; null where the issuer's credential
    // does not check.
    public static Func<string, ECDsa?>? Through(JsonObject issuerCredential, JsonObject issuerCii,
                                                  string anchorId, ECDsa anchorKey, DateTimeOffset now)
    {
        ECDsa? AnchorOnly(string id) => id == anchorId ? anchorKey : null;
        if (CheckCredential(issuerCredential, issuerCii, AnchorOnly, now) != Outcome.Valid) return null;
        if (PtfIdentity.Check(issuerCii, out var issuerKey) != PtfIdentity.Outcome.Valid) return null;
        var issuerId = (string?)issuerCredential["Subject"]?["InstanceID"];
        return id => id == anchorId ? anchorKey : id == issuerId ? issuerKey : null;
    }

    public static Outcome CheckLifecycle(JsonObject lifecycle, string instanceId, string expectedState,
                                         Func<string, ECDsa?> keys, DateTimeOffset now)
    {
        if ((string?)lifecycle["Header"] != PtfIssuer.LifecycleHeader || (string?)lifecycle["ProcessInstanceID"] != instanceId)
            return Outcome.Malformed;
        var signed = Signed(lifecycle, keys);
        if (signed != Outcome.Valid) return signed;
        if ((string?)lifecycle["LifecycleState"] != expectedState) return Outcome.WrongState;
        return Current(lifecycle, now);
    }

    private static Outcome Signed(JsonObject obj, Func<string, ECDsa?> keys) =>
        PtfSignature.Verify(obj, keys) switch
        {
            PtfSignature.Outcome.Valid => Outcome.Valid,
            PtfSignature.Outcome.UnknownKey => Outcome.UnknownIssuer,
            PtfSignature.Outcome.Missing or PtfSignature.Outcome.Malformed => Outcome.Malformed,
            _ => Outcome.InvalidSignature
        };

    private static Outcome Current(JsonObject obj, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse((string?)obj["Validity"]?["NotBefore"], out var notBefore) ||
            !DateTimeOffset.TryParse((string?)obj["Validity"]?["NotAfter"], out var notAfter))
            return Outcome.Malformed;
        if (now < notBefore) return Outcome.NotYetValid;
        if (now > notAfter) return Outcome.Expired;
        return Outcome.Valid;
    }
}
