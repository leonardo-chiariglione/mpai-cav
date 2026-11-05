using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

using Mpai.Pgm;

namespace Mpai.Aif.Tests;

// THE RIG OF THE FIRST SLICE OF PGM-AUA: the AIMs of the slice made by the provider from their L3s, the schemas of their Data Types (found by the Header), and a router that applies the
// Topology of the A-User Module (1PGM-ATU-V1.0-I01) to the Messages the AIMs give.
internal sealed class PgmRig
{
    public const string Module = "1PGM-ATU-V1.0-I01";
    public const string Pdx = "1PAF-PDX-V1.6-I01";

    public static readonly string[] SliceAims =
    [
        "1PGM-AUC-V1.0-I01", "1PGM-CXT-V1.0-I01", "1PGM-PRC-V1.0-I01", "1PGM-BKN-V1.0-I01", "1PGM-DAC-V1.0-I01", "1PGM-USR-V1.0-I01", "1PGM-PAL-V1.0-I01", "1PGM-AUF-V1.0-I01", Pdx
    ];

    // ---- the schemas, by Header -----------------------------------------------------------------------------------------------------------------

    private static readonly Lazy<Dictionary<string, Json.Schema.JsonSchema>> ByHeader = new(() =>
    {
        var all = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var map = new Dictionary<string, Json.Schema.JsonSchema>();
        foreach (var (path, schema) in all)
        {
            if (!path.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar) || Path.GetFileName(path).StartsWith("Any")) continue;   // the Data Types, not the wrappers
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(path)); } catch { continue; }
            foreach (var candidate in new[] { node }.Concat(node?["allOf"] as JsonArray ?? []))
                if ((string?)candidate?["properties"]?["Header"]?["const"] is { } header && !map.ContainsKey(header))
                    map[header] = schema;
        }
        return map;
    });

    // "valid", or why not. A Data of a Data Type without a schema (the string of the Avatar ID) is not judged.
    public static string Check(JsonNode data)
    {
        if (data is not JsonObject o || (string?)o["Header"] is not { } header) return "valid";
        if (!ByHeader.Value.TryGetValue(header, out var schema)) return $"no schema for {header}";
        using var doc = JsonDocument.Parse(data.ToJsonString());
        Json.Schema.EvaluationResults r;
        lock (AIF.Metadata.PublishedSchemas.Lock)
            r = schema.Evaluate(doc.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (r.IsValid) return "valid";
        var errors = r.Details.Where(d => d.Errors is { Count: > 0 }).Take(3).Select(d => $"{d.InstanceLocation}: {d.Errors!.First().Value}");
        return $"{header} not valid: " + string.Join("; ", errors);
    }

    // ---- the AIMs -------------------------------------------------------------------------------------------------------------------------------

    public AmdStore Store { get; }
    public Dictionary<string, IAimProcessor> Aims { get; } = new();
    public Dictionary<string, AimPortReader> Readers { get; } = new();

    // Every Data the router delivered to an AIM or took from one, with its verdict: what the faithfulness tests read.
    public List<(string Aim, string Direction, string DataType, string Verdict)> Seen { get; } = new();

    // What left the Module's boundary, by Data Type.
    public List<(string DataType, JsonNode Data)> Boundary { get; } = new();

    // Outputs the Topology does not connect to anything (the Ports PAF-PDX declares that the A-User Module leaves unconnected): given and not delivered.
    public List<(string Aim, string DataType)> Unconnected { get; } = new();

    private readonly List<(string OutAim, string DataType, string InAim)> _topology = new();

    public PgmRig(ILanguageModel model, ISpeechRecogniser? recogniser = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        Store = new AmdStore(Repository.Amds);
        Store.Scan();
        var provider = new PgmProvider(Store, model, recogniser);
        settings ??= new Dictionary<string, string>();
        foreach (var aim in SliceAims)
        {
            Readers[aim] = AimPortReader.Load(Store, aim);
            Aims[aim] = aim == Pdx ? new Mpai.Paf.Pdx.PdxAimProcessor(aim, Readers[aim]) : provider.Make(aim, settings);
        }
        var amd = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, Module + ".json")))!;
        foreach (var t in amd["Topology"]!.AsArray())
            _topology.Add(((string)t!["Output"]!["AIMName"]!, (string)t["Output"]!["DataType"]!, (string)t["Input"]!["AIMName"]!));
    }

    // ---- the router -----------------------------------------------------------------------------------------------------------------------------

    private readonly Queue<(string Aim, string Key, string Json)> _queue = new();
    public int Firings { get; private set; }

    private static string TypeOf(string key) => key[..key.IndexOf('#')];

    // A Message at the boundary of the Module, for the AIM the Topology connects that Data Type to.
    public void Write(string dataType, JsonNode data)
    {
        foreach (var t in _topology.Where(t => t.OutAim == "" && t.DataType == dataType))
            Deliver(t.InAim, Readers[t.InAim].Input(dataType), data.ToJsonString());
    }

    // The latest Data of each Data Type delivered to an AIM (for a report).
    public Dictionary<string, string> Latest { get; } = new();

    private void Deliver(string aim, string key, string json)
    {
        Latest[TypeOf(key)] = json;
        Seen.Add((aim, "Input", TypeOf(key), json.StartsWith('{') ? Check(JsonNode.Parse(json)!) : "valid"));
        _queue.Enqueue((aim, key, json));
    }

    // Fires the AIMs until nothing is pending.
    public async Task RunAsync(int maxFirings = 200)
    {
        while (_queue.Count > 0)
        {
            if (++Firings > maxFirings) throw new InvalidOperationException("The slice does not settle: " + Firings + " firings.");
            var (aim, key, json) = _queue.Dequeue();
            var result = await Aims[aim].ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new Dictionary<string, string> { [key] = json } });
            if (result.IsError) throw new InvalidOperationException($"{aim}: {result.Payload}");
            foreach (var (outKey, outJson) in result.Ports)
            {
                var type = TypeOf(outKey);
                var node = JsonNode.Parse(outJson)!;
                Seen.Add((aim, "Output", type, node is JsonObject ? Check(node) : "valid"));
                var destinations = _topology.Where(t => t.OutAim == aim && t.DataType == type).ToList();
                if (destinations.Count == 0) { Unconnected.Add((aim, type)); continue; }
                foreach (var d in destinations)
                {
                    if (d.InAim == "") Boundary.Add((type, node));
                    else Deliver(d.InAim, Readers[d.InAim].Input(type), outJson);
                }
            }
        }
    }

    public IEnumerable<JsonNode> At(string dataType) => Boundary.Where(b => b.DataType == dataType).Select(b => b.Data);

    // ---- what the User Agent writes ------------------------------------------------------------------------------------------------------------

    public static JsonObject HumanCommand(string id = "hc-1") => new()
    {
        ["Header"] = "PGM-HCM-V1.0", ["Mode"] = "RQ", ["MInstance"] = "m-instance-1", ["UEnvironment"] = "u-environment-1", ["SessionID"] = "session-1",
        ["HumanCommandID"] = id, ["HumanCommandTime"] = PgmAim.Time(), ["CommandType"] = "instruct", ["CommandIntent"] = "go and open the doors"
    };
}

// A language model of the test's: it answers as a good one should, so that the rest of the slice is judged on its own.
internal sealed class ScriptedModel : ILanguageModel
{
    public List<string> Asked { get; } = new();

    public Task<string> AnswerAsync(string system, string user, JsonObject schema)
    {
        Asked.Add(user);
        var opens = user.Contains("open", StringComparison.OrdinalIgnoreCase);
        var answer = new JsonObject
        {
            ["reply"] = opens ? "I am going to open the doors." : "Yes.",
            ["worldActions"] = opens ? new JsonArray(new JsonObject { ["verb"] = "MU-Actuate", ["target"] = "doors" }) : new JsonArray(),
            ["personaGestures"] = opens ? new JsonArray("nod", "walk-to-doors") : new JsonArray()
        };
        return Task.FromResult(answer.ToJsonString());
    }
}
