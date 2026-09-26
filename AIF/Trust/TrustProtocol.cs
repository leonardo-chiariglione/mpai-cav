using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRUST PROTOCOL (MPAI-PTF V1.0, Trust Establishment Protocol; M3223 3.4), as a
// Controller and an AIM host run it when a link between them opens. Each end is a
// Trust Anchor - a Controller is one (the author, 2026/09/25), and so is a host -
// configured with the anchors it trusts: a host, the Controllers it serves; a
// Controller, the hosts it uses. There is no key shared between them.
//
// The Controller sends a TrustRequest, the host answers with a TrustResponse; each
// is signed by its sender's anchor key (KeyID the AnchorID) and checked by the other
// against the anchors it trusts: known, current, signed by that key, fresh. Each
// names the TLS certificate of the other end as its sender sees it - the request,
// the host's; the response, the Controller's - so a message is good only on the TLS
// link it was sent on, and the channel and the identity are the same: a party that
// relays it to a third end presents a certificate the message does not name.
public sealed class TrustProtocol
{
    public const string Header = "PTF-MSG-V1.0";
    public const string Operation = "EstablishTrust";
    public const string CertificateTarget = "TLSCertificate";

    // How far a message's time may be from the receiver's.
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly TrustAnchorKey self;
    private readonly ECDsa key;
    private readonly IEnumerable<JsonObject> trusted;          // read at each check: the list may change
    private readonly Func<DateTimeOffset> now;

    public TrustProtocol(TrustAnchorKey self, ECDsa key, IEnumerable<JsonObject> trusted, Func<DateTimeOffset>? now = null)
    {
        this.self = self;
        this.key = key;
        this.trusted = trusted;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Id => self.AnchorId;

    // The hash by which a message names a certificate: SHA-256 of its DER encoding.
    public static string CertificateHash(byte[] der) => Convert.ToHexString(SHA256.HashData(der));

    // THE REQUEST: the Controller's, naming the certificate the host presented.
    public JsonObject Request(string hostCertificate) => Signed(new JsonObject
    {
        ["Header"] = Header,
        ["MessageType"] = "TrustRequest",
        ["MessageID"] = $"{Id}#MSG-{Guid.NewGuid():N}",
        ["MessageTime"] = Time(),
        ["RequesterID"] = Id,
        ["Request"] = new JsonObject { ["Operation"] = Operation, ["TargetType"] = CertificateTarget, ["TargetID"] = hostCertificate }
    });

    // THE ANSWER: the host's, to a request received on a link whose certificates are
    // these. Signed whether it admits the requester or not; the requester it admits,
    // or why not.
    public (JsonObject Response, string? Admitted, string? Refused) Answer(JsonObject request, string ownCertificate, string requesterCertificate)
    {
        var refused = Check(request, "TrustRequest", "RequesterID", (string?)request["Request"]?["TargetID"], ownCertificate);
        if (refused is null && (string?)request["Request"]?["Operation"] != Operation) refused = $"it asks for {request["Request"]?["Operation"]}, not {Operation}";
        var response = new JsonObject
        {
            ["Header"] = Header,
            ["MessageType"] = "TrustResponse",
            ["MessageID"] = $"{Id}#MSG-{Guid.NewGuid():N}",
            ["MessageTime"] = Time(),
            ["ResponderID"] = Id,
            ["Response"] = refused is null
                ? new JsonObject { ["Status"] = "Success", ["Result"] = requesterCertificate }
                : new JsonObject { ["Status"] = "Failure", ["Reason"] = refused, ["Result"] = requesterCertificate }
        };
        return (Signed(response), refused is null ? (string)request["RequesterID"]! : null, refused);
    }

    // THE RESPONSE, checked by the requester: from a host it trusts, for this link,
    // and admitting it. Null where it is; otherwise why not.
    public string? Check(JsonObject response, string ownCertificate)
    {
        var refused = Check(response, "TrustResponse", "ResponderID", (string?)response["Response"]?["Result"], ownCertificate);
        if (refused is not null) return refused;
        return (string?)response["Response"]?["Status"] == "Success" ? null : $"it did not admit this Controller: {response["Response"]?["Reason"]}";
    }

    // One message: well formed; signed by an anchor this end trusts, current, whose
    // key verifies it; its sender that anchor; fresh; and naming this end's
    // certificate.
    private string? Check(JsonObject message, string type, string senderField, string? namedCertificate, string ownCertificate)
    {
        if ((string?)message["Header"] != Header || (string?)message["MessageType"] != type) return $"not a {type}";
        var keyId = (string?)message["KeyID"] ?? "";
        var anchor = trusted.FirstOrDefault(a => (string?)a["AnchorID"] == keyId);
        if (anchor is null) return $"foreign: {keyId} is not an anchor this end trusts";
        var t = now();
        if (TrustAnchorKey.ValidityOf(anchor) is not var (notBefore, notAfter) || t < notBefore || t > notAfter)
            return $"expired: the anchor {keyId} is not valid now";
        using var anchorKey = TrustAnchorKey.KeyOf(anchor);
        var signed = PtfSignature.Verify(message, id => id == keyId ? anchorKey : null);
        if (signed != PtfSignature.Outcome.Valid) return $"forged: its signature is {signed} under the key of {keyId}";
        if ((string?)message[senderField] != keyId) return $"it names {message[senderField]} and is signed by {keyId}";
        if (!DateTimeOffset.TryParse((string?)message["MessageTime"]?["Data"], out var sent) || (t - sent).Duration() > Freshness)
            return $"stale: sent {(t - sent).Duration().TotalMinutes:0} minutes from its receipt";
        if (!string.Equals(namedCertificate, ownCertificate, StringComparison.OrdinalIgnoreCase))
            return "not for this link: it names another certificate";
        return null;
    }

    private JsonObject Time() => new()
    {
        ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{Id}#TIM-{Guid.NewGuid():N}", ["Data"] = PtfIdentity.Time(now())
    };

    private JsonObject Signed(JsonObject message) => PtfSignature.Sign(message, key, Id);
}
