using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using AIF.Metadata;
using AIF.Store;
using AIF.Trust;
using Json.Schema;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 4 (M3223 3.3): the Controller is the Verifier. Before an AIM runs,
// the Verification Pipeline checks its identity, its credential through the chain
// presented with it, its lifecycle, the evidence of what it runs and the policy it
// is bound by - derived from its approved Metadata; each check a signed Trust
// Operation (PTF-TOP). A package - one binary whose L3 names its SubAIMs Packaged -
// is credentialed as an issuer, and presents each AIM inside it, verified through
// the chain to the Controller. An AIM not trusted refuses the Module: NOT_TRUSTED.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustVerificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static string TestBinary => typeof(RemoteAims).Assembly.Location;
    private static string Phase13 => Path.Combine(Repository.Root, "Test", "Data", "Phase13");

    // THE PIPELINE, step by step: what passes, and where each failure stops it.
    [Fact]
    public void Pipeline()
    {
        var result = new Dictionary<string, string>();
        var now = T0;
        var trust = new TrustDomain("controller-1", () => now);
        const string id = "TST#1/1TST-UPP-V1.0-I01";
        var aim = trust.IssueLocal(id, "1TST-UPP-V1.0-I01", null);
        var code = new PtfEvidence.Item(PtfEvidence.CodeHash, "Mpai.Aif.Tests.dll", new string('A', 64));
        aim.Evidence = trust.SignEvidence(id, [code]);
        (string, string)[] allowed = [("Port", "Input TST-TXT-V1.0#1"), ("Port", "Output TST-TXT-V1.0#1")];
        var policy = trust.BindPolicy(id, allowed);

        IReadOnlyList<string> SameCode(IReadOnlyList<PtfEvidence.Item> items) =>
            items.Any(i => i.Type == PtfEvidence.CodeHash && i.Hash == code.Hash) ? [] : ["not the binary approved"];
        IReadOnlyList<string> Allowed(JsonObject bound) =>
            (bound["Constraints"] as JsonArray)!.Select(c => ((string)c!["Name"]!, (string)c["Value"]!)).OrderBy(c => c).SequenceEqual(allowed.OrderBy(c => c))
                ? [] : ["its policy is not what its approved Metadata allows"];
        VerificationPipeline.Expectation Expect(string instanceId = id, string state = "Created", JsonObject? bound = null) =>
            new(instanceId, state, EvidenceRequired: true, CheckEvidence: SameCode, Policy: bound ?? policy, CheckPolicy: Allowed);
        string Run(TrustDomain.Instance instance, VerificationPipeline.Expectation e, IReadOnlyList<PtfCredential.Link>? chain = null)
        {
            var decision = trust.Verify(instance, e, chain);
            return $"{(decision.Trusted ? "trusted" : "not trusted: " + decision.Reason)}; " +
                   string.Join(", ", decision.Operations.Select(o => $"{o["OperationType"]} {o["Status"]}"));
        }

        result["all as expected"] = Run(aim, Expect());
        result["the verdict recorded"] = aim.Verdict;
        result["expected as another instance"] = Run(aim, Expect("TST#1/1TST-XXX-V1.0-I01"));
        result["expected Running, while Created"] = Run(aim, Expect(state: "Running"));

        var other = trust.IssueLocal("TST#1/1TST-SLP-V1.0-I01", "1TST-SLP-V1.0-I01", null);
        var presentingAnother = new TrustDomain.Instance { Id = id, Cii = aim.Cii, Credential = other.Credential, Lifecycle = aim.Lifecycle, Evidence = aim.Evidence };
        result["a credential for another CII"] = Run(presentingAnother, Expect());

        var elsewhere = new TrustDomain("controller-2", () => now);
        var foreign = elsewhere.IssueLocal(id, "1TST-UPP-V1.0-I01", null);
        result["a credential from another Controller"] = Run(foreign, Expect());

        var noEvidence = new TrustDomain.Instance { Id = id, Cii = aim.Cii, Credential = aim.Credential, Lifecycle = aim.Lifecycle };
        result["no evidence"] = Run(noEvidence, Expect());
        var otherCode = new TrustDomain.Instance { Id = id, Cii = aim.Cii, Credential = aim.Credential, Lifecycle = aim.Lifecycle,
                                                   Evidence = trust.SignEvidence(id, [code with { Hash = new string('B', 64) }]) };
        result["evidence of another binary"] = Run(otherCode, Expect());
        var forged = aim.Evidence!.DeepClone().AsObject();
        forged["EvidenceItems"]![0]!["HashValue"] = new string('B', 64);
        var forgedEvidence = new TrustDomain.Instance { Id = id, Cii = aim.Cii, Credential = aim.Credential, Lifecycle = aim.Lifecycle, Evidence = forged };
        result["evidence changed after it was signed"] = Run(forgedEvidence, Expect());

        var widened = policy.DeepClone().AsObject();
        ((JsonArray)widened["Constraints"]!).Add(new JsonObject { ["Name"] = "Port", ["Value"] = "Output TST-TXT-V1.0#2" });
        result["a policy changed after it was bound"] = Run(aim, Expect(bound: widened));
        result["a policy bound for another instance"] = Run(aim, Expect(bound: trust.BindPolicy("TST#1/1TST-SLP-V1.0-I01", allowed)));
        result["a policy bound with more than the Metadata allows"] = Run(aim, Expect(bound: trust.BindPolicy(id, [.. allowed, ("Port", "Output TST-TXT-V1.0#2")])));

        // THROUGH A PACKAGE: its credential entitles it to issue; one without that
        // entitlement does not.
        var package = trust.IssueLocal("TST#1/1TST-PKG-V1.0-I01", "1TST-PKG-V1.0-I01", null, issuer: true);
        var plain = trust.IssueLocal("TST#1/1TST-PLN-V1.0-I01", "1TST-PLN-V1.0-I01", null);
        foreach (var (what, issuerInstance) in new[] { ("a package", package), ("an AIM not entitled to issue", plain) })
        {
            var innerId = issuerInstance.Id + "/1TST-REV-V1.0-I01";
            using var innerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var cii = PtfIdentity.Make(innerKey, innerId, null, now);
            var issued = new PtfIssuer(issuerInstance.Id, trust.KeyOf(issuerInstance.Id)!, issuerInstance.Id)
                .IssueCredential(cii, "AIMInstance", innerId, PtfIdentity.AimType("1TST-REV-V1.0-I01"), now, TrustDomain.CredentialLifetime);
            var inner = trust.RecordPackaged(innerId, issuerInstance.Id, cii, issued, null);
            var link = new PtfCredential.Link(issuerInstance.Cii, issuerInstance.Credential);
            result[$"an AIM inside {what}, with its chain"] = Run(inner, new VerificationPipeline.Expectation(innerId), [link]);
            result[$"an AIM inside {what}, without its chain"] = Run(inner, new VerificationPipeline.Expectation(innerId));
        }

        // Each check a Trust Operation, signed by the Controller; each policy a PTF-POL.
        result["the Trust Operations"] = $"{trust.Operations.Count}; " +
            string.Join(", ", trust.Operations.GroupBy(o => $"{o["OperationType"]} {o["Status"]}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));
        result["the Trust Operations against PTF-TOP"] = string.Join("; ", trust.Operations.Select(o => Violations("TrustOperation", o)).Distinct());
        result["the Trust Operations signed by the Controller"] = string.Join("; ", trust.Operations.Select(o => PtfSignature.Verify(o, trust.KeyFor).ToString()).Distinct());
        result["a policy against PTF-POL"] = Violations("PolicyBinding", policy);
        Expected.Match("trust-pipeline.json", result);
    }

    // A MODULE: every AIM verified before it runs, a package's AIMs through it; a
    // Module with an AIM not trusted refused, NOT_TRUSTED, and why.
    [Fact]
    public void Modules()
    {
        var result = new Dictionary<string, string>();
        result["TST-RXP, with the package TST-PKG"] = Start(Store(), "TST-RXP", Presents.Honestly, out var trust, out var instance);
        foreach (var aim in trust!.OfModule(instance!))
            result[$"TST-RXP {aim.Id[(instance!.Length + 1)..]}"] =
                $"{aim.Verdict}; credential issued by {Shown((string)aim.Credential["Issuer"]!["KeyID"]!, instance)}" +
                (aim.Credential["Validity"]!["Scope"] is { } scope ? $", scope {scope}" : "") +
                $"; policy {(aim.Policy is null ? "none" : string.Join(", ", aim.Policy["Constraints"]!.AsArray().Where(c => (string)c!["Name"]! == "Port").Select(c => (string)c!["Value"]!)))}" +
                $"; lifecycle {string.Join(" > ", aim.States)}";

        result["a package presenting one AIM of two"] = Start(Store(), "TST-RXP", Presents.OneOfTwo, out _, out _);
        result["a package presenting an AIM it does not contain"] = Start(Store(), "TST-RXP", Presents.AnotherAim, out _, out _);
        result["a package issuing with a key of its own making"] = Start(Store(), "TST-RXP", Presents.WithItsOwnKey, out _, out _);
        result["a package presenting a credential for another CII"] = Start(Store(), "TST-RXP", Presents.AnotherCii, out _, out _);
        result["a package whose AIM runs another binary"] = Start(Store(), "TST-RXP", Presents.AnotherBinary, out _, out _);
        result["an implementation that does not present its AIMs"] = Start(Store(), "TST-RXP", Presents.Nothing, out _, out _);
        result["a package not approved"] = Start(Store(approvePackage: false), "TST-RXP", Presents.Honestly, out _, out _);
        result["a package that is not all Packaged"] = Start(Store(), "TST-RXM", Presents.Honestly, out _, out _);
        result["without trust, the package"] = Start(Store(), "TST-RXP", Presents.Nothing, out _, out _, trusted: false);
        Expected.Match("trust-verify.json", result);
    }

    private static string Shown(string text, string? instance) => instance is null ? text : text.Replace(instance, "<instance>");

    // The test Store of Phase 5 and the packages of Phase 13, approved.
    private static string Store(bool approvePackage = true)
    {
        var copy = TrustEvidenceTests.ApprovedCopy();
        var fingerprints = new ImplementationFingerprints(copy);
        foreach (var file in Directory.EnumerateFiles(Phase13, "*.json"))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            var aim = Path.GetFileNameWithoutExtension(file);
            if (approvePackage || aim != "1TST-PKG-V1.0-I01")
                fingerprints.Approve(aim, "Mpai.Aif.Tests", ImplementationFingerprints.Of(TestBinary), DateTimeOffset.UtcNow);
        }
        return copy;
    }

    private static string Start(string amds, string module, Presents presents, out TrustDomain? trust, out string? instance, bool trusted = true)
    {
        trust = trusted ? new TrustDomain("controller-1") : null;
        instance = null;
        using var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), new PackageAims(presents));
        api.Controller.Trust = trust;
        var name = $"1{module}-V1.0-I01";
        AifError outcome;
        try { outcome = api.StartFlow(name); }
        catch (InvalidOperationException refused) { return "refused: " + refused.Message; }
        if (outcome == AifError.NotTrusted)
            return "NOT_TRUSTED: " + Regex.Replace(api.Controller.LastRefusal ?? "", @"1TST-RXP-V1\.0-I01#[0-9a-f]{32}", "<instance>");
        if (outcome != AifError.OK) return outcome.ToString();
        instance = api.Controller.InstanceName(name);
        var run = api.Advance(name, [new ControllerApi.Datum(RemoteTests.Text, "abc")]);
        api.StopFlow(name);
        return $"started; an exchange: {run.Error}; " + string.Join("; ", run.Outputs.OrderBy(o => o.PortNumber).Select(o => $"#{o.PortNumber} '{o.Json}'"));
    }

    public enum Presents { Honestly, OneOfTwo, AnotherAim, WithItsOwnKey, AnotherCii, AnotherBinary, Nothing }

    // The test AIMs, with TST-PKG: TST-UPP and TST-REV inside one binary.
    private sealed class PackageAims(Presents presents) : IAimProvider
    {
        private readonly RemoteAims inner = new();
        public string? ImplementationOf(string aimName) => TestBinary;
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
            aimName == "1TST-PKG-V1.0-I01"
                ? presents == Presents.Nothing ? new Plain(aimName) : new TestPackage(aimName, presents)
                : inner.Create(aimName, settings, storage);
    }

    private class Plain(string instanceId) : IAimProcessor
    {
        public string InstanceId => instanceId;
        public Task<Message> ProcessAsync(Message m)
        {
            var text = m.Ports.TryGetValue("Text", out var t) ? t : "";
            return Task.FromResult(new Message { Ports = new Dictionary<string, string>
            {
                ["Upper"] = text.ToUpperInvariant(), ["Reversed"] = new string(text.Reverse().ToArray())
            } });
        }
    }

    private sealed class TestPackage(string instanceId, Presents presents) : Plain(instanceId), IAimPackage
    {
        public IReadOnlyList<PackagedAim> Present(PtfIssuer asIssuer, string packageInstanceId, DateTimeOffset now)
        {
            string[] aims = presents switch
            {
                Presents.OneOfTwo   => ["1TST-UPP-V1.0-I01"],
                Presents.AnotherAim => ["1TST-UPP-V1.0-I01", "1TST-ECH-V1.0-I01"],
                _                   => ["1TST-UPP-V1.0-I01", "1TST-REV-V1.0-I01"]
            };
            var measured = new PtfEvidence.Item(PtfEvidence.CodeHash, Path.GetFileName(TestBinary),
                presents == Presents.AnotherBinary ? new string('0', 64) : ImplementationFingerprints.Of(TestBinary));
            using var own = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var issuer = presents == Presents.WithItsOwnKey ? new PtfIssuer(packageInstanceId, own, packageInstanceId) : asIssuer;
            return aims.Select(aim =>
            {
                var id = $"{packageInstanceId}/{aim}";
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var cii = PtfIdentity.Make(key, id, aim, now);
                var credentialFor = presents == Presents.AnotherCii ? PtfIdentity.Make(ECDsa.Create(ECCurve.NamedCurves.nistP256), id, aim, now) : cii;
                var credential = issuer.IssueCredential(credentialFor, "AIMInstance", id, PtfIdentity.AimType(aim), now, TrustDomain.CredentialLifetime);
                var evidence = PtfEvidence.Make(id, [measured], packageInstanceId, key, id);
                return new PackagedAim(aim, cii, credential, evidence);
            }).ToList();
        }
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
