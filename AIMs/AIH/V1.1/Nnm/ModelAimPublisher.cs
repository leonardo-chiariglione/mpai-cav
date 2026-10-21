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
// it (AIH-NNM-V1.1). The MPAI Store does not hold the AIM: it validates it. The Back End - the Implementer - keeps the model file where it says (the ImplementationURI of the
// AIM's L3) and submits the L3, which the Store checks against its L2 and publishes, and the model, of which it records the SHA-256 as submitted and approved. A Controller
// gets the L3 and the fingerprint from the Store (AIF.Store.StoreAimSource), the model from the Back End's location, and checks it against the fingerprint the Store approved.
public sealed class ModelAimPublisher
{
    public const string Type = "AIH-NNM-V1.1";

    private readonly string _folder = "", _implementations;
    private readonly MpaiStore? _library;                                      // the Store as a library (tests, one machine) ...
    private readonly HttpClient? _service;                                     // ... or the Store Service, reached as an Implementer (the Back End is one)
    private readonly string _implementerId = "";
    private readonly ImplementerKey? _key;

    private MpaiStore _store => _library ?? throw new InvalidOperationException("This publisher submits to the Store Service.");

    // folder: the MPAI Store's (L3s and approvals only). implementations: where the Back End keeps what it implemented, the models. conformance: the Store's check of an L3
    // against its L2 (AIF.Metadata.L2Conformance.Check).
    public ModelAimPublisher(string folder, string implementations, Func<JsonElement, IReadOnlyList<string>> conformance)
    {
        _folder = folder; _implementations = implementations;
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
    public string Upload(JsonObject model, string implementerId = "1", JsonObject? aimLicence = null)
    {
        if (_service is not null) implementerId = _implementerId;                  // the Implementer is the one that is registered
        var n = _service is not null
            ? ((JsonArray?)JsonNode.Parse(Text($"MPAI/Store/L3?name={Type}") ?? "[]"))!.Count(e => ((string)e!["id"]!).StartsWith(implementerId + Type, StringComparison.Ordinal)) + 1
            : Directory.EnumerateFiles(_folder, $"{implementerId}{Type}-I*.json").Count() + 1;
        var implementation = $"{Type}-I{n:00}";
        var aim = implementerId + implementation;
        var modelFile = Path.Combine(_implementations, aim + ".model.json");
        File.WriteAllText(modelFile, model.ToJsonString());

        var l3 = new JsonObject
        {
            ["Identifier"] = new JsonObject { ["ImplementerID"] = implementerId, ["ImplementationID"] = implementation, ["AIMName"] = aim },
            ["Header"] = Type, ["APIProfile"] = "Basic",
            ["Description"] = $"This AIM wraps the Neural Network Model {model["MLModelObjectID"]}: AIH Data in, ARA Data out.",
            ["InternalTypes"] = new JsonArray(new JsonObject { ["Name"] = "AIHData", ["DataType"] = AihJson.AihData }, new JsonObject { ["Name"] = "ARAData", ["DataType"] = AihJson.AraData }, new JsonObject { ["Name"] = "NNModel", ["DataType"] = "AIP-MLO-V1.0" }),
            ["ExternalPorts"] = new JsonArray(
                new JsonObject { ["Name"] = "AIHData", ["Direction"] = "Input", ["DataType"] = AihJson.AihData, ["Depth"] = 8, ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "ARAData", ["Direction"] = "Output", ["DataType"] = AihJson.AraData, ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "NNModelIn", ["Direction"] = "Input", ["IsOptional"] = true, ["DataType"] = "AIP-MLO-V1.0", ["Depth"] = 8, ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false },
                new JsonObject { ["Name"] = "NNModelOut", ["Direction"] = "Output", ["DataType"] = "AIP-MLO-V1.0", ["Technology"] = "Software", ["Protocol"] = "", ["IsRemote"] = false }),
            ["SubAIMs"] = new JsonArray(), ["Topology"] = new JsonArray(),
            ["Implementations"] = new JsonArray(
                new JsonObject { ["ImplementationURI"] = "https://github.com/leonardo-chiariglione/mpai-cav/tree/main/AIMs/AIH/V1.1/Nnm", ["MASPort"] = 443, ["BinaryName"] = "Mpai.Aih.Nnm", ["Architecture"] = "x86-64", ["OperatingSystem"] = "Windows", ["Version"] = "1.1", ["Source"] = "MPAIStore" },
                new JsonObject { ["ImplementationURI"] = new Uri(modelFile).AbsoluteUri, ["BinaryName"] = Path.GetFileName(modelFile), ["Architecture"] = "x86-64", ["OperatingSystem"] = "Windows", ["Version"] = "1.1", ["Source"] = "AIMURL" }),
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
            return aim;                                                              // the Service read the model and recorded its fingerprint
        }
        var published = _store.Publish(l3.ToJsonString());
        if (!published.WasPublished)
        {
            File.Delete(modelFile);
            throw new InvalidOperationException($"The Store did not publish {aim}: {string.Join("; ", published.Errors)}");
        }
        // The Store approves what it is submitted - the model - and records its fingerprint; it does not keep the file.
        _store.Approve(aim, new Dictionary<string, string> { [Path.GetFileName(modelFile)] = modelFile });
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
        topology.Add(new JsonObject { ["Output"] = new JsonObject { ["AIMName"] = modelAim, ["DataType"] = AihJson.AraData }, ["Input"] = new JsonObject { ["AIMName"] = hdp, ["DataType"] = AihJson.AraData } });
        // The ARA Data it finds is also what the Front End gives at its boundary: the alert is the ARA Data (as Anomaly and Risk Alerting's is).
        topology.Add(new JsonObject { ["Output"] = new JsonObject { ["AIMName"] = modelAim, ["DataType"] = AihJson.AraData }, ["Input"] = new JsonObject { ["AIMName"] = "", ["DataType"] = AihJson.AraData } });
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
        var published = _store.Publish(l3.ToJsonString(), replace: true);
        if (!published.WasPublished) throw new InvalidOperationException($"The Store did not publish the new {name}: {string.Join("; ", published.Errors)}");
        return (string)implementation["Version"]!;
    }

    // The path of an AIM's model, where the Back End keeps it (for a test that damages it).
    public string ModelFile(string aim) => Path.Combine(_implementations, aim + ".model.json");
}
