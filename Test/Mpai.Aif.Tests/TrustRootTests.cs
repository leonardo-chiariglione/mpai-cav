using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Channels;
using AIF.Controller;
using AIF.Metadata;
using AIF.RootOfTrust;
using AIF.Trust;
using Json.Schema;
using Mpai.Aif.Api;
using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 6 (M3223 3.5): the simulated root of trust. Each party - the
// Controller, each AIM host - has a TPM 2.0 (Microsoft's reference simulator) that
// holds its identity key and never releases it, measures its code and each
// Implementation it loads, quotes them over a nonce the verifier chose under an
// attestation key its manufacturer certified, and seals secrets to them. A verifier
// detects a party whose measurements, keys or quotes are not what they should be.
// The TPMs are shared: these tests run alone.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class TrustRootTests(ITestOutputHelper output)
{
    private static readonly Lazy<SimulatedManufacturer> manufacturer = new(() => SimulatedManufacturer.Make());
    private static string Bin => AppContext.BaseDirectory;
    private static Dictionary<string, string> Reference(string directory) =>
        PartyCode.Of(directory).ToDictionary(c => c.File, c => c.Sha256, StringComparer.Ordinal);

    // A party provisioned in the TPM on this port: its file.
    private static string Provision(string id, int port)
    {
        var (host, at) = TpmSimulators.At(port);
        var path = Path.Combine(Path.GetTempPath(), $"mpai-phase13-tpm-{id}-{Guid.NewGuid():N}.json");
        TpmParty.Provision(path, id, host, at, manufacturer.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        return path;
    }

    // THE TPM ALONE: its keys, its quote, what it seals; and what a verifier detects.
    [Fact]
    public void Tpm()
    {
        var result = new Dictionary<string, string>();
        var file = Provision("party-1", TpmSimulators.Alone);
        var (anchor, tpm) = TpmParty.Load(file);
        using (tpm)
        {
            var policy = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(Reference(Bin), m));
            PartyCode.MeasureInto(tpm, Bin);
            result["its code measured"] = $"{tpm.Log.Count} files into register {Attestation.CodeRegister}";

            // Its identity key: it signs; its private part cannot be had.
            var signed = PtfSignature.Sign(new JsonObject { ["Header"] = "PTF-TOP-V1.0", ["What"] = "signed in the TPM" }, tpm.IdentityKey, anchor.AnchorId);
            result["a PTF object signed by the identity key"] = PtfSignature.Verify(signed, id => id == anchor.AnchorId ? anchor.PublicKey : null).ToString();
            result["its private key, exported"] = Try(() => tpm.IdentityKey.ExportParameters(true));
            result["its private key, as PKCS#8"] = Try(() => tpm.IdentityKey.ExportPkcs8PrivateKey());
            result["its private key, duplicated out of the TPM"] = $"refused by the TPM: {tpm.TryDuplicateIdentityKey()} (the key is FixedTPM, and has no policy that would let it be duplicated)";

            // The quote, over a nonce the verifier chose.
            var nonce = RandomNumberGenerator.GetBytes(32);
            var evidence = tpm.Evidence(anchor.AnchorId, nonce);
            string Checked(JsonObject e, byte[] n, Attestation.Policy p) =>
                Attestation.Check(e, anchor.PublicKey, anchor.AnchorId, n, p, DateTimeOffset.UtcNow) ?? "proven";
            result["the evidence against PTF-ATE"] = Violations("AttestationEvidence", evidence);
            result["the evidence, checked"] = Checked(evidence, nonce, policy);
            result["the evidence, over an old nonce"] = Checked(evidence, RandomNumberGenerator.GetBytes(32), policy);
            var altered = Reference(Bin);
            altered["AIF.Controller.dll"] = new string('0', 64);
            result["the evidence, the code altered"] = Checked(evidence, nonce, new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(altered, m)));
            result["the evidence, from a TPM of another manufacturer"] = Checked(evidence, nonce, new Attestation.Policy(SimulatedManufacturer.Make("another").Root, policy.Approve));

            var shorter = evidence.DeepClone().AsObject();
            ((JsonArray)shorter["EvidenceItems"]!).RemoveAt(2);
            PtfSignature.Sign(shorter, tpm.IdentityKey, anchor.AnchorId);
            result["the evidence, a measurement left out of its log"] = Checked(shorter, nonce, policy);
            var changed = evidence.DeepClone().AsObject();
            changed["EvidenceItems"]![1]!["HashValue"] = new string('0', 64);
            result["the evidence, changed after it was signed"] = Checked(changed, nonce, policy);

            // Sealed: released only while the registers hold.
            var secret = Encoding.UTF8.GetBytes("a secret of this party");
            var sealedSecret = tpm.Seal(secret);
            result["a secret sealed, unsealed while the registers hold"] = tpm.Unseal(sealedSecret) is { } s ? Encoding.UTF8.GetString(s) : "refused";
            tpm.Measure(Attestation.ImplementationRegister, "implementation 1TST-UPP-V1.0-I01 Mpai.Aif.Tests", new string('A', 64));
            result["a secret sealed, unsealed after another measurement"] = tpm.Unseal(sealedSecret) is { } t ? Encoding.UTF8.GetString(t) : "refused";
        }

        // Booted again: its registers from zero, its identity the same.
        var (again, rebooted) = TpmParty.Load(file);
        using (rebooted)
        {
            result["booted again: its identity key"] = again.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(anchor.PublicKey.ExportSubjectPublicKeyInfo()) ? "the same" : "another";
            result["booted again: its log"] = $"{rebooted.Log.Count} measurements";
        }
        // Its file pointing at another TPM, which does not hold its key.
        var elsewhere = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        elsewhere["Tpm"] = $"127.0.0.1:{TpmSimulators.At(TpmSimulators.Controller).Port}";
        var moved = file + ".moved.json";
        File.WriteAllText(moved, elsewhere.ToJsonString());
        result["a party whose file names a TPM that does not hold its key"] =
            Try(() => { TpmParty.Load(moved).Tpm.Dispose(); return null; }).Replace(file + ".moved.json", "<file>").Replace(moved, "<file>");
        Expected.Match("trust-root.json", result);
    }

    private static string Try(Func<object?> act)
    {
        try { act(); return "given"; }
        catch (Exception e) { return $"refused: {e.Message}"; }
    }

    // A LINK BETWEEN ATTESTED PARTIES, in this process: each quotes over the other's
    // certificate for the link; each checks the other's code.
    [Fact]
    public async Task Link()
    {
        var result = new Dictionary<string, string>();
        var (controllerAnchor, controllerTpm) = TpmParty.Load(Provision("controller-1", TpmSimulators.Controller));
        var (hostAnchor, hostTpm) = TpmParty.Load(Provision("aimhost-1", TpmSimulators.Host));
        using (controllerTpm)
        using (hostTpm)
        {
            PartyCode.MeasureInto(controllerTpm, Bin);
            PartyCode.MeasureInto(hostTpm, Bin);
            var genuine = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(Reference(Bin), m));
            var altered = Reference(Bin);
            altered["AIF.AimHost.dll"] = new string('0', 64);
            var alteredPolicy = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(altered, m));

            TrustProtocol Controller(IRootOfTrust? attestor, Attestation.Policy? requires) =>
                new(controllerAnchor, controllerTpm.IdentityKey, [hostAnchor.Object()], null, attestor, requires);
            TrustProtocol Host(IRootOfTrust? attestor, Attestation.Policy? requires) =>
                new(hostAnchor, hostTpm.IdentityKey, [controllerAnchor.Object()], null, attestor, requires);

            var opened = System.Diagnostics.Stopwatch.StartNew();
            result["both attested, each requiring it"] = await Open(Controller(controllerTpm, genuine), Host(hostTpm, genuine));
            output.WriteLine($"an attested link opened in {opened.ElapsedMilliseconds} ms");
            result["a host that is not attested, to a Controller that requires it"] = await Open(Controller(controllerTpm, genuine), Host(null, genuine));
            result["a Controller that is not attested, to a host that requires it"] = await Open(Controller(null, genuine), Host(hostTpm, genuine));
            result["a host whose code is not the code approved"] = await Open(Controller(controllerTpm, alteredPolicy), Host(hostTpm, genuine));
            result["a host replaying the evidence of an earlier link"] = await Open(Controller(controllerTpm, genuine), Host(new Replaying(hostTpm), genuine));
            result["a host attested by a TPM of another manufacturer"] = await Open(
                Controller(controllerTpm, new Attestation.Policy(SimulatedManufacturer.Make("another").Root, genuine.Approve)), Host(hostTpm, genuine));
        }
        Expected.Match("trust-root-link.json", result);
    }

    // A root of trust that gives, at every link, the evidence it gave the first time.
    private sealed class Replaying(IRootOfTrust inner) : IRootOfTrust
    {
        private JsonObject? first;
        public ECDsa IdentityKey => inner.IdentityKey;
        public void Measure(int register, string what, string sha256) => inner.Measure(register, what, sha256);
        public JsonObject Evidence(string partyId, byte[] nonce) => (JsonObject)(first ??= inner.Evidence(partyId, nonce)).DeepClone();
    }

    private static async Task<string> Open(TrustProtocol controller, TrustProtocol host)
    {
        string? hostSaw = null;
        var hostEnd = new TrustedLink(host) { Answered = (c, refused) => hostSaw = refused is null ? "admitted" : $"refused: {refused}" };
        await using var listener = new RemoteLink.Listener(0, hostEnd, _ => { });
        listener.Failed += why => hostSaw = $"failed: {why}";
        var outcomes = new List<string>();
        for (var i = 0; i < 2; i++)                                                   // twice: a new nonce each link
        {
            try
            {
                await using var link = await RemoteLink.ConnectAsync("localhost", listener.Port, new TrustedLink(controller));
                outcomes.Add($"admitted; the host {hostSaw}");
            }
            catch (UnauthorizedAccessException refused)
            {
                outcomes.Add($"refused - {Regex.Replace(refused.Message, @"The host at localhost:\d+: ", "")} The host: {hostSaw}");
            }
        }
        return outcomes[0] == outcomes[1] ? outcomes[0] : $"first link: {outcomes[0]} Second link: {outcomes[1]}";
    }

    // MODULES WITH AIMs ON AN ATTESTED HOST, from an attested Controller: as before;
    // a host whose code was altered, refused.
    [Fact]
    public void Modules()
    {
        var result = new Dictionary<string, string>();
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Repository.Root, "Test", "Expected", "remote-placed.json")))!;
        var amds = TrustEvidenceTests.ApprovedCopy();
        var controllerFile = Provision("controller-1", TpmSimulators.Controller);
        var controllerAnchor = TrustedParties.AnchorOf(controllerFile);
        var reference = Path.Combine(Path.GetTempPath(), $"mpai-phase13-reference-{Guid.NewGuid():N}.json");
        File.WriteAllText(reference, new JsonObject(Reference(Bin).Select(r => KeyValuePair.Create(r.Key, (JsonNode?)r.Value))).ToJsonString());
        var certificate = Path.Combine(Path.GetTempPath(), $"mpai-phase13-manufacturer-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(certificate, manufacturer.Value.Root.RawData);

        using (var host = AttestedHost(amds, Provision("aimhost-1", TpmSimulators.Host), controllerAnchor, certificate, reference, Bin))
        {
            foreach (var (module, continuous) in RemoteTests.Modules.Where(m => m.Module is "TST-RXC" or "TST-RLP"))
            {
                var aims = new RemoteAims();
                var (api, tpm) = Api(amds, aims, host, controllerFile);
                using (tpm)
                using (api)
                {
                    var now = RemoteTests.Run(api, module, continuous, aims);
                    result[$"{module}, both attested"] = now == before[module] ? "as before" : now + (api.Controller.LastRefusal is { } why ? $" ({why.Replace(host.Address, "<host>")})" : "")
                        + string.Concat(host.Output.Split(Environment.NewLine).Where(l => l.Contains("failed")).Select(l => " [host] " + l.Trim()));
                }
            }
        }

        // A host run from code altered after it was released: one byte added to its
        // binary. It runs; its measurement shows it.
        var alteredBin = Path.Combine(Path.GetTempPath(), $"mpai-phase13-altered-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(alteredBin);
        foreach (var f in Directory.EnumerateFiles(Bin)) File.Copy(f, Path.Combine(alteredBin, Path.GetFileName(f)));
        File.AppendAllText(Path.Combine(alteredBin, "AIF.AimHost.dll"), " ");
        using (var host = AttestedHost(amds, Provision("aimhost-2", TpmSimulators.AlteredHost), controllerAnchor, certificate, reference, alteredBin))
        {
            var (api, tpm) = Api(amds, new RemoteAims(), host, controllerFile);
            using (tpm)
            using (api)
            {
                var name = "1TST-RXC-V1.0-I01";
                var outcome = api.StartFlow(name);
                result["a host whose code was altered"] = outcome == AifError.NotTrusted
                    ? "NOT_TRUSTED: " + api.Controller.LastRefusal!.Replace(host.Address, "<host>") : outcome.ToString();
                if (outcome == AifError.OK) api.StopFlow(name);
            }
        }
        output.WriteLine($"links opened again after a transport failure, in this run: {RemoteLink.Reopened}");
        Expected.Match("trust-root-modules.json", result);
    }

    private static HostProcess AttestedHost(string amds, string hostFile, JsonObject controllerAnchor, string manufacturerCertificate, string reference, string bin) =>
        new(amds, hostFile, TrustedParties.TrustFile(controllerAnchor), bin, ["--manufacturer", manufacturerCertificate, "--reference", reference])
        { Anchor = TrustedParties.AnchorOf(hostFile) };

    private static (ControllerApi Api, SimulatedTpm Tpm) Api(string amds, RemoteAims aims, HostProcess host, string controllerFile)
    {
        var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), aims);
        var (anchor, tpm) = TpmParty.Load(controllerFile);
        api.Controller.Trust = new TrustDomain(anchor, tpm.IdentityKey);
        api.Controller.Attest(tpm);
        api.Controller.Manufacturer = manufacturer.Value.Root;
        foreach (var (file, sha256) in Reference(Bin)) api.Controller.HostCode[file] = sha256;
        api.Controller.HostAnchors.Add(host.Anchor!);
        foreach (var hosted in RemoteTests.Hosted.Values) foreach (var aim in hosted) api.Controller.AimHosts[aim] = host.Address;
        return (api, tpm);
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
