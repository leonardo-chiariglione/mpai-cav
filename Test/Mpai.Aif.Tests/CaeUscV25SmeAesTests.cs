using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Ees;
using Mpai.Osd.Speech;

namespace Mpai.Aif.Tests;

// SPEAK MODEL WITH EMOTION (CAE-SME) AND ADD EMOTION TO SPEECH (CAE-AES) of CAE-USC V2.5: the two Modules the author's Reference Models draw (the deck, 2026/11/08), each a part of
// Emotion Enhanced Speech and implemented on their own. SME: Speech Feature Analysis 1 and Prosodic Emotion Insertion - the emotionless speech takes the prosody of a model utterance.
// AES: Speech Feature Analysis 2, Emotion Feature Production and Neural Emotion Insertion - the emotionless speech takes the features of a named emotion. Each Module is judged under a
// Controller, the two apart (its AIMs execute in a process of their own, wired as its L3 says); then each App is run as a person runs it.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25SmeAesTests
{
    private sealed class ModuleRig : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "cae-sme-aes-" + Guid.NewGuid().ToString("N"));
        private readonly HostProcess process;
        public readonly ControllerApi Api;
        public readonly string Module;
        public readonly int ModuleId;                                                  // the MODULE_ID the Controller returned when it started the Module

        public ModuleRig(string module, string[] aims)
        {
            Module = module;
            Directory.CreateDirectory(work);
            var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
            foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
            var l3 = File.ReadAllText(Path.Combine(amds, module + ".json"));
            foreach (var aim in aims)
                l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
            File.WriteAllText(Path.Combine(amds, module + ".json"), l3);
            var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
            process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Cae.Usc.dll") + ":Mpai.Cae.Usc.UscProvider",
                                      extra: ["--settings", settings], workingDirectory: Repository.Root);
            Api = new ControllerApi(amds, settings, _ => new CompositeProvider());
            Api.Controller.AimHostKey = HostProcess.Key;
            foreach (var aim in aims) Api.Controller.AimHosts[aim] = process.Address;
            Assert.Equal(AifError.OK, Api.StartFlow(module, out ModuleId));
        }

        public void Write(string type, int n, string json) => Assert.Equal(AifError.OK, Api.InputWrite(ModuleId, type, n, json, 10_000));
        public void Dispose() { Api.Dispose(); process.Dispose(); try { Directory.Delete(work, true); } catch { } }
    }

    private static Audio Syllables(double hertz, double perSecond, double level, double seconds = 3)
    {
        var s = new short[(int)(seconds * 16000)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)(level * Math.Pow(Math.Sin(Math.PI * perSecond * i / 16000), 2) * Math.Sin(2 * Math.PI * hertz * i / 16000));
        return new Audio(s, 16000);
    }

    private static string Selector(string id) => new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = id }.ToJsonString();

    [Fact]
    public void SpeakModelWithEmotionGivesTheEmotionlessSpeechTheProsodyOfTheModelUtterance()
    {
        using var rig = new ModuleRig("1CAE-SME-V2.5-I01", ["1CAE-SFA-V2.5-I01", "1CAE-PEI-V2.5-I01"]);
        var neutral = Syllables(200, 3, 8000);
        rig.Write("OSD-SEL-V1.5", 1, Selector("prosodic"));
        rig.Write("OSD-BSO-V1.5", 1, SpeechObject.Make("model", Syllables(300, 5, 16000)));        // the model utterance
        rig.Write("OSD-BSO-V1.5", 2, SpeechObject.Make("neutral", neutral));                       // the emotionless speech

        var read = rig.Api.OutputRead(rig.ModuleId, "OSD-BSO-V1.5", 1, 20_000);
        Assert.True(read.Error == AifError.OK, $"nothing at Speech with Emotion: {read.Error}");
        var f = Prosody.Measure(SpeechObject.Read(read.Json!).Single());
        Assert.InRange(f.MeanF0Hz, 285, 315);                                                       // the pitch of the model utterance
        Assert.InRange(f.RmsDb - Prosody.Measure(neutral).RmsDb, 5, 7);                            // and its level
    }

    [Fact]
    public void AddEmotionToSpeechGivesTheEmotionlessSpeechTheFeaturesOfTheEmotion()
    {
        using var rig = new ModuleRig("1CAE-AES-V2.5-I01", ["1CAE-SFB-V2.5-I01", "1CAE-EFP-V2.5-I01", "1CAE-NEI-V2.5-I01"]);
        var neutral = Syllables(200, 3, 8000);
        rig.Write("OSD-SEL-V1.5", 1, Selector("neural"));
        rig.Write("OSD-SEL-V1.5", 2, new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["Language"] = new JsonObject { ["InputLanguage"] = "en", ["OutputLanguage"] = "en" } }.ToJsonString());
        rig.Write("MMC-EEM-V2.5", 1, new JsonObject { ["Header"] = "MMC-EEM-V2.5", ["EmotionCategory"] = new JsonObject { ["SADNESS"] = new JsonObject() }, ["Degree"] = 0.5 }.ToJsonString());
        rig.Write("OSD-BSO-V1.5", 1, SpeechObject.Make("neutral-features", neutral));              // the emotionless speech, for its features
        rig.Write("OSD-BSO-V1.5", 2, SpeechObject.Make("neutral", neutral));                       // and for the neural insertion

        var read = rig.Api.OutputRead(rig.ModuleId, "OSD-BSO-V1.5", 1, 20_000);
        Assert.True(read.Error == AifError.OK, $"nothing at Speech with Emotion: {read.Error}");
        var f = Prosody.Measure(SpeechObject.Read(read.Json!).Single());
        Assert.InRange(f.MeanF0Hz, 170, 190);                                                       // sadness: the pitch x 0.90
        Assert.InRange(f.RmsDb - Prosody.Measure(neutral).RmsDb, -5, -3);
    }

    [Fact]
    public void TheTwoModulesAreTheSubAIMsOfEmotionEnhancedSpeechAndHaveItsPorts()
    {
        // The L3s: each Module's SubAIMs are among those of Emotion Enhanced Speech, and every Port of each is a Port of the Module of the same Data Type.
        JsonNode Read(string aim) => JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")))!;
        var ees = Read("1CAE-EES-V2.5-I01");
        var eesAims = ees["SubAIMs"]!.AsArray().Select(s => (string)s!["Identifier"]!["AIMName"]!).ToHashSet();
        foreach (var (module, expect) in new[] { ("1CAE-SME-V2.5-I01", new[] { "1CAE-SFA-V2.5-I01", "1CAE-PEI-V2.5-I01" }),
                                                 ("1CAE-AES-V2.5-I01", new[] { "1CAE-SFB-V2.5-I01", "1CAE-EFP-V2.5-I01", "1CAE-NEI-V2.5-I01" }) })
        {
            var l3 = Read(module);
            var aims = l3["SubAIMs"]!.AsArray().Select(s => (string)s!["Identifier"]!["AIMName"]!).ToArray();
            Assert.Equal(expect, aims);
            Assert.All(aims, a => Assert.Contains(a, eesAims));
            var types = ees["ExternalPorts"]!.AsArray().Select(p => (string)p!["DataType"]!).ToHashSet();
            Assert.All(l3["ExternalPorts"]!.AsArray(), p => Assert.Contains((string)p!["DataType"]!, types));
            Assert.Single(l3["ExternalPorts"]!.AsArray(), p => (string)p!["Direction"]! == "Output");
        }
    }

    // ---- The two Apps, as a person runs them ------------------------------------------------------------------------------------------------------------------------

    private static string Exe(string use, string name) => Path.Combine(Repository.Root, "Apps", "CAE", use, "App", "bin", "Debug", "net10.0", name + ".exe");

    private static (int Code, string Out, string Error) Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Repository.Root };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(6))) { process.Kill(true); Assert.Fail("The App did not finish: " + output.Result); }
        return (process.ExitCode, output.Result, error.Result);
    }

    [Fact]
    [Trait("Group", "Slow")]
    public void TheTwoAppsGiveSpeechWithEmotionFromFilesAndSayWhatIsMissing()
    {
        var work = Path.Combine(Path.GetTempPath(), "cae-sme-aes-apps-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            string Wav(string name, double hertz, double perSecond, double level) { var p = Path.Combine(work, name); File.WriteAllBytes(p, Syllables(hertz, perSecond, level).ToWav()); return p; }
            double Level(string wav) => Math.Sqrt(Audio.FromWav(File.ReadAllBytes(wav)).Samples.Average(v => (double)v * v));
            var neutral = Wav("neutral.wav", 200, 3, 8000); var model = Wav("model.wav", 300, 5, 16000);

            var prosodic = Path.Combine(work, "prosodic.wav");
            var (code, text, error) = Run(Exe("SME", "SmeApp"), "--speech", neutral, "--model", model, "--out", prosodic);
            Assert.True(code == 0, text + error);
            Assert.True(Level(prosodic) > Level(neutral), "the model utterance is louder, and the speech takes its level");
            Assert.Equal(1, Run(Exe("SME", "SmeApp"), "--speech", neutral, "--out", Path.Combine(work, "z.wav")).Code);             // no model utterance

            var sad = Path.Combine(work, "sad.wav");
            (code, text, error) = Run(Exe("AES", "AesApp"), "--speech", neutral, "--emotion", "SADNESS", "--degree", "0.5", "--out", sad);
            Assert.True(code == 0, text + error);
            Assert.True(Level(sad) < Level(neutral), "sadness is quieter");
            Assert.Equal(1, Run(Exe("AES", "AesApp"), "--speech", neutral, "--out", Path.Combine(work, "z.wav")).Code);             // no emotion
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
