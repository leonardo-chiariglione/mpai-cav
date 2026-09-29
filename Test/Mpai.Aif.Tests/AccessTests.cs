using System.Text.Json;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;

namespace Mpai.Aif.Tests;

// PHASE 16 (M3245): MAC and ACR, Apps of MAS-App.
//
// STEP 1: THE MODULES IN PROCESS. 1MMC-ACR-V2.5-I01 and 1MMC-MAC-V2.5-I01 under one
// Controller, built by the providers of AIMs\Providers, their Shared Storage one
// gallery - where ACR registers and MAC recognises. Two persons: the author and
// Ronald Reagan, each a photograph and a voice (synthesised with Piper, a voice each).
// ACR registers them. MAC is shown the author with his voice; another photograph of
// Reagan with his; Barack Obama, whom nobody registered; and the author's face with
// Reagan's voice. Judged: registered, both subjects in the gallery, face and voice;
// access granted to the two, by name; refused to Reagan and to the face of one with
// the voice of the other; every Response a text, every Identification a boolean.
// Reported: what MAC said, the time of a registration and of a check.
//
// The photographs are the author's (D:\Images, or MPAI_TEST_IMAGES), outside the
// repository: no photograph of a person is kept in it.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class AccessTests
{
    private const string Acr = "1MMC-ACR-V2.5-I01", Mac = "1MMC-MAC-V2.5-I01";
    internal static readonly string Images = Environment.GetEnvironmentVariable("MPAI_TEST_IMAGES") ?? @"D:\Images";

    internal static string Face(string file) =>
        MpaiJson.ToJson(BasicVisualObject.FromFile(file, File.ReadAllBytes(Path.Combine(Images, file)), "Face"));

    // A person's voice: what they say, synthesised with their voice.
    internal static string Voice(string text, string voice)
    {
        var all = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!;
        var settings = all["1MMC-TTS-V2.5-I01"]!.AsObject().ToDictionary(p => p.Key, p =>
        {
            var v = (string)p.Value!;
            return File.Exists(Path.Combine(Repository.Root, v)) ? Path.Combine(Repository.Root, v) : v;
        });
        settings["VoiceModel"] = Path.Combine(Repository.Root, "Models", "Piper", "voices", voice, voice + ".onnx");
        settings["VoiceConfig"] = settings["VoiceModel"] + ".json";
        foreach (var k in settings.Keys.Where(k => k.StartsWith("Voice:") || k.StartsWith("VoiceConfig:")).ToList()) settings.Remove(k);
        return MpaiJson.ToJson(Mpai.Aims.Tts.TtsFactory.Create(settings).ProcessAsync(BasicTextObject.FromText(text)).GetAwaiter().GetResult());
    }

    internal static string Text(string words) => MpaiJson.ToJson(BasicTextObject.FromText(words));

    [SkippableFact]
    public void Step1InProcess()
    {
        Skip.IfNot(File.Exists(Path.Combine(Images, "R.Reagan1.jpg")), $"The photographs of the tests are not in {Images}.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var gallery = Path.Combine(Path.GetTempPath(), "mpai-p16-gallery-" + Guid.NewGuid().ToString("N"));
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
                                          store => new CompositeProvider(new MacProvider(store), new AcrProvider(store)));
        try
        {
            foreach (var module in new[] { Acr, Mac })
            {
                var started = api.StartFlow(module);
                if (started != AifError.OK) throw new InvalidOperationException($"{module} did not start: {started}");
                api.SharedStorageInit(module, gallery);
            }

            // ACR registers the two.
            const string author = "Leonardo", reagan = "Ronald_Reagan";
            var authorVoice = "en_GB-alan-medium";
            var reaganVoice = "en_US-lessiac-medium";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (name, photo, voice) in new[] { (author, "Leonardo Speaking.jpg", authorVoice), (reagan, "R.Reagan.jpg", reaganVoice) })
            {
                var r = api.Advance(Acr, [new("OSD-BVO-V1.5", 1, Face(photo)), new("OSD-BSO-V1.5", 1, Voice("My name is " + name.Replace('_', ' ') + ", and I want to register.", voice)),
                                          new("OSD-BTO-V1.5", 2, Text(name))]);
                if (r.Error != AifError.OK) throw new InvalidOperationException($"ACR, {name}: {r.Error} {JsonSerializer.Serialize(api.Status(Acr).Aims)}");
            }
            var registration = clock.Elapsed.TotalSeconds / 2;
            var registered = new AIF.SharedStorage.FileSharedStorage(gallery, "test", "test");
            var subjects = SubjectGallery.Load(registered);
            result["ACR"] = subjects.Contains(author) && subjects.Contains(reagan) && subjects.ScoreFace(new float[512]).Count == 2 && subjects.ScoreVoice(new float[192]).Count == 2
                ? "both registered, face and voice" : $"registered: {string.Join(", ", subjects.SubjectIds)}";

            // MAC checks.
            (bool? Granted, string? Said) Check(string photo, string voice, string words)
            {
                var r = api.Advance(Mac, [new("OSD-BVO-V1.5", 1, Face(photo)), new("OSD-BSO-V1.5", 1, Voice(words, voice))]);
                if (r.Error != AifError.OK) throw new InvalidOperationException($"MAC, {photo}: {r.Error} {JsonSerializer.Serialize(api.Status(Mac).Aims)}");
                var said = r.ByType("OSD-BTO-V1.5") is { } t ? MpaiJson.FromJson<BasicTextObject>(t)?.GetText() : null;
                return (r.ByType("boolean") is { } b ? bool.Parse(b) : null, said);
            }
            clock.Restart();
            var cases = new (string Case, string Photo, string Voice, bool Expected)[]
            {
                ("the author, his voice", "Leonardo Speaking.jpg", authorVoice, true),
                ("Reagan, another photograph, his voice", "R.Reagan1.jpg", reaganVoice, true),
                ("Obama, not registered", "B.Obama.jpg", authorVoice, false),
                ("the author's face, Reagan's voice", "Leonardo Speaking.jpg", reaganVoice, false),
            };
            foreach (var (name, photo, voice, expected) in cases)
            {
                var (granted, said) = Check(photo, voice, "Good morning, I would like to come in, please.");
                result[$"MAC: {name}"] = granted == expected ? (expected ? "access granted" : "access refused") : $"granted {granted?.ToString() ?? "not said"}: {said}";
                report[$"MAC: {name}"] = said ?? "-";
            }
            report["times"] = $"a registration {registration:0.0} s, a check {clock.Elapsed.TotalSeconds / cases.Length:0.0} s";

            // MAC speaks the words its workflow gives it (the author: its text input).
            var prompt = api.Advance(Mac, [new("OSD-BTO-V1.5", 1, Text("Now please say a short sentence."))]);
            result["MAC: a prompt"] = prompt.ByType("OSD-BSO-V1.5") is { } spoken && MpaiJson.FromJson<BasicSpeechObject>(spoken)?.Data.Length > 1000 && prompt.ByType("boolean") is null
                ? "spoken; no verdict" : "not spoken, or a verdict given";
        }
        finally
        {
            api.StopFlow(Acr);
            api.StopFlow(Mac);
            try { Directory.Delete(gallery, recursive: true); } catch { }
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "access-in-process.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("access-in-process.json", result);
    }

    // STEP 2 (M3245 3.3; the author): FORGOTTEN WHEN THE SESSION CLOSES. As the
    // server keeps it: the gallery's data kept as long as the session that wrote
    // them. Reagan in the gallery beforehand, as BI's subjects are, no session's.
    // The author registered by ACR in a client's session; MAC grants him; the
    // session ends. Judged: his descriptors gone from the gallery, at once; MAC
    // refuses him after; Reagan, whom no session registered, kept and granted.
    [SkippableFact]
    public void Step2ForgottenWhenTheSessionCloses()
    {
        Skip.IfNot(File.Exists(Path.Combine(Images, "R.Reagan1.jpg")), $"The photographs of the tests are not in {Images}.");
        var result = new Dictionary<string, string>();
        var gallery = Path.Combine(Path.GetTempPath(), "mpai-p16-session-" + Guid.NewGuid().ToString("N"));
        using var api = new ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"),
                                          store => new CompositeProvider(new MacProvider(store), new AcrProvider(store)));
        try
        {
            foreach (var module in new[] { Acr, Mac })
            {
                var started = api.StartFlow(module);
                if (started != AifError.OK) throw new InvalidOperationException($"{module} did not start: {started}");
                api.SharedStorageInit(module, gallery);
            }
            const string author = "Leonardo", reagan = "Ronald_Reagan";
            const string authorVoice = "en_GB-alan-medium", reaganVoice = "en_US-lessiac-medium";
            ControllerApi.Datum[] Registration(string name, string photo, string voice) =>
                [new("OSD-BVO-V1.5", 1, Face(photo)), new("OSD-BSO-V1.5", 1, Voice("My name is " + name.Replace('_', ' ') + ", and I want to register.", voice)), new("OSD-BTO-V1.5", 2, Text(name))];
            bool? Check(string photo, string voice)
            {
                var r = api.Advance(Mac, [new("OSD-BVO-V1.5", 1, Face(photo)), new("OSD-BSO-V1.5", 1, Voice("Good morning, I would like to come in, please.", voice))]);
                return r.ByType("boolean") is { } b ? bool.Parse(b) : null;
            }

            // Reagan, before the rule: no session's.
            api.Advance(Acr, Registration(reagan, "R.Reagan.jpg", reaganVoice));
            // The server's rule; the author registered in a client's session.
            api.SharedStorageKeep(gallery, AIF.SharedStorage.RuledStore.Default, new AIF.SharedStorage.StorageTime(AIF.SharedStorage.StorageLifetime.Session));
            const string session = "client-session-1";
            var r = api.Advance(Acr, Registration(author, "Leonardo Speaking.jpg", authorVoice), session);
            if (r.Error != AifError.OK) throw new InvalidOperationException($"ACR: {r.Error}");
            var before = SubjectGallery.Load(new AIF.SharedStorage.FileSharedStorage(gallery, "test", "test"));
            var grantedBefore = Check("Leonardo Speaking.jpg", authorVoice);

            api.SessionEnded(session);
            var files = Directory.EnumerateFiles(gallery, "*.data").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            var authorKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SubjectGallery.SubjectKeyPrefix + author)).Replace('/', '_').Replace('+', '-');
            var reaganKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SubjectGallery.SubjectKeyPrefix + reagan)).Replace('/', '_').Replace('+', '-');

            result["registered in the session"] = before.Contains(author) && grantedBefore == true ? "in the gallery; access granted" : $"in the gallery {before.Contains(author)}, granted {grantedBefore}";
            result["the session ended"] = !files.Contains(authorKey) ? "his descriptors deleted at once" : "his descriptors still there";
            result["after"] = Check("Leonardo Speaking.jpg", authorVoice) == false ? "access refused" : "access not refused";
            result["registered by no session"] = files.Contains(reaganKey) && Check("R.Reagan1.jpg", reaganVoice) == true ? "kept; access granted" : "not kept, or refused";
        }
        finally
        {
            api.StopFlow(Acr);
            api.StopFlow(Mac);
            try { Directory.Delete(gallery, recursive: true); } catch { }
        }
        Expected.Match("access-session.json", result);
    }
}
