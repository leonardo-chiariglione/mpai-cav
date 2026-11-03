using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
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

    private const string Asm = "1CAE-ASM-V1.0-I01";
    private static string? Value(string[] args, string name) { var at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
    private static IEnumerable<string> Values(string[] args, string name) { for (var i = 0; i + 1 < args.Length; i++) if (args[i] == name) yield return args[i + 1]; }

    private static async Task<int> RunAsync(string[] args, Action<string> say, Stopwatch clock)
    {
        // THE CONTROLLER IS A PROGRAM OF ITS OWN (AsmController, started by whoever runs this: `--controller <url>`); the workflow, read below, asks it to start the Module.
        using var api = new Mpai.Mas.Client.RemoteControllerApi(Value(args, "--controller") ?? App.ControllerUrl);

        var speech = new GivingDevice("OSD-BSO-V1.5"); var typed = new GivingDevice("OSD-BTO-V1.5");
        var sounds = new GivingDevice("CAE-UCM-V1.0"); var sceneIn = new GivingDevice("OSD-ASD-V1.5");
        var heard = new List<string>(); var texts = new List<string>(); var spoken = new List<string>(); var scenes = new List<string>();
        var devices = new DeviceRegistry();
        devices.RegisterDevice("Voice", speech).RegisterDevice("TypedIn", typed).RegisterDevice("SoundIn", sounds).RegisterDevice("SceneIn", sceneIn);
        devices.RegisterDevice("Heard", new ActingDevice("Heard", j => { var t = Text(j); lock (heard) heard.Add(t); say("HEARD: " + t); return Task.CompletedTask; }));
        devices.RegisterDevice("ReplyText", new ActingDevice("ReplyText", j => { var t = Text(j); lock (texts) texts.Add(t); say("RESPONSE TEXT: " + t); return Task.CompletedTask; }));
        devices.RegisterDevice("ReplySpeech", new ActingDevice("ReplySpeech", j => { lock (spoken) spoken.Add(j); say("RESPONSE SPEECH: " + (MpaiJson.FromJson<BasicSpeechObject>(j)?.Data?.Length ?? 0) + " bytes"); return Task.CompletedTask; }));
        devices.RegisterDevice("Scene", new ActingDevice("Scene", j => { lock (scenes) scenes.Add(j); say("SCENE: " + string.Join("; ", MainWindow.SoundsOf(j).Select(s => $"{s.Name} at {s.X:0.##}, {s.Y:0.##}, {s.Z:0.##}")) + (MainWindow.RoomOf(j) is { } r ? $" | ROOM {r.Width:0.##} x {r.Depth:0.##} m, ceiling {r.Height:0.##} m, centre {r.CentreX:0.##}, {r.CentreY:0.##}" : " | no room")); if (MainWindow.RoomOf(j) is not null || MainWindow.SoundsOf(j).Count > 0) File.WriteAllText(Value(args, "--drive") + ".asd.json", j); return Task.CompletedTask; }));
        devices.RegisterDevice("Unused", new ActingDevice("Unused", _ => Task.CompletedTask));
        string? loadingId = null, loadingName = null; var loaded = new List<string>();
        devices.RegisterDevice("ObjectReport", new ActingDevice("ObjectReport", j =>
        {
            say("OBJECT REPORT: " + System.Text.RegularExpressions.Regex.Replace(j, @"\s+", " "));
            if (loadingId is not null && MainWindow.LoadSaid(j, loadingId, loadingName!) is { } said) { lock (loaded) loaded.Add(said); say("WINDOW SAYS: " + said); }
            return Task.CompletedTask;
        }));
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
            loadingId = Guid.NewGuid().ToString("N")[..8]; loadingName = name;
            sounds.Give(MpaiJson.ToJson(new UserCommand
            {
                UserCommandID = loadingId, UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandRQ = new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }
            }));
            say("SOUND SENT: " + name);
            await Task.Delay(2500);
        }

        foreach (var file in Values(args, "--speech"))          // a person's speech, recorded: a WAV file, given as the microphone would
        {
            int before; lock (texts) before = texts.Count;
            say("SPEECH: " + file);
            speech.Give(VoiceDevice.SpeechOf(await File.ReadAllBytesAsync(file)));
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(120)) { lock (texts) if (texts.Count > before) break; await Task.Delay(200); }
            lock (texts) say(texts.Count > before ? $"answered after {waited.Elapsed.TotalSeconds:0.0} s" : "NO ANSWER within 120 s");
            await Task.Delay(1500);
        }

        foreach (var words in Values(args, "--say"))
        {
            int before; lock (texts) before = texts.Count;
            say("SAY: " + words);
            typed.Give(MpaiJson.ToJson(BasicTextObject.FromText(words)));           // the words typed: the text Port of the Module (OSD-BTO #1)
            say("the typed words are given to the Module");
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
