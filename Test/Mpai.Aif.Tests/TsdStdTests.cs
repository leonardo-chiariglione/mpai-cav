using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aims.Tts;
using Mpai.Cae.Usc.Voice;
using Mpai.Core;
using Mpai.Mmc.Tsd;
using Mpai.Providers;
using Mpai.Osd.Speech;
using UscSpeech = Mpai.Osd.Speech.SpeechObject;
using UscAudio = Mpai.Osd.Speech.Audio;

namespace Mpai.Aif.Tests;

// MMC-TSD-V2.5 Text-to-Speech with Descriptors and MMC-STD-V2.5 Speech Translation with Descriptors.
//
// TSD speaks a Text Object with the voice that a Speech Descriptors Object describes: the descriptors are the tone-colour embedding of the voice (OpenVoice V2), which the Qualifier of the Object
// names. Entity Speech Description (its OpenVoice instance, I02) makes them from the speech of the Entity. STD is the composite of Automatic Speech Recognition, Entity Speech Description,
// Text-to-Text Translation and TSD: what is said, in another language, in the voice that said it. There is no direct speech-to-speech translation.
//
// The refusals are quick (Fast); the voice, and the composite, need the voice engine and the models (Python with torch, OpenVoice, Piper, Whisper, M2M100): Slow.
public class TsdStdTests
{
    private static readonly string Clips = MpaiPaths.Clips;
    private static UscAudio Clip(string speaker) => UscAudio.FromWav(File.ReadAllBytes(Path.Combine(Clips, "clip" + speaker + ".wav")));
    private static double Distance(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => (double)(x - y) * (x - y)).Sum());

    private static AIF.Store.AmdStore Store() { var s = new AIF.Store.AmdStore(Repository.Amds); s.Scan(); return s; }

    // ---- what is refused: without the voice engine ---------------------------------------------------------------------------------------------

    [Fact, Trait("Group", "Fast")]
    public void OnlyAToneColourEmbeddingDrivesTheVoice()
    {
        var good = Core.OSD.SpeechDescriptorsObject.FromEmbedding(new float[OpenVoiceWorker.EmbeddingLength], TsdAimProcessor.DescriptorFormat);
        Assert.Equal(OpenVoiceWorker.EmbeddingLength, TsdAimProcessor.Embedding(MpaiJson.ToJson(good)).Length);

        var ecapa = Core.OSD.SpeechDescriptorsObject.FromEmbedding(new float[192], "ECAPA-TDNN (192-d)");
        Assert.Contains("ECAPA-TDNN (192-d)", Assert.Throws<FormatException>(() => TsdAimProcessor.Embedding(MpaiJson.ToJson(ecapa))).Message);

        var short_ = Core.OSD.SpeechDescriptorsObject.FromEmbedding(new float[12], TsdAimProcessor.DescriptorFormat);
        Assert.Contains("not a tone-colour embedding", Assert.Throws<FormatException>(() => TsdAimProcessor.Embedding(MpaiJson.ToJson(short_))).Message);
    }

    [Fact, Trait("Group", "Fast")]
    public async Task WithoutTheTextOrTheDescriptorsThereIsNothingToSpeak()
    {
        var ports = AimPortReader.Load(Store(), "1MMC-TSD-V2.5-I01");
        var tsd = new TsdAimProcessor("tsd", null!, new OpenVoiceWorker(), ports);          // refused before the engines are used
        var text = ports.Input("OSD-BTO-V1.5"); var sdo = ports.Input("MMC-SDO-V2.5");
        var noText = await tsd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [sdo] = "{}" } });
        Assert.True(noText.IsError); Assert.Contains("no Text Object", noText.Payload);
        var noDescriptors = await tsd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [text] = MpaiJson.ToJson(BasicTextObject.FromText("Hello.")) } });
        Assert.True(noDescriptors.IsError); Assert.Contains("no Speech Descriptors Object", noDescriptors.Payload);
        var wrong = await tsd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [text] = MpaiJson.ToJson(BasicTextObject.FromText("Hello.")), [sdo] = MpaiJson.ToJson(Core.OSD.SpeechDescriptorsObject.FromEmbedding(new float[192], "ECAPA-TDNN (192-d)")) } });
        Assert.True(wrong.IsError); Assert.Contains("this voice engine takes", wrong.Payload);
    }

    [Fact, Trait("Group", "Fast")]
    public void TheNewFormatIsOneOfTheFormatsOfTheSpeechDescriptors()
    {
        var formats = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "TFA", "V1.5", "formats", "SpeechDescriptorsFormats.json")))!["oneOf"]!.AsArray();
        Assert.Contains(formats, f => f!["const"]!.GetValue<string>() == TsdAimProcessor.DescriptorFormat);
    }

    [Fact, Trait("Group", "Fast")]
    public void TsdTakesTheSelectorAndTheStdFeedsIt()
    {
        var store = Store();
        // TSD: the Selector is an optional input (it says whether the output Speech retains the input Speech Descriptors).
        Assert.StartsWith("OSD-SEL-V1.5", AimPortReader.Load(store, "1MMC-TSD-V2.5-I01").Input("OSD-SEL-V1.5"));
        // STD: the external inputs of the figure (Input Speech, Language Selector, Input Text, Text/Speech Selector, Descriptor Selector), and the Descriptor Selector goes to TSD.
        var std = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-STD-V2.5-I01.json")))!;
        var inputs = std["ExternalPorts"]!.AsArray().Where(p => p!["Direction"]!.GetValue<string>() == "Input").Select(p => p!["Name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "InputSpeech", "LanguageSelector", "InputText", "TextSpeechSelector", "DescriptorSelector" }, inputs);
        Assert.Contains(std["Topology"]!.AsArray(), t => t!["Output"]!["AIMName"]!.GetValue<string>() == "" && t["Output"]!["PortNumber"]?.GetValue<int>() == 3
            && t["Input"]!["AIMName"]!.GetValue<string>() == "1MMC-TSD-V2.5-I01" && t["Input"]!["DataType"]!.GetValue<string>() == "OSD-SEL-V1.5");
    }

    // ---- the voice -----------------------------------------------------------------------------------------------------------------------------------

    [SkippableFact, Trait("Group", "Slow")]
    public async Task TheTextIsSpokenInTheVoiceThatTheDescriptorsDescribe()
    {
        Skip.IfNot(TestModels.Can("openvoice-source", "openvoice-converter", "voice-en") && MpaiPaths.Python != "python", "the voice engine, or the Piper voice, is not installed");
        var settings = TestModels.Settings("1MMC-TSD-V2.5-I01");
        var worker = new OpenVoiceWorker(settings);
        var store = Store();

        // Entity Speech Description (the OpenVoice instance): the descriptors of the voice of speaker 1272.
        var esdPorts = AimPortReader.Load(store, "1MMC-ESD-V2.5-I02");
        var esd = new EsdOpenVoiceAimProcessor("esd", worker, esdPorts);
        var target = Clip("1272");
        var described = await esd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [esdPorts.Input("OSD-BSO-V1.5")] = UscSpeech.Make("s", target) } });
        Assert.False(described.IsError, described.Payload);
        var sdo = described.Ports[esdPorts.Output("MMC-SDO-V2.5")];
        Assert.Equal(TsdAimProcessor.DescriptorFormat, JsonNode.Parse(sdo)!["SpeechDescriptorsQualifier"]!["Formats"]!["OtherSpeechDescriptorsFormats"]!.GetValue<string>());
        var voice = TsdAimProcessor.Embedding(sdo);

        // Text-to-Speech with Descriptors: a text, in that voice.
        var tsdPorts = AimPortReader.Load(store, "1MMC-TSD-V2.5-I01");
        var tts = TtsFactory.Create(settings);
        var tsd = new TsdAimProcessor("tsd", tts, worker, tsdPorts);
        var text = BasicTextObject.FromText("Good morning. The concert starts at eight.");
        var spoken = await tsd.ProcessAsync(new Message { MessageId = "m", Ports = new() { [tsdPorts.Input("OSD-BTO-V1.5")] = MpaiJson.ToJson(text), [tsdPorts.Input("MMC-SDO-V2.5")] = sdo } });
        Assert.False(spoken.IsError, spoken.Payload);
        var piece = UscSpeech.Read(spoken.Ports[tsdPorts.Output("OSD-BSO-V1.5")]).Single();
        Assert.Equal(16000, piece.Rate); Assert.InRange(piece.Seconds, 1.0, 20.0);

        // The voice of the speech is nearer the described voice than the voice of Piper that said the same text.
        var plain = UscSpeech.Read(Mpai.Cae.Usc.Voice.SchemaForm.Speech(MpaiJson.ToJson(await tts.ProcessAsync(text, "")))).Single().At(16000);
        Assert.True(Distance(worker.Extract([piece]), voice) < 0.7 * Distance(worker.Extract([plain]), voice), "the speech is nearer the voice described than the voice that said it was");
    }

    // ---- the composite -------------------------------------------------------------------------------------------------------------------------------

    private const string Std = "1MMC-STD-V2.5-I01";

    [SkippableFact, Trait("Group", "Slow")]
    public void EnglishSpeechComesOutInItalianInTheVoiceThatSaidIt()
    {
        Skip.IfNot(TestModels.Can("openvoice-source", "openvoice-converter", "ttt-encoder", "voice-it", "asr-model") && MpaiPaths.Python != "python", "the models, or the voice engine, are not installed");
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        var work = Path.Combine(Path.GetTempPath(), "std-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, all.ToJsonString());
        var speech = Clip("1272");                                  // English, LibriSpeech

        using var api = new ControllerApi(Repository.Amds, settings, s => new LeafAimsProvider(s));
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Std));
            Assert.Equal(AifError.OK, api.InputWrite(Std, "OSD-SEL-V1.5", 1, MpaiJson.ToJson(BasicSelectorObject.Languages("en", "it"))));
            Assert.Equal(AifError.OK, api.InputWrite(Std, "OSD-BSO-V1.5", 1, UscSpeech.Make("said", speech)));
            var translatedText = api.OutputRead(Std, "OSD-BTO-V1.5", 1, 240_000);
            Assert.Equal(AifError.OK, translatedText.Error);
            var text = MpaiJson.FromJson<BasicTextObject>(translatedText.Json!);
            Assert.False(string.IsNullOrWhiteSpace(text?.GetText()), "the translated text is empty");
            var translated = api.OutputRead(Std, "OSD-BSO-V1.5", 1, 240_000);
            Assert.Equal(AifError.OK, translated.Error);
            var piece = UscSpeech.Read(translated.Json!).First().At(16000);

            // The voice of the translated speech is nearer the voice that said the speech than the voice of Piper that says the same text alone.
            var tsd = TestModels.Settings("1MMC-TSD-V2.5-I01");
            var worker = new OpenVoiceWorker(tsd);
            var voice = worker.Extract([speech]);
            var alone = UscSpeech.Read(Mpai.Cae.Usc.Voice.SchemaForm.Speech(MpaiJson.ToJson(TtsFactory.Create(tsd).ProcessAsync(text!, "").GetAwaiter().GetResult()))).First().At(16000);
            var heard = Distance(worker.Extract([piece]), voice); var plain = Distance(worker.Extract([alone]), voice);
            Assert.True(heard < 0.8 * plain, $"the translated speech has the voice that said the speech: {heard:0.00} from it, the voice of Piper alone {plain:0.00}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
