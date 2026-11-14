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
    private const string AMQ = "1MMC-AMQ-V2.5-I01";

    private static ControllerApi Controller()
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
            _ => new AmqProvider(store));
    }

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"llama3.2:3b\""); }
        catch { return false; }
    }

    private static void RequireModels()
    {
        Skip.IfNot(TestModels.Can("ggml-small.bin"), "The models cannot be had: the L3s declare them, and Access holds them or cannot obtain them.");
        Skip.IfNot(Ollama(), "Ollama is not running with llama3.2:3b.");
    }

    // What each AIM of the Module says of itself: the reason a turn failed is here.
    private static string Why(ControllerApi api, int id)
    {
        var status = api.Status(id);
        var sb = new StringBuilder();
        foreach (var a in status.Aims) sb.Append($"\n  {a.Aim}: {a.Status} {a.Reason} [{string.Join("; ", a.Reports)}]");
        return sb.ToString();
    }

    // One turn: the Module is started (its MODULE_ID goes to the caller, who stops it), answers, with speech, and says so or says why not.
    private static ControllerApi.Result Turn(ControllerApi api, string module, out int id, params ControllerApi.Datum[] inputs)
    {
        Assert.Equal(AifError.OK, api.StartFlow(module, out id));
        var r = api.Advance(id, inputs);
        Assert.True(r.Ok, $"{module}: {r.Error}{Why(api, id)}");
        return r;
    }

    private static bool Spoke(ControllerApi.Result r) =>
        AvatarUtterance.FromPortableAvatar(r.ByType("PAF-PAV-V1.6")) is { MachineSpeechWav.Length: > 200 };

    [SkippableFact]
    public void AmqAnswersAQuestionAboutAPicture()
    {
        RequireModels();
        using var api = Controller();
        int id = -1;
        try
        {
            var r = Turn(api, AMQ, out id, Picture("red.jpg"), Text(1, "What color is the picture?"), MachineId);
            // AMQ (slide 3 of the MMC Reference Models) answers with a Portable Avatar that carries the answer as a Text Object; the colour of red.jpg is red.
            var pav = JsonNode.Parse(r.ByType("PAF-PAV-V1.6") ?? "{}") as JsonObject;
            var answer = pav?["TextObject"] is JsonObject txo ? MpaiJson.FromJson<BasicTextObject>(txo.ToJsonString()).GetText() : "";
            Assert.True(answer.Contains("red", StringComparison.OrdinalIgnoreCase), $"{AMQ} answered \"{answer}\" to a question about a red picture{Why(api, id)}");
        }
        finally { api.StopFlow(id); }
    }

    // THE APP, NOT ONLY THE MODULE: the workflow of the App (Apps/MMC/AMQ/MMC-AMQ.orch) run by the User Agent's
    // interpreter, with simulated devices instead of a person - a picture chosen (red.jpg), a question typed
    // (the microphone never hears anything), a stage and an avatar that record what they are given. The
    // answer reaches the avatar as the machine's Portable Avatar carrying the answer as a Text Object and as a
    // Speech Object, made by Text-To-Speech in the Module; the User Agent presents it and does not make speech.
    [SkippableFact]
    public async Task AmqAppAnswersThroughItsWorkflow()
    {
        RequireModels();
        using var api = Controller();
        using var done = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string? staged = null, avatar = null; var prompts = new List<string>(); var yesNo = 0;

        var devices = new Mpai.Ua.DeviceRegistry()
            .RegisterAcquire("OSD-BVO-V1.5", (_, _) => Task.FromResult<string?>(Picture("red.jpg").Json))
            .RegisterAcquire("OSD-BTO-V1.5", (_, wanted) => Task.FromResult<string?>(Text(1, (wanted ?? "").Contains("YesNo") ? (yesNo++ == 0 ? "yes, please" : "no, thank you") : "What color is the picture?").Json))
            .RegisterAcquire("OSD-BSO-V1.5", async (_, _, abandon) => { try { await Task.Delay(Timeout.Infinite, abandon); } catch (OperationCanceledException) { } return null; })
            .RegisterPresent("stage", data => { if (data.TryGetValue("OSD-BVO-V1.5", out var v)) staged = v; return Task.CompletedTask; })
            .RegisterPresent("screen", data =>
            {
                if (data.TryGetValue("Prompt", out var said1)) lock (prompts) prompts.Add(said1);
                if (data.TryGetValue("PAF-PAV-V1.6", out var pav)) avatar = pav;
                return Task.CompletedTask;
            });
        var said = new List<string>();
        var workflow = new Mpai.Wdl.WorkflowReader().Read(File.ReadAllText(Path.Combine(Repository.Root, "Apps", "MMC", "AMQ", "MMC-AMQ.orch")));
        var interpreter = new Mpai.Ua.WorkflowInterpreter(api.Async(), devices, line => { lock (said) said.Add(line); });
        try { await interpreter.RunAsync(workflow, done.Token); } catch (OperationCanceledException) { }

        Assert.True(staged is not null, "the picture was never put on the stage: " + string.Join(" / ", said));
        // She asks whether the person wants to ask about a picture and, told yes, asks to select one; the machine's Portable Avatar, with the
        // answer as text and as speech, is presented; she asks about another picture and, told no, says maybe another time, and the App ends.
        Assert.True(prompts.Count >= 4, "what she said: " + string.Join(" / ", prompts) + " :: " + string.Join(" / ", said));
        Assert.Equal("Do you want to ask a question about a picture?", prompts[0]);
        Assert.Equal("Please select a picture.", prompts[1]);
        Assert.Equal("Do you want to ask a question about another picture?", prompts[2]);
        Assert.Equal("Maybe another time.", prompts[3]);
        Assert.True(avatar is not null, "the Portable Avatar was never presented: " + string.Join(" / ", said));
        Assert.Contains("red", AvatarUtterance.TextOfPortableAvatar(avatar!) ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(AvatarUtterance.FromPortableAvatar(avatar!) is { } utterance && utterance.MachineSpeechWav.Length > 0, "the Portable Avatar carries no speech");
    }
    // THE STANDALONE AMQ PROGRAM (Apps/MMC/AMQ/Standalone), what it does with the Controller its window holds - the one an
    // AmqProvider gives, and nothing else: the Module is given the picture, the typed question and the machine's ID, and
    // answers with a Portable Avatar that carries the answer as text and as speech, made by Text-To-Speech in the Module.
    // The window renders it on the avatar it keeps: it makes no speech of its own.
    [SkippableFact]
    public void TheStandaloneAmqProgramAnswersAndTheAnswerSpeaks()
    {
        RequireModels();
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => new AmqProvider(store));
        int id = -1;
        try
        {
            var r = Turn(api, AMQ, out id, Picture("red.jpg"), Text(1, "What color is the picture?"), MachineId);
            var pav = r.ByType("PAF-PAV-V1.6");
            Assert.Contains("red", AvatarUtterance.TextOfPortableAvatar(pav) ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.True(AvatarUtterance.FromPortableAvatar(pav) is { } utterance && utterance.MachineSpeechWav.Length > 0, "the Portable Avatar of AMQ carries no speech");
        }
        finally { api.StopFlow(id); }
    }
    // THE STANDALONE PROGRAMS (Apps/MMC/*/Standalone) speak their guidance through Response and Scene Rendering, given the
    // Controller their window holds - the one the provider of that App gives, and nothing else. For each App's provider: a
    // prompt spoken as the program asks for it (the words twice, the avatar model, the machine's ID) comes back as a
    // Portable Avatar that carries speech.
    [SkippableTheory]
    [InlineData("ACR")] [InlineData("MAC")]
    public void TheStandaloneProgramSpeaksItsPromptThroughRsr(string app)
    {
        RequireModels();
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => app switch
        {
            "ACR" => new AcrProvider(store), _ => new MacProvider(store)
        });
        const string rsr = "1PAF-RSR-V1.6-I01";
        int id = -1;
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(rsr, out id));
            var said = api.Advance(id, new[] { Text(1, "Please look at the camera."), Text(2, "Please look at the camera."), Avatar, MachineId });
            Assert.True(said.Ok, $"{app}: {rsr}: {said.Error}{Why(api, id)}");
            Assert.True(Spoke(said), $"{app}: {rsr} gave no speech{Why(api, id)}");
        }
        finally { api.StopFlow(id); }
    }

    // THE CONVERSATION MODULES, as their Apps send a turn (Apps/MMC/{MCA,MTS,AMC}): the person's speech or text, the
    // Avatar Model and the machine's ID; the Portable Avatar that comes back speaks. Each through the provider of its App.
    private static ControllerApi Of(Func<AIF.Store.AmdStore, AIF.Controller.IAimProvider> provider)
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => provider(store));
    }

    [SkippableFact]
    public void McaAnswersATypedTurn()
    {
        RequireModels();
        const string MCA = "1MMC-MCA-V2.5-I01";
        using var api = Of(s => new McaProvider(s));
        int id = -1;
        try { Assert.True(Spoke(Turn(api, MCA, out id, Text(1, "Name one fruit. Answer in five words."), Avatar, MachineId)), $"{MCA} gave no speech{Why(api, id)}"); }
        finally { api.StopFlow(id); }
    }

    [SkippableFact]
    public void McaAnswersASpokenTurn()
    {
        RequireModels();
        const string MCA = "1MMC-MCA-V2.5-I01";
        using var api = Of(s => new McaProvider(s));
        int id = -1;
        try { Assert.True(Spoke(Turn(api, MCA, out id, Speech("question.wav"), Avatar, MachineId)), $"{MCA} gave no speech{Why(api, id)}"); }
        finally { api.StopFlow(id); }
    }

    [SkippableFact]
    public void MtsTranslatesATypedTurn()
    {
        RequireModels();
        const string MTS = "1MMC-MTS-V2.5-I01";
        using var api = Of(s => new MtsProvider(s));
        var languages = new ControllerApi.Datum("OSD-SEL-V1.5", 1, MpaiJson.ToJson(BasicSelectorObject.Languages("en", "fr")));
        int id = -1;
        try { Assert.True(Spoke(Turn(api, MTS, out id, languages, Text(1, "Good morning, how are you?"), Avatar, MachineId)), $"{MTS} gave no speech{Why(api, id)}"); }
        finally { api.StopFlow(id); }
    }

    [SkippableFact]
    public void AmcAnswersASpokenTurnWithTheFace()
    {
        RequireModels();
        const string AMC = "1MMC-AMC-V2.5-I01";
        using var api = Of(s => new AmcProvider(s));
        int id = -1;
        try { Assert.True(Spoke(Turn(api, AMC, out id, Speech("question.wav"), Picture("red.jpg"), Avatar, MachineId)), $"{AMC} gave no speech{Why(api, id)}"); }
        finally { api.StopFlow(id); }
    }

    // THE MEMORY LOOP: the Module gives the Summary of the conversation with each turn (Edited Summary), the User Agent
    // gives it back with the next (Summary), and the Module takes it.
    [SkippableTheory]
    [InlineData("MCA")] [InlineData("AMC")]
    public void TheConversationKeepsItsMemoryFromTurnToTurn(string app)
    {
        RequireModels();
        string module = $"1MMC-{app}-V2.5-I01";
        using var api = Of(s => app == "MCA" ? new McaProvider(s) : new AmcProvider(s));
        int id = -1;
        try
        {
            var extra = app == "AMC" ? new[] { Picture("red.jpg") } : Array.Empty<ControllerApi.Datum>();
            var first = Turn(api, module, out id, new[] { Text(1, "My name is Marta. Say hello."), Avatar, MachineId }.Concat(extra).ToArray());
            var summary = first.ByType("MMC-SUM-V2.5");
            Assert.False(string.IsNullOrWhiteSpace(summary), $"{module} gave no Edited Summary{Why(api, id)}");
            var second = api.Advance(id, new[] { Text(1, "Thank you."), Avatar, MachineId, new ControllerApi.Datum("MMC-SUM-V2.5", 1, summary!) }.Concat(extra).ToArray());
            Assert.True(second.Ok, $"{module}: {second.Error}{Why(api, id)}");
            Assert.True(Spoke(second), $"{module} gave no speech on the second turn{Why(api, id)}");
        }
        finally { api.StopFlow(id); }
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
