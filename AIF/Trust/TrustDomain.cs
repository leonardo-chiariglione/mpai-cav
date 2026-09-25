using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// A TRUST ANCHOR (MPAI-PTF V1.0, PTF-TRA): a key whose public part a verifier
// trusts, and which issues credentials. Its PTF object is what a verifier is given
// to trust it.
public sealed class TrustAnchorKey
{
    public const string Header = "PTF-TRA-V1.0";

    public string AnchorId { get; }
    public PtfIssuer Issuer { get; }
    public ECDsa PublicKey { get; }
    private readonly DateTimeOffset notBefore, notAfter;

    public TrustAnchorKey(string anchorId, ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        AnchorId = anchorId;
        Issuer = new PtfIssuer(anchorId, key, anchorId);
        PublicKey = ECDsa.Create(key.ExportParameters(false));
        this.notBefore = notBefore;
        this.notAfter = notAfter;
    }

    // The anchor as PTF has it. Its validity is a pair of Simple Times (OSD-STM), as
    // the author decided: each an absolute instant in milliseconds.
    public JsonObject Object() => new()
    {
        ["Header"] = Header,
        ["AnchorID"] = AnchorId,
        ["PublicKey"] = new JsonObject
        {
            ["Algorithm"] = PtfIdentity.SignatureAlgorithm, ["KeyEncoding"] = "spki",
            ["KeyValue"] = Convert.ToHexString(PublicKey.ExportSubjectPublicKeyInfo())
        },
        ["Validity"] = new JsonObject
        {
            ["NotBefore"] = TimeObject(AnchorId + "#NotBefore", notBefore),
            ["NotAfter"] = TimeObject(AnchorId + "#NotAfter", notAfter)
        }
    };

    // The public key a PTF Trust Anchor object names.
    public static ECDsa? KeyOf(JsonObject anchor)
    {
        if ((string?)anchor["Header"] != Header || (string?)anchor["PublicKey"]?["KeyEncoding"] != "spki") return null;
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromHexString((string?)anchor["PublicKey"]?["KeyValue"] ?? ""), out _);
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    // A Simple Time (OSD-STM-V1.5) of one instant: a segment whose start and end are
    // the instant, absolute (FlagsByte bit 0), in milliseconds (bits 1-2 = 01).
    private static JsonObject TimeObject(string id, DateTimeOffset t)
    {
        var ms = t.ToUnixTimeMilliseconds();
        return new()
        {
            ["Header"] = "OSD-STM-V1.5",
            ["SimpleTimeID"] = id,
            ["SimpleTimeData"] = new JsonArray(new JsonObject
            {
                ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms,
                ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0
            })
        };
    }
}

// THE TRUST OF ONE CONTROLLER (Phase 13, M3223 3.1). In the AIF only AIM
// Instances are PTF Process Instances, and a Controller is itself a Trust Anchor
// (the author, 2026/09/25): its key is the anchor's, its Trust Anchor object is what
// hosts and other Controllers are given to trust it, and it issues the credentials
// of the AIM Instances it runs directly - every AIM Instance with a key of its own,
// its CII, the Instance Credential that binds that CII to the instance, and a
// Process Lifecycle Credential that follows the instance's state.
//
// The key of an AIM the Controller runs is made here, and never leaves this
// process. The key of an AIM on a host is made on the host, which sends only the
// CII; the Controller checks that the host holds the key before it issues the
// credential (IssueHeld).
public sealed class TrustDomain
{
    public static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan LifecycleLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan AnchorLifetime = TimeSpan.FromDays(365);

    public sealed class Instance
    {
        public required string Id { get; init; }
        public required JsonObject Cii { get; init; }
        public required JsonObject Credential { get; init; }
        public JsonObject Lifecycle { get; set; } = new();
        public List<string> States { get; } = new();
        public bool KeyHeldHere { get; init; }
    }

    public string ControllerId { get; }
    public TrustAnchorKey Anchor { get; }

    private readonly Func<DateTimeOffset> now;
    private readonly ConcurrentDictionary<string, Instance> instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ECDsa> keys = new(StringComparer.Ordinal);

    public TrustDomain(string controllerId, Func<DateTimeOffset>? now = null)
    {
        ControllerId = controllerId;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        var t = this.now();
        Anchor = new TrustAnchorKey(controllerId, ECDsa.Create(ECCurve.NamedCurves.nistP256), t, t + AnchorLifetime);
    }

    // The public key a KeyID names, among those this Controller trusts: its own.
    public ECDsa? KeyFor(string keyId) => keyId == Anchor.AnchorId ? Anchor.PublicKey : null;

    // AN AIM THE CONTROLLER RUNS: its key made here. An issuer - a package that
    // credentials the AIMs inside it (M3223 3.3) - is entitled to issue by its scope.
    public Instance IssueLocal(string instanceId, string aimInstance, string? implementation, bool issuer = false)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys[instanceId] = key;
        return Issue(instanceId, aimInstance, PtfIdentity.Make(key, instanceId, implementation, now()), heldHere: true, issuer);
    }

    // AN AIM ON A HOST: its key made there, its CII sent here. Refused unless the CII
    // shows the host holds the key it names.
    public Instance IssueHeld(string instanceId, string aimInstance, JsonObject cii)
    {
        if (PtfIdentity.Check(cii, out _) is var checkedCii && checkedCii != PtfIdentity.Outcome.Valid)
            throw new InvalidOperationException($"{instanceId}: its CII is not valid ({checkedCii}); no credential issued.");
        if ((string?)cii["CryptographicInstanceID"] != instanceId)
            throw new InvalidOperationException($"{instanceId}: its CII names {cii["CryptographicInstanceID"]}; no credential issued.");
        return Issue(instanceId, aimInstance, cii, heldHere: false, issuer: false);
    }

    private Instance Issue(string instanceId, string aimInstance, JsonObject cii, bool heldHere, bool issuer)
    {
        var instance = new Instance
        {
            Id = instanceId,
            Cii = cii,
            Credential = Anchor.Issuer.IssueCredential(cii, "AIMInstance", instanceId, PtfIdentity.AimType(aimInstance), now(),
                                                        CredentialLifetime, issuer ? PtfCredential.IssuerScope : null),
            KeyHeldHere = heldHere
        };
        instances[instanceId] = instance;
        Transition(instance, "Created");
        return instance;
    }

    // THE LIFECYCLE: a new Process Lifecycle Credential at each change of state.
    public void Transition(string instanceId, string state)
    {
        if (instances.TryGetValue(instanceId, out var instance)) Transition(instance, state);
    }

    // Every instance of a Module instance (<Module instance>/<AIM>).
    public void TransitionModule(string moduleInstance, string state)
    {
        foreach (var instance in instances.Values.Where(i => i.Id.StartsWith(moduleInstance + "/", StringComparison.Ordinal)))
            Transition(instance, state);
    }

    private void Transition(Instance instance, string state)
    {
        lock (instance)
        {
            instance.Lifecycle = Anchor.Issuer.IssueLifecycle(instance.Id, state, now(), LifecycleLifetime);
            instance.States.Add(state);
        }
    }

    public Instance? Of(string instanceId) => instances.GetValueOrDefault(instanceId);

    public IReadOnlyList<Instance> OfModule(string moduleInstance) =>
        instances.Values.Where(i => i.Id.StartsWith(moduleInstance + "/", StringComparison.Ordinal)).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();

    // The signing key of an AIM the Controller runs: for that AIM, and nothing else.
    public ECDsa? KeyOf(string instanceId) => keys.GetValueOrDefault(instanceId);
}
