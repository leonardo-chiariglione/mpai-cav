using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Providers;
using UscAudio = Mpai.Osd.Speech.Audio;
using UscSpeech = Mpai.Osd.Speech.SpeechObject;

namespace Mpai.Aif.Tests;

// MMC-CPS-V2.5 (Conversation with Personal Status) and MMC-CAS-V2.5 (Conversation About a Scene), run through the Controller with the AIMs that have code: Automatic Speech Recognition, Natural Language
// Understanding, Personal Status Extraction (the composite of nine AIMs), Entity Dialogue Processing (the language model of Ollama) and Portable Avatar Multiplexing; Conversation with Personal Status
// takes what the camera and the microphone deliver from the Multimodal Frontend Description, and Visual Instance Identification identifies the objects.
// Visual Scene Description / Speech Scene Description (OSD-BMS-V1.5 of the L2) and Visual Object Identification (CVE-DVI-V1.0) have no code: the instances do not have them (see the Description of the L3).
// The models are many (Whisper, GoEmotions, wav2vec 2.0, HSEmotion, BlazePose, the language model): Slow.
public class CpsCasTests
{
    private static readonly string[] Models = { "text-emotion", "text-emotion-vocab", "text-emotion-merges", "spe-model", "face-emotion", "pose-landmarks", "asr-model" };

    private static bool OllamaIsThere()
    {
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode; }
        catch { return false; }
    }

    private static string Settings(out string work)
    {
        work = Path.Combine(Path.GetTempPath(), "cps-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.Copy(Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), settings);
        return settings;
    }

    private static string Face(string kind)
    {
        var file = Directory.GetFiles(Path.Combine(MpaiPaths.TestData, "Images", "Faces", kind), "*.jpg").OrderBy(f => f, StringComparer.Ordinal).First();
        return MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(file), File.ReadAllBytes(file), "Face"));
    }

    [Fact, Trait("Group", "Fast")]
    public void TheInstancesHaveTheAimsThatHaveCodeAndSayWhatTheyLack()
    {
        static string[] Subs(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, name + ".json")))!["SubAIMs"]!.AsArray()
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s!["Identifier"]!["ImplementationID"]!.GetValue<string>(), @"-V\d.*$", "")).ToArray();

        // Conversation with Personal Status is made around the Multimodal Frontend Description: every one of its Sub-AIMs has code.
        Assert.Equal(new[] { "OSD-MFD", "CVE-VII", "MMC-ASR", "MMC-NLU", "MMC-PSE", "MMC-EDP", "PAF-PMX" }, Subs("1MMC-CPS-V2.5-I01"));

        // Conversation About a Scene still has two Sub-AIMs with no code, and says so.
        var cas = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, "1MMC-CAS-V2.5-I01.json")))!;
        Assert.Equal(new[] { "MMC-ASR", "MMC-NLU", "MMC-PSE", "MMC-EDP", "PAF-PMX" }, Subs("1MMC-CAS-V2.5-I01"));
        var description = cas["Description"]!.GetValue<string>();
        Assert.Contains("OSD-BMS-V1.5", description); Assert.Contains("CVE-DVI-V1.0", description); Assert.Contains("have no code", description);
    }

    [SkippableFact, Trait("Group", "Slow")]
    public void TheConversationWithPersonalStatusAnswersWithAPortableAvatarFromTextAndFace()
    {
        Skip.IfNot(TestModels.Can(Models) && OllamaIsThere(), "the models, or Ollama, are not there");
        var settings = Settings(out var work);
        try
        {
            var trace = new List<string>();
            using var api = new ControllerApi(Repository.Amds, settings, s => new TracingProvider(Mpai.Hmc.Provider.HmcProviders.For(s, Repository.Root), trace));
            const string Cps = "1MMC-CPS-V2.5-I01";
            Assert.Equal(AifError.OK, api.StartFlow(Cps));
            Assert.Equal(AifError.OK, api.InputWrite(Cps, "OSD-BVO-V1.5", 1, Face("happy")));
            Assert.Equal(AifError.OK, api.InputWrite(Cps, "OSD-BTO-V1.5", 1, MpaiJson.ToJson(BasicTextObject.FromText("I am so excited about the concert tonight! Do you know what time it starts?"))));
            Assert.Equal(AifError.OK, api.InputWrite(Cps, "OSD-SEL-V1.5", 1, "{\"Header\":\"OSD-SEL-V1.5\",\"InputMedia\":\"Text\"}"));
            Assert.Equal(AifError.OK, api.InputWrite(Cps, "string", 1, "\"Thalia\""));
            var avatar = api.OutputRead(Cps, "PAF-PAV-V1.6", 1, 600_000);
            Assert.True(avatar.Error == AifError.OK, avatar.Error + Environment.NewLine + string.Join(Environment.NewLine, trace));
            Assert.Contains("PAF-PAV-V1.6", avatar.Json!);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [SkippableFact, Trait("Group", "Slow")]
    public void TheConversationAboutASceneAnswersWithAPortableAvatarFromSpeechAndFace()
    {
        Skip.IfNot(TestModels.Can(Models) && OllamaIsThere(), "the models, or Ollama, are not there");
        var settings = Settings(out var work);
        try
        {
            var trace = new List<string>();
            using var api = new ControllerApi(Repository.Amds, settings, s => new TracingProvider(s, trace));
            const string Cas = "1MMC-CAS-V2.5-I01";
            var speech = UscSpeech.Make("said", UscAudio.FromWav(File.ReadAllBytes(Path.Combine(MpaiPaths.Clips, "clip1272.wav"))));
            Assert.Equal(AifError.OK, api.StartFlow(Cas));
            Assert.Equal(AifError.OK, api.InputWrite(Cas, "OSD-BVO-V1.5", 1, Face("neutral")));
            Assert.Equal(AifError.OK, api.InputWrite(Cas, "OSD-BSO-V1.5", 1, speech));
            Assert.Equal(AifError.OK, api.InputWrite(Cas, "OSD-BSO-V1.5", 2, speech));
            Assert.Equal(AifError.OK, api.InputWrite(Cas, "string", 1, "\"Thalia\""));
            var avatar = api.OutputRead(Cas, "PAF-PAV-V1.6", 1, 600_000);
            Assert.True(avatar.Error == AifError.OK, avatar.Error + Environment.NewLine + string.Join(Environment.NewLine, trace));
            Assert.Contains("PAF-PAV-V1.6", avatar.Json!);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
