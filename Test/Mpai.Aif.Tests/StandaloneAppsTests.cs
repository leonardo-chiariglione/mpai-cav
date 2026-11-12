using System.Net.Http;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// THE MMC APPS ON THEIR OWN (Apps/MMC/<App>/Standalone): the User Agent of the App is a window with the avatar and the devices,
// and the Controller it holds runs the Module in its own process, built from the provider of the App. Each test gives the Module
// what the window gives it, turn by turn, and reads what the window reads: the Portable Avatar, which carries the reply as text
// and as speech made inside the Module. The window renders it and makes no speech of its own.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class StandaloneAppsTests
{
    private const string PAV = "PAF-PAV-V1.6";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetStringAsync("http://127.0.0.1:11434/api/tags").Result.Contains("\"llama3.2:3b\""); }
        catch { return false; }
    }

    private static void RequireSpeech() =>
        Skip.IfNot(TestModels.Can("ggml-small.bin", "tts-program"), "The speech models cannot be had: the L3s declare them, and Access holds them or cannot obtain them.");

    private static ControllerApi Api(Func<AIF.Store.AmdStore, IAimProvider> provider)
    {
        var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
        return new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), _ => provider(store));
    }

    private static ControllerApi.Datum Avatar => new("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb"));
    private static ControllerApi.Datum Machine => new("string", 1, "\"MPAI as a Service\"");
    private static ControllerApi.Datum Words(int port, string text) => new("OSD-BTO-V1.5", port, MpaiJson.ToJson(BasicTextObject.FromText(text)));

    private static string Why(ControllerApi api, string module)
    {
        var status = api.Status(module);
        var sb = new System.Text.StringBuilder();
        foreach (var a in status.Aims) sb.Append($"\n  {a.Aim}: {a.Status} {a.Reason} [{string.Join("; ", a.Reports)}]");
        return sb.ToString();
    }

    // What the window shows and renders: the reply as text, and the speech the Portable Avatar carries.
    private static (string Text, int SpeechBytes) Reading(ControllerApi.Result r)
    {
        var pav = r.ByType(PAV);
        return (AvatarUtterance.TextOfPortableAvatar(pav) ?? "", AvatarUtterance.FromPortableAvatar(pav)?.MachineSpeechWav.Length ?? 0);
    }

    // MCA: the Module is started once; each turn offers the person's words, the Avatar Model and the machine's ID, with the Edited
    // Summary of the turn before; it answers with a Portable Avatar that speaks, and the memory of the conversation.
    [SkippableFact]
    public void McaHoldsAConversationAndSpeaksEachReply()
    {
        RequireSpeech();
        Skip.IfNot(Ollama(), "Ollama is not running with llama3.2:3b.");
        const string module = "1MMC-MCA-V2.5-I01";
        using var api = Api(s => new McaProvider(s));
        Assert.Equal(AifError.OK, api.StartFlow(module));
        try
        {
            var first = api.Advance(module, new[] { Words(1, "Hello, my name is Anna. How are you today?"), Avatar, Machine });
            Assert.True(first.Ok, $"{module}: {first.Error}{Why(api, module)}");
            var (text1, speech1) = Reading(first);
            Assert.False(string.IsNullOrWhiteSpace(text1), "the first reply has no text");
            Assert.True(speech1 > 200, "the first reply carries no speech");
            var memory = first.ByType("MMC-SUM-V2.5");
            Assert.False(string.IsNullOrWhiteSpace(memory), "the Module gave no memory of the conversation");
            Assert.Contains("Anna", memory!, StringComparison.OrdinalIgnoreCase);

            var second = api.Advance(module, new[] { Words(1, "What is my name?"), Avatar, Machine, new ControllerApi.Datum("MMC-SUM-V2.5", 1, memory!) });
            Assert.True(second.Ok, $"{module}: {second.Error}{Why(api, module)}");
            var (text2, speech2) = Reading(second);
            Assert.True(speech2 > 200, "the second reply carries no speech");
            Assert.Contains("Anna", text2, StringComparison.OrdinalIgnoreCase);
        }
        finally { api.StopFlow(module); }
    }

    // MTS: each turn offers the Language Selector, the sentence (typed, or spoken), the Avatar Model and the machine's ID; the Module
    // answers with the translation as a Text Object and as a Portable Avatar that speaks it.
    [SkippableFact]
    public void MtsTranslatesATypedAndASpokenSentenceAndSpeaksTheTranslation()
    {
        RequireSpeech();
        Skip.IfNot(TestModels.Can("ttt-encoder", "voice-it"), "The translation or the Italian voice cannot be had.");
        const string module = "1MMC-MTS-V2.5-I01";
        var english = new BasicTextObject[] { BasicTextObject.FromText("Good morning. The concert starts at eight.", new TextQualifier
            { TextQualifierID = Guid.NewGuid().ToString(), Attributes = new TextAttributes { Language = new Language { LanguageCode = "en", LanguageFormat = LanguageFormat.Iso639_1 } } }) };
        var languages = new ControllerApi.Datum("OSD-SEL-V1.5", MpaiJson.ToJson(BasicSelectorObject.Languages("en", "it")));
        using var api = Api(s => new MtsProvider(s));
        Assert.Equal(AifError.OK, api.StartFlow(module));
        try
        {
            var typed = api.Advance(module, new[] { languages, new ControllerApi.Datum("OSD-BTO-V1.5", 1, MpaiJson.ToJson(english[0])), Avatar, Machine });
            Assert.True(typed.Ok, $"{module}: {typed.Error}{Why(api, module)}");
            var translation = MpaiJson.FromJson<BasicTextObject>(typed.ByType("OSD-BTO-V1.5")!)!.GetText();
            Assert.Contains("concert", translation, StringComparison.OrdinalIgnoreCase);       // Italian: "il concerto inizia alle otto"
            Assert.NotEqual(english[0].GetText(), translation);
            Assert.True(Reading(typed).SpeechBytes > 200, "the translation is not spoken");

            // The same sentence spoken: Text-To-Speech makes the English speech, the Module recognises it, translates it and speaks it.
            var spoken = Mpai.Aims.Tts.TtsFactory.Create(TestModels.Settings("1MMC-TTS-V2.5-I01")).ProcessAsync(english[0]).GetAwaiter().GetResult();
            var heard = api.Advance(module, new[] { languages, new ControllerApi.Datum("OSD-BSO-V1.5", MpaiJson.ToJson(spoken)), Avatar, Machine });
            Assert.True(heard.Ok, $"{module}: {heard.Error}{Why(api, module)}");
            Assert.Contains("concert", MpaiJson.FromJson<BasicTextObject>(heard.ByType("OSD-BTO-V1.5")!)!.GetText(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Reading(heard).SpeechBytes > 200, "the translation of the spoken sentence is not spoken");
        }
        finally { api.StopFlow(module); }
    }

    // AMC: speech-only affective dialogue: each turn offers the person's speech and a picture of the face, which the camera takes (a
    // Visual Object whose type is a Face); the Module reads the words, the voice and the face, and answers with a Portable Avatar that speaks.
    [SkippableFact]
    public void AmcAnswersWhatItHearsAndSeesAndSpeaksTheReply()
    {
        RequireSpeech();
        Skip.IfNot(Ollama(), "Ollama is not running with llama3.2:3b.");
        var faces = Path.Combine(Mpai.Core.MpaiPaths.TestData, "Images", "Faces", "happy");
        Skip.IfNot(Directory.Exists(faces) && Directory.GetFiles(faces).Length > 0, "There is no face picture in " + faces);
        var photo = Directory.GetFiles(faces).OrderBy(f => f, StringComparer.Ordinal).First();
        const string module = "1MMC-AMC-V2.5-I01";
        using var api = Api(s => new AmcProvider(s));
        Assert.Equal(AifError.OK, api.StartFlow(module));
        try
        {
            var face = new ControllerApi.Datum("OSD-BVO-V1.5", MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(photo), File.ReadAllBytes(photo), "Face")));
            // The person speaks: Text-To-Speech makes the speech of the sentence, as the microphone would give it.
            var said = Mpai.Aims.Tts.TtsFactory.Create(TestModels.Settings("1MMC-TTS-V2.5-I01"))
                .ProcessAsync(BasicTextObject.FromText("I have just received wonderful news! I got the job.")).GetAwaiter().GetResult();
            var r = api.Advance(module, new[] { new ControllerApi.Datum("OSD-BSO-V1.5", MpaiJson.ToJson(said)), face, Avatar, Machine });
            Assert.True(r.Ok, $"{module}: {r.Error}{Why(api, module)}");
            var (_, speech) = Reading(r);
            Assert.True(speech > 200, "the reply carries no speech");
            Assert.False(string.IsNullOrWhiteSpace(r.ByType("MMC-SUM-V2.5")), "the Module gave no memory of the conversation");
        }
        finally { api.StopFlow(module); }
    }

    // NPR: the page and a Point at the top of a column (in pixels of the picture) and the machine's ID; the Module answers with a
    // Portable Avatar that carries the text of the column and the speech that reads it, made by Text-To-Speech inside the Module.
    [SkippableFact]
    public void NprReadsTheClickedColumnAloud()
    {
        RequireSpeech();
        Skip.IfNot(TestModels.Can("ocr-det"), "The text recognition model cannot be had.");
        var folder = Path.Combine(Mpai.Core.MpaiPaths.TestData, "Newspapers", "synthetic", "en-clean");
        Skip.IfNot(File.Exists(Path.Combine(folder, "page.png")) && File.Exists(Path.Combine(folder, "columns.json")), "There is no test page in " + folder);
        using var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "columns.json")));
        var column = meta.RootElement.GetProperty("Columns")[0];
        var at = column.GetProperty("Point");
        var truth = column.GetProperty("Text").GetString()!;
        const string module = "1MMC-NPR-V2.5-I01";
        using var api = Api(s => new NprProvider(s));
        Assert.Equal(AifError.OK, api.StartFlow(module));
        try
        {
            var page = BasicVisualObject.FromFile("page.png", File.ReadAllBytes(Path.Combine(folder, "page.png")), "Picture");
            var point = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{at[0].GetDouble():0.#},\"Y\":{at[1].GetDouble():0.#}}}}}");
            var r = api.Advance(module, new[] { new ControllerApi.Datum("OSD-BVO-V1.5", 1, MpaiJson.ToJson(page)), new ControllerApi.Datum("OSD-PNT-V1.5", 1, point), Machine });
            Assert.True(r.Ok, $"{module}: {r.Error}{Why(api, module)}");
            var (text, speech) = Reading(r);
            var title = truth.Split((char)10)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3);
            Assert.Contains(title.First(), text, StringComparison.OrdinalIgnoreCase);
            Assert.True(speech > 200, "the column is not read aloud");
        }
        finally { api.StopFlow(module); }
    }
}
