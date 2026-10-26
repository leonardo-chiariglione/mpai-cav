using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Mmc.Edp;
using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE JSON INTERFACE OF AUDIO SCENE MANAGEMENT, MEASURED (the author, 2026/10/26). The 59 requests of Test/AsmVoice - play, move, turn, loudness, add, remove, the listener,
// undo, several in one sentence, misrecognised, and those that cannot become a command - each with the numbers it carries, are given to the JSON Interface with the scene they
// are about (the ApplicationContext) and the local language model. A request is understood when the command that comes out, after it has been completed and checked against
// the schema of the User Command, is the one expected: the same operation, the same object by name, the numbers within a tolerance (0.15 m, 5 degrees, 0.5 LUFS); and a request
// that cannot become a command is understood when no command comes out. Six requests that carry no number ("a bit", "closer") are counted apart: they have to give no command.
// Where the report is: Test/Reports/jsi-asm-voice.json.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class JsiAsmVoiceTests
{
    private const string Model = "llama3.2:3b";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"" + Model + "\""); }
        catch { return false; }
    }

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task TheRequestsOfTheSceneBecomeTheExpectedCommands()
    {
        Skip.IfNot(Ollama(), $"Ollama is not running with {Model}.");
        var set = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "AsmVoice", "cases.json")))!;
        var scene = set["scene"]!;
        var context = JsiRig.Context(scene);
        var ports = JsiRig.Ports(JsiRig.Jsi);
        using var llm = new OllamaClient(Model, context: 4096);
        var seconds = new List<double>(); var raws = new List<string>(); var promptChars = 0;
        StructuredModel model = async (system, turns, schema) =>
        {
            var watch = Stopwatch.StartNew();
            var list = turns.ToList();
            promptChars = system.Length + list.Sum(t => t.Content.Length) + schema.ToJsonString().Length;
            if (Environment.GetEnvironmentVariable("JSI_DUMP") is { Length: > 0 } dump)
                File.WriteAllText(dump, new JsonObject { ["system"] = system, ["turns"] = new JsonArray(list.Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray()), ["schema"] = schema.DeepClone() }.ToJsonString());
            try { var raw = await llm.ChatSchemaAsync(system, list, schema); raws.Add(raw); return raw; }
            finally { seconds.Add(watch.Elapsed.TotalSeconds); }
        };
        // The model is loaded before the clock runs.
        await JsiRig.Processor(model).ProcessAsync(JsiRig.Say(ports, "play the scene", context));
        seconds.Clear();

        var rows = new List<JsonObject>();
        async Task<(bool Ok, string Kind)> Run(JsonNode c, bool numberless, string group = "tuned")
        {
            var processor = JsiRig.Processor(model);
            var expected = (JsonObject)c["expected"]!.DeepClone();
            if (expected["revoked"] is not null)
            {
                var earlier = await processor.ProcessAsync(JsiRig.Say(ports, (string)set["earlier"]!["request"]!, context));
                var id = (string?)JsiRig.CommandOf(earlier, ports)?["Instance"]?["UserCommandID"];
                expected["revoked"] = id ?? "(the earlier command was not understood)";
                seconds.Clear();
            }
            var before = seconds.Count; var rawsBefore = raws.Count;
            var request = (string)c["request"]!;
            var answer = await processor.ProcessAsync(JsiRig.Say(ports, request, context));
            var took = seconds.Skip(before).Sum();
            var command = JsiRig.CommandOf(answer, ports);
            var (ok, kind, got) = Judge(expected, command?["Instance"] is { } i ? JsiRig.Normal(i) : null, scene);
            rows.Add(new JsonObject
            {
                ["request"] = request, ["ok"] = ok, ["kind"] = kind, ["seconds"] = Math.Round(took, 2),
                ["expected"] = expected.DeepClone(), ["got"] = got, ["facts"] = JsiRig.FactsOf(answer, ports), ["numberless"] = numberless, ["group"] = group,
                ["raw"] = string.Join(" || ", raws.Skip(rawsBefore)), ["decidedBy"] = raws.Count == rawsBefore ? "guard" : "model", ["promptChars"] = promptChars
            });
            return (ok, kind);
        }

        int right = 0; var misses = new SortedDictionary<string, int>(); var times = new List<double>();
        // A part of the set while the package is being tuned: JSI_CASES=6-20,30 (the numbers of the requests, from 0).
        var only = Environment.GetEnvironmentVariable("JSI_CASES");
        bool Selected(int i) => string.IsNullOrEmpty(only) || only.Split(',').Any(part => part.Contains('-') ? i >= int.Parse(part.Split('-')[0]) && i <= int.Parse(part.Split('-')[1]) : i == int.Parse(part));
        var index = -1;
        foreach (var c in set["cases"]!.AsArray())
        {
            if (!Selected(++index)) continue;
            var (ok, kind) = await Run(c!, false);
            if (ok) right++; else misses[kind] = misses.GetValueOrDefault(kind) + 1;
            times.Add((double)rows[^1]["seconds"]!);
        }
        var of = string.IsNullOrEmpty(only) ? set["cases"]!.AsArray().Count : rows.Count;
        int numberlessRight = 0;
        if (string.IsNullOrEmpty(only))
            foreach (var c in set["withoutNumbers"]!.AsArray()) if ((await Run(c!, true, "withoutNumbers")).Ok) numberlessRight++;

        // Requests written after the package was tuned and not looked at while it was: the measure of how much of the result is the tuning's.
        int heldRight = 0; var heldCases = set["heldOut"] as JsonArray ?? [];
        if (string.IsNullOrEmpty(only))
            foreach (var c in heldCases) if ((await Run(c!, true, "heldOut")).Ok) heldRight++;
        times.Sort();
        var summary = new JsonObject
        {
            ["model"] = Model, ["right"] = right, ["of"] = of, ["share"] = Math.Round((double)right / of, 3),
            ["withoutNumbersRight"] = numberlessRight, ["withoutNumbersOf"] = string.IsNullOrEmpty(only) ? set["withoutNumbers"]!.AsArray().Count : 0,
            ["medianSeconds"] = times[times.Count / 2], ["p90Seconds"] = times[(int)(times.Count * 0.9)],
            ["heldOutRight"] = heldRight, ["heldOutOf"] = string.IsNullOrEmpty(only) ? heldCases.Count : 0,
            ["byGuards"] = rows.Count(r => !(bool)r["numberless"]! && (string)r["decidedBy"]! == "guard"),
            ["byGuardsRight"] = rows.Count(r => !(bool)r["numberless"]! && (string)r["decidedBy"]! == "guard" && (bool)r["ok"]!),
            ["byModel"] = rows.Count(r => !(bool)r["numberless"]! && (string)r["decidedBy"]! == "model"),
            ["byModelRight"] = rows.Count(r => !(bool)r["numberless"]! && (string)r["decidedBy"]! == "model" && (bool)r["ok"]!),
            ["misses"] = JsonSerializer.SerializeToNode(misses)
        };
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "jsi-asm-voice.json"),
            new JsonObject { ["summary"] = summary, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        Assert.True(!string.IsNullOrEmpty(only) || (double)right / of >= FloorOfTheShare, $"{right} of {of} understood: {summary.ToJsonString()}");
        if (string.IsNullOrEmpty(only)) Assert.Equal(set["withoutNumbers"]!.AsArray().Count, numberlessRight);
    }

    // What is asked of the language model, and of the package that guides it: the share that has to be understood.
    private const double FloorOfTheShare = 0.75;

    // ---- the judgement -------------------------------------------------------------------------------------------------------------------------

    internal static (bool Ok, string Kind, JsonNode? Got) Judge(JsonObject expected, JsonObject? got, JsonNode scene)
    {
        if (expected["none"] is not null) return got is null ? (true, "", null) : (false, "a command where none is possible", got);
        if (got is null) return (false, "no command", null);

        var wanted = expected.Select(p => p.Key).ToHashSet();
        var made = got.Select(p => p.Key).ToHashSet();
        if (!wanted.SetEquals(made))
            return (false, "wrong operation: " + (made.Except(wanted).Any() ? "extra " + string.Join(",", made.Except(wanted)) : "") + (wanted.Except(made).Any() ? " missing " + string.Join(",", wanted.Except(made)) : ""), got);

        foreach (var key in wanted)
        {
            var e = expected[key]!; var g = got[key]!;
            switch (key)
            {
                case "delivered": case "revoked":
                    if ((string?)e != (string?)g) return (false, "wrong object", got);
                    break;
                case "lufs":
                    if (Math.Abs(e.GetValue<double>() - g.GetValue<double>()) > 0.5) return (false, "wrong number", got);
                    break;
                case "pov":
                    if (!Near(e, g, 0.15)) return (false, "wrong number", got);
                    break;
                case "removed":
                    if (!e.AsArray().Select(x => (string?)x).OrderBy(x => x).SequenceEqual(g.AsArray().Select(x => (string?)x).OrderBy(x => x))) return (false, "wrong object", got);
                    break;
                case "level":
                    {
                        var byName = ByName(g);
                        foreach (var item in e.AsArray())
                        {
                            if (!byName.TryGetValue((string)item!["name"]!, out var found)) return (false, "wrong object", got);
                            if (found["lufs"] is null || Math.Abs(item["lufs"]!.GetValue<double>() - found["lufs"]!.GetValue<double>()) > 0.5) return (false, "wrong number", got);
                        }
                        if (g.AsArray().Count != e.AsArray().Count) return (false, "wrong operation: other objects too", got);
                        break;
                    }
                case "moved": case "added":
                    {
                        var byName = ByName(g);
                        foreach (var item in e.AsArray())
                        {
                            var name = (string)item!["name"]!;
                            if (!byName.TryGetValue(name, out var found)) return (false, "wrong object", got);
                            var member = scene["members"]![name];
                            var position = item["position"] ?? (key == "moved" ? member?["position"] : null);
                            var orientation = item["orientation"] ?? (key == "moved" ? member?["orientation"] : null);
                            if (position is not null && !Near(position, found["position"], 0.15)) return (false, "wrong number: position", got);
                            if (orientation is not null && !Near(orientation, found["orientation"], 5, degrees: true)) return (false, "wrong number: orientation", got);
                        }
                        if (g.AsArray().Count != e.AsArray().Count) return (false, "wrong operation: other objects too", got);
                        break;
                    }
            }
        }
        return (true, "", null);
    }

    private static Dictionary<string, JsonNode> ByName(JsonNode list) =>
        list.AsArray().GroupBy(x => (string?)x?["name"] ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First()!, StringComparer.Ordinal);

    private static bool Near(JsonNode? a, JsonNode? b, double tolerance, bool degrees = false)
    {
        if (a is not JsonArray x || b is not JsonArray y || x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
        {
            var d = Math.Abs(x[i]!.GetValue<double>() - y[i]!.GetValue<double>());
            if (degrees) d = Math.Min(d % 360, 360 - d % 360);
            if (d > tolerance) return false;
        }
        return true;
    }
}
