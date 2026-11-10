using System.Diagnostics;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;

using Mpai.Pgm;

namespace Mpai.Aif.Tests;

// THE FIRST SLICE OF PGM-AUA V1.0: "go and open the doors". Faithfulness first: the Ports of each AIM are those of its L3 (no more, no fewer, same Data Type and direction), and
// every Data an AIM receives or gives validates against the JSON Schema of its Data Type. Then the slice end to end.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class PgmSliceTests
{
    private static readonly string[] All =
    [
        "1PGM-AUC-V1.0-I01", "1PGM-CXT-V1.0-I01", "1PGM-PRC-V1.0-I01", "1PGM-BKN-V1.0-I01", "1PGM-DAC-V1.0-I01", "1PGM-USR-V1.0-I01", "1PGM-PAL-V1.0-I01", "1PGM-AUF-V1.0-I01",
        "1PGM-PSD-V1.0-I01", "1PGM-FRD-V1.0-I01", "1PGM-AOG-V1.0-I01", "1PGM-EFD-V1.0-I01", "1PGM-EBD-V1.0-I01"
    ];

    public static IEnumerable<object[]> AimNames => All.Select(a => new object[] { a });

    // The Ports of an L3 as the framework keys them: Data Type and the declared PortNumber, else the position among the Ports of the same Direction and Data Type.
    private static (Dictionary<string, string> Inputs, Dictionary<string, string> Outputs) PortsOf(string aim)
    {
        var amd = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")))!;
        var inputs = new Dictionary<string, string>(); var outputs = new Dictionary<string, string>();
        var count = new Dictionary<(string, string), int>();
        foreach (var p in amd["ExternalPorts"]!.AsArray())
        {
            var direction = (string)p!["Direction"]!; var type = (string)p["DataType"]!;
            var n = count[(direction, type)] = count.GetValueOrDefault((direction, type)) + 1;
            var declared = (int?)p["PortNumber"];
            (direction == "Input" ? inputs : outputs)[PortKey.Of(type, declared ?? n)] = type;
        }
        return (inputs, outputs);
    }

    private static PgmAim Make(string aim)
    {
        var store = new AmdStore(Repository.Amds); store.Scan();
        return new PgmProvider(store, new ScriptedModel()).Make(aim, new Dictionary<string, string>());
    }

    // (a) The Ports of the implementation are the Ports of the L3: every one of them, and no other.
    [Theory]
    [MemberData(nameof(AimNames))]
    public void PortsAreThoseOfTheMetadata(string aim)
    {
        var (inputs, outputs) = PortsOf(aim);
        var implementation = Make(aim);
        Assert.Equal(inputs.OrderBy(x => x.Key), implementation.InputPorts.OrderBy(x => x.Key));
        Assert.Equal(outputs.OrderBy(x => x.Key), implementation.OutputPorts.OrderBy(x => x.Key));
    }

    // The metadata of every AIM of the slice names Data Types that have a schema (except the Avatar ID of PAF-PDX, which is a string).
    [Fact]
    public void EveryDataTypeOfTheSliceHasASchema()
    {
        var headers = new SortedSet<string>();
        foreach (var aim in PgmRig.SliceAims.Concat(All))
        {
            var (i, o) = PortsOf(aim);
            foreach (var t in i.Values.Concat(o.Values)) headers.Add(t);
        }
        headers.Remove("string");
        var missing = headers.Where(h => PgmRig.Check(new JsonObject { ["Header"] = h }).StartsWith("no schema")).ToList();
        Assert.True(missing.Count == 0, "No schema found for the Header of: " + string.Join(", ", missing));
    }

    private static async Task<PgmRig> RunTextAsync(ILanguageModel model, string words)
    {
        var rig = new PgmRig(model);
        rig.Write("PGM-HCM-V1.0", PgmRig.HumanCommand());
        rig.Write("OSD-BTO-V1.5", PgmAim.Text(words));
        await rig.RunAsync();
        return rig;
    }

    private static void AssertAllValid(PgmRig rig)
    {
        var bad = rig.Seen.Where(s => s.Verdict != "valid").Select(s => $"{s.Aim} {s.Direction} {s.DataType}: {s.Verdict}").Distinct().ToList();
        Assert.True(bad.Count == 0, "Data not valid against its schema:\n" + string.Join("\n", bad));
    }

    // The slice, text first, with a model of the test's.
    [Fact]
    public async Task OpenTheDoorsWithText()
    {
        var rig = await RunTextAsync(new ScriptedModel(), "go and open the doors");
        AssertAllValid(rig);

        // The human is answered once, accepted.
        var answers = rig.At("PGM-HCM-V1.0").ToList();
        Assert.Single(answers);
        Assert.Equal("accepted", (string?)answers[0]["ExecutionResult"]);

        // The Process Action on another Item is asked of the M-Instance: an Action and a Process Action Request. The gesture is the Persona's own: not asked of anybody.
        Assert.Equal("MU-Actuate", (string?)rig.At("MMM-ACT-V2.2").Single()["Action"]);
        var request = rig.At("MMM-PAC-V2.2").Single();
        Assert.Equal("doors", (string?)request["RQ-Complements"]![0]!["ToItem"]);

        // The Module gives the Portable Avatar (speech, face, body and model together) to the Controller; the User Agent separates it for its Units.
        var avatar = rig.At("PAF-PAV-V1.6").Single();
        Assert.Contains("nod", avatar.ToJsonString());

        // The test stub of the M-Instance answers the Process Action Request.
        var response = (JsonObject)request.DeepClone();
        response["PAStatus"] = new JsonArray(new JsonObject { ["Ack"] = "Ack" });
        rig.Write("MMM-PAC-V2.2", response);
        await rig.RunAsync();
        AssertAllValid(rig);
        Assert.Equal("done", ((AucAim)rig.Aims["1PGM-AUC-V1.0-I01"]).Outcome);
    }

    // Basic Knowledge gives its Final Response to A-User Formation only.
    [Fact]
    public async Task TheFinalResponseGoesOnlyToAUserFormation()
    {
        var rig = await RunTextAsync(new ScriptedModel(), "go and open the doors");
        var receivers = rig.Seen.Where(s => s.DataType == "PGM-FRS-V1.0" && s.Direction == "Input").Select(s => s.Aim).Distinct().ToList();
        Assert.Equal(["1PGM-AUF-V1.0-I01"], receivers);
    }

    // A command that is not understood is rejected, and nothing else happens.
    [Fact]
    public async Task NoWordsNoAction()
    {
        var rig = new PgmRig(new ScriptedModel());
        rig.Write("PGM-HCM-V1.0", PgmRig.HumanCommand());
        rig.Write("OSD-BTO-V1.5", PgmAim.Text(""));
        await rig.RunAsync();
        AssertAllValid(rig);
        Assert.Equal("rejected", (string?)rig.At("PGM-HCM-V1.0").Single()["ExecutionResult"]);
        Assert.Empty(rig.At("MMM-PAC-V2.2"));
        Assert.Empty(rig.At("OSD-BSO-V1.5"));
    }

    private static JsonObject ActionObject(string gesture) => new()
    {
        ["Header"] = "PGM-ACO-V1.0", ["ActionObjectID"] = "ao-" + gesture, ["SubjectID"] = "a-user-persona",
        ["ActionObjectData"] = new JsonArray(new JsonObject { ["Data"] = gesture }),
        ["ActionQualifier"] = new JsonObject { ["Header"] = "TFA-ACQ-V1.5", ["ActionQualifierID"] = "acq-1", ["Formats"] = new JsonObject { ["ContentFormat"] = "GestureName" } }
    };

    // Final Response Demultiplexing takes the Final Response apart: the Text, the Action Object of the Persona and the Process Actions, which it gives on unchanged.
    [Fact]
    public async Task FinalResponseDemultiplexingTakesTheFinalResponseApart()
    {
        var frd = (FrdAim)Make("1PGM-FRD-V1.0-I01");
        var final = new JsonObject
        {
            ["Header"] = "PGM-FRS-V1.0", ["MInstance"] = "m", ["UEnvironment"] = "u", ["SessionID"] = "s", ["FinalResponseID"] = "f", ["FinalResponseTime"] = PgmAim.Time(),
            ["Text"] = PgmAim.Text("hello"),
            ["ActionObjects"] = new JsonArray(ActionObject("nod")),
            ["ProcessActions"] = new JsonObject
            {
                ["Header"] = "MMM-PAT-V2.2",
                ["ProcessActions"] = new JsonObject
                {
                    ["MM-Move"] = new JsonObject
                    {
                        ["Header"] = "MMM-2MP-V2.2", ["Time"] = PgmAim.Time(),
                        ["MMMovePARQ"] = new JsonObject { ["RQ1"] = new JsonObject { ["Complement"] = "Nil", ["ItemID"] = "doors" } }
                    }
                }
            }
        };
        var result = await frd.ProcessAsync(new Message { Ports = new Dictionary<string, string> { [frd.InputKey("PGM-FRS-V1.0")] = final.ToJsonString() } });
        Assert.Equal("nod", (string?)JsonNode.Parse(result.Ports[frd.OutputKey("PGM-ACO-V1.0")])!["ActionObjectData"]![0]!["Data"]);
        Assert.Equal(["MM-Move"], JsonNode.Parse(result.Ports[frd.OutputKey("MMM-PAT-V2.2")])!["ProcessActions"]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.True(result.Ports.ContainsKey(frd.OutputKey("OSD-BTO-V1.5")));
        foreach (var (key, json) in result.Ports) Assert.Equal("valid", PgmRig.Check(JsonNode.Parse(json)!));
    }

    // Animation Object Generation: an Action Object of the face becomes a Face Animation Object (Port 1), one of the body a Body Animation Object (Port 2).
    [Fact]
    public async Task AnimationObjectGenerationAnimatesTheFaceOrTheBody()
    {
        var aog = (AogAim)Make("1PGM-AOG-V1.0-I01");
        var face = await aog.ProcessAsync(new Message { Ports = new Dictionary<string, string> { [aog.InputKey("PGM-ACO-V1.0")] = ActionObject("nod").ToJsonString() } });
        Assert.True(face.Ports.ContainsKey(aog.OutputKey("PAF-AMO-V1.6", 1)));
        Assert.False(face.Ports.ContainsKey(aog.OutputKey("PAF-AMO-V1.6", 2)));
        var body = await aog.ProcessAsync(new Message { Ports = new Dictionary<string, string> { [aog.InputKey("PGM-ACO-V1.0")] = ActionObject("wave").ToJsonString() } });
        Assert.True(body.Ports.ContainsKey(aog.OutputKey("PAF-AMO-V1.6", 2)));
        Assert.False(body.Ports.ContainsKey(aog.OutputKey("PAF-AMO-V1.6", 1)));
        foreach (var (key, json) in face.Ports.Concat(body.Ports)) Assert.Equal("valid", PgmRig.Check(JsonNode.Parse(json)!));
    }

    // The slice with a real language model (Ollama, the model of PGM_LLM_MODEL, default llama3.2:3b): several runs, timed.
    [SkippableFact]
    [Trait("Group", "Slow")]
    [Trait("Blocks", "No")]
    public async Task OpenTheDoorsWithOllama()
    {
        var model = new OllamaModel(Environment.GetEnvironmentVariable("PGM_LLM_MODEL") is { Length: > 0 } m ? m : "llama3.2:3b");
        try { await model.AnswerAsync("Answer in JSON.", "ping", new JsonObject { ["type"] = "object" }); }
        catch (Exception e) { Skip.If(true, "Ollama is not available: " + e.Message); }

        var runs = int.TryParse(Environment.GetEnvironmentVariable("PGM_RUNS"), out var n) ? n : 3;
        var lines = new List<string>(); var failures = 0;
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            string verdict, said = "";
            try
            {
                var rig = await RunTextAsync(model, "go and open the doors");
                said = rig.Latest.TryGetValue("PGM-FRS-V1.0", out var frs) ? $" | said \"{PgmAim.TextOf(JsonNode.Parse(frs)?["Text"] as JsonObject)}\" | actions {string.Join(",", (JsonNode.Parse(frs)?["ProcessActions"]?["ProcessActions"] as JsonObject)?.Select(k => k.Key) ?? [])}" : "";
                var bad = rig.Seen.Where(s => s.Verdict != "valid").Select(s => s.Verdict).Distinct().ToList();
                var accepted = rig.At("PGM-HCM-V1.0").Any(x => (string?)x["ExecutionResult"] == "accepted");
                var acted = rig.At("MMM-ACT-V2.2").Any(x => (string?)x["Action"] == "MU-Actuate");
                verdict = bad.Count > 0 ? "NOT VALID: " + string.Join("; ", bad) : !accepted ? "not accepted" : !acted ? "no MU-Actuate" : "ok";
            }
            catch (Exception e) { verdict = "EXCEPTION: " + e.Message; }
            if (verdict != "ok") failures++;
            lines.Add($"{model.Model}\trun {i + 1}\t{clock.Elapsed.TotalSeconds:F2} s\t{verdict}{said}");
        }
        var folder = Path.Combine(Repository.Root, "Test", "Results", "2026-11-05");
        Directory.CreateDirectory(folder);
        File.AppendAllLines(Path.Combine(folder, "pgm-slice-ollama.txt"), lines.Prepend($"--- {DateTime.Now:s}"));
        Assert.True(failures == 0, string.Join("\n", lines));
    }
}
