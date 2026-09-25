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

// PHASE 13, STEP 3 (M3223 3.2): the fingerprints of Implementations in the Store,
// and the evidence of what an AIM runs. The Store records, as it approves an L3,
// the SHA-256 of the binary the L3 names; a Controller that verifies what it runs
// measures, before it builds an AIM, the binary its provider names and the models
// its settings name, and refuses the Module where the binary is not the one the
// Store approved or a model not the one the settings declare. An AIM on a host is
// measured there, and the evidence signed by the key it holds there.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustEvidenceTests
{
    private static string TestBinary => typeof(RemoteAims).Assembly.Location;

    // A copy of the test Store of Phase 5, its L3s approved with the fingerprint
    // given - by default the test binary's - or not approved at all.
    public static string ApprovedCopy(bool approve = true, Func<string, string>? fingerprintFor = null)
    {
        var copy = Path.Combine(Path.GetTempPath(), "mpai-phase13-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.EnumerateFiles(RemoteTests.Amds, "*.json")) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        if (approve)
        {
            var fingerprints = new ImplementationFingerprints(copy);
            var real = ImplementationFingerprints.Of(TestBinary);
            foreach (var file in Directory.EnumerateFiles(copy, "*.json"))
            {
                var aim = (string?)JsonNode.Parse(File.ReadAllText(file))?["Identifier"]?["AIMName"];
                if (aim is not null) fingerprints.Approve(aim, "Mpai.Aif.Tests", fingerprintFor?.Invoke(aim) ?? real, DateTimeOffset.UtcNow);
            }
        }
        return copy;
    }

    // THE STORE APPROVES an L3 it holds, with its binary: the fingerprint of the
    // binary it names recorded. One without its binary: nothing recorded, and said.
    [Fact]
    public void StoreApproves()
    {
        var result = new Dictionary<string, string>();
        var folder = Path.Combine(Path.GetTempPath(), "mpai-phase13-approve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var l3 in new[] { "1TST-UPP-V1.0-I01.json", "1TST-SLP-V1.0-I01.json" })
            File.Copy(Path.Combine(RemoteTests.Amds, l3), Path.Combine(folder, l3));
        var store = new MpaiStore(folder);

        var approved = store.Approve("1TST-UPP-V1.0-I01", new Dictionary<string, string> { ["Mpai.Aif.Tests"] = TestBinary });
        result["an L3 approved with its binary"] = approved.IsValid ? "approved" + (approved.Warnings.Count > 0 ? "; " + string.Join("; ", approved.Warnings) : "") : string.Join("; ", approved.Errors);
        var recorded = new ImplementationFingerprints(folder).Approved("1TST-UPP-V1.0-I01", "Mpai.Aif.Tests");
        result["the fingerprint recorded"] = recorded == ImplementationFingerprints.Of(TestBinary) ? "the binary's SHA-256" : recorded ?? "none";

        var without = store.Approve("1TST-SLP-V1.0-I01", new Dictionary<string, string>());
        result["an L3 approved without its binary"] = string.Join("; ", without.Warnings);
        result["its fingerprint"] = new ImplementationFingerprints(folder).Approved("1TST-SLP-V1.0-I01", "Mpai.Aif.Tests") ?? "none recorded";
        result["an AIM not in the store"] = string.Join("; ", store.Approve("1TST-XXX-V1.0-I01", new Dictionary<string, string>()).Errors);

        // The Store's own check of Metadata, as it is: Publish refuses a current L3.
        var published = new MpaiStore(Path.Combine(folder, "publish")).Publish(File.ReadAllText(Path.Combine(RemoteTests.Amds, "1TST-UPP-V1.0-I01.json")));
        result["the Store publishing a current L3 (a finding)"] = published.WasPublished ? "published" : "refused: " + string.Join("; ", published.Errors);
        Expected.Match("trust-store.json", result);
    }

    // WHAT A MODULE RUNS, checked: approved, it starts, each AIM with its evidence;
    // otherwise it is refused, and says why.
    [Fact]
    public void Modules()
    {
        var result = new Dictionary<string, string>();

        result["approved: TST-RXL"] = Start(ApprovedCopy(), "TST-RXL", out var trust, out var instance);
        foreach (var aim in trust!.OfModule(instance!))
        {
            var evidence = aim.Evidence!;
            result[$"approved: {aim.Id[(instance!.Length + 1)..]}'s evidence"] =
                $"{Violations("AttestationEvidence", evidence)}; signed by the Controller: {PtfSignature.Verify(evidence, trust.KeyFor)}; " +
                string.Join(", ", PtfEvidence.Items(evidence).Select(i => $"{i.Type} {i.What} {(i.Hash == ImplementationFingerprints.Of(TestBinary) ? "(the binary's SHA-256)" : i.Hash[..12])}"));
        }

        result["not approved"] = Start(ApprovedCopy(approve: false), "TST-RXL", out _, out _);
        result["one AIM approved with another fingerprint"] = Start(ApprovedCopy(fingerprintFor: aim => aim == "1TST-SLP-V1.0-I01" ? new string('0', 64) : ImplementationFingerprints.Of(TestBinary)), "TST-RXL", out _, out _);
        result["a provider that names another binary"] = Start(ApprovedCopy(), "TST-RXL", out _, out _, new NamingAnother());
        result["a provider that cannot say"] = Start(ApprovedCopy(), "TST-RXL", out _, out _, new CannotSay());
        result["without trust, not approved"] = Start(ApprovedCopy(approve: false), "TST-RXL", out _, out _, trusted: false);

        // A MODEL its settings name, with the SHA-256 they declare - and another.
        var model = Path.Combine(Path.GetTempPath(), "mpai-phase13-model-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(model, "a model"u8.ToArray());
        var hash = ImplementationFingerprints.Of(model);
        result["a model with the SHA-256 its settings declare"] = Start(ApprovedCopy(), "TST-RXL", out var withModel, out var modelInstance,
            settings: Settings("1TST-UPP-V1.0-I01", ("ModelPath", model), ("SHA256:ModelPath", hash)));
        result["its evidence"] = string.Join(", ", PtfEvidence.Items(withModel!.OfModule(modelInstance!).First(a => a.Id.EndsWith("1TST-UPP-V1.0-I01")).Evidence!)
            .Select(i => $"{i.Type} {i.What.Replace(Path.GetFileName(model), "<model>")}"));
        result["a model with another SHA-256"] = Start(ApprovedCopy(), "TST-RXL", out _, out _,
            settings: Settings("1TST-UPP-V1.0-I01", ("ModelPath", model), ("SHA256:ModelPath", new string('A', 64))));
        result["a model declared and not there"] = Start(ApprovedCopy(), "TST-RXL", out _, out _,
            settings: Settings("1TST-UPP-V1.0-I01", ("ModelPath", model + ".missing"), ("SHA256:ModelPath", hash)));
        Expected.Match("trust-evidence.json", result);
    }

    // AIMs ON A HOST: measured there, the evidence signed by the key each holds there,
    // checked by the Controller against its Store.
    [Fact]
    public void OnAHost()
    {
        var result = new Dictionary<string, string>();
        using var hostProcess = new HostProcess();
        string[] placed = ["1TST-UPP-V1.0-I01", "1TST-SLP-V1.0-I01", "1TST-RPT-V1.0-I01"];

        result["approved: TST-RXC with three AIMs on a host"] = Start(ApprovedCopy(), "TST-RXC", out var trust, out var instance, host: hostProcess, placed: placed);
        foreach (var aim in trust!.OfModule(instance!).Where(a => !a.KeyHeldHere))
        {
            using var key = PtfIdentity.PublicKey(aim.Cii)!;
            result[$"{aim.Id[(instance!.Length + 1)..]}'s evidence"] =
                $"{Violations("AttestationEvidence", aim.Evidence!)}; signed by the AIM's key on the host: {PtfSignature.Verify(aim.Evidence!, _ => key)}; " +
                string.Join(", ", PtfEvidence.Items(aim.Evidence!).Select(i => $"{i.Type} {i.What}"));
        }
        result["one AIM on the host approved with another fingerprint"] = Start(
            ApprovedCopy(fingerprintFor: aim => aim == "1TST-SLP-V1.0-I01" ? new string('0', 64) : ImplementationFingerprints.Of(TestBinary)),
            "TST-RXC", out _, out _, host: hostProcess, placed: placed).Replace(hostProcess.Address, "<host>");
        Expected.Match("trust-evidence-host.json", result);
    }

    private static string Start(string amds, string module, out TrustDomain? trust, out string? instance,
                                IAimProvider? provider = null, string? settings = null, bool trusted = true,
                                HostProcess? host = null, string[]? placed = null)
    {
        trust = trusted ? new TrustDomain("controller-1") : null;
        instance = null;
        using var api = new ControllerApi(amds, settings ?? Path.Combine(amds, "no-settings.json"), provider ?? new RemoteAims());
        api.Controller.Trust = trust;
        if (host is not null)
        {
            api.Controller.AimHostKey = HostProcess.Key;
            foreach (var aim in placed ?? []) api.Controller.AimHosts[aim] = host.Address;
        }
        var name = $"1{module}-V1.0-I01";
        // The measured SHA-256 of a binary changes with each build: not recorded.
        static string Masked(string? why) => Regex.Replace(why ?? "", @"SHA-256 [0-9A-F]{16}\.\.\.,", "SHA-256 <as measured>,");
        AifError outcome;
        try { outcome = api.StartFlow(name); }
        catch (InvalidOperationException refused) { return "refused: " + Masked(refused.Message); }
        if (outcome == AifError.NotTrusted) return "NOT_TRUSTED: " + Masked(api.Controller.LastRefusal);
        if (outcome != AifError.OK) return outcome.ToString();
        instance = api.Controller.InstanceName(name);
        var run = api.Advance(name, [new ControllerApi.Datum(RemoteTests.Text, "x")]);
        api.StopFlow(name);
        return $"started; an exchange: {run.Error}";
    }

    private static string Settings(string aim, params (string Key, string Value)[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), "mpai-phase13-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var obj = new JsonObject();
        foreach (var (k, v) in values) obj[k] = v;
        File.WriteAllText(path, new JsonObject { [aim] = obj }.ToJsonString());
        return path;
    }

    // Providers that name the wrong binary, or none.
    private sealed class NamingAnother : IAimProvider
    {
        private readonly RemoteAims inner = new();
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => inner.Create(aimName, settings, storage);
        public string? ImplementationOf(string aimName) => typeof(PtfEvidence).Assembly.Location;
    }

    private sealed class CannotSay : IAimProvider
    {
        private readonly RemoteAims inner = new();
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => inner.Create(aimName, settings, storage);
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
