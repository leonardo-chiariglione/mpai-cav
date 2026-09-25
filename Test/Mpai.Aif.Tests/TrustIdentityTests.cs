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
// A Trust Anchor the deployment configures issues the Controller its credential;
// the Controller issues each AIM Instance it starts an Instance Credential for the
// CII of a key of its own, and a Process Lifecycle Credential at each change of
// state. An AIM on a host makes its key there, and is issued its credential only if
// its CII shows it holds that key.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustIdentityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static TrustAnchorKey Anchor(out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new TrustAnchorKey("anchor.test.mpai", key, T0.AddDays(-1), T0.AddYears(1));
    }

    // THE OBJECTS, and the chain from the anchor to an AIM Instance.
    [Fact]
    public void Identities()
    {
        var result = new Dictionary<string, string>();
        var anchor = Anchor(out _);
        var now = T0;
        var trust = new TrustDomain(anchor, "controller-1", () => now);
        var aim = trust.IssueLocal("TST#1/1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01");

        result["the Trust Anchor against its schema"] = Violations("TrustAnchor", anchor.Object());
        result["the Controller's CII against its schema"] = Violations("CryptographicInstanceIdentity", trust.ControllerCii);
        result["the Controller's credential against its schema"] = Violations("InstanceCredential", trust.ControllerCredential);
        result["the AIM's CII against its schema"] = Violations("CryptographicInstanceIdentity", aim.Cii);
        result["the AIM's credential against its schema"] = Violations("InstanceCredential", aim.Credential);
        result["the AIM's lifecycle credential against its schema"] = Violations("ProcessLifecycleCredential", aim.Lifecycle);

        ECDsa? AnchorOnly(string id) => id == anchor.AnchorId ? anchor.PublicKey : null;
        result["the Controller's CII, checked"] = PtfIdentity.Check(trust.ControllerCii, out _).ToString();
        result["the Controller's credential, by the anchor"] = PtfCredential.CheckCredential(trust.ControllerCredential, trust.ControllerCii, AnchorOnly, now).ToString();
        result["the AIM's CII, checked"] = PtfIdentity.Check(aim.Cii, out _).ToString();
        result["the AIM's credential, by the Controller"] = PtfCredential.CheckCredential(aim.Credential, aim.Cii, trust.KeyFor, now).ToString();
        result["the AIM's credential, by the anchor alone"] = PtfCredential.CheckCredential(aim.Credential, aim.Cii, AnchorOnly, now).ToString();
        var chain = PtfCredential.Through(trust.ControllerCredential, trust.ControllerCii, anchor.AnchorId, anchor.PublicKey, now);
        result["the AIM's credential, by the anchor through the Controller's credential"] = chain is null ? "the Controller's credential does not check"
            : PtfCredential.CheckCredential(aim.Credential, aim.Cii, chain, now).ToString();
        var forged = trust.ControllerCredential.DeepClone().AsObject();
        forged["Subject"]!["Specification"] = "AIF-XXX";
        result["the same, the Controller's credential altered"] = PtfCredential.Through(forged, trust.ControllerCii, anchor.AnchorId, anchor.PublicKey, now) is null
            ? "the chain breaks" : "the chain holds";
        result["the AIM's lifecycle: Created"] = PtfCredential.CheckLifecycle(aim.Lifecycle, aim.Id, "Created", trust.KeyFor, now).ToString();
        result["the AIM's credential says"] = $"{aim.Credential["Subject"]!["InstanceType"]} {aim.Credential["Subject"]!["InstanceID"]}, of {aim.Credential["Subject"]!["Specification"]}, issued by {aim.Credential["Issuer"]!["Name"]}";

        var key = trust.KeyOf(aim.Id)!;
        using var cii = PtfIdentity.PublicKey(aim.Cii)!;
        var probe = "held"u8.ToArray();
        result["the AIM's key, the one its CII names"] = cii.VerifyData(probe, key.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256) ? "yes" : "no";
        result["another AIM's key"] = trust.IssueLocal("TST#1/1TST-SLP-V1.0-I01", "1TST-SLP-V1.0-I01", null).Cii["CryptographicBinding"]!["PublicKey"]!["KeyValue"]!.ToString()
            == aim.Cii["CryptographicBinding"]!["PublicKey"]!["KeyValue"]!.ToString() ? "the same" : "its own";
        Expected.Match("trust-identities.json", result);
    }

    // WHAT IS REFUSED: a CII that is not what it says, a credential changed, for
    // another CII, out of its time, or from an issuer not trusted; a lifecycle not in
    // the state claimed; an AIM on a host that does not hold the key it names.
    [Fact]
    public void Refused()
    {
        var result = new Dictionary<string, string>();
        var anchor = Anchor(out _);
        var now = T0;
        var trust = new TrustDomain(anchor, "controller-1", () => now);
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

        var elsewhere = new TrustDomain(Anchor(out _), "controller-2", () => now);
        result["a credential from a Controller of another deployment"] = PtfCredential.CheckCredential(
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
        using var hostProcess = new HostProcess();
        foreach (var (module, placed) in new[] { ("TST-RXL", Array.Empty<string>()), ("TST-RXC", new[] { "1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01" }) })
        {
            var now = T0;
            var trust = new TrustDomain(Anchor(out _), "controller-1");
            using var api = new ControllerApi(RemoteTests.Amds, Path.Combine(RemoteTests.Amds, "no-settings.json"), new RemoteAims());
            api.Controller.Trust = trust;
            api.Controller.AimHostKey = HostProcess.Key;
            foreach (var aim in placed) api.Controller.AimHosts[aim] = hostProcess.Address;

            var name = $"1{module}-V1.0-I01";
            api.StartFlow(name);
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
