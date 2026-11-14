using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

namespace Mpai.Aih.Nnm;

// A NEURAL NETWORK MODEL AS AN AIM, PUBLISHED. What the Health Back End publishes, and the Controllers of the Health Front Ends obtain, is not the model but the AIM that wraps
// it (AIH-NNM-V1.1). The MPAI Store does not hold the AIM: it validates its L3 against the L2 and publishes it. THE MODEL IS DATA OF ACCESS (AIF V3.0 Storage 6.5): the Back End
// keeps the model file where it says, and the L3 names it in AccessSources by that URI and pins it with its SHA-256; the Controller of a Front End obtains it from there when
// the Module starts, refuses it if its SHA-256 is another, and gives it to the AIM as its ModelPath. The L3 is what the Store approved, so the pin is the Store's too.
public sealed class ModelAimPublisher
{
    public const string Type = "AIH-NNM-V1.1";

    private readonly string _folder = "", _implementations;
    private readonly MpaiStore? _library;                                      // the Store as a library (tests, one machine) ...
    private readonly HttpClient? _service;                                     // ... or the Store Service, reached as an Implementer (the Back End is one)
    private readonly string _implementerId = "";
    private readonly ImplementerKey? _key;

    private MpaiStore _store => _library ?? throw new InvalidOperationException("This publisher submits to the Store Service.");

    // THE PROFILE OF WHAT IT PUBLISHES (2026-11-13): Basic, or Secure - the model AIMs a Back End publishes for Secure Front Ends (1AIH-HFE-V1.1-I02) are Secure
    // Implementations too, built only where the party they execute in has the Security API. A Front End of one Profile is given no model AIM of the other.
    public string ApiProfile { get; init; } = "Basic";

    // folder: the MPAI Store's (L3s and approvals only). implementations: where the Back End keeps what it implemented, the models. conformance: the Store's check of an L3
    // against its L2 (AIF.Metadata.L2Conformance.Check). implementerId: the Back End's own Implementer ID - never MPAI's "1", whose reference L3s of this AIM
    // (1AIH-NNM-V1.1-I01, Basic; -I02, Secure) wrap the initial model (2026-11-13, author: "Back End own Implementer").
    public ModelAimPublisher(string folder, string implementations, Func<JsonElement, IReadOnlyList<string>> conformance, string implementerId = "hbe1")
    {
        _folder = folder; _implementations = implementations; _implementerId = implementerId;
        Directory.CreateDirectory(implementations);
        _library = new MpaiStore(folder, conformance);
    }

    // THE BACK END AS AN IMPLEMENTER OF THE STORE SERVICE: it is registered (as a machine), approved by MPAI, and signs what it submits with its key (AIF.Store.ImplementerKey).
    // The Store Service validates the L3, reads the model where the Back End keeps it - the folder must be one the Service inspects (its packages folder) - records its
    // fingerprint and publishes the L3 as a version. Nothing is written to the Store's folder from here.
    public ModelAimPublisher(string storeUrl, string implementerId, ImplementerKey key, string implementations)
    {
        _implementations = implementations; _implementerId = implementerId; _key = key;
        Directory.CreateDirectory(implementations);
        _service = new HttpClient { BaseAddress = new Uri(storeUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    // Submits an L3 to the Store Service, signed: the version it published, or why not.
    private string Submit(JsonObject l3)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "MPAI/Store/L3") { Content = new StringContent(l3.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("MPAI-Signature", _key!.Sign(l3));
        using var answer = _service!.SendAsync(request).GetAwaiter().GetResult();
        var body = answer.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (answer.StatusCode != System.Net.HttpStatusCode.Created) throw new InvalidOperationException($"The Store did not publish {l3["Identifier"]!["AIMName"]}: {body}");
        return body;
    }

    private string? Text(string path)
    {
        using var answer = _service!.GetAsync(path).GetAwaiter().GetResult();
        return answer.IsSuccessStatusCode ? answer.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
    }
    // THE BACK END PUBLISHES: wraps the model (an ML Model Object, AIP-MLO-V1.0) in a new AIM, keeps the model where it implements, submits the L3 and the model to the
    // MPAI Store for approval, and gives the AIM's identifier.
    public string Upload(JsonObject model, JsonObject? aimLicence = null)
    {
        var implementerId = _implementerId;                                        // the Back End: with the Store Service, the Implementer it is registered as
        var n = _service is not null
            ? ((JsonArray?)JsonNode.Parse(Text($"MPAI/Store/L3?name={Type}") ?? "[]"))!.Count(e => ((string)e!["id"]!).StartsWith(implementerId + Type, StringComparison.Ordinal)) + 1
            : Directory.EnumerateFiles(_folder, $"{implementerId}{Type}-I*.json").Count() + 1;
        var implementation = $"{Type}-I{n:00}";
        var aim = implementerId + implementation;
        var modelFile = Path.Combine(_implementations, aim + ".model.json");
        File.WriteAllText(modelFile, model.ToJsonString());
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modelFile)));
        // THE CODE OF THE AIM: the Back End is its Implementer too, and keeps the binary where it implements; the L3 names that file, and the Store approves its fingerprint.
        // THE PACKAGE (MPAI Store 4): the binary and the libraries it loads - those of the package, not of MPAI-AIF - kept together where the Back End implements, so that
        // the fingerprint the Store records is that of what the AIM runs.
        var code = Path.Combine(_implementations, "Mpai.Aih.Nnm.dll");
        var built = typeof(NnmAimProcessor).Assembly.Location;
        var from = Path.GetDirectoryName(built)!;
        foreach (var (file, _) in AIF.Store.ImplementationFingerprints.PackageFiles(Path.GetFileName(built), n => File.Exists(Path.Combine(from, n)) ? File.ReadAllBytes(Path.Combine(from, n)) : null))
        {
            var source = Path.Combine(from, file);
            var target = Path.Combine(_implementations, file);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source))) File.Copy(source, target, overwrite: true);
        }

        var l3 = new JsonObject
        {
            ["Identifier"] = new JsonObject { ["ImplementerID"] = implementerId, ["ImplementationID"] = implementation, ["AIMName"] = aim },
            ["Header"] = Type, ["APIProfile"] = ApiProfile,
            ["Description"] = $"This AIM wraps the Neural Network Model {model["MLModelObjectID"]} and processes AIH Data with it: AIH Data in, the AIH Data resulting from the processing out.",
            ["InternalTypes"] = new JsonArray(new JsonObject { ["Name"] = "AIHData", ["DataType"] = AihJson.AihData }, new JsonObject { ["Name"] = "NNModel", ["DataType"] = "AIP-MLO-V1.0" }),
            ["ExternalPorts"] = new JsonArray(
                new JsonObject { ["Name"] = "AIHDataIn", ["Direction"] = "Input", ["DataType"] = AihJson.AihData, ["Depth"] = 8, ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "NNModelIn", ["Direction"] = "Input", ["IsOptional"] = true, ["DataType"] = "AIP-MLO-V1.0", ["Depth"] = 1, ["Overflow"] = "DropOldest", ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "AIHDataOut", ["Direction"] = "Output", ["DataType"] = AihJson.AihData, ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "NNModelOut", ["Direction"] = "Output", ["DataType"] = "AIP-MLO-V1.0", ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false }),
            ["SubAIMs"] = new JsonArray(), ["Topology"] = new JsonArray(),
            ["Implementations"] = new JsonArray(
                new JsonObject { ["ImplementationURI"] = new Uri(code).AbsoluteUri, ["MASPort"] = 443, ["BinaryName"] = "Mpai.Aih.Nnm", ["Architecture"] = "x86-64", ["OperatingSystem"] = "Windows", ["Version"] = "1.1", ["Source"] = "AIMURL" }),
            // The model: data of Access, by the URI where the Back End keeps it and its SHA-256, given to the AIM as the file of its ModelPath setting.
            ["AccessSources"] = new JsonArray(new JsonObject
            {
                ["Name"] = "nn-model", ["Uri"] = new Uri(modelFile).AbsoluteUri, ["Sha256"] = sha256, ["Setting"] = "ModelPath", ["AsFile"] = aim + ".model.json",
                ["Terms"] = new JsonObject { ["Licence"] = "Only for the processing of AIH Data (AIH-HSP V1.1)", ["Uri"] = "https://mpai.community/standards/mpai-aih/hsp/v1-1/ai-modules/neural-network-model/" },
                ["Description"] = $"The Neural Network Model {model["MLModelObjectID"]} the AIM wraps (an ML Model Object, AIP-MLO-V1.0)."
            }),
            ["ResourcePolicies"] = new JsonArray(
                new JsonObject { ["Name"] = "Memory", ["Minimum"] = "0.5 GB", ["Request"] = "1 GB", ["Maximum"] = "2 GB" },
                new JsonObject { ["Name"] = "CPU:Class", ["Minimum"] = "Low", ["Request"] = "Medium", ["Maximum"] = "High" }),
            ["DataXMData"] = new JsonObject(),
            ["Documentation"] = new JsonArray(new JsonObject { ["Type"] = "Specification", ["URI"] = "https://mpai.community/standards/mpai-aih/hsp/v1-1/" })
        };
        // The AIM Licence the Back End offers for the AIM that wraps its model (AIF-LIC-V3.0 as an offer): without it the AIM needs none.
        if (aimLicence is not null) l3["AIMLicence"] = aimLicence.DeepClone();
        if (_service is not null)
        {
            try { Submit(l3); } catch { File.Delete(modelFile); throw; }
            return aim;
        }
        var published = _store.Publish(l3.ToJsonString());
        if (!published.WasPublished)
        {
            File.Delete(modelFile);
            throw new InvalidOperationException($"The Store did not publish {aim}: {string.Join("; ", published.Errors)}");
        }
        // The Store approves the code it was submitted and records its fingerprint; the model is pinned by the SHA-256 the L3 gives. The Store keeps neither file.
        _store.Approve(aim, new Dictionary<string, string> { ["Mpai.Aih.Nnm"] = code });
        return aim;
    }

    // WHETHER THE STORE HAS PUBLISHED the AIM.
    public bool Has(string aim) => _service is not null ? Text($"MPAI/Store/L3/{aim}") is not null : _store.Exists(aim);

    // THE BACK END PUBLISHES A NEW VERSION OF THE FRONT END: the L3 it has of the Health Front End, with the AIM that wraps the model as one more Sub-AIM, wired to
    // AIH Data Processing as Anomaly and Risk Alerting is (AIH Data out to it, ARA Data back). A Module has no AIM its Metadata does not declare, so a Front End
    // has the model when it has this version; the Controller of the Front End finds that the Store has it, and follows it or not. The version is the next minor one.
    public string PublishFrontEnd(string frontEndL3, string modelAim)
    {
        var l3 = JsonNode.Parse(frontEndL3)!.AsObject();
        if (((string?)l3["APIProfile"] ?? "Basic") != ApiProfile)
            throw new InvalidOperationException($"A Front End of the {(string?)l3["APIProfile"]} Profile is given no model AIM of the {ApiProfile} Profile: publish the model AIM in the Profile of the Front End.");
        // As an Implementer of the Store Service the Back End publishes the Front End as its own Implementation (its Implementer ID in the Identifier).
        if (_service is not null)
        {
            var id = l3["Identifier"]!;
            id["ImplementerID"] = _implementerId;
            id["AIMName"] = _implementerId + (string)id["ImplementationID"]!;
        }
        var name = (string)l3["Identifier"]!["AIMName"]!;
        var sub = (JsonArray)l3["SubAIMs"]!;
        // Its Relation is that of the AIMs the Front End has: the model AIM executes where they do, with the Controller or apart from it.
        var relation = (string?)sub[0]!["Identifier"]!["Relation"] ?? "Internal";
        sub.Add(new JsonObject { ["Identifier"] = new JsonObject
            { ["ImplementerID"] = (string)l3["Identifier"]!["ImplementerID"]!, ["ImplementationID"] = modelAim[(modelAim.IndexOf("AIH", StringComparison.Ordinal))..], ["AIMName"] = modelAim, ["Relation"] = relation } });
        var hdp = ((JsonArray)l3["SubAIMs"]!).Select(s => (string)s!["Identifier"]!["AIMName"]!).First(n => n.Contains("AIH-HDP-V1.1", StringComparison.Ordinal));
        var topology = (JsonArray)l3["Topology"]!;
        topology.Add(new JsonObject { ["Output"] = new JsonObject { ["AIMName"] = hdp, ["DataType"] = AihJson.AihData }, ["Input"] = new JsonObject { ["AIMName"] = modelAim, ["DataType"] = AihJson.AihData } });
        // The AIH Data resulting from the processing goes back to AIH Data Processing on its Input AIH Data 2, which keeps it (the L2 HealthFrontEnd.json says the same).
        topology.Add(new JsonObject { ["Output"] = new JsonObject { ["AIMName"] = modelAim, ["DataType"] = AihJson.AihData }, ["Input"] = new JsonObject { ["AIMName"] = hdp, ["DataType"] = AihJson.AihData, ["PortNumber"] = 2 } });
        // The model it holds, trained, goes to AIH Data Processing, which returns it in the answer to a Federated Learn request (L2 HealthFrontEnd.json).
        topology.Add(new JsonObject { ["Output"] = new JsonObject { ["AIMName"] = modelAim, ["DataType"] = "AIP-MLO-V1.0" }, ["Input"] = new JsonObject { ["AIMName"] = hdp, ["DataType"] = "AIP-MLO-V1.0" } });
        var implementation = l3["Implementations"]![0]!;
        // The next version after the one the Store has (the base's, the first time).
        var held = _service is not null ? Text($"MPAI/Store/L3/{name}") : _store.Exists(name) ? _store.Retrieve(name) : null;
        var current = held is not null ? (string)JsonNode.Parse(held)!["Implementations"]![0]!["Version"]! : (string)implementation["Version"]!;
        var parts = current.Split('.');
        implementation["Version"] = $"{parts[0]}.{int.Parse(parts[1]) + 1}";
        if (_service is not null)
        {
            Submit(l3);                                                              // the Store Service keeps every version (STR-2)
            return (string)implementation["Version"]!;
        }
        // A published version is never overwritten (STR-2): the one the Store had stays, under versions/, and the new one is the latest.
        if (_store.Exists(name))
        {
            Directory.CreateDirectory(Path.Combine(_folder, "versions"));
            File.WriteAllText(Path.Combine(_folder, "versions", $"{name}.{current}.json"), _store.Retrieve(name));
        }
        // The Front End is submitted with its Sub-AIMs: their Metadata is in the package of the Front End (and with the Controllers that run it), not in this Store.
        var withIt = (l3["SubAIMs"] as JsonArray ?? []).Select(x => (string?)x?["Identifier"]?["AIMName"]).OfType<string>().ToList();
        var published = _store.Publish(l3.ToJsonString(), replace: true, submittedWith: withIt);
        if (!published.WasPublished) throw new InvalidOperationException($"The Store did not publish the new {name}: {string.Join("; ", published.Errors)}");
        return (string)implementation["Version"]!;
    }

    // The path of an AIM's model, where the Back End keeps it (for a test that damages it).
    public string ModelFile(string aim) => Path.Combine(_implementations, aim + ".model.json");
}
