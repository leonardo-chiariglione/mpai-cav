using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Mmc.Edp;
using Mpai.Mmc.Jsi;

namespace Mpai.Aif.Tests;

// THE SENTENCES OF THE DEMO, WITH THE REAL LANGUAGE MODEL (the JSON Interface alone, given the scene of Test/AsmVoice/cases.json as its state): each is given, and the command it
// becomes - or the Response Hint that says why there is none - is written to Test/Reports/jsi-demo.json. The sentences are in the environment variable JSI_DEMO, separated by '|';
// without it, the sentences of the demo script. What is judged is that every one gives a command or a hint that says what is missing; which command is read in the report.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class JsiDemoSentencesTests
{
    private const string Model = "llama3.2:3b";

    private static readonly string[] Demo =
    [
        "create a square room of 3 metres by 3 metres centred at 0 0", "make the room 6 by 4 metres", "put the violin at x 2, y 1, z 0", "move the violin 2 metres to the right",
        "turn the violin to 90 degrees", "make the violin louder", "make the violin minus 14 LUFS", "remove the violin", "undo that", "translate the speech into French"
    ];

    [SkippableFact]
    [Trait("Duration", "Long")]
    public async Task TheDemoSentencesGiveACommandOrSayWhatIsMissing()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; Skip.IfNot(h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"" + Model + "\""), "Ollama is not running with " + Model); }
        catch (Exception) { Skip.If(true, "Ollama is not running."); }
        var sentences = Environment.GetEnvironmentVariable("JSI_DEMO") is { Length: > 0 } given ? given.Split('|') : Demo;
        var ports = JsiRig.Ports(JsiRig.Jsi);
        var context = JsiRig.Context(JsiRig.Scene());
        using var llm = new OllamaClient(Model, context: 4096);
        var rows = new JsonArray();
        foreach (var sentence in sentences)
        {
            var jsi = JsiRig.Processor((system, turns, schema) => llm.ChatSchemaAsync(system, turns, schema));
            var watch = Stopwatch.StartNew();
            var answer = await jsi.ProcessAsync(JsiRig.Say(ports, sentence, context));
            var command = JsiRig.CommandOf(answer, ports);
            rows.Add(new JsonObject
            {
                ["request"] = sentence, ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1),
                ["command"] = command?["UserCommandRQ"]?.DeepClone(), ["hint"] = JsiRig.FactsOf(answer, ports)
            });
            Assert.True(command is not null || JsiRig.FactsOf(answer, ports) is { Length: > 0 }, sentence);
        }
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "jsi-demo.json"), rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
