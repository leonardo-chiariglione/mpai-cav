using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Aif.Api;
using Mpai.Cae.AsmVoice;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;
using Mpai.Ua;
using Mpai.UaKit;
using Mpai.Wdl;

namespace Mpai.Cae.AsmUa;

// A SELF-DRIVER WITHOUT A WINDOW: the same start-up and the same path as the window - the Controller, the Module, the workflow, the devices - with the sounds and the sentences from the
// command line, and what the devices were given written to a file. For finding out what happens to a request without a person at the screen:
//   AsmUa.exe --drive out.txt [--load violin=C:\a.wav ...] --say "move the violin 2 metres to the right" [--say "..."]
// The sentences are typed ones: spoken by the machine's voice, given to the Module as speech, as the window does. Nothing is played aloud.
public static class Driver
{
    public static int Run(string[] args)
    {
        var outPath = Value(args, "--drive") ?? "asm-ua-driver.txt";
        var report = new StringBuilder();
        var clock = Stopwatch.StartNew();
        void Say(string line) { lock (report) { report.AppendLine($"{clock.Elapsed.TotalSeconds,7:0.0}s  {line}"); File.WriteAllText(outPath, report.ToString()); } App.Log("[driver] " + line); }
        try { return RunAsync(args, Say, clock).GetAwaiter().GetResult(); }
        catch (Exception e) { Say("FAILED: " + e); return 1; }
    }

    private static string? Value(string[] args, string name) { var at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
    private static IEnumerable<string> Values(string[] args, string name) { for (var i = 0; i + 1 < args.Length; i++) if (args[i] == name) yield return args[i + 1]; }

    private static async Task<int> RunAsync(string[] args, Action<string> say, Stopwatch clock)
    {
        var run = Path.Combine(Path.GetTempPath(), "asm-ua-drive-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(run);
        var prepared = AsmVoiceModules.Prepare(MpaiPaths.Root, Path.Combine(run, "module"));
        using var api = AsmVoiceModules.Controller(prepared);
        say("the Controller is built");
        var started = api.StartFlow(AsmVoiceModules.Asm);
        say("the Module starts: " + started);
        api.SharedStorageInit(AsmVoiceModules.Asm, Value(args, "--storage") ?? Path.Combine(run, "storage"));
        using var voice = new MachineVoice(null!, "Thalia", () => new ControllerApi(MpaiPaths.Amds, MpaiPaths.Settings, store => new AmqProvider(store)), (c, e) => say("voice error [" + c + "]: " + e.Message));

        var speech = new GivingDevice("OSD-BSO-V1.5");
        var sounds = new GivingDevice("CAE-UCM-V1.0"); var sceneIn = new GivingDevice("OSD-ASD-V1.5");
        var heard = new List<string>(); var texts = new List<string>(); var spoken = new List<string>(); var scenes = new List<string>();
        var devices = new DeviceRegistry();
        devices.RegisterDevice("Voice", speech).RegisterDevice("SoundIn", sounds).RegisterDevice("SceneIn", sceneIn);
        devices.RegisterDevice("Heard", new ActingDevice("Heard", j => { var t = Text(j); lock (heard) heard.Add(t); say("HEARD: " + t); return Task.CompletedTask; }));
        devices.RegisterDevice("ReplyText", new ActingDevice("ReplyText", j => { var t = Text(j); lock (texts) texts.Add(t); say("RESPONSE TEXT: " + t); return Task.CompletedTask; }));
        devices.RegisterDevice("ReplySpeech", new ActingDevice("ReplySpeech", j => { lock (spoken) spoken.Add(j); say("RESPONSE SPEECH: " + (MpaiJson.FromJson<BasicSpeechObject>(j)?.Data?.Length ?? 0) + " bytes"); return Task.CompletedTask; }));
        devices.RegisterDevice("Scene", new ActingDevice("Scene", j => { lock (scenes) scenes.Add(j); say("SCENE: " + string.Join("; ", MainWindow.SoundsOf(j).Select(s => $"{s.Name} at {s.X:0.##}, {s.Y:0.##}, {s.Z:0.##}"))); return Task.CompletedTask; }));
        devices.RegisterDevice("Unused", new ActingDevice("Unused", _ => Task.CompletedTask));
        devices.RegisterPresent("screen", data => { if (data.TryGetValue("Prompt", out var p)) say("SCREEN: " + p); return Task.CompletedTask; });

        var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
        using var stop = new CancellationTokenSource();
        var interpreter = new WorkflowInterpreter(api.Async(), devices, line => say("[workflow] " + line));
        var running = Task.Run(async () => { try { await interpreter.RunAsync(workflow, stop.Token); } catch (OperationCanceledException) { } catch (Exception e) { say("WORKFLOW FAILED: " + e); } });
        await Task.Delay(1500);
        say("the workflow runs");
        if (args.Contains("--open")) { sceneIn.Give(MainWindow.OpenStub("stage")); say("the scene stage is asked for by its name"); await Task.Delay(2500); }

        foreach (var load in Values(args, "--load"))
        {
            var eq = load.IndexOf('=');
            var name = load[..eq]; var file = load[(eq + 1)..];
            var sound = new BasicAudioObject { BasicAudioObjectID = name, DescrMetadata = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(await File.ReadAllBytesAsync(file)))] };
            sounds.Give(MpaiJson.ToJson(new UserCommand
            {
                UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandRQ = new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }
            }));
            say("SOUND SENT: " + name);
            await Task.Delay(2500);
        }

        foreach (var words in Values(args, "--say"))
        {
            int before; lock (texts) before = texts.Count;
            say("SAY: " + words);
            byte[]? wav;
            if (words.Contains('|'))                                                  // "first part|second part": what the microphone captures in two pieces, joined as the window joins them
            {
                var pieces = new List<byte[]>();
                foreach (var part in words.Split('|')) if (await voice.SynthesizeAsync(part) is { } w) pieces.Add(w);
                var data = pieces.SelectMany(w => w.Skip(44)).ToArray();
                wav = pieces[0].Take(44).Concat(data).ToArray();
                BitConverter.GetBytes(data.Length).CopyTo(wav, 40); BitConverter.GetBytes(data.Length + 36).CopyTo(wav, 4);
                say($"{pieces.Count} parts joined: {data.Length / 2.0 / 22050:0.0} s");
            }
            else wav = await voice.SynthesizeAsync(words);
            say("the typed words as speech: " + (wav?.Length ?? 0) + " bytes");
            if (wav is null) continue;
            speech.Give(VoiceDevice.SpeechOf(wav));
            say("the speech is given to the Module");
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(120)) { lock (texts) if (texts.Count > before) break; await Task.Delay(200); }
            lock (texts) say(texts.Count > before ? $"answered after {waited.Elapsed.TotalSeconds:0.0} s" : "NO ANSWER within 120 s");
            await Task.Delay(1500);
        }
        stop.Cancel();
        await Task.WhenAny(running, Task.Delay(20000));
        say("done");
        return 0;
    }

    private static string Text(string json) { try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; } catch { return "(not text)"; } }
}
