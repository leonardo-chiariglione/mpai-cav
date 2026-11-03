using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Mpai.Cae.Apps;
using Mpai.Cae.Usc;
using Mpai.Ua;

namespace Mpai.Cae.Eae;

// ENHANCED AUDIOCONFERENCE EXPERIENCE, the App.
//
//   EaeApp --capture room.wav --array array.json --out <folder>
//   EaeApp --demo <folder> [--speech a.wav b.wav]            writes a demonstration capture and its array, to try the App with: two tones, or - with --speech - real recordings of speech, the first
//                                                            from 60 degrees (a little above) and the second from -90, in a noisy room
//
// --capture  the audio of the microphone array: a WAV with a channel for each microphone (16-bit PCM)
// --array    the Microphone Array Geometry (CAE-MAG-V2.5, a JSON file)
// --out      the folder to write into: scene.json (the Audio Scene Descriptors) and a WAV for each speaker found
internal static class Program
{
    private const string Module = "1CAE-EAE-V2.5-I01";

    private static int Main(string[] args)
    {
        try
        {
            var o = new Options(args, "--capture", "--array", "--out", "--demo", "--speech", "--noise", "--radius");
            if (o.One("--demo") is { } demo) return Demo(demo, o.Many("--speech"), int.Parse(o.One("--noise") ?? "150"), double.Parse(o.One("--radius") ?? "0.012", System.Globalization.CultureInfo.InvariantCulture));
            var capture = o.Need("--capture"); var arrayFile = o.Need("--array"); var output = o.Need("--out");
            if (!File.Exists(capture)) throw new FileNotFoundException($"There is no file {capture}.");
            if (!File.Exists(arrayFile)) throw new FileNotFoundException($"There is no file {arrayFile}.");
            var array = ArrayGeometry.FromJson(JsonNode.Parse(File.ReadAllText(arrayFile)));
            var audio = MultiAudio.FromWav(File.ReadAllBytes(capture));
            if (audio.Channels.Length != array.Count) throw new ArgumentException($"The array has {array.Count} microphones and the capture {audio.Channels.Length} channels: a channel for each microphone.");
            Directory.CreateDirectory(output);

            Console.WriteLine("Starting the Module (a first start takes a while)...");
            using var module = ModuleHost.Start(Module, null, Console.WriteLine);
            string? scene = null;
            var reference = new JsonObject { ["Header"] = "CAE-ASR-V1.0", ["PolynomialVariable"] = "AngleSine", ["Polynomial"] = new JsonArray(new JsonObject { ["Coefficient"] = 1.0, ["Exponent"] = 0 }) }.ToJsonString();
            var devices = new DeviceRegistry()
                .RegisterDevice("Capture", new GivenDevice("OSD-BAO-V1.5", AudioObject.MakeMultichannel("capture", audio, array)))
                .RegisterDevice("Array", new GivenDevice("CAE-MAG-V2.5", array.ToJson().ToJsonString()))
                .RegisterDevice("Reference", new GivenDevice("CAE-ASR-V1.0", reference))
                .RegisterPresent("file", d => { if (d.TryGetValue("OSD-BAS-V1.5", out var s)) scene = s; return Task.CompletedTask; });            Console.WriteLine("Finding the speakers...");
            new WorkflowRun(module.Api, Path.Combine(ModuleHost.Root, "Apps", "CAE", "EAE", "EAE.orch"), devices, Environment.GetEnvironmentVariable("MPAI_DIAG") == "1" ? Console.WriteLine : null).Run();

            if (scene is null) throw new InvalidOperationException("The Module gave no Audio Scene.");
            File.WriteAllText(Path.Combine(output, "scene.json"), scene);
            Console.WriteLine("Audio scene: " + Path.Combine(output, "scene.json"));
            foreach (var member in JsonNode.Parse(scene)!["BasicAudioSceneDescriptorsData"]!.AsArray())
            {
                var item = AudioObject.Read(member!["AObjectIDOrAObject"]![0]!.ToJsonString()).Single();
                var source = SceneDescriptors.Read(new JsonObject { ["Header"] = SceneDescriptors.Header, ["BasicAudioSceneDescriptorsData"] = new JsonArray(member.DeepClone()) }.ToJsonString()).Single();
                var name = Path.Combine(output, (item.Id ?? "source") + ".wav"); File.WriteAllBytes(name, item.Audio.ToWav());
                var when = string.Join(", ", source.Active.Select(a => $"{a.Start:0.0}-{a.End:0.0} s"));
                Console.WriteLine($"Speaker {item.Id}: azimuth {source.Azimuth:0}, elevation {source.Elevation:0}, active {when}: {name}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Demo(string folder, IReadOnlyList<string> speech, int noise, double radius)
    {
        Directory.CreateDirectory(folder);
        var array = Room.Octahedron(radius);
        MultiAudio capture;
        if (speech.Count == 0) capture = Room.Demo(array);
        else
        {
            if (speech.Count != 2) throw new ArgumentException("--speech takes two recordings: the speakers.");
            var a = Inputs.Speech(speech[0]); var b = Inputs.Speech(speech[1]);
            var seconds = Math.Max(a.Seconds, 2.0 + b.Seconds) + 0.5;
            capture = Room.Speech(array, seconds, noise, new Room.Voice(a, 60, 20, 0.9), new Room.Voice(b, -90, 0, 0.9, 2.0));          // the second starts 2 s after the first: they speak together for a while
        }
        File.WriteAllBytes(Path.Combine(folder, "room.wav"), capture.ToWav());
        File.WriteAllText(Path.Combine(folder, "array.json"), array.ToJson().ToJsonString());
        Console.WriteLine($"Written {Path.Combine(folder, "room.wav")} ({capture.Length / (double)capture.Rate:0.0} s) and {Path.Combine(folder, "array.json")}.");
        return 0;
    }
}