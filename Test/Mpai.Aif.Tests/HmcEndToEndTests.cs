using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Hmc.Provider;

namespace Mpai.Aif.Tests;

// COMMUNICATING ENTITIES IN CONTEXT (HMC-CEC V2.2), run through the Controller with the AIMs that have code: the Multimodal Frontend Description (the scene AIMs, the alignment, the identification of the objects), the
// Entity and Context Understanding (ASR, VII, AII, NLU, Personal Status Extraction, Multimodal Scene Multiplexing), Text and Speech Translation, Entity Dialogue Processing (the language model of Ollama), Text-to-Text
// Translation and Portable Avatar Multiplexing. A typed turn with a face in front of the camera: the Machine answers with a Portable Avatar.
// The models are many (Whisper, GoEmotions, wav2vec 2.0, HSEmotion, BlazePose, SCRFD, YOLOX, YAMNet, the translation model, the language model): Slow. To be run at night.
public class HmcEndToEndTests
{
    private static readonly string[] Models = { "text-emotion", "text-emotion-vocab", "text-emotion-merges", "spe-model", "face-emotion", "pose-landmarks", "asr-model" };

    private static bool OllamaIsThere()
    {
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode; }
        catch { return false; }
    }

    private static string Face(string kind)
    {
        var file = Directory.GetFiles(Path.Combine(MpaiPaths.TestData, "Images", "Faces", kind), "*.jpg").OrderBy(f => f, StringComparer.Ordinal).First();
        return MpaiJson.ToJson(BasicVisualObject.FromFile(Path.GetFileName(file), File.ReadAllBytes(file), "Face"));
    }

    // Each AIM the provider builds, wrapped to say what went in and what came out.
    private sealed class Tracing(IAimProvider inner, List<string> log) : IAimProvider, IDisposable
    {
        public bool CanCreate(string aimName) => inner.CanCreate(aimName);
        public string? ImplementationOf(string aimName) => inner.ImplementationOf(aimName);
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => new Wrapped(inner.Create(aimName, settings, storage), aimName, log);
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage, ISharedStorage? privateStorage, IRuledStorage? moduleStorage, IAccessReader? access)
            => new Wrapped(inner.Create(aimName, settings, storage, privateStorage, moduleStorage, access), aimName, log);
        public void Dispose() => (inner as IDisposable)?.Dispose();

        private sealed class Wrapped(IAimProcessor p, string name, List<string> log) : IAimProcessor
        {
            public string InstanceId => p.InstanceId;
            public async Task<Message> ProcessAsync(Message message)
            {
                Message r;
                try { r = await p.ProcessAsync(message); }
                catch (Exception ex) { lock (log) log.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}"); throw; }
                lock (log) log.Add($"{name}: in [{string.Join(", ", message.Ports.Keys)}] -> {(r.IsError ? "ERROR " + r.Payload : "out [" + string.Join(", ", r.Ports.Keys) + "]")}");
                return r;
            }
        }
    }

    [SkippableFact, Trait("Group", "Slow")]
    public void ATypedTurnWithAFaceIsAnsweredWithAPortableAvatar()
    {
        Skip.IfNot(TestModels.Can(Models) && OllamaIsThere(), "the models, or Ollama, are not there");
        var work = Path.Combine(Path.GetTempPath(), "hmc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.Copy(Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), settings);
        try
        {
            var trace = new List<string>();
            using var api = new ControllerApi(Repository.Amds, settings, store => new Tracing(HmcProviders.For(store, Repository.Root), trace));
            const string Cec = HmcProvider.Cec;
            Assert.Equal(AifError.OK, api.StartFlow(Cec));
            Assert.Equal(AifError.OK, api.InputWrite(Cec, "OSD-BVO-V1.5", 1, Face("happy")));
            Assert.Equal(AifError.OK, api.InputWrite(Cec, "OSD-BTO-V1.5", 1, MpaiJson.ToJson(BasicTextObject.FromText("I am so excited about the concert tonight! Do you know what time it starts?"))));
            Assert.Equal(AifError.OK, api.InputWrite(Cec, "OSD-SEL-V1.5", 1, "{\"Header\":\"OSD-SEL-V1.5\",\"InputMedia\":\"Text\",\"InputLanguage\":\"en\",\"OutputLanguage\":\"fr\"}"));
            Assert.Equal(AifError.OK, api.InputWrite(Cec, "string", 1, "\"Thalia\""));
            var wait = int.TryParse(Environment.GetEnvironmentVariable("HMC_E2E_WAIT_MS"), out var ms) ? ms : 900_000;      // the night run waits as long as the models need
            var avatar = api.OutputRead(Cec, "PAF-PAV-V1.6", 1, wait);
            Assert.True(avatar.Error == AifError.OK, avatar.Error + Environment.NewLine + string.Join(Environment.NewLine, trace));
            Assert.Contains("PAF-PAV-V1.6", avatar.Json!);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
