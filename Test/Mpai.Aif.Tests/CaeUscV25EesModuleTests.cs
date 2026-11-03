using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Ees;

namespace Mpai.Aif.Tests;

// EMOTION ENHANCED SPEECH AS A MODULE under a Controller, the two apart (the Controller and the Module are always separate): its five SubAIMs execute in a process of their own
// (AIF.AimHost), wired as the L3 says. One exchange asks for the prosodic pathway (a model utterance, the emotionless speech), another for the neural one (an emotion, a
// language, the emotionless speech); the SubAIMs of the pathway not asked for are given nothing and do not run.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EesModuleTests : IDisposable
{
    private const string Ees = "1CAE-EES-V2.5-I01";
    private static readonly string[] Aims = ["1CAE-SFA-V2.5-I01", "1CAE-SFB-V2.5-I01", "1CAE-EFP-V2.5-I01", "1CAE-PEI-V2.5-I01", "1CAE-NEI-V2.5-I01"];
    private readonly string work = Path.Combine(Path.GetTempPath(), "cae-ees-" + Guid.NewGuid().ToString("N"));
    private readonly HostProcess process;
    private readonly ControllerApi api;

    public CaeUscV25EesModuleTests()
    {
        Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var l3 = File.ReadAllText(Path.Combine(amds, Ees + ".json"));
        foreach (var aim in Aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, Ees + ".json"), l3);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Cae.Usc.dll") + ":Mpai.Cae.Usc.UscProvider",
                                  extra: ["--settings", settings], workingDirectory: Repository.Root);
        api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in Aims) api.Controller.AimHosts[aim] = process.Address;
        Assert.Equal(AifError.OK, api.StartFlow(Ees));
    }

    public void Dispose() { api.Dispose(); process.Dispose(); try { Directory.Delete(work, true); } catch { } }

    private void Write(string type, int n, string json) => Assert.Equal(AifError.OK, api.InputWrite(Ees, type, n, json, 10_000));

    private static Audio Syllables(double hertz, double perSecond, double level, double seconds = 3)
    {
        var s = new short[(int)(seconds * 16000)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Pow(Math.Sin(Math.PI * perSecond * i / 16000), 2) * Math.Sin(2 * Math.PI * hertz * i / 16000));
        return new Audio(s, 16000);
    }

    private static string Selector(string id) => new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = id }.ToJsonString();

    [Fact]
    public void TheProsodicPathwayGivesTheEmotionlessSpeechTheProsodyOfTheModelUtterance()
    {
        var neutral = Syllables(200, 3, 8000);
        Write("OSD-SEL-V1.5", 1, Selector("prosodic"));
        Write("OSD-BSO-V1.5", 1, SpeechObject.Make("model", Syllables(300, 5, 16000)));            // the model utterance
        Write("OSD-BSO-V1.5", 3, SpeechObject.Make("neutral", neutral));                           // the emotionless speech, for the prosodic insertion

        var read = api.OutputRead(Ees, "OSD-BSO-V1.5", 1, 20_000);
        Assert.True(read.Error == AifError.OK, $"nothing at Speech with Emotion (prosodic): {read.Error}");
        var f = Prosody.Measure(SpeechObject.Read(read.Json!).Single());
        Assert.InRange(f.MeanF0Hz, 285, 315);
        Assert.InRange(f.RmsDb - Prosody.Measure(neutral).RmsDb, 5, 7);
        Assert.Equal(AifError.NotProduced, api.OutputRead(Ees, "OSD-BSO-V1.5", 2, 1_000).Error);        // the neural pathway was not asked for: nothing from it
    }

    [Fact]
    public void TheNeuralPathwayGivesTheEmotionlessSpeechTheFeaturesOfTheEmotion()
    {
        var neutral = Syllables(200, 3, 8000);
        Write("OSD-SEL-V1.5", 2, Selector("neural"));
        Write("OSD-SEL-V1.5", 3, new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["Language"] = new JsonObject { ["InputLanguage"] = "en", ["OutputLanguage"] = "en" } }.ToJsonString());
        Write("MMC-EEM-V2.5", 1, new JsonObject { ["Header"] = "MMC-EEM-V2.5", ["EmotionCategory"] = new JsonObject { ["SADNESS"] = new JsonObject() }, ["Degree"] = 0.5 }.ToJsonString());
        Write("OSD-BSO-V1.5", 2, SpeechObject.Make("neutral-features", neutral));                  // the emotionless speech, for its features
        Write("OSD-BSO-V1.5", 4, SpeechObject.Make("neutral", neutral));                           // and for the neural insertion

        var read = api.OutputRead(Ees, "OSD-BSO-V1.5", 2, 20_000);
        Assert.True(read.Error == AifError.OK, $"nothing at Speech with Emotion (neural): {read.Error}");
        var f = Prosody.Measure(SpeechObject.Read(read.Json!).Single());
        Assert.InRange(f.MeanF0Hz, 170, 190);                                                       // sadness: the pitch x 0.90
        Assert.InRange(f.RmsDb - Prosody.Measure(neutral).RmsDb, -5, -3);
        Assert.Equal(AifError.NotProduced, api.OutputRead(Ees, "OSD-BSO-V1.5", 1, 1_000).Error);
    }
}
