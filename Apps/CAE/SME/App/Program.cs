using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Cae.Apps;
using Mpai.Ua;

namespace Mpai.Cae.Sme;

// SPEAK MODEL WITH EMOTION, the App.
//
//   SmeApp --speech neutral.wav --model model.wav --out out.wav
//
// --speech    the emotionless speech
// --model     a model utterance: the speech takes its prosody
// --out       the speech with emotion
internal static class Program
{
    private const string Module = "1CAE-SME-V2.5-I01";

    private static int Main(string[] args)
    {
        try
        {
            var o = new Options(args, "--speech", "--model", "--out");
            var speech = o.Need("--speech"); var model = o.Need("--model"); var output = o.Need("--out");

            Console.WriteLine("Starting the Module (a first start takes a while)...");
            using var module = ModuleHost.Start(Module, null, Console.WriteLine);

            string? given = null;
            string Selector(string id) => new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = id }.ToJsonString();
            GivenDevice Of(string type, string json) => new(type, json);
            var devices = new DeviceRegistry()
                .RegisterDevice("ModeSelector", Of("OSD-SEL-V1.5", Selector("prosodic")))
                .RegisterDevice("Model", Of("OSD-BSO-V1.5", Inputs.SpeechObjectOf("model", model)))
                .RegisterDevice("Speech", Of("OSD-BSO-V1.5", Inputs.SpeechObjectOf("neutral", speech)))
                .RegisterPresent("file", d => { if (d.TryGetValue("OSD-BSO-V1.5", out var s)) given = s; return Task.CompletedTask; });
            Console.WriteLine("Adding the emotion...");
            new WorkflowRun(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "SME", "SME.orch"), devices, Environment.GetEnvironmentVariable("MPAI_DIAG") == "1" ? Console.WriteLine : null).Run();

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
