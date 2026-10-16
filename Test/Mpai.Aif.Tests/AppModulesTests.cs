using System.Text;
using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE MODULES OF THE APPS, RUN THROUGH THE CONTROLLER WITH SIMULATED INPUT. The same inputs a person
// gives an App - a typed question, a spoken one (Test/Data/question.wav), a picture (red.jpg) - are
// given to each Module at the Ports of its metadata, by Data Type and Port Number, as a User Agent
// does; what is judged is that the Module answers (a Portable Avatar that carries speech, or a text),
// and, if it does not, WHY: the status and reason of each AIM, which the MAS Service does not show.
// No microphone, camera or screen. The Modules need the models (Models/) and the language model
// (Ollama, llama3.2:3b); where either is absent the test is skipped.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AppModulesTests
{
    private const string MAD = "1MMC-MAD-V2.5-I01", AMQ = "1MMC-AMQ-V2.5-I01", MAT = "1MMC-MAT-V2.5-I01", MPD = "1MMC-MPD-V2.5-I01";

    private static ControllerApi Controller()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
            _ => new CompositeProvider(new AmqProvider(store), new MadProvider(store), new MatProvider(store), new MpdProvider(store)));
    }

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"llama3.2:3b\""); }
        catch { return false; }
    }

    private static void RequireModels()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models")), "Models is absent: the model files are obtained separately.");
        Skip.IfNot(Ollama(), "Ollama is not running with llama3.2:3b.");
    }

    // What each AIM of the Module says of itself: the reason a turn failed is here.
    private static string Why(ControllerApi api, string module)
    {
        var status = api.Status(module);
        var sb = new StringBuilder();
        foreach (var a in status.Aims) sb.Append($"\n  {a.Aim}: {a.Status} {a.Reason} [{string.Join("; ", a.Reports)}]");
        return sb.ToString();
    }

    // One turn: the Module answers, with speech, and says so or says why not.
    private static ControllerApi.Result Turn(ControllerApi api, string module, params ControllerApi.Datum[] inputs)
    {
        Assert.Equal(AifError.OK, api.StartFlow(module));
        var r = api.Advance(module, inputs);
        Assert.True(r.Ok, $"{module}: {r.Error}{Why(api, module)}");
        return r;
    }

    private static bool Spoke(ControllerApi.Result r) =>
        AvatarUtterance.FromPortableAvatar(r.ByType("PAF-PAV-V1.6")) is { MachineSpeechWav.Length: > 200 };

    [SkippableFact]
    public void AmqAnswersAQuestionAboutAPicture()
    {
        RequireModels();
        using var api = Controller();
        try
        {
            var r = Turn(api, AMQ, Picture("red.jpg"), Text(1, "What color is the picture?"), MachineId);
            // AMQ (slide 3 of the MMC Reference Models) answers with a Portable Avatar that carries the answer as a Text Object; the colour of red.jpg is red.
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var answer = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
            Assert.True(answer.Contains("red", StringComparison.OrdinalIgnoreCase), $"{AMQ} answered \"{answer}\" to a question about a red picture{Why(api, AMQ)}");
        }
        finally { api.StopFlow(AMQ); }
    }

    [SkippableFact]
    public void MadAnswersATypedTurn()
    {
        RequireModels();
        using var api = Controller();
        try
        {
            var r = Turn(api, MAD, Text(2, "Name one fruit. Answer in five words."), Avatar);
            Assert.True(Spoke(r), $"{MAD} gave no speech{Why(api, MAD)}");
        }
        finally { api.StopFlow(MAD); }
    }

    [SkippableFact]
    public void MadAnswersASpokenTurn()
    {
        RequireModels();
        using var api = Controller();
        try
        {
            var r = Turn(api, MAD, Speech("question.wav"), Avatar);
            Assert.True(Spoke(r), $"{MAD} gave no speech{Why(api, MAD)}");
        }
        finally { api.StopFlow(MAD); }
    }

    [SkippableFact]
    public void MatTranslatesATypedTurn()
    {
        RequireModels();
        using var api = Controller();
        try
        {
            var r = Turn(api, MAT, new("OSD-SEL-V1.5", 1, MpaiJson.ToJson(BasicSelectorObject.Languages("en", "fr"))), Text(1, "Good morning, how are you?"), Avatar);
            Assert.True(Spoke(r), $"{MAT} gave no speech{Why(api, MAT)}");
        }
        finally { api.StopFlow(MAT); }
    }

    [SkippableFact]
    public void MpdAnswersASpokenTurn()
    {
        RequireModels();
        using var api = Controller();
        try
        {
            var r = Turn(api, MPD, Speech("question.wav"), Avatar);
            Assert.True(Spoke(r), $"{MPD} gave no speech{Why(api, MPD)}");
        }
        finally { api.StopFlow(MPD); }
    }

    // ---- the simulated inputs (the same Data Types and Ports a User Agent writes) ----

    private static ControllerApi.Datum Avatar => new("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb"));

    // The Machine ID: the Portable Avatar the Module gives is the one of this machine.
    private static ControllerApi.Datum MachineId => new("string", 1, "\"cav-machine\"");

    private static string Data(string file) => Path.Combine(Repository.Root, "Test", "Data", file);

    private static ControllerApi.Datum Text(int port, string text) =>
        new("OSD-BTO-V1.5", port, MpaiJson.ToJson(BasicTextObject.FromText(text)));

    private static ControllerApi.Datum Picture(string file) =>
        new("OSD-BVO-V1.5", 1, MpaiJson.ToJson(BasicVisualObject.FromFile(file, File.ReadAllBytes(Data(file)), "Picture")));

    private static ControllerApi.Datum Speech(string file) =>
        new("OSD-BSO-V1.5", 1, MpaiJson.ToJson(BasicSpeechObject.FromData(File.ReadAllBytes(Data(file)), new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat
            {
                ContentFormats   = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = 16000, Precision = 16 } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            },
            Attributes = new SpeechAttributes
            {
                Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } }
            }
        })));
}
