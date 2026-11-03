using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Trust;

namespace Mpai.StoreService;

// WHO ASKS FOR AN AIM LICENCE, checked. The request carries, under Caller, the Cryptographic Instance Identity of the asker, its Instance Credential and the links of its credential
// chain, and is signed with the key of that identity (PtfSignature, over the whole request) at a Time not older than a few minutes. The Store accepts it if the signature is the identity's,
// the credential is valid, current and for that identity, and its chain ends at the Trust Anchor the request asks the licence for - a Trust Anchor the Store recognises. Where the
// request names a Controller, the credential is that Controller's.
public static class LicenceCaller
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    // What is wrong with the caller of this request, null when it is authenticated.
    public static string? Check(JsonObject request, TrustAnchorRegistry anchors, DateTimeOffset now)
    {
        if (request["Caller"] is not JsonObject caller) return "The request is not authenticated: it carries no Caller (Cii, Credential, Chain) and no signature.";
        if (caller["Cii"] is not JsonObject cii || caller["Credential"] is not JsonObject credential) return "The Caller needs its Cii and its Credential.";
        var key = KeyOf(cii);
        if (key is null) return "The Cii of the Caller has no readable key.";
        if (PtfSignature.Verify(request, _ => key) != PtfSignature.Outcome.Valid) return "The signature of the request is not that of the Cii of the Caller.";
        if (!DateTimeOffset.TryParse((string?)request["Time"], null, System.Globalization.DateTimeStyles.AssumeUniversal, out var time) || (now - time).Duration() > MaxAge)
            return "The request has no Time, or it is not within " + MaxAge.TotalMinutes + " minutes of now.";

        var chain = new List<PtfCredential.Link>();
        foreach (var link in caller["Chain"] as JsonArray ?? [])
            if (link?["Cii"] is JsonObject c && link["Credential"] is JsonObject k) chain.Add(new(c, k));
        var anchorId = (string?)request["TrustAnchorID"] ?? "";
        var outcome = PtfCredential.CheckChain(credential, cii, chain, id => id == anchorId ? anchors.Key(id) : null, now);
        if (outcome != PtfCredential.Outcome.Valid)
            return anchors.Key(anchorId) is null ? $"The Store does not recognise the Trust Anchor {anchorId}." : $"The credential of the Caller is not under the Trust Anchor {anchorId} ({outcome}).";
        if ((string?)request["ControllerID"] is { Length: > 0 } controller && (string?)credential["Subject"]?["InstanceID"] != controller)
            return $"The credential of the Caller is not that of the Controller {controller}.";
        return null;
    }

    // The key of an identity: that of a Cryptographic Instance Identity, or of a Trust Anchor object used as one.
    public static ECDsa? KeyOf(JsonObject cii)
    {
        if (PtfIdentity.PublicKey(cii) is { } key) return key;
        try
        {
            var anchorKey = ECDsa.Create();
            anchorKey.ImportSubjectPublicKeyInfo(Convert.FromHexString((string?)cii["PublicKey"]?["KeyValue"] ?? ""), out _);
            return anchorKey;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }
}
