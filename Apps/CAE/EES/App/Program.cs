using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Cae.Apps;
using Mpai.Ua;

namespace Mpai.Cae.Ees;

// EMOTION ENHANCED SPEECH, the App.
//
//   EesApp --speech neutral.wav --emotion SADNESS [--degree 0.5] [--language en] --out out.wav          the emotion's features (the neural pathway)
//   EesApp --speech neutral.wav --model model.wav --out out.wav                                          the prosody of a model utterance (the prosodic pathway)
//
// --speech    the emotionless speech
// --emotion   one of the emotions of MMC-EEM (SADNESS, HAPPINESS, ANGER, FEAR, ...); --degree 0 to 1 (default 0.5)
// --language  the language of the speech (default en)
// --model     a model utterance: the speech takes its prosody
// --out       the speech with emotion
internal static class Program
{
    private const string Module = "1CAE-EES-V2.5-I01";

    private static int Main(string[] args)
    {
        try
        {
            var o = new Options(args, "--speech", "--emotion", "--degree", "--language", "--model", "--out");
            var speech = o.Need("--speech"); var output = o.Need("--out"); var model = o.One("--model");
            if (model is null && o.One("--emotion") is null) throw new ArgumentException("It needs --emotion (the neural pathway) or --model (the prosodic one).");
            var language = o.One("--language") ?? "en";
            var degree = double.Parse(o.One("--degree") ?? "0.5", System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine("Starting the Module (a first start takes a while)...");
            using var module = ModuleHost.Start(Module, null, Console.WriteLine);

            string? given = null;
            string Selector(string id) => new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = id }.ToJsonString();
            GivenDevice Of(string type, string json) => new(type, json);
            var devices = new DeviceRegistry()
                .RegisterAcquire("OSD-BTO-V1.5", (_, _) => Task.FromResult<string?>(Inputs.TextList([model is null ? "neural" : "prosodic"])))           // the pathway, in words: the one the person's options name
                .RegisterDevice("ProsodicSelector", Of("OSD-SEL-V1.5", Selector("prosodic")))
                .RegisterDevice("Model", Of("OSD-BSO-V1.5", model is null ? "{}" : Inputs.SpeechObjectOf("model", model)))
                .RegisterDevice("ProsodicSpeech", Of("OSD-BSO-V1.5", Inputs.SpeechObjectOf("neutral", speech)))
                .RegisterDevice("NeuralSelector", Of("OSD-SEL-V1.5", Selector("neural")))
                .RegisterDevice("Language", Of("OSD-SEL-V1.5", new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["Language"] = new JsonObject { ["InputLanguage"] = language, ["OutputLanguage"] = language } }.ToJsonString()))
                .RegisterDevice("Emotion", Of("MMC-EEM-V2.5", new JsonObject { ["Header"] = "MMC-EEM-V2.5", ["EmotionCategory"] = new JsonObject { [(o.One("--emotion") ?? "").ToUpperInvariant()] = new JsonObject() }, ["Degree"] = degree }.ToJsonString()))
                .RegisterDevice("FeaturesSpeech", Of("OSD-BSO-V1.5", Inputs.SpeechObjectOf("neutral-features", speech)))
                .RegisterDevice("NeuralSpeech", Of("OSD-BSO-V1.5", Inputs.SpeechObjectOf("neutral", speech)))
                .RegisterPresent("file", d => { if (d.TryGetValue("OSD-BSO-V1.5", out var s)) given = s; return Task.CompletedTask; });            Console.WriteLine("Adding the emotion...");
            new WorkflowRun(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "EES", "EES.orch"), devices, Environment.GetEnvironmentVariable("MPAI_DIAG") == "1" ? Console.WriteLine : null).Run();

            if (given is null) throw new InvalidOperationException("The Module gave no speech.");
            foreach (var file in Inputs.WriteSpeech(given, output)) Console.WriteLine("Speech with emotion: " + file);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}