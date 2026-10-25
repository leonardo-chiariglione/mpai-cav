using System.Net.Http;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// AMQ FROM PACKAGES. The L3s of MPAIStore/L3s/AMQ - the Composite AIM, which carries no code, and the four leaf AIMs, each with the
// address of its package - are all the Controller is given. It fetches the packages one by one, loads the AIM each carries and runs
// the Module. The packages are those AIMs/Build-Packages.ps1 made (here read from the folder they were made in, MPAI_AMQ_PACKAGES;
// published, their addresses are those of the release).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AmqFromPackagesTests
{
    private const string Module = "1MMC-AMQ-V2.5-I02";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"llama3.2:3b\""); }
        catch { return false; }
    }

    [SkippableFact]
    public void AmqRunsFromTheL3sAndThePackagesOfItsAims()
    {
        Skip.IfNot(TestModels.Can("ggml-small.bin", "tts-program"), "The speech models cannot be had.");
        Skip.IfNot(Ollama(), "Ollama is not running with llama3.2:3b.");
        var packages = Environment.GetEnvironmentVariable("MPAI_AMQ_PACKAGES") ?? Path.Combine(Repository.Root, "MPAIStore", "Packages");
        var l3s = Path.Combine(Repository.Root, "MPAIStore", "L3s", "AMQ");
        Skip.IfNot(Directory.Exists(l3s) && File.Exists(Path.Combine(packages, "MMC-ASR-V2.5-I03-win-x64.zip")), "The packages of AMQ are not in " + packages);

        var work = Path.Combine(Path.GetTempPath(), "amq-packages-" + Guid.NewGuid().ToString("N"));
        var amds = Path.Combine(work, "AMDs"); Directory.CreateDirectory(amds);
        foreach (var f in Directory.GetFiles(l3s, "*.json"))
        {
            // the L3 names where the packages are; here, where they are on this machine
            var text = File.ReadAllText(f);
            text = System.Text.RegularExpressions.Regex.Replace(text, "file:///[^\"]*/(Packages|upload)", new Uri(packages).AbsoluteUri.TrimEnd('/'));
            File.WriteAllText(Path.Combine(amds, Path.GetFileName(f)), text);
        }

        var said = new List<string>();
        using var api = new ControllerApi(amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
            store => new PackageAimProvider(store, Path.Combine(work, "cache"), line => { lock (said) said.Add(line); }));
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Module));
            var picture = new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile("red.jpg",
                File.ReadAllBytes(Path.Combine(Mpai.Core.MpaiPaths.TestData, "red.jpg")), "Picture")));
            var question = new ControllerApi.Datum("OSD-BTO-V1.5", 1, MpaiJson.ToJson(BasicTextObject.FromText("What color is the picture?")));
            var machine = new ControllerApi.Datum("string", 1, "\"Thalia\"");
            var r = api.Advance(Module, new[] { picture, question, machine });
            Assert.True(r.Ok, $"{Module}: {r.Error} :: {string.Join(" / ", said)}");

            var pav = r.ByType("PAF-PAV-V1.6");
            Assert.Contains("red", AvatarUtterance.TextOfPortableAvatar(pav) ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(AvatarUtterance.FromPortableAvatar(pav) is { MachineSpeechWav.Length: > 200 }, "the answer carries no speech");
            Assert.True(said.Count(l => l.Contains("from the package")) >= 4, "the AIMs did not come from packages: " + string.Join(" / ", said));
        }
        finally { api.StopFlow(Module); try { Directory.Delete(work, true); } catch { } }
    }
}
