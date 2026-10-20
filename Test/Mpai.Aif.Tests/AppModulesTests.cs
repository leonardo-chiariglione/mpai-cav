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

    // THE APP, NOT ONLY THE MODULE: the workflow of the App (Apps/MMC/AMQ/MMC-AMQ.orch) run by the User Agent's
    // interpreter, with simulated devices instead of a person - a picture chosen (red.jpg), a question typed
    // (the microphone never hears anything), a stage and an avatar that record what they are given. The
    // answer reaches the avatar as the machine's Portable Avatar carrying the answer as a Text Object.
    [SkippableFact]
    public async Task AmqAppAnswersThroughItsWorkflow()
    {
        RequireModels();
        using var api = Controller();
        using var done = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string? staged = null; var prompts = new List<string>(); var yesNo = 0;

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, _) => Task.FromResult<string?>(Picture("red.jpg").Json))
            .RegisterAcquire("OSD-BTO-V1.5", (_, wanted) => Task.FromResult<string?>(Text(1, (wanted ?? "").Contains("YesNo") ? (yesNo++ == 0 ? "yes, please" : "no, thank you") : "What color is the picture?").Json))
            .RegisterAcquire("OSD-BSO-V1.5", async (_, _, abandon) => { try { await Task.Delay(Timeout.Infinite, abandon); } catch (OperationCanceledException) { } return null; })
            .RegisterPresent("stage", data => { if (data.TryGetValue("OSD-BVO-V1.5", out var v)) staged = v; return Task.CompletedTask; })
            .RegisterPresent("screen", data =>
            {
                if (data.TryGetValue("Prompt", out var said1)) lock (prompts) prompts.Add(said1);
                return Task.CompletedTask;
            });
        var said = new List<string>();
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MMC", "AMQ", "MMC-AMQ.orch")));
        var interpreter = new Mpai.Ua.WorkflowInterpreter(api.Async(), devices, line => { lock (said) said.Add(line); });
        try { await interpreter.RunAsync(workflow, done.Token); } catch (OperationCanceledException) { }

        Assert.True(staged is not null, "the picture was never put on the stage: " + string.Join(" / ", said));
        // She asks whether the person wants to ask about a picture and, told yes, asks to select one; she says what the answer is, asks about
        // another picture and, told no, says maybe another time, and the App ends.
        Assert.True(prompts.Count >= 5, "what she said: " + string.Join(" / ", prompts) + " :: " + string.Join(" / ", said));
        Assert.Equal("Do you want to ask a question about a picture?", prompts[0]);
        Assert.Equal("Please select a picture.", prompts[1]);
        Assert.StartsWith("The answer is ", prompts[2]);
        Assert.Contains("red", prompts[2], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Do you want to ask a question about another picture?", prompts[3]);
        Assert.Equal("Maybe another time.", prompts[4]);
    }
    // THE STANDALONE AMQ PROGRAMS (Apps/MMC/AMQ/Standalone), what they do with the Controller their window holds -
    // the one an AmqProvider gives, and nothing else: the Module is given the picture, the typed question and the
    // machine's ID; the answer is read from the Portable Avatar's Text Object; the sentence made of it is spoken
    // through Response and Scene Rendering, given the avatar model and the machine's ID, and comes back as a
    // Portable Avatar that carries speech.
    [SkippableFact]
    public void TheStandaloneAmqProgramAnswersAndSpeaks()
    {
        RequireModels();
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => new AmqProvider(store));
        const string rsr = "1PAF-RSR-V1.6-I01";
        try
        {
            var r = Turn(api, AMQ, Picture("red.jpg"), Text(1, "What color is the picture?"), MachineId);
            var answer = AvatarUtterance.TextOfPortableAvatar(r.ByType("PAF-PAV-V1.6")) ?? "";
            Assert.Contains("red", answer, StringComparison.OrdinalIgnoreCase);

            var sentence = $"It is {answer.Trim().TrimEnd('.')}.";
            Assert.Equal(AifError.OK, api.StartFlow(rsr));
            var said = api.Advance(rsr, new[] { Text(1, sentence), Text(2, sentence), Avatar, MachineId });
            Assert.True(said.Ok, $"{rsr}: {said.Error}{Why(api, rsr)}");
            Assert.True(Spoke(said), $"{rsr} gave no speech{Why(api, rsr)}");
        }
        finally { api.StopFlow(AMQ); api.StopFlow(rsr); }
    }
    // THE STANDALONE PROGRAMS (Apps/MMC/*/Standalone) speak their guidance through Response and Scene Rendering, given the
    // Controller their window holds - the one the provider of that App gives, and nothing else. For each App's provider: a
    // prompt spoken as the program asks for it (the words twice, the avatar model, the machine's ID) comes back as a
    // Portable Avatar that carries speech.
    [SkippableTheory]
    [InlineData("ACR")] [InlineData("MAC")] [InlineData("MAD")] [InlineData("MAT")] [InlineData("MPD")]
    public void TheStandaloneProgramSpeaksItsPromptThroughRsr(string app)
    {
        RequireModels();
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => app switch
        {
            "ACR" => new AcrProvider(store), "MAC" => new MacProvider(store), "MAD" => new MadProvider(store),
            "MAT" => new MatProvider(store), _ => new MpdProvider(store)
        });
        const string rsr = "1PAF-RSR-V1.6-I01";
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(rsr));
            var said = api.Advance(rsr, new[] { Text(1, "Please look at the camera."), Text(2, "Please look at the camera."), Avatar, MachineId });
            Assert.True(said.Ok, $"{app}: {rsr}: {said.Error}{Why(api, rsr)}");
            Assert.True(Spoke(said), $"{app}: {rsr} gave no speech{Why(api, rsr)}");
        }
        finally { api.StopFlow(rsr); }
    }

    // The conversation turn of MAD, MAT and MPD as their standalone programs send it, through the provider of that App:
    // speech and its time (MAT: and the languages), the avatar model, the machine's ID; the Portable Avatar read back.
    [SkippableTheory]
    [InlineData("MAD")] [InlineData("MAT")] [InlineData("MPD")]
    public void TheStandaloneProgramTakesATurn(string app)
    {
        RequireModels();
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => app switch
        {
            "MAD" => new MadProvider(store), "MAT" => new MatProvider(store), _ => new MpdProvider(store)
        });
        var module = $"1MMC-{app}-V2.5-I01";
        var now = new ControllerApi.Datum("OSD-STM-V1.5", 1, MpaiJson.ToJson(SimpleTime.At(DateTimeOffset.UtcNow)));
        var inputs = new List<ControllerApi.Datum> { Speech("question.wav"), now };
        if (app == "MAT") inputs.Add(new("OSD-SEL-V1.5", 1, MpaiJson.ToJson(BasicSelectorObject.Languages("en", "fr"))));
        inputs.Add(Avatar); inputs.Add(MachineId);
        try
        {
            var r = Turn(api, module, inputs.ToArray());
            Assert.True(Spoke(r), $"{module} gave no speech{Why(api, module)}");
        }
        finally { api.StopFlow(module); }
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
