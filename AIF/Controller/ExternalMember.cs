using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// A CONTROLLER'S TRUST IN THE SERVICE IT JOINED (Basic API 3.9; Communication 9): what its
// External hub needs - its links admitted by the Trust Protocol, its anchor presented
// with the credential of the Trust Authority of the service, and only to the roles that
// the policy lets exchange with its own; what its Module sends signed with its key; what
// it receives verified against the Controller the link admitted.
public sealed class ExternalTrust
{
    private readonly TrustProtocol protocol;
    private readonly TrustAnchorKey anchor;
    private readonly ECDsa key;
    private readonly string authority;
    private readonly Func<DateTimeOffset> now;

    public string ControllerId => anchor.AnchorId;
    public string Role { get; }
    public ExternalPolicy Policy { get; }
    public ILinkAdmission Admission { get; }

    public ExternalTrust(TrustAnchorKey anchor, ECDsa key, JsonObject credential, JsonObject authorityAnchor, string role,
                         ExternalPolicy policy, Func<DateTimeOffset>? now = null)
    {
        this.anchor = anchor;
        this.key = key;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        authority = (string)authorityAnchor["AnchorID"]!;
        Role = role;
        Policy = policy;
        protocol = new TrustProtocol(anchor, key, [authorityAnchor], this.now, credential: credential);
        Admission = new RoleCheckedLink(new TrustedLink(protocol), role, policy);
    }

    public string Sign(string json) => ExternalSigning.Sign(json, anchor, key, authority, now());

    public string SignPayloads(string json, IReadOnlyList<PayloadDescriptor> payloads) =>
        ExternalSigning.Sign(json, anchor, key, authority, now(), payloads.Select(p => (p.Reference, p.Length, p.Hash)).ToList());

    public string? Verify(string from, string json)
    {
        var outcome = ExternalSigning.Verify(json, from, protocol.AnchorOf);
        return outcome == ExternalSigning.Outcome.Valid ? null : outcome.ToString();
    }
}

// A LINK ADMITTED BY THE TRUST PROTOCOL, AND BY THE POLICY OF THE SERVICE: the Controller
// at the other end holds a credential of the Authority - the Trust Protocol checked that -
// and its role may exchange with this one's.
public sealed class RoleCheckedLink(TrustedLink inner, string ownRole, ExternalPolicy policy) : ILinkAdmission
{
    public X509Certificate2? NewCertificate() => inner.NewCertificate();
    public JsonObject Opening(X509Certificate2? own, X509Certificate? host) => inner.Opening(own, host);
    public string? PeerOf(JsonObject opening) => inner.PeerOf(opening);

    private static string? RoleIn(JsonObject? message) => (string?)message?["Presentation"]?["Credential"]?["Subject"]?["Specification"];

    private string? Judge(JsonObject? message) =>
        RoleIn(message) is { } role && policy.MayExchange(ownRole, role) ? null : $"the roles {ownRole} and {RoleIn(message) ?? "(none)"} do not exchange under the policy of the service";

    public string? Admitted(JsonObject answer, X509Certificate2? own, X509Certificate? host) =>
        inner.Admitted(answer, own, host) ?? Judge(answer["Message"] as JsonObject);

    public (JsonObject Answer, bool Admits) Answer(JsonObject opening, X509Certificate2? own, X509Certificate? controller)
    {
        var (answer, admits) = inner.Answer(opening, own, controller);
        if (!admits || Judge(opening["Message"] as JsonObject) is not { } why) return (answer, admits);
        return (new JsonObject { ["Ok"] = false, ["Error"] = why }, false);
    }
}

// THE RENDEZVOUS AS THE MEDIUM OF ANNOUNCEMENTS: what a Controller announces goes to the
// Authority, which gives it to the Controllers admitted whose roles may exchange with its
// own; what they announce comes back the same way.
public sealed class RendezvousDiscovery : IDiscoveryMedium
{
    private readonly RemoteLink link;
    public event Action<Announcement>? Heard;

    public RendezvousDiscovery(RemoteLink link)
    {
        this.link = link;
        link.OnNotice = frame =>
        {
            if ((string?)frame["Kind"] == "Announce")
                Heard?.Invoke(new Announcement((string)frame["ControllerId"]!, (string)frame["ModuleType"]!, (string)frame["Host"]!, (int)frame["Port"]!));
            return Task.CompletedTask;
        };
    }

    public Task AnnounceAsync(Announcement a, CancellationToken cancel) => link.NoticeAsync(new JsonObject
    {
        ["Kind"] = "Announce", ["ControllerId"] = a.ControllerId, ["ModuleType"] = a.ModuleType, ["Host"] = a.Host, ["Port"] = a.Port
    }, cancel);

    public ValueTask DisposeAsync() => link.DisposeAsync();
}

// A CONTROLLER JOINED TO A SERVICE (MPAI_AIFU_External_Join): it trusts the Authority its
// User decided to trust - the anchor it was given - and presents itself for admission at
// the rendezvous. The key of its anchor is made here and never leaves.
public sealed class ExternalMember : IAsyncDisposable
{
    public ExternalTrust Trust { get; }
    public RendezvousDiscovery Discovery { get; }

    private ExternalMember(ExternalTrust trust, RendezvousDiscovery discovery) => (Trust, Discovery) = (trust, discovery);

    // authorityAnchor: the Trust Anchor object of the Authority, as the User Agent was
    // given it. role: the role asked for (null: the Authority's default). attestor: the
    // Controller's root of trust, where it has one.
    // Throws ExternalJoinException: NotTrusted where the Authority does not prove that it
    // holds the anchor; NotAuthorised where it refuses admission.
    public static async Task<ExternalMember> JoinAsync(string controllerId, JsonObject authorityAnchor, string rendezvous, string? role = null,
                                                       IRootOfTrust? attestor = null, Func<DateTimeOffset>? now = null, CancellationToken cancel = default)
    {
        now ??= () => DateTimeOffset.UtcNow;
        var authorityId = (string?)authorityAnchor["AnchorID"] ?? throw new ExternalJoinException(AifErrorKind.Failed, "the anchor given names no AnchorID");
        using var authorityKey = TrustAnchorKey.KeyOf(authorityAnchor) ?? throw new ExternalJoinException(AifErrorKind.Failed, "the anchor given is not a Trust Anchor");
        var colon = rendezvous.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(rendezvous[(colon + 1)..], out var port)) throw new ExternalJoinException(AifErrorKind.Failed, $"the rendezvous {rendezvous} is not host:port");

        RemoteLink link;
        try { link = await RemoteLink.ConnectAsync(rendezvous[..colon], port, ExternalAuthorityService.Open(), cancel); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Net.Sockets.SocketException)
        { throw new ExternalJoinException(AifErrorKind.Failed, $"the rendezvous {rendezvous} cannot be reached: {e.Message}"); }

        try
        {
            // A reply is the Authority's if its signature is by the anchor and it echoes
            // what this Controller chose (the nonce) or was given (the Challenge).
            string? Proven(JsonObject received, string member, string value)
            {
                // The link adds its own members to a reply after it is signed.
                var reply = received.DeepClone().AsObject();
                reply.Remove("ReplyTo");
                if (PtfSignature.Verify(reply, id => id == authorityId ? authorityKey : null) != PtfSignature.Outcome.Valid) return "signature";
                if ((string?)reply["AuthorityID"] != authorityId || (string?)reply[member] != value) return "identity";
                return null;
            }

            // 1. The Authority proves that it holds the anchor: it signs the nonce.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var begin = await link.RequestAsync(new JsonObject { ["Kind"] = "EnrolBegin", ["Nonce"] = nonce, ["Role"] = role }, cancel);
            if (begin["Ok"]?.GetValue<bool>() != true) throw new ExternalJoinException(AifErrorKind.NotAuthorised, (string?)begin["Error"] ?? "refused");
            if (Proven(begin, "Nonce", nonce) is { } bad)
                throw new ExternalJoinException(AifErrorKind.NotTrusted, $"the Authority at {rendezvous} does not prove that it holds the anchor {authorityId} ({bad})");
            var challenge = (string)begin["Challenge"]!;
            var granted = (string)begin["Role"]!;

            // 2. This Controller: its anchor, the key it holds, and the Challenge signed.
            var t = now();
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var anchor = new TrustAnchorKey(controllerId, key, t.AddMinutes(-5), t.AddDays(30));
            var proof = PtfSignature.Sign(new JsonObject { ["Header"] = ExternalAuthorityService.Header, ["Kind"] = "EnrolProof", ["Challenge"] = challenge, ["AuthorityID"] = authorityId, ["Role"] = granted }, key, controllerId);
            var enrol = new JsonObject { ["Kind"] = "Enrol", ["Anchor"] = anchor.Object(), ["Proof"] = proof };
            if (attestor is not null) enrol["Evidence"] = attestor.Evidence(controllerId, Convert.FromHexString(challenge));
            var enrolled = await link.RequestAsync(enrol, cancel);
            if (enrolled["Ok"]?.GetValue<bool>() != true) throw new ExternalJoinException(AifErrorKind.NotAuthorised, (string?)enrolled["Error"] ?? "refused");
            if (Proven(enrolled, "Challenge", challenge) is { } worse)
                throw new ExternalJoinException(AifErrorKind.NotTrusted, $"the admission is not signed by the anchor {authorityId} ({worse})");

            var credential = enrolled["Credential"]?.AsObject() ?? throw new ExternalJoinException(AifErrorKind.NotAuthorised, "no credential was issued");
            if (PtfCredential.CheckCredential(credential, anchor.Object(), id => id == authorityId ? authorityKey : null, now()) != PtfCredential.Outcome.Valid)
                throw new ExternalJoinException(AifErrorKind.NotTrusted, "the credential issued is not one of the Authority for this anchor");

            var trust = new ExternalTrust(anchor, key, credential, authorityAnchor, (string)enrolled["Role"]!,
                                          ExternalPolicy.FromJson(enrolled["Policy"]!.AsObject()), now);
            return new ExternalMember(trust, new RendezvousDiscovery(link));
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Discovery.DisposeAsync();
}
