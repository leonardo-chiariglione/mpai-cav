using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// SPEECH TRANSLATION IN AUDIO SCENE MANAGEMENT (the author, 2026/10/03). A User
// Command's Translated Objects names a Speech Object and the Speech Qualifier of its
// translation, whose language is the target; ASM has it translated by Text and Speech
// Translation (MMC-TST: ASR, Text-to-Text Translation, TTS - reused, not a new AIM).
// The translation replaces the original under its identifier: there is no Object in
// two versions, nor two Objects at one place. Judged: the translated speech leaves the
// Module, valid, its Qualifier stating Italian; the Speech Object stored under the
// original's identifier is now the translation.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmTranslationTests
{
    private const string Asm = AsmProvider.Module;

    [SkippableFact]
    public void ASpeechObjectTranslatedInPlace()
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        string Model(string aim, string key) => Path.Combine(Repository.Root, (string)all[aim]![key]!);
        Skip.IfNot(File.Exists(Model("1MMC-TTT-V2.5-I01", "TttEncoderModel")) && File.Exists(Model("1MMC-TTS-V2.5-I01", "Voice:it")), "the translation or the Italian voice model is absent.");

        var work = Path.Combine(Path.GetTempPath(), "asm-translation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, all.ToJsonString());

        // The speech: English, as Text-To-Speech makes it, its Qualifier stating English.
        var tts = all["1MMC-TTS-V2.5-I01"]!.AsObject().ToDictionary(p => p.Key, p =>
            File.Exists(Path.Combine(Repository.Root, (string)p.Value!)) ? Path.Combine(Repository.Root, (string)p.Value!) : (string)p.Value!);
        var english = Mpai.Aims.Tts.TtsFactory.Create(tts).ProcessAsync(BasicTextObject.FromText("Good morning. The concert starts at eight.")).GetAwaiter().GetResult();

        var r = new Dictionary<string, string>();
        using var api = new ControllerApi(Repository.Amds, settings, store => new CompositeProvider(new AsmProvider(Repository.Amds), new MatProvider(store)));
        try
        {
            r["the Module starts"] = api.StartFlow(Asm).ToString();
            api.SharedStorageInit(Asm, Path.Combine(work, "assets"));

            api.InputWrite(Asm, "OSD-BSO-V1.5", 1, MpaiJson.ToJson(english));
            api.InputWrite(Asm, "CAE-UCM-V1.0", 1, MpaiJson.ToJson(new UserCommand
            {
                UserCommandID = Guid.NewGuid().ToString(), UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandData = new UserCommandData
                {
                    TranslatedObjects = new ObjectTranslations { Objects = [new ObjectTranslation
                    {
                        ObjectID = new ManagedObject { ObjectID = english.BasicSpeechObjectID },
                        SpeechQualifier = new SpeechQualifier
                        {
                            SpeechQualifierID = Guid.NewGuid().ToString(),
                            Attributes = new SpeechAttributes { Metadata = new SpeechMetadata { Language = new Language { LanguageCode = "it" } } }
                        }
                    }] }
                }
            }));

            var read = api.OutputRead(Asm, "OSD-BSO-V1.5", 1, 300_000);
            if (read.Error != AifError.OK) r["the translated speech"] = $"nothing ({read.Error})";
            else
            {
                var translated = MpaiJson.FromJson<BasicSpeechObject>(read.Json!);
                var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD/V1.5/data/BasicSpeechObject.json"))];
                using var doc = JsonDocument.Parse(read.Json!);
                bool valid; lock (AIF.Metadata.PublishedSchemas.Lock) valid = schema.Evaluate(doc.RootElement).IsValid;
                var language = translated.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode ?? "none";
                r["the translated speech"] = $"{(translated.Data.Length > 10_000 ? "speech" : "no speech")}, {(valid ? "valid" : "not valid")}, language {language.Split('-')[0]}";
            }

            // The Speech Object kept under the original's identifier is now the translation.
            Thread.Sleep(2000);   // ASE stores it when the translation reaches it
            var storage = api.SharedStorage(Asm);
            var stored = storage is not null && storage.MPAI_AIFM_RuledStorage_Get(english.BasicSpeechObjectID, out var bytes).ToString() == "OK"
                ? MpaiJson.FromJson<BasicSpeechObject>(Encoding.UTF8.GetString(bytes)) : null;
            r["the Speech Object under the original's identifier"] = stored is null ? "none"
                : $"{(stored.Data.SequenceEqual(english.Data) ? "still the original" : "the translation")}, language {(stored.SpeechQualifier?.Attributes?.Metadata?.Language?.LanguageCode ?? "none").Split('-')[0]}";
        }
        finally
        {
            api.StopFlow(Asm);
            try { Directory.Delete(work, true); } catch { }
        }
        Expected.Match("asm-translation.json", r);
    }
}
