using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using AIF.Channels;
using AIF.Trust;

namespace AIF.Controller;

// THE POLICY OF A TRUST AUTHORITY OF A SERVICE (Basic API 3.9.3, Communication 9): the
// roles it admits, the conditions of admission of each, and which roles may exchange.
//   { "Roles": ["Host", "Participant"], "DefaultRole": "Participant", "AuthorityRole": "Host",
//     "Exchange": [["Host", "Participant"], ["Participant", "Participant"]],
//     "Attest": ["Participant"], "LifetimeMinutes": 60, "Directed": true, "Correlate": ["RegisterID"], "Address": ["HFEID"] }
// Attest: the roles whose Controller shall prove by Attestation Evidence what it runs.
// Directed: the Controllers of the service answer only the Controller they answer (ExternalHub.Options.DirectedReplies).
// Correlate: the members of a Message by which an answer is matched to its request (ExternalHub.Options.CorrelateBy).
// Address: the members by which a Message names the Controller it is for (ExternalHub.Options.AddressedBy).
public sealed record ExternalPolicy(IReadOnlyList<string> Roles, string DefaultRole, string AuthorityRole,
                                    IReadOnlyList<(string A, string B)> Exchange, IReadOnlyList<string> Attest, TimeSpan Lifetime, bool Directed = false, IReadOnlyList<string>? Correlate = null, IReadOnlyList<string>? Address = null)
{
    public static ExternalPolicy Parse(string? json)
    {
        var o = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json)!.AsObject();
        var defaultRole = (string?)o["DefaultRole"] ?? "Member";
        var roles = (o["Roles"] as JsonArray)?.Select(r => (string)r!).ToList() ?? [defaultRole];
        var authorityRole = (string?)o["AuthorityRole"] ?? defaultRole;
        if (!roles.Contains(authorityRole)) roles.Add(authorityRole);
        var exchange = (o["Exchange"] as JsonArray)?.Select(p => ((string)p![0]!, (string)p[1]!)).ToList()
                       ?? roles.SelectMany(a => roles.Select(b => (a, b))).ToList();
        var attest = (o["Attest"] as JsonArray)?.Select(r => (string)r!).ToList() ?? [];
        return new ExternalPolicy(roles, defaultRole, authorityRole, exchange, attest, TimeSpan.FromMinutes((double?)o["LifetimeMinutes"] ?? 60), (bool?)o["Directed"] ?? false,
                                  (o["Correlate"] as JsonArray)?.Select(c => (string)c!).ToList() ?? [],
                                  (o["Address"] as JsonArray)?.Select(c => (string)c!).ToList() ?? []);
    }

    public bool MayExchange(string a, string b) => Exchange.Any(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));

    public JsonObject ToJson() => new()
    {
        ["Roles"] = new JsonArray(Roles.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["DefaultRole"] = DefaultRole,
        ["AuthorityRole"] = AuthorityRole,
        ["Exchange"] = new JsonArray(Exchange.Select(p => (JsonNode)new JsonArray(JsonValue.Create(p.A), JsonValue.Create(p.B))).ToArray()),
        ["Attest"] = new JsonArray(Attest.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        ["LifetimeMinutes"] = Lifetime.TotalMinutes,
        ["Directed"] = Directed,
        ["Correlate"] = new JsonArray((Correlate ?? []).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
        ["Address"] = new JsonArray((Address ?? []).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())
    };

    public static ExternalPolicy FromJson(JsonObject o) => Parse(o.ToJsonString());
}

// WHY A CONTROLLER WAS NOT JOINED: the outcome of MPAI_AIFU_External_Join.
public sealed class ExternalJoinException(AifErrorKind kind, string reason) : Exception(reason)
{
    public AifErrorKind Kind { get; } = kind;
}

public enum AifErrorKind { NotTrusted, NotAuthorised, Failed }

// THE CONTROLLER AS THE TRUST AUTHORITY OF A SERVICE (Basic API 3.9.3; Communication 9;
// MPAI-PTF Trust Establishment Protocol). At the request of its User Agent a Controller
// admits Controllers to the External links of a Module, under a policy. Its anchor is
// the one its User Agent publishes with the service: a Controller that was told to
// trust it knows it, and the Authority proves that it holds the key by signing what it
// answers (the nonce of the Controller that asks).
//
// The Controller that asks is admitted in two steps, on a link of its own to the
// rendezvous - this endpoint:
//   1. EnrolBegin: it names the role it asks for and a nonce; the Authority answers
//      with a Challenge, signed by its anchor.
//   2. Enrol: it presents its own Trust Anchor and a proof - the Challenge signed by
//      its key - and, where the policy requires it of the role, its Attestation
//      Evidence over the Challenge. The Authority verifies, and answers with the
//      Instance Credential it issues for the anchor and the policy of the service.
// The same endpoint is the rendezvous: an admitted Controller announces itself there
// and hears those of the roles that may exchange with its own.
public sealed class ExternalAuthorityService : IAsyncDisposable
{
    public const string Header = "MPAI-AIF-ENROL-V1.0";

    private readonly TrustAnchorKey anchor;
    private readonly ECDsa key;
    private readonly Func<DateTimeOffset> now;
    private readonly Attestation.Policy? attestation;
    private readonly RemoteLink.Listener listener;
    private readonly ConcurrentDictionary<string, Member> members = new();

    public ExternalPolicy Policy { get; }
    public string AnchorId => anchor.AnchorId;
    public JsonObject AnchorObject => anchor.Object();
    public int Port => listener.Port;
    public string Rendezvous { get; }
    public event Action<string>? Said;
    public IReadOnlyCollection<string> Members => members.Keys.ToList();

    private sealed record Member(string Id, string Role, RemoteLink? Link, Action<Announcement>? Local);

    public ExternalAuthorityService(TrustAnchorKey anchor, ECDsa key, ExternalPolicy policy, Attestation.Policy? attestation = null,
                                    Func<DateTimeOffset>? now = null, int port = 0, string host = "127.0.0.1")
    {
        this.anchor = anchor;
        this.key = key;
        Policy = policy;
        this.attestation = attestation;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        listener = new RemoteLink.Listener(port, new OpenAdmission(), Accepted);
        listener.Failed += why => Said?.Invoke($"an enrolment not completed: {why}");
        Rendezvous = $"{host}:{listener.Port}";
    }

    // THE AUTHORITY CONTROLLER'S OWN ADMISSION: it is admitted to its own service, in the
    // role the policy gives it, and its announcements reach the others.
    public (JsonObject Credential, IDiscoveryMedium Discovery) AdmitSelf(string controllerId)
    {
        var credential = anchor.Issuer.IssueCredential(anchor.Object(), "Controller", anchor.AnchorId, Policy.AuthorityRole, now(), Policy.Lifetime);
        var local = new LocalDiscovery(this, controllerId);
        members[anchor.AnchorId] = new Member(anchor.AnchorId, Policy.AuthorityRole, null, a => local.Hear(a));
        return (credential, local);
    }

    // The Authority's Controller, hearing and announcing as a member.
    private sealed class LocalDiscovery(ExternalAuthorityService service, string controllerId) : IDiscoveryMedium
    {
        public event Action<Announcement>? Heard;
        public void Hear(Announcement a) => Heard?.Invoke(a);
        public Task AnnounceAsync(Announcement a, CancellationToken cancel)
        {
            service.Relay(service.anchor.AnchorId, a);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // An announcement of an admitted member, to the members whose role may exchange with
    // its own (Communication 9).
    private void Relay(string fromId, Announcement a)
    {
        if (!members.TryGetValue(fromId, out var from)) return;
        foreach (var (id, to) in members)
        {
            if (id == fromId || !Policy.MayExchange(from.Role, to.Role)) continue;
            if (to.Local is not null) { to.Local(a); continue; }
            _ = to.Link!.NoticeAsync(new JsonObject
            {
                ["Kind"] = "Announce", ["ControllerId"] = a.ControllerId, ["ModuleType"] = a.ModuleType, ["Host"] = a.Host, ["Port"] = a.Port
            }).ContinueWith(_ => { });
        }
    }

    private void Accepted(RemoteLink link)
    {
        string? nonce = null, challenge = null, role = null, enrolled = null;
        link.OnRequest = frame => Task.FromResult<JsonObject?>(Handle(frame));
        link.OnNotice = frame =>
        {
            if ((string?)frame["Kind"] == "Announce" && enrolled is not null && (string?)frame["ControllerId"] is { } id)
                Relay(enrolled, new Announcement(id, (string)frame["ModuleType"]!, (string)frame["Host"]!, (int)frame["Port"]!));
            return Task.CompletedTask;
        };
        link.Lost += why => { if (enrolled is not null && members.TryRemove(enrolled, out var gone)) Said?.Invoke($"{enrolled} left"); };

        JsonObject Handle(JsonObject frame)
        {
            switch ((string?)frame["Kind"])
            {
                case "EnrolBegin":
                    nonce = (string?)frame["Nonce"];
                    role = (string?)frame["Role"] is { Length: > 0 } asked ? asked : Policy.DefaultRole;
                    if (nonce is null || !Policy.Roles.Contains(role)) return Refusal("NOT_AUTHORISED", $"the service admits no role {role}");
                    challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    return Signed(new JsonObject { ["Kind"] = "EnrolChallenge", ["Ok"] = true, ["Nonce"] = nonce, ["Challenge"] = challenge, ["Role"] = role });

                case "Enrol":
                    if (challenge is null || role is null) return Refusal("NOT_AUTHORISED", "no EnrolBegin");
                    var used = challenge;
                    var refused = Enrol(frame, challenge, role, out var credential, out var memberId);
                    challenge = null;                                  // a Challenge serves once
                    if (refused is not null) { Said?.Invoke($"refused: {refused}"); return Refusal("NOT_AUTHORISED", refused); }
                    enrolled = memberId;
                    members[memberId!] = new Member(memberId!, role, link, null);
                    Said?.Invoke($"{memberId} admitted as {role}");
                    return Signed(new JsonObject { ["Kind"] = "Enrolled", ["Ok"] = true, ["Challenge"] = used, ["Role"] = role, ["Credential"] = credential!, ["Policy"] = Policy.ToJson() });

                default:
                    return new JsonObject { ["Ok"] = true };            // the opening frame of the link
            }
        }
    }

    // Admitted, or why not.
    private string? Enrol(JsonObject frame, string challenge, string role, out JsonObject? credential, out string? memberId)
    {
        credential = null;
        memberId = null;
        if (frame["Anchor"] is not JsonObject presented || (string?)presented["AnchorID"] is not { } id) return "it presents no Trust Anchor";
        if (members.TryGetValue(id, out var already) && already.Link is { IsClosed: false }) return $"{id} is admitted already";
        if (TrustAnchorKey.KeyOf(presented) is not { } holderKey) return "its Trust Anchor is not one";
        using (holderKey)
        {
            var t = now();
            if (TrustAnchorKey.ValidityOf(presented) is not var (notBefore, notAfter) || t < notBefore || t > notAfter) return "its Trust Anchor is not valid now";
            // It holds the key of the anchor: the Challenge, signed with it.
            if (frame["Proof"] is not JsonObject proof || PtfSignature.Verify(proof, k => k == id ? holderKey : null) != PtfSignature.Outcome.Valid)
                return "it does not prove that it holds the key of its anchor";
            if ((string?)proof["Challenge"] != challenge || (string?)proof["AuthorityID"] != anchor.AnchorId || (string?)proof["Role"] != role)
                return "its proof is not for this admission";
            if (Policy.Attest.Contains(role))
            {
                if (attestation is null) return "the service requires attestation and has no policy to judge it";
                if (frame["Evidence"] is not JsonObject evidence) return $"the role {role} requires Attestation Evidence";
                if (Attestation.Check(evidence, holderKey, id, Convert.FromHexString(challenge), attestation, t) is { } why) return $"not attested: {why}";
            }
            credential = anchor.Issuer.IssueCredential(presented, "Controller", id, role, t, Policy.Lifetime);
            memberId = id;
            return null;
        }
    }

    private JsonObject Signed(JsonObject reply)
    {
        reply["Header"] = Header;
        reply["AuthorityID"] = anchor.AnchorId;
        reply["MessageTime"] = PtfIdentity.Time(now());
        return PtfSignature.Sign(reply, key, anchor.AnchorId);
    }

    private static JsonObject Refusal(string code, string why) => new() { ["Ok"] = false, ["Code"] = code, ["Error"] = why };

    public async ValueTask DisposeAsync()
    {
        foreach (var m in members.Values) if (m.Link is not null) await m.Link.DisposeAsync();
        members.Clear();
        await listener.DisposeAsync();
    }

    // The endpoint is public: who may use it is decided by the enrolment, not the link.
    private sealed class OpenAdmission : ILinkAdmission
    {
        public System.Security.Cryptography.X509Certificates.X509Certificate2? NewCertificate() => null;
        public JsonObject Opening(System.Security.Cryptography.X509Certificates.X509Certificate2? own, System.Security.Cryptography.X509Certificates.X509Certificate? host) =>
            new() { ["Kind"] = "Hello" };
        public string? Admitted(JsonObject answer, System.Security.Cryptography.X509Certificates.X509Certificate2? own, System.Security.Cryptography.X509Certificates.X509Certificate? host) =>
            answer["Ok"]?.GetValue<bool>() == true ? null : "the rendezvous did not answer";
        public (JsonObject Answer, bool Admits) Answer(JsonObject opening, System.Security.Cryptography.X509Certificates.X509Certificate2? own, System.Security.Cryptography.X509Certificates.X509Certificate? controller) =>
            (new JsonObject { ["Ok"] = true }, (string?)opening["Kind"] == "Hello");
    }

    internal static ILinkAdmission Open() => new OpenAdmission();
}
