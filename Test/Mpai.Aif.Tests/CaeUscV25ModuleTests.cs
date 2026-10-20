using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Usc;

namespace Mpai.Aif.Tests;

// THE SPEECH RESTORATION SYSTEM AS A MODULE under a Controller, the two apart (the Controller and the Module are always separate): Speech Model Creation, the Text To Speech of
// MPAI-MMC (a stand-in here: it makes a tone from the Speech Model) and Speech Restoration Assembly, wired as the L3 says, execute in a process of their own (AIF.AimHost).
// The User Agent gives the Module its four inputs - the Speech Segments for Modelling, the Text List, the Damaged Segment and the Damaged List - in one exchange, and reads the
// Restored Segment.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25ModuleTests : IDisposable
{
    private const string Srs = "1CAE-SRS-V2.5-I01";
    private static readonly string[] Aims = ["1CAE-SMC-V2.5-I01", "1MMC-TTS-V2.5-I01", "1CAE-SRA-V2.5-I01"];
    private readonly string work = Path.Combine(Path.GetTempPath(), "cae-usc-" + Guid.NewGuid().ToString("N"));
    private readonly HostProcess process;
    private readonly ControllerApi api;

    public CaeUscV25ModuleTests()
    {
        Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var l3 = File.ReadAllText(Path.Combine(amds, Srs + ".json"));
        foreach (var aim in Aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, Srs + ".json"), l3);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject { ["1MMC-TTS-V2.5-I01"] = new JsonObject { ["Tts"] = "stand-in" } }.ToJsonString());
        process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Cae.Usc.dll") + ":Mpai.Cae.Usc.UscProvider",
                                  extra: ["--settings", settings], workingDirectory: Repository.Root);
        api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in Aims) api.Controller.AimHosts[aim] = process.Address;
        Assert.Equal(AifError.OK, api.StartFlow(Srs));
    }

    public void Dispose() { api.Dispose(); process.Dispose(); try { Directory.Delete(work, true); } catch { } }

    private void Write(string type, int n, string json) => Assert.Equal(AifError.OK, api.InputWrite(Srs, type, n, json, 10_000));

    private static Audio Tone(double seconds, double level = 8000, double hertz = 220)
    {
        var s = new short[(int)(seconds * 16000)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Sin(2 * Math.PI * hertz * i / 16000));
        return new Audio(s, 16000);
    }

    private static string Section(double start, double end) => new JsonObject
    {
        ["DamagedSectionTime"] = new JsonObject
        {
            ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t", ["Data"] = $"{start} {end}",
            ["Qualifier"] = new JsonObject
            {
                ["Header"] = "TFA-TIQ-V1.5", ["TimeQualifierID"] = "tq", ["SubTypes"] = new JsonObject(),
                ["Formats"] = new JsonObject { ["MPAIFormat"] = new JsonObject
                {
                    ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "st",
                    ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = start, ["EndTime"] = end, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
                } }
            }
        }
    }.ToJsonString();

    private static string DamagedList(params (double, double)[] sections) =>
        $"{{\"Header\":\"CAE-DLS-V2.5\",\"DamagedListID\":\"dl\",\"DamagedListData\":[{string.Join(",", sections.Select(s => Section(s.Item1, s.Item2)))}]}}";

    private static string TextList(params string[] texts) => new JsonObject
    {
        ["Header"] = "OSD-BTO-V1.5", ["BasicTextObjectID"] = "texts",
        ["BasicTextData"] = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["Data"] = t }).ToArray())
    }.ToJsonString();

    [Fact]
    public void TheModuleRestoresTheDamagedSectionsWithSpeechMadeFromTheModel()
    {
        // The four inputs of the exchange: the speech the model is made from (2 s), the texts of the two damaged sections, the Damaged Segment (3 s) and where it is damaged.
        Write("OSD-BSO-V1.5", 1, SpeechObject.Make("segments", Tone(2, 6000)));
        Write("OSD-BTO-V1.5", 1, TextList("hello", "world wide"));
        Write("OSD-BSO-V1.5", 2, SpeechObject.Make("damaged", Tone(3, 9000)));
        Write("CAE-DLS-V2.5", 1, DamagedList((0.5, 1.0), (2.0, 2.5)));

        var read = api.OutputRead(Srs, "OSD-BSO-V1.5", 1, 20_000);
        Assert.True(read.Error == AifError.OK, $"nothing at the Restored Segment: {read.Error}");
        var restored = SpeechObject.Read(read.Json!).Single();
        Assert.Equal(16000, restored.Rate);
        // 3 s, less the two damaged sections (1 s), plus the speech for "hello" (0.3 s) and for "world wide" (0.6 s).
        Assert.Equal(3 - 1.0 + 0.3 + 0.6, restored.Seconds, 3);
        // What is outside the damaged sections is the Damaged Segment, untouched; what is inside is the speech made from the model (its level the model's, 6000, not 9000).
        Assert.Equal(Tone(3, 9000).Samples[100], restored.Samples[100]);
        var inserted = restored.Samples.Skip(8000 + 400).Take(2000).Select(v => (int)Math.Abs((double)v)).Max();
        Assert.InRange(inserted, 5000, 6500);
    }
}
