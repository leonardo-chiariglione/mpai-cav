using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRUST ANCHOR OF A DEPLOYMENT (M3223 3.1, proposed): a key the deployment
// configures, whose public part every party trusts, and which issues the
// credentials of Controllers and hosts. Its PTF object (PTF-TRA) is what a verifier
// is given to trust it.
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

    // The anchor as PTF has it. Its validity is a pair of Time objects (OSD-TIM), as
    // the Trust Anchor schema requires, where every other PTF Data Type has a
    // date-time: a finding for PTF.
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

    private static JsonObject TimeObject(string id, DateTimeOffset t) =>
        new() { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = id, ["Data"] = PtfIdentity.Time(t) };
}

// THE TRUST OF ONE CONTROLLER (Phase 13, M3223 3.1). Given the anchor of its
// deployment, the Controller has an identity - its key, its CII, and the credential
// the anchor issued it - and is the issuer of the credentials of the AIM Instances
// it runs: every AIM Instance a PTF Process Instance, with a key of its own, its
// CII, the Instance Credential that binds that CII to the instance, and a Process
// Lifecycle Credential that follows the instance's state.
//
// The key of an AIM the Controller runs is made here, and never leaves this
// process. The key of an AIM on a host is made on the host, which sends only the
// CII; the Controller checks that the host holds the key before it issues the
// credential (IssueHeld).
public sealed class TrustDomain
{
    public static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan LifecycleLifetime = TimeSpan.FromHours(1);

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
    public JsonObject ControllerCii { get; }
    public JsonObject ControllerCredential { get; }

    private readonly Func<DateTimeOffset> now;
    private readonly PtfIssuer issuer;
    private readonly ECDsa controllerPublic;
    private readonly ConcurrentDictionary<string, Instance> instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ECDsa> keys = new(StringComparer.Ordinal);

    public TrustDomain(TrustAnchorKey anchor, string controllerId, Func<DateTimeOffset>? now = null)
    {
        Anchor = anchor;
        ControllerId = controllerId;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        controllerPublic = ECDsa.Create(key.ExportParameters(false));
        ControllerCii = PtfIdentity.Make(key, controllerId, "AIF Controller", this.now());
        ControllerCredential = anchor.Issuer.IssueCredential(ControllerCii, "ProcessInstance", controllerId, "AIF-CTR", this.now(), CredentialLifetime);
        issuer = new PtfIssuer(controllerId, key, controllerId);
    }

    // The public keys this Controller trusts, by KeyID: the anchor's and its own.
    public ECDsa? KeyFor(string keyId) =>
        keyId == Anchor.AnchorId ? Anchor.PublicKey : keyId == ControllerId ? controllerPublic : null;

    // AN AIM THE CONTROLLER RUNS: its key made here.
    public Instance IssueLocal(string instanceId, string aimInstance, string? implementation)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys[instanceId] = key;
        return Issue(instanceId, aimInstance, PtfIdentity.Make(key, instanceId, implementation, now()), heldHere: true);
    }

    // AN AIM ON A HOST: its key made there, its CII sent here. Refused unless the CII
    // shows the host holds the key it names.
    public Instance IssueHeld(string instanceId, string aimInstance, JsonObject cii)
    {
        if (PtfIdentity.Check(cii, out _) is var checkedCii && checkedCii != PtfIdentity.Outcome.Valid)
            throw new InvalidOperationException($"{instanceId}: its CII is not valid ({checkedCii}); no credential issued.");
        if ((string?)cii["CryptographicInstanceID"] != instanceId)
            throw new InvalidOperationException($"{instanceId}: its CII names {cii["CryptographicInstanceID"]}; no credential issued.");
        return Issue(instanceId, aimInstance, cii, heldHere: false);
    }

    private Instance Issue(string instanceId, string aimInstance, JsonObject cii, bool heldHere)
    {
        var instance = new Instance
        {
            Id = instanceId,
            Cii = cii,
            Credential = issuer.IssueCredential(cii, "AIMInstance", instanceId, PtfIdentity.AimType(aimInstance), now(), CredentialLifetime),
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
            instance.Lifecycle = issuer.IssueLifecycle(instance.Id, state, now(), LifecycleLifetime);
            instance.States.Add(state);
        }
    }

    public Instance? Of(string instanceId) => instances.GetValueOrDefault(instanceId);

    public IReadOnlyList<Instance> OfModule(string moduleInstance) =>
        instances.Values.Where(i => i.Id.StartsWith(moduleInstance + "/", StringComparison.Ordinal)).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();

    // The signing key of an AIM the Controller runs: for that AIM, and nothing else.
    public ECDsa? KeyOf(string instanceId) => keys.GetValueOrDefault(instanceId);
}
