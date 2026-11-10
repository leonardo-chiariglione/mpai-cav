using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Store;
using Mpai.Aif.Api;
using Mpai.Pgm;

namespace Mpai.Aif.Tests;

// THE SLICE ON THE CONTROLLER: the A-User Module (1PGM-ATU-V1.0-I01) started by the Controller API on the L3s of the repository AS THEY ARE, its AIMs made by the provider,
// the human's words written to the Module's boundary. The L3s say what the Module needs: "Execution": "Continuous" (the Module has loops), the Inputs of the AIMs that answer
// events optional (the AIM fires when any of them has a Message), and no Sub-AIMs for the composites (CXT, PRC, DAC, USR, PAL, AUF) that the implementation makes as one AIM each.
[Trait("Group", "Slow")]
[Trait("Blocks", "No")]
public class PgmControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pgm-controller-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch { } }

    private sealed class Provider : IAimProvider
    {
        private readonly PgmProvider _pgm; private readonly AmdStore _store;
        public Provider(AmdStore store, ILanguageModel model) { _store = store; _pgm = new PgmProvider(store, model); }
        public bool CanCreate(string aimName) => _pgm.CanCreate(aimName) || aimName == PgmRig.Pdx;
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) =>
            aimName == PgmRig.Pdx ? new Mpai.Paf.Pdx.PdxAimProcessor(aimName, AimPortReader.Load(_store, aimName)) : _pgm.Make(aimName, settings);
    }

    private string Prepare()
    {
        Directory.CreateDirectory(_folder);
        foreach (var aim in PgmRig.SliceAims.Append(PgmRig.Module).Concat(["1PGM-PSD-V1.0-I01", "1PGM-AOG-V1.0-I01", "1PGM-EFD-V1.0-I01", "1PGM-EBD-V1.0-I01"]))
            File.Copy(Path.Combine(Repository.Amds, aim + ".json"), Path.Combine(_folder, aim + ".json"), overwrite: true);
        var settings = Path.Combine(_folder, "settings.json");
        File.WriteAllText(settings, "{}");
        return settings;
    }

    private static string Run(ControllerApi api, int seconds)
    {
        if (api.StartFlow(PgmRig.Module) != AifError.OK) return "the Module did not start: " + api.StartFlow(PgmRig.Module);
        var written = api.InputWrite(PgmRig.Module, "PGM-HCM-V1.0", 1, PgmRig.HumanCommand().ToJsonString(), 5000);
        written = written == AifError.OK ? api.InputWrite(PgmRig.Module, "OSD-BTO-V1.5", 1, PgmAim.Text("go and open the doors").ToJsonString(), 5000) : written;
        if (written != AifError.OK) return "write: " + written;
        var answer = api.OutputRead(PgmRig.Module, "PGM-HCM-V1.0", 1, seconds * 1000);
        var speech = api.OutputRead(PgmRig.Module, "OSD-BSO-V1.5", 1, seconds * 1000);
        var action = api.OutputRead(PgmRig.Module, "MMM-ACT-V2.2", 1, seconds * 1000);
        return $"answer {answer.Error} {(answer.Json is { } j ? (string?)JsonNode.Parse(j)?["ExecutionResult"] : "-")}; speech {speech.Error}; action {action.Error} {(action.Json is { } a ? (string?)JsonNode.Parse(a)?["Action"] : "-")}";
    }

    private static string Record(string line)
    {
        var folder = Path.Combine(Repository.Root, "Test", "Results", "2026-11-05");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "pgm-slice-controller.txt"), $"{DateTime.Now:s}\t{line}\n");
        return line;
    }

    // On the L3s as they are: the human's words go in, the A-User answers, speaks and asks for the Process Action.
    [Fact]
    public void TheModuleRunsOnItsL3s()
    {
        var settings = Prepare();
        using var api = new ControllerApi(_folder, settings, store => new Provider(store, new ScriptedModel()));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = Record($"L3s as they are: {Run(api, 20)} ({clock.Elapsed.TotalSeconds:F1} s)");
        Assert.Contains("accepted", result);
        Assert.Contains("MU-Actuate", result);
    }

    // The same, with a real language model (Ollama; PGM_LLM_MODEL, default llama3.2:3b), three runs, timed.
    [SkippableFact]
    public async Task TheModuleOnTheControllerWithOllama()
    {
        var model = new OllamaModel(Environment.GetEnvironmentVariable("PGM_LLM_MODEL") is { Length: > 0 } m ? m : "llama3.2:3b");
        try { await model.AnswerAsync("Answer in JSON.", "ping", new JsonObject { ["type"] = "object" }); }
        catch (Exception e) { Skip.If(true, "Ollama is not available: " + e.Message); }
        var settings = Prepare();
        var failures = 0;
        for (var i = 1; i <= 3; i++)
        {
            using var api = new ControllerApi(_folder, settings, store => new Provider(store, model));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = Record($"{model.Model}, Controller, run {i}: {Run(api, 60)} ({clock.Elapsed.TotalSeconds:F1} s)");
            if (!result.Contains("accepted") || !result.Contains("MU-Actuate")) failures++;
        }
        Assert.Equal(0, failures);
    }
}
