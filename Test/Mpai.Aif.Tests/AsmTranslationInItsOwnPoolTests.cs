using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using Mpai.Aif.Api;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// N-16 OF THE NIGHT LIST (agreed 2026-11-10): EACH COMPOSITE ITS OWN POOL (M3194). Audio Scene Management holds Text and Speech Translation (MMC-TST: ASR, TTT, TTS) and the
// Voice Command Interface (MMC-VCI: ASR, JSI, EDP, TTS). When the Controller put the leaves of the Module in one flat list, the ASR and the TTS of the two were ONE instance each,
// and the "translation" of a speech segment was the VCI's spoken reply. Judged here, with the real VCI (no stand-in): a speech segment translated in ASM is in the target
// language - its TEXT (what TST's TTT gave and TST's TTS spoke), not only its Qualifier - and the VCI does not answer it (its ASR never hears it, its EDP and its TTS give nothing).
// The real VCI is the VciProvider (its ASR, JSI, EDP and TTS), as a Service has it. Nothing of ASM is changed by this test: it drives the Module through its Ports, as AsmTranslationTests does.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmTranslationInItsOwnPoolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Asm = AsmProvider.Module, Vci = "1MMC-VCI-V2.5-I01", Tst = "1MMC-TST-V2.5-I01";
    private const string English = "Good morning. The concert starts at eight.";

    private static bool Ollama()
    {
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return http.GetAsync("http://localhost:11434/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode; }
        catch { return false; }
    }

    // What each instance took and gave: (instance, in-keys, out-ports with their JSON).
    private sealed record Turn(string Instance, string[] In, Dictionary<string, string> Out);

    // The providers of the Module, each AIM that is fired (not a runner) wrapped to record what its INSTANCE (DescriptorNode.Instance, told by the Controller) did.
    private sealed class Tracing(IAimProvider inner, List<Turn> log) : IAimProvider, IDisposable
    {
        public bool CanCreate(string aimName) => inner.CanCreate(aimName);
        public string? ImplementationOf(string aimName) => inner.ImplementationOf(aimName);
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => Wrap(inner.Create(aimName, settings, storage), aimName);
        public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage, ISharedStorage? privateStorage, IRuledStorage? moduleStorage, IAccessReader? access)
            => Wrap(inner.Create(aimName, settings, storage, privateStorage, moduleStorage, access), aimName);
        public void Dispose() => (inner as IDisposable)?.Dispose();

        private IAimProcessor Wrap(IAimProcessor p, string aimName) => p is IAimRunner ? p : new Wrapped(p, AIF.Controller.Controller.CreatingInstance ?? aimName, log);

        private sealed class Wrapped(IAimProcessor p, string instance, List<Turn> log) : IAimProcessor
        {
            public string InstanceId => p.InstanceId;
            public async Task<Message> ProcessAsync(Message message)
            {
                var r = await p.ProcessAsync(message);
                lock (log) log.Add(new Turn(instance, message.Ports.Keys.ToArray(), r is null ? new() : new Dictionary<string, string>(r.Ports)));
                return r!;
            }
        }
    }

    private static string? TextOf(string json)
    {
        try { return string.Join(" ", JsonNode.Parse(json)?["BasicTextData"]?.AsArray().Select(d => d?["Data"]?.GetValue<string>()) ?? []); } catch { return null; }
    }

    [SkippableFact]
    public void ASpeechSegmentTranslatedInAsmIsInTheTargetLanguageAndTheVciDoesNotAnswerIt()
    {
        Skip.IfNot(TestModels.Can("ttt-encoder", "voice-it") && Ollama(), "the translation model, the Italian voice or Ollama is absent.");
        var work = Path.Combine(Path.GetTempPath(), "asm-pool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.Copy(Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), settings);
        var tts = TestModels.Settings("1MMC-TTS-V2.5-I01");
        var english = Mpai.Mmc.Soe.SpeechStore.With(Mpai.Aims.Tts.TtsFactory.Create(tts).ProcessAsync(BasicTextObject.FromText(English)).GetAwaiter().GetResult(), id: "welcome");

        var log = new List<Turn>();
        using var api = new ControllerApi(Repository.Amds, settings, store => new Tracing(new CompositeProvider(new AsmProvider(Repository.Amds), new VciProvider(store), new LeafAimsProvider(store)), log));
        int id = -1;
        try
        {
            Assert.Equal(AifError.OK, api.StartFlow(Asm, out id));
            api.SharedStorageInit(id, Path.Combine(work, "assets"));
            api.InputWrite(id, "CAE-UCM-V1.0", 3, Forms.Acquire(MpaiJson.ToJson(english)));
            Assert.Equal(AifError.OK, api.OutputRead(id, "CAE-UCM-V1.0", 3, 60_000).Error);
            lock (log) log.Clear();                                                                       // from here: the translation alone
            api.InputWrite(id, "CAE-UCM-V1.0", 3, MpaiJson.ToJson(new UserCommand
            {
                UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandRQ = new UserCommandRQ
                {
                    TranslatedObjects = new ObjectTranslations { Objects = [new ObjectTranslation
                    {
                        ObjectID = new ManagedObject { ObjectID = english.BasicSpeechObjectID },
                        SpeechQualifier = new SpeechQualifier { SpeechQualifierID = Guid.NewGuid().ToString(), Attributes = new SpeechAttributes { Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "it" } } } }
                    }] }
                }
            }));
            Assert.Equal(AifError.OK, api.OutputRead(id, "CAE-UCM-V1.0", 3, 300_000).Error);           // the translation is reported
            var spoken = api.OutputRead(id, "OSD-BSO-V1.5", 1, 3_000);                                   // what the VCI would say at the Module's boundary

            List<Turn> turns; lock (log) turns = log.ToList();
            foreach (var t in turns)
            {
                var said = t.Out.Values.Select(TextOf).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                output.WriteLine($"{t.Instance}: in [{string.Join(", ", t.In)}] -> out [{string.Join(", ", t.Out.Keys)}]{(said is null ? "" : " \"" + said + "\"")}");
            }

            // TST translated: its TTT gave a text that is not the English one, and TST's ASR and TTS were the ones that worked.
            var translated = turns.Where(t => t.Instance == "1MMC-TTT-V2.5-I01").SelectMany(t => t.Out.Values).Select(TextOf).LastOrDefault(x => !string.IsNullOrWhiteSpace(x));
            output.WriteLine("Translated text: " + (translated ?? "(none)"));
            Assert.False(string.IsNullOrWhiteSpace(translated), "TST's TTT gave no text");
            Assert.DoesNotContain("concert starts", translated!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(turns, t => t.Instance == $"{Tst}/1MMC-ASR-V2.5-I01");
            Assert.Contains(turns, t => t.Instance == $"{Tst}/1MMC-TTS-V2.5-I01");

            // The VCI did not answer: its ASR never heard the segment, its EDP and its TTS gave nothing, and the Module's boundary carries no spoken reply.
            Assert.DoesNotContain(turns, t => t.Instance == $"{Vci}/1MMC-ASR-V2.5-I01");
            Assert.DoesNotContain(turns, t => t.Instance == $"{Vci}/1MMC-TTS-V2.5-I01");
            Assert.DoesNotContain(turns, t => t.Instance == "1MMC-EDP-V2.5-I03" && t.Out.Count > 0);
            Assert.NotEqual(AifError.OK, spoken.Error);
        }
        finally
        {
            api.StopFlow(id);
            try { Directory.Delete(work, true); } catch { }
        }
    }
}
