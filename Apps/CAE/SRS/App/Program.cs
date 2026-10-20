using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Cae.Apps;
using Mpai.Ua;

namespace Mpai.Cae.Srs;

// SPEECH RESTORATION SYSTEM, the App.
//
//   SrsApp --voice a.wav [b.wav ...] --damaged d.wav --sections 0.5-1.0,2.0-2.5 --say "first text" --say "second text" --out restored.wav [--stand-in]
//
// --voice     speech of the speaker, from which the model of the voice is made (the more, the better the voice)
// --damaged   the recording that is damaged
// --sections  where it is damaged, in seconds from its start
// --say       what was said in each damaged section, in order (one --say for each section)
// --out       the restored speech
// --stand-in  speak with the stand-in tone and not in the voice of the speaker (no voice engine needed)
//
//   SrsApp --demo <folder> --original clip.wav [--sections 1.0-1.8,3.0-3.6]      for a demonstration: writes original.wav and damaged.wav (the sections made silent) to restore
internal static class Program
{
    private const string Module = "1CAE-SRS-V2.5-I01";

    private static int Main(string[] args)
    {
        try
        {
            var voice = new List<string>(); var say = new List<string>(); string? damaged = null, sections = null, output = null, demo = null, original = null; var standIn = false;
            for (var i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--voice": while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) voice.Add(args[++i]); break;
                    case "--damaged": damaged = args[++i]; break;
                    case "--sections": sections = args[++i]; break;
                    case "--say": say.Add(args[++i]); break;
                    case "--out": output = args[++i]; break;
                    case "--stand-in": standIn = true; break;
                    case "--demo": demo = args[++i]; break;
                    case "--original": original = args[++i]; break;
                    default: throw new ArgumentException($"I do not know {args[i]}. See the top of Program.cs, or the README.");
                }
            if (demo is not null) return Demo(demo, original ?? throw new ArgumentException("--demo needs --original."), sections ?? "1.0-1.8,3.0-3.6");
            if (voice.Count == 0 || damaged is null || sections is null || say.Count == 0 || output is null)
                throw new ArgumentException("It needs --voice, --damaged, --sections, --say and --out.");
            var damagedSections = Inputs.Sections(sections);
            if (damagedSections.Count != say.Count) throw new ArgumentException($"{damagedSections.Count} damaged section(s) and {say.Count} --say: one text for each section.");

            var settings = new JsonObject();
            if (!standIn)
            {
                settings["1CAE-SMC-V2.5-I01"] = new JsonObject { ["SpeechModel"] = "openvoice" };
                settings["1MMC-TTS-V2.5-I01"] = new JsonObject { ["Tts"] = "piper", ["SpeechModel"] = "openvoice" };
            }
            Console.WriteLine("Starting the Module (a first start takes a while)...");
            using var module = ModuleHost.Start(Module, settings, Console.WriteLine);
            string? restored = null;
            var devices = new DeviceRegistry()
                .RegisterDevice("Voice", new GivenDevice("OSD-BSO-V1.5", Inputs.SpeechObjectOf("voice", voice.ToArray())))
                .RegisterDevice("Texts", new GivenDevice("OSD-BTO-V1.5", Inputs.TextList(say)))
                .RegisterDevice("Damaged", new GivenDevice("OSD-BSO-V1.5", Inputs.SpeechObjectOf("damaged", damaged)))
                .RegisterDevice("DamagedList", new GivenDevice("CAE-DLS-V2.5", Inputs.DamagedList(damagedSections)))
                .RegisterPresent("file", given => { if (given.TryGetValue("OSD-BSO-V1.5", out var speech)) restored = speech; return Task.CompletedTask; });            Console.WriteLine("Restoring...");
            new WorkflowRun(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "SRS", "SRS.orch"), devices, Environment.GetEnvironmentVariable("MPAI_DIAG") == "1" ? Console.WriteLine : null).Run();

            if (restored is null) throw new InvalidOperationException("The Module gave no restored speech.");
            foreach (var file in Inputs.WriteSpeech(restored, output)) Console.WriteLine("Restored speech: " + file);            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // A recording and where it is to be damaged: the original is copied and the sections are made silent, so that the Module has something to restore and the result can be heard against the original.
    private static int Demo(string folder, string original, string sections)
    {
        Directory.CreateDirectory(folder);
        var audio = Inputs.Speech(original); var samples = (short[])audio.Samples.Clone();
        foreach (var (start, end) in Inputs.Sections(sections))
            for (var i = (int)(start * audio.Rate); i < Math.Min(samples.Length, (int)(end * audio.Rate)); i++) samples[i] = 0;
        File.WriteAllBytes(Path.Combine(folder, "original.wav"), audio.ToWav());
        File.WriteAllBytes(Path.Combine(folder, "damaged.wav"), new Mpai.Cae.Usc.Audio(samples, audio.Rate).ToWav());
        Console.WriteLine($"Written original.wav ({audio.Seconds:0.0} s) and damaged.wav (silent at {sections} s) in {folder}");
        return 0;
    }
}