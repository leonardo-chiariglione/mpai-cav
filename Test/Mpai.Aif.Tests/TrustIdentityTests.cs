using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Metadata;
using AIF.Trust;
using Json.Schema;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 2 (M3223 3.1): the identities and credentials of AIM Instances.
// In the AIF only AIM Instances are PTF Process Instances, and a Controller is
// itself a Trust Anchor (the author, 2026/09/25): it issues each AIM Instance it
// starts an Instance Credential for the CII of a key of its own, and a Process
// Lifecycle Credential at each change of state. An AIM on a host makes its key
// there, and is issued its credential only if its CII shows it holds that key. An
// AIM its credential entitles to issue - a package - credentials the AIMs inside it,
// and a verifier follows the CredentialChain to the anchor.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustIdentityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // THE OBJECTS: the Controller's Trust Anchor, an AIM's CII, credential and
    // lifecycle credential, and what each is checked against.
    [Fact]
    public void Identities()
    {
        var result = new Dictionary<string, string>();
        var now = T0;
        var trust = new TrustDomain("controller-1", () => now);
        var aim = trust.IssueLocal("TST#1/1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01");

        result["the Controller's Trust Anchor against its schema"] = Violations("TrustAnchor", trust.Anchor.Object());
        result["the AIM's CII against its schema"] = Violations("CryptographicInstanceIdentity", aim.Cii);
        result["the AIM's credential against its schema"] = Violations("InstanceCredential", aim.Credential);
        result["the AIM's lifecycle credential against its schema"] = Violations("ProcessLifecycleCredential", aim.Lifecycle);

        // A verifier given the Controller's Trust Anchor object, and nothing else.
        var anchorObject = trust.Anchor.Object();
        var anchorKey = TrustAnchorKey.KeyOf(anchorObject);
        ECDsa? Given(string id) => id == (string?)anchorObject["AnchorID"] ? anchorKey : null;
        result["the AIM's CII, checked"] = PtfIdentity.Check(aim.Cii, out _).ToString();
        result["the AIM's credential, by the Controller's anchor"] = PtfCredential.CheckCredential(aim.Credential, aim.Cii, Given, now).ToString();
        result["the AIM's lifecycle: Created"] = PtfCredential.CheckLifecycle(aim.Lifecycle, aim.Id, "Created", Given, now).ToString();
        result["the AIM's credential says"] = $"{aim.Credential["Subject"]!["InstanceType"]} {aim.Credential["Subject"]!["InstanceID"]}, of {aim.Credential["Subject"]!["Specification"]}, issued by {aim.Credential["Issuer"]!["Name"]}";

        var key = trust.KeyOf(aim.Id)!;
        using var cii = PtfIdentity.PublicKey(aim.Cii)!;
        var probe = "held"u8.ToArray();
        result["the AIM's key, the one its CII names"] = cii.VerifyData(probe, key.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256) ? "yes" : "no";
        result["another AIM's key"] = trust.IssueLocal("TST#1/1TST-SLP-V1.0-I01", "1TST-SLP-V1.0-I01", null).Cii["CryptographicBinding"]!["PublicKey"]!["KeyValue"]!.ToString()
            == aim.Cii["CryptographicBinding"]!["PublicKey"]!["KeyValue"]!.ToString() ? "the same" : "its own";
        Expected.Match("trust-identities.json", result);
    }

    // THE CREDENTIAL CHAIN: a package - an AIM the Controller credentials as an
    // issuer - credentials the AIMs inside it; a verifier that trusts only the
    // Controller's anchor follows the chain presented with a credential.
    [Fact]
    public void Chain()
    {
        var result = new Dictionary<string, string>();
        var now = T0;
        var trust = new TrustDomain("controller-1", () => now);
        var package = trust.IssueLocal("TST#1/1TST-PKG-V1.0-I01", "1TST-PKG-V1.0-I01", null, issuer: true);
        var byPackage = new PtfIssuer(package.Id, trust.KeyOf(package.Id)!, package.Id);

        (JsonObject Cii, JsonObject Credential, ECDsa Key) Inner(PtfIssuer by, string id, string? scope = null)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var cii = PtfIdentity.Make(key, id, null, now);
            return (cii, by.IssueCredential(cii, "AIMInstance", id, PtfIdentity.AimType(id.Split('/')[^1]), now, TrustDomain.CredentialLifetime, scope), key);
        }
        var inner = Inner(byPackage, "TST#1/1TST-PKG-V1.0-I01/1TST-UPP-V1.0-I01");
        var link = new PtfCredential.Link(package.Cii, package.Credential);

        result["an AIM inside the package, with the chain"] = PtfCredential.CheckChain(inner.Credential, inner.Cii, [link], trust.KeyFor, now).ToString();
        result["the same, without the chain"] = PtfCredential.CheckChain(inner.Credential, inner.Cii, [], trust.KeyFor, now).ToString();
        result["the package's own credential, no chain needed"] = PtfCredential.CheckChain(package.Credential, package.Cii, [], trust.KeyFor, now).ToString();

        var plain = trust.IssueLocal("TST#1/1TST-PLN-V1.0-I01", "1TST-PLN-V1.0-I01", null);
        var byPlain = Inner(new PtfIssuer(plain.Id, trust.KeyOf(plain.Id)!, plain.Id), "TST#1/1TST-PLN-V1.0-I01/1TST-UPP-V1.0-I01");
        result["an AIM credentialed by an AIM not entitled to issue"] = PtfCredential.CheckChain(byPlain.Credential, byPlain.Cii,
            [new PtfCredential.Link(plain.Cii, plain.Credential)], trust.KeyFor, now).ToString();

        var altered = package.Credential.DeepClone().AsObject();
        altered["Validity"]!["NotAfter"] = PtfIdentity.Time(now.AddYears(10));
        result["the chain with the package's credential altered"] = PtfCredential.CheckChain(inner.Credential, inner.Cii,
            [new PtfCredential.Link(package.Cii, altered)], trust.KeyFor, now).ToString();

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        result["the chain with a CII of the package's name and another key"] = PtfCredential.CheckChain(inner.Credential, inner.Cii,
            [new PtfCredential.Link(PtfIdentity.Make(stranger, package.Id, null, now), package.Credential)], trust.KeyFor, now).ToString();

        var other = new TrustDomain("controller-2", () => now);
        result["the chain, verified by another Controller's anchor"] = PtfCredential.CheckChain(inner.Credential, inner.Cii, [link], other.KeyFor, now).ToString();

        // Two levels: the package credentials a sub-package as an issuer, which
        // credentials an AIM inside it.
        var sub = Inner(byPackage, "TST#1/1TST-PKG-V1.0-I01/1TST-SUB-V1.0-I01", PtfCredential.IssuerScope);
        var subId = (string)sub.Cii["CryptographicInstanceID"]!;
        var deep = Inner(new PtfIssuer(subId, sub.Key, subId), subId + "/1TST-SLP-V1.0-I01");
        result["two levels, the whole chain"] = PtfCredential.CheckChain(deep.Credential, deep.Cii,
            [new PtfCredential.Link(sub.Cii, sub.Credential), link], trust.KeyFor, now).ToString();
        result["two levels, the upper link missing"] = PtfCredential.CheckChain(deep.Credential, deep.Cii,
            [new PtfCredential.Link(sub.Cii, sub.Credential)], trust.KeyFor, now).ToString();

        // A loop: an issuer that presents itself as its own issuer.
        const string selfId = "TST#9/1TST-SLF-V1.0-I01";
        var selfKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var selfCii = PtfIdentity.Make(selfKey, selfId, null, now);
        var selfIssued = new PtfIssuer(selfId, selfKey, selfId).IssueCredential(selfCii, "AIMInstance", selfId, null, now, TrustDomain.CredentialLifetime, PtfCredential.IssuerScope);
        result["a chain that loops"] = PtfCredential.CheckChain(selfIssued, selfCii, [new PtfCredential.Link(selfCii, selfIssued)], trust.KeyFor, now).ToString();

        // THE PRESENTATION: a TrustRequest carrying the inner AIM's identity,
        // credential and chain, signed by the inner AIM.
        var request = new JsonObject
        {
            ["Header"] = "PTF-MSG-V1.0", ["MessageID"] = "msg-1",
            ["MessageTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t-1", ["Data"] = PtfIdentity.Time(now) },
            ["MessageType"] = "TrustRequest", ["RequesterID"] = (string)inner.Cii["CryptographicInstanceID"]!,
            ["Request"] = new JsonObject { ["Operation"] = "Exchange", ["TargetType"] = "AIMInstance", ["TargetID"] = "TST#1/1TST-SLP-V1.0-I01" },
            ["Presentation"] = new JsonObject
            {
                ["CII"] = inner.Cii.DeepClone(), ["InstanceCredential"] = inner.Credential.DeepClone(),
                ["CredentialChain"] = new JsonArray(new JsonObject { ["CII"] = package.Cii.DeepClone(), ["InstanceCredential"] = package.Credential.DeepClone() })
            }
        };
        PtfSignature.Sign(request, inner.Key, (string)inner.Cii["CryptographicInstanceID"]!);
        result["a TrustRequest with its Presentation, against its schema"] = Violations("TrustMessage", request);
        var presented = request["Presentation"]!.AsObject();
        var presentedKey = PtfIdentity.PublicKey(presented["CII"]!.AsObject());
        result["the TrustRequest: signed by the key its Presentation's CII names"] = PtfSignature.Verify(request, _ => presentedKey).ToString();
        result["the TrustRequest: its credential, through the chain it presents"] = PtfCredential.CheckChain(
            presented["InstanceCredential"]!.AsObject(), presented["CII"]!.AsObject(),
            presented["CredentialChain"]!.AsArray().Select(l => new PtfCredential.Link(l!["CII"]!.AsObject(), l["InstanceCredential"]!.AsObject())).ToList(),
            trust.KeyFor, now).ToString();
        Expected.Match("trust-chain.json", result);
    }

    // WHAT IS REFUSED: a CII that is not what it says, a credential changed, for
    // another CII, out of its time, or from an issuer not trusted; a lifecycle not in
    // the state claimed; an AIM on a host that does not hold the key it names.
    [Fact]
    public void Refused()
    {
        var result = new Dictionary<string, string>();
        var now = T0;
        var trust = new TrustDomain("controller-1", () => now);
        var aim = trust.IssueLocal("TST#1/1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01", null);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var swapped = aim.Cii.DeepClone().AsObject();
        swapped["CryptographicBinding"]!["PublicKey"]!["KeyValue"] = Convert.ToHexString(stranger.ExportSubjectPublicKeyInfo());
        result["a CII whose key was swapped"] = PtfIdentity.Check(swapped, out _).ToString();

        var claimed = PtfIdentity.Make(stranger, "TST#1/1TST-UPP-V1.0-I01", null, now);
        claimed["CryptographicBinding"] = aim.Cii["CryptographicBinding"]!.DeepClone();
        claimed["Integrity"]!["Fingerprint"] = aim.Cii["Integrity"]!["Fingerprint"]!.DeepClone();
        result["a CII naming another's key, signed by a key not that one"] = PtfIdentity.Check(claimed, out _).ToString();

        var changed = aim.Credential.DeepClone().AsObject();
        changed["Subject"]!["InstanceID"] = "TST#1/1TST-XXX-V1.0-I01";
        result["a credential changed"] = PtfCredential.CheckCredential(changed, aim.Cii, trust.KeyFor, now).ToString();

        var other = trust.IssueLocal("TST#1/1TST-SLP-V1.0-I01", "1TST-SLP-V1.0-I01", null);
        result["a credential presented with another CII"] = PtfCredential.CheckCredential(aim.Credential, other.Cii, trust.KeyFor, now).ToString();
        result["a credential after its time"] = PtfCredential.CheckCredential(aim.Credential, aim.Cii, trust.KeyFor, now + TrustDomain.CredentialLifetime + TimeSpan.FromMinutes(1)).ToString();
        result["a credential before its time"] = PtfCredential.CheckCredential(aim.Credential, aim.Cii, trust.KeyFor, now - TimeSpan.FromMinutes(1)).ToString();

        var elsewhere = new TrustDomain("controller-2", () => now);
        result["a credential from another Controller"] = PtfCredential.CheckCredential(
            elsewhere.IssueLocal("TST#2/1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01", null).Credential,
            elsewhere.Of("TST#2/1TST-UPP-V1.0-I01")!.Cii, trust.KeyFor, now).ToString();
        result["a lifecycle credential claimed Running while Created"] = PtfCredential.CheckLifecycle(aim.Lifecycle, aim.Id, "Running", trust.KeyFor, now).ToString();

        try { trust.IssueHeld("TST#1/1TST-RPT-V1.0-I01", "1TST-RPT-V1.0-I01", claimed); result["a host presenting a CII whose key it does not hold"] = "issued"; }
        catch (InvalidOperationException e) { result["a host presenting a CII whose key it does not hold"] = "refused: " + e.Message; }
        try { trust.IssueHeld("TST#1/1TST-RPT-V1.0-I01", "1TST-RPT-V1.0-I01", PtfIdentity.Make(stranger, "TST#1/1TST-OTHER", null, now)); result["a host presenting the CII of another instance"] = "issued"; }
        catch (InvalidOperationException e) { result["a host presenting the CII of another instance"] = "refused: " + e.Message; }
        Expected.Match("trust-refused.json", result);
    }

    // A MODULE: every AIM Instance an identity, a credential, and a lifecycle that
    // follows Start, Pause, Resume and Stop - here and on a host.
    [Fact]
    public void Module()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = TrustedParties.Host();
        foreach (var (module, placed) in new[] { ("TST-RXL", Array.Empty<string>()), ("TST-RXC", new[] { "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01" }) })
        {
            var trust = TrustedParties.Controller();
            var amds = TrustEvidenceTests.ApprovedCopy();              // what runs under trust is approved (Step 3)
            using var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), new RemoteAims());
            api.Controller.Trust = trust;
            api.Controller.HostAnchors.Add(hostProcess.Anchor!);
            foreach (var aim in placed) api.Controller.AimHosts[aim] = hostProcess.Address;

            var name = $"1{module}-V1.0-I01";
            var started = api.StartFlow(name);
            Assert.True(started == AIF.Controller.AifError.OK, $"{module}: {started} {api.Controller.LastRefusal}{Environment.NewLine}The host:{Environment.NewLine}{hostProcess.Output}");
            var instance = api.Controller.InstanceName(name)!;
            api.Pause(name);
            api.Resume(name);
            var status = placed.Length > 0 ? HostStatus(api, name) : new Dictionary<string, string>();
            api.StopFlow(name);

            foreach (var aim in trust.OfModule(instance))
            {
                var short_ = aim.Id[(instance.Length + 1)..];
                var at = DateTimeOffset.UtcNow;
                result[$"{module} {short_}"] =
                    $"key {(aim.KeyHeldHere ? "made by the Controller" : "made on the host")}; " +
                    $"CII {PtfIdentity.Check(aim.Cii, out _)}; credential {PtfCredential.CheckCredential(aim.Credential, aim.Cii, trust.KeyFor, at)}; " +
                    $"lifecycle {string.Join(" > ", aim.States)}, the last {PtfCredential.CheckLifecycle(aim.Lifecycle, aim.Id, "Terminated", trust.KeyFor, at)}" +
                    (status.TryGetValue(short_, out var held) ? $"; on the host: {held}" : "");
            }
        }
        Expected.Match("trust-module.json", result);
    }

    private static Dictionary<string, string> HostStatus(ControllerApi api, string name)
    {
        var client = api.Controller.HostsOf(name).Single();
        var status = client.AskAsync("Status", api.Controller.InstanceName(name)!).GetAwaiter().GetResult();
        return status["Aims"]!.AsArray().ToDictionary(a => (string)a!["Aim"]!, a => $"{a!["Identity"]}, credential {a["Credential"]}");
    }

    private static string Violations(string schemaName, JsonObject instance)
    {
        var schemas = Path.Combine(Repository.Root, "schemas");
        var schema = PublishedSchemas.At(schemas)[Path.GetFullPath(Path.Combine(schemas, "PTF", "V1.0", "data", schemaName + ".json"))];
        EvaluationResults evaluation;
        using var document = JsonDocument.Parse(instance.ToJsonString());
        lock (PublishedSchemas.Lock)
            evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid) return "valid";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in evaluation.Details ?? [])
            if (d.Errors is { Count: > 0 })
                foreach (var (k, m) in d.Errors)
                    found.Add($"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l : "/")} {k}: {Regex.Replace(m.Length > 90 ? m[..90] + "..." : m, @"\s+", " ")}");
        return string.Join("; ", found);
    }
}
