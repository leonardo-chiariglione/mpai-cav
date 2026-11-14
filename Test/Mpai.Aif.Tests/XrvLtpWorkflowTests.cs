using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Mpai.Aif.Api;
using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Mmm.Server;
using Mpai.Ua;
using Mpai.Wdl;
using Mpai.Xrv;
using Mpai.Xrv.End;
using Mpai.Xrv.VirtualVenue;

namespace Mpai.Aif.Tests;

// N-17: THE APP OF THE LIVE THEATRICAL PERFORMANCE (Apps\XRV\LTP\XRV-LTP.orch), interpreted by the User Agent over the Module 1XRV-LTP-V1.0-I01, with three devices: the Show (the
// documents of the mini show), the Real Venue and the Virtual Venue. Each venue plays a recording of a performance (its Data In) and has its Output Unit, a stand-in: the lighting of the
// real venue is a DMX writer (the level of each Universe/Channel, written to a file). The virtual venue is MMM-TEC (MmmVirtualVenue): the virtual stage is the M-Location "Stage" of an
// M-Instance behind the MMM-API; a guest's Persona walks onto it, a Bot Service places its own, and the Process that animates the virtual venue changes the Light Source of the virtual lamp. No real hardware.
// The User Agent deals with the Controller only through the Controller API; the Output Units only receive what the Module delivers.
[Trait("Group", "Fast")]
public class XrvLtpWorkflowTests
{
    private static readonly string Workflow = Path.Combine(Repository.Root, "Apps", "XRV", "LTP", "XRV-LTP.orch");

    // THE SHOW: its documents, one after the other, and then nothing; it receives the Participant Data the show gives back.
    private sealed class ShowDevice(params (string DataType, string File)[] documents) : IDevice
    {
        public readonly TaskCompletionSource Given = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> ParticipantData = new();
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            foreach (var (type, file) in documents) yield return (type, XrvShow.Text(file));
            Given.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancel);
        }
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel) { lock (ParticipantData) ParticipantData.Add(json); return Task.CompletedTask; }
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // A VENUE: what it plays (its Data In, as the test gives it) and its Output Unit.
    private abstract class Venue(string dataInType) : IDevice
    {
        private readonly Channel<string> recording = Channel.CreateUnbounded<string>();
        public void Play(string dataIn) => recording.Writer.TryWrite(dataIn);
        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            await foreach (var d in recording.Reader.ReadAllAsync(cancel)) yield return (dataInType, d);
        }
        public abstract Task DeliverAsync(string dataType, string json, CancellationToken cancel);
        public Task SafeStopAsync() => Task.CompletedTask;
    }

    // The lighting of the real venue: a DMX writer. Each Control Object whose command is DMX sets the level of its Universe/Channel; every change is a line of the file.
    private sealed class RealVenue(string file) : Venue(EndAimProcessor.RDI)
    {
        public readonly Dictionary<string, int> Levels = new();
        public override Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            foreach (var c in JsonNode.Parse(json)!["REDataOut"]?["ControlObjects"]?.AsArray() ?? [])
            {
                var command = JsonNode.Parse(c!["ControlObject"]!["ControlData"]!.GetValue<string>())!;
                if (command["Protocol"]?.GetValue<string>() != "DMX") continue;
                var key = $"{command["Universe"]}/{command["Channel"]}";
                var value = command["Value"]!.GetValue<int>();
                lock (Levels) Levels[key] = value;
                File.AppendAllText(file, $"DMX {key} {value}\n");
            }
            return Task.CompletedTask;
        }
        public int? Level(string key) { lock (Levels) return Levels.TryGetValue(key, out var v) ? v : null; }
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 10_000)
    {
        for (var waited = 0; waited < ms; waited += 50) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    // A venue whose Output Unit takes what it is given and does nothing with it.
    private sealed class QuietVenue(string dataInType) : Venue(dataInType)
    {
        public override Task DeliverAsync(string dataType, string json, CancellationToken cancel) => Task.CompletedTask;
    }

    // ON DEGRADED, IN CONTINUOUS EXECUTION (review finding X6): the real venue sends an RE Data In that is not valid; Environment Description reports it and is DEGRADED.
    // The Module's policy is Continue; the workflow's 'on Degraded:' tells the operator which AIM and why - once - and the show goes on: C1 still brings the lamps up.
    [Fact]
    public async Task AnAimThatDegradesIsToldToTheOperatorAndTheShowGoesOn()
    {
        var work = Path.Combine(Path.GetTempPath(), "xrv-degraded-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        try
        {
            using var api = new ControllerApi(Repository.Amds, settings, new XrvProvider());
            var show = new ShowDevice((Types.ScriptVenueAssociations, "ScriptVenueAssociations.json"), (Types.REVenueSpecification, "REVenueSpecification.json"),
                (Types.VEVenueSpecification, "VEVenueSpecification.json"), (Types.ActionTable, "ActionTable.json"), (Types.ParticipantData, "ParticipantData.json"), (Types.Script, "Script.json"));
            var real = new RealVenue(Path.Combine(work, "dmx.txt"));
            var prompts = new List<string>();
            var devices = new DeviceRegistry().RegisterDevice("Show", show).RegisterDevice("RealVenue", real).RegisterDevice("VirtualVenue", new QuietVenue(EndAimProcessor.VDI))
                .RegisterPresent("operator", shown => { if (shown.TryGetValue("Prompt", out var p)) lock (prompts) prompts.Add(p); return Task.CompletedTask; });
            var told = new List<string>();
            var interpreter = new WorkflowInterpreter(api.Async(), devices, line => { lock (told) told.Add(line); });
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => interpreter.RunAsync(new WorkflowReader().Read(File.ReadAllText(Workflow)), stop.Token));
            string Why() { lock (told) return "\nworkflow: " + string.Join(" | ", told); }
            bool Told() { lock (prompts) return prompts.Any(p => p.Contains("not running as designed")); }

            await show.Given.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await Until(() => show.ParticipantData.Count > 0), "the Participant Data did not come back." + Why());
            Assert.False(Told(), "the operator was told of a degradation before there was one." + Why());

            real.Play(new JsonObject { ["Header"] = EndAimProcessor.RDI, ["REDataInID"] = "broken" }.ToJsonString());      // not a valid RE Data In
            Assert.True(await Until(Told), "the operator was not told." + Why());
            string prompt; lock (prompts) prompt = prompts.Single(p => p.Contains("not running as designed"));
            Assert.Contains("1XRV-END-V1.0-I01 DEGRADED", prompt);
            Assert.Contains("The show goes on.", prompt);

            // the show goes on: C1, Anna on the real stage, brings both lamps up
            real.Play(XrvLtpTests.REDataIn("rdi-1", new JsonObject { ["3DModelObjects"] = new JsonArray(new JsonObject { ["3DModelObjectTime"] = XrvTime.Simple("a-t", 0), ["3DModelObject"] = new JsonObject { ["Header"] = "OSD-3DO-V1.5", ["MInstanceID"] = "MI1", ["3DModelObjectID"] = "Anna", ["3DModelObjectSpaceTime"] = XrvTime.Simple("a-st", 0) } }) }));
            Assert.True(await Until(() => real.Level("1/1") == 255 && real.Level("1/2") == 255), "C1: the lamps did not come up after the degradation." + Why());
            await Task.Delay(1_500);
            lock (prompts) Assert.Single(prompts, p => p.Contains("not running as designed"));          // once: END stays DEGRADED, it has not left ALIVE again

            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task TheMiniShowRunsThroughTheAppAndTheOutputUnitsActAsTheScriptSays()
    {
        var work = Path.Combine(Path.GetTempPath(), "xrv-app-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        var dmx = Path.Combine(work, "dmx.txt");
        try
        {
            using var api = new ControllerApi(Repository.Amds, settings, new XrvProvider());
            var show = new ShowDevice((Types.ScriptVenueAssociations, "ScriptVenueAssociations.json"), (Types.REVenueSpecification, "REVenueSpecification.json"),
                (Types.VEVenueSpecification, "VEVenueSpecification.json"), (Types.ActionTable, "ActionTable.json"), (Types.ParticipantData, "ParticipantData.json"), (Types.Script, "Script.json"));
            // THE M-INSTANCE: the stage, public. The show is a collection of Processes: here the Process that animates the virtual venue (VE-Venue), the guest's User and the Bot, with its Persona.
            var m = new MInstance(Repository.Schemas);
            m.AddLocation("Stage", owner: "VE-Venue", isPublic: true, size: 10);
            var venueToken = m.AddService("VE-Venue");
            var botToken = m.AddService("BotSvc", personaId: "Bot");
            await using var mmm = MmmServer.Build(m, "http://127.0.0.1:0");
            await mmm.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(mmm.Urls.First()) };
            var real = new RealVenue(dmx);
            var virt = new MmmVirtualVenue(new MmmClient(http, "VE-Venue", venueToken), "Stage", (JsonObject)JsonNode.Parse(XrvShow.Text("VEVenueSpecification.json"))!, TimeSpan.FromMilliseconds(100));
            double? Intensity(string id) => m.Get(id)?.LightSource?["Intensity"]?.GetValue<double>();
            var devices = new DeviceRegistry().RegisterDevice("Show", show).RegisterDevice("RealVenue", real).RegisterDevice("VirtualVenue", virt);
            var told = new List<string>();
            var interpreter = new WorkflowInterpreter(api.Async(), devices, line => { lock (told) told.Add(line); });
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => interpreter.RunAsync(new WorkflowReader().Read(File.ReadAllText(Workflow)), stop.Token));
            string Why() { lock (told) return "\nworkflow: " + string.Join(" | ", told) + "\nstage: " + string.Join("; ", virt.Unperformed) + "\nAIMs: " + string.Join("; ", api.Status(interpreter.ModuleId).Aims.Select(a => a.Aim + " " + a.Status + " " + a.Reason + " " + string.Join("/", a.Reports))); }

            await show.Given.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await Until(() => show.ParticipantData.Count > 0), "the Participant Data did not come back." + Why());     // the documents have reached the Module
            await Task.Delay(300);

            // C1: Anna on the real stage -> both lamps up
            real.Play(XrvLtpTests.REDataIn("rdi-1", new JsonObject { ["3DModelObjects"] = new JsonArray(new JsonObject { ["3DModelObjectTime"] = XrvTime.Simple("a-t", 0), ["3DModelObject"] = new JsonObject { ["Header"] = "OSD-3DO-V1.5", ["MInstanceID"] = "MI1", ["3DModelObjectID"] = "Anna", ["3DModelObjectSpaceTime"] = XrvTime.Simple("a-st", 0) } }) }));
            Assert.True(await Until(() => real.Level("1/1") == 255 && real.Level("1/2") == 255), "C1: the lamps did not come up." + Why());

            Assert.True(await Until(() => m.Get("VLamp1")?.Location == "Stage"), "the virtual lamp was not placed on the stage." + Why());

            // C2: Guest1 walks onto the virtual stage (its User registers and MM-Adds its Persona) -> the virtual lamp on
            var human = new MmmClient(http, "Guest1-human");
            var reg = await human.PerformAsync("Register", new Complements { With = [Profile("Guest1")] });
            Assert.Equal(201, reg.Http);
            var guest = new MmmClient(http, "Guest1-user", reg.Tokens["Guest1-user"]);
            Assert.Equal(200, (await guest.PerformAsync("MM-Add", new Complements { Nil = "Guest1", At = "Stage" })).Http);
            Assert.True(await Until(() => Intensity("VLamp1") == 1.0), "C2: the virtual lamp did not come on." + Why());

            // C3: P1 sits in S1 and the Bot appears -> everything off
            real.Play(XrvLtpTests.REDataIn("rdi-2", new JsonObject { ["ControlObjects"] = new JsonArray(new JsonObject { ["ControlObject"] = new JsonObject
            {
                ["Header"] = "XRV-CTO-V1.0", ["ControlObjectID"] = "seat", ["UEnvironmentID"] = XrvShow.Environment, ["ControlObjectTime"] = XrvTime.Simple("s-t", 0), ["ControllerID"] = "P1", ["ControlData"] = "S1",
                ["ControlQualifier"] = new JsonObject { ["Header"] = "TFA-CXQ-V1.5", ["ControlQualifierID"] = "seat-q", ["SubTypes"] = new JsonObject(), ["Formats"] = "OSC" }
            } }) }));
            Assert.Equal(200, (await new MmmClient(http, "BotSvc", botToken).PerformAsync("MM-Add", new Complements { Nil = "Bot", At = "Stage" })).Http);
            Assert.True(await Until(() => real.Level("1/1") == 0 && real.Level("1/2") == 0 && Intensity("VLamp1") == 0.0), "C3: the lamps did not go off." + Why());
            Assert.Empty(virt.Unperformed);
            Assert.Contains(m.ActivityData, a => a.Source == "VE-Venue" && a.Action == "Property Change" && a.Http == 200);         // the lamp was changed by a Process Action of the show

            Assert.Equal(["DMX 1/1 255", "DMX 1/2 255", "DMX 1/1 0", "DMX 1/2 0"], File.ReadAllLines(dmx));                       // what the DMX writer was told, in order
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    // THE LAMPLIGHTER (Test/Data/XRV/Lamplighter/Script.json): two Cues, machine-driven, no operator. C1 (Mixed): Anna on the real stage and Guest1 on the virtual stage - the real
    // lamps up, the virtual lamp at 30%. C2 (Virtual): Bot, the virtual double of Anna, comes onto the virtual stage - the real lamps off, the virtual lamp full; the show ends.
    // Nobody drives C2: the Bot is a Process of the M-Instance that animates its own Persona - it watches the stage with MM-Capture and, a second after it sees the virtual lamp
    // lit, MM-Adds its Persona to the stage.
    [Fact]
    public async Task TheLamplighterRunsByItselfThroughTheAppWithTheVirtualVenueOnMmmTec()
    {
        var work = Path.Combine(Path.GetTempPath(), "xrv-lamp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        var dmx = Path.Combine(work, "dmx.txt");
        try
        {
            using var api = new ControllerApi(Repository.Amds, settings, new XrvProvider());
            var show = new ShowDevice((Types.ScriptVenueAssociations, "ScriptVenueAssociations.json"), (Types.REVenueSpecification, "REVenueSpecification.json"),
                (Types.VEVenueSpecification, "VEVenueSpecification.json"), (Types.ActionTable, "ActionTable.json"), (Types.ParticipantData, "ParticipantData.json"),
                (Types.Script, Path.Combine("..", "Lamplighter", "Script.json")));

            // THE M-INSTANCE: the virtual stage, public. The Processes of the show in it: VE-Venue (animates the Items of the virtual venue), Guest1's User, the Bot.
            var m = new MInstance(Repository.Schemas);
            m.AddLocation("Stage", owner: "VE-Venue", isPublic: true, size: 10);
            var venueToken = m.AddService("VE-Venue");
            var botToken = m.AddService("BotSvc", personaId: "Bot");
            await using var mmm = MmmServer.Build(m, "http://127.0.0.1:0");
            await mmm.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(mmm.Urls.First()) };
            var real = new RealVenue(dmx);
            var virt = new MmmVirtualVenue(new MmmClient(http, "VE-Venue", venueToken), "Stage", (JsonObject)JsonNode.Parse(XrvShow.Text("VEVenueSpecification.json"))!, TimeSpan.FromMilliseconds(100));
            double? Intensity(string id) => m.Get(id)?.LightSource?["Intensity"]?.GetValue<double>();
            var intensities = new List<double>();

            var devices = new DeviceRegistry().RegisterDevice("Show", show).RegisterDevice("RealVenue", real).RegisterDevice("VirtualVenue", virt);
            var told = new List<string>();
            var interpreter = new WorkflowInterpreter(api.Async(), devices, line => { lock (told) told.Add(line); });
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => interpreter.RunAsync(new WorkflowReader().Read(File.ReadAllText(Workflow)), stop.Token));
            var bot = Task.Run(() => BotAsync(new MmmClient(http, "BotSvc", botToken), stop.Token));
            var watch = Task.Run(async () => { while (!stop.IsCancellationRequested) { if (Intensity("VLamp1") is { } v) lock (intensities) if (intensities.Count == 0 || intensities[^1] != v) intensities.Add(v); await Task.Delay(20); } });
            string Why() { lock (told) return "\nworkflow: " + string.Join(" | ", told) + "\nstage: " + string.Join("; ", virt.Unperformed) + "\nAIMs: " + string.Join("; ", api.Status(interpreter.ModuleId).Aims.Select(a => a.Aim + " " + a.Status + " " + a.Reason + " " + string.Join("/", a.Reports))); }

            await show.Given.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await Until(() => show.ParticipantData.Count > 0), "the Participant Data did not come back." + Why());
            Assert.True(await Until(() => m.Get("VLamp1")?.Location == "Stage"), "the virtual lamp was not placed on the stage." + Why());
            await Task.Delay(300);

            // The performance: Anna walks onto the real stage (what the real venue records), Guest1 onto the virtual stage (its User). The rest happens by itself.
            real.Play(XrvLtpTests.REDataIn("rdi-1", new JsonObject { ["3DModelObjects"] = new JsonArray(new JsonObject { ["3DModelObjectTime"] = XrvTime.Simple("a-t", 0), ["3DModelObject"] = new JsonObject { ["Header"] = "OSD-3DO-V1.5", ["MInstanceID"] = "MI1", ["3DModelObjectID"] = "Anna", ["3DModelObjectSpaceTime"] = XrvTime.Simple("a-st", 0) } }) }));
            var reg = await new MmmClient(http, "Guest1-human").PerformAsync("Register", new Complements { With = [Profile("Guest1")] });
            Assert.Equal(201, reg.Http);
            Assert.Equal(200, (await new MmmClient(http, "Guest1-user", reg.Tokens["Guest1-user"]).PerformAsync("MM-Add", new Complements { Nil = "Guest1", At = "Stage" })).Http);

            Assert.True(await Until(() => real.Level("1/1") == 255 && real.Level("1/2") == 255 && Intensity("VLamp1") == 0.3), "C1: the lamps did not come up." + Why());
            Assert.True(await Until(() => m.Get("Bot")?.Location == "Stage"), "the Bot did not come onto the stage." + Why());
            Assert.True(await Until(() => real.Level("1/1") == 0 && real.Level("1/2") == 0 && Intensity("VLamp1") == 1.0), "C2: the lamps did not change." + Why());
            await Task.Delay(500);                                                                                                     // the show is over: nothing more happens

            Assert.Equal(["DMX 1/1 255", "DMX 1/2 255", "DMX 1/1 0", "DMX 1/2 0"], File.ReadAllLines(dmx));
            lock (intensities) Assert.Equal([0.3, 1.0], intensities);
            Assert.Empty(virt.Unperformed);
            stop.Cancel();
            try { await Task.WhenAll(run, bot, watch).WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { }
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    // THE BOT: a Process that animates its own Persona. It looks at the stage (MM-Capture) and, a second after it sees a light on it, walks on (MM-Add) - once.
    private static async Task BotAsync(MmmClient bot, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var rs = await bot.PerformAsync("MM-Capture", new Complements { At = "Stage" });
            var things = (rs.Response.Complements?.With?.FirstOrDefault() as JsonObject)?["ItemValueOrRef"]?[0]?["Things"]?.AsArray().OfType<JsonObject>() ?? [];
            if (things.Any(t => t["Intensity"]?.GetValue<double>() > 0))
            {
                await Task.Delay(1000, cancel);
                await bot.PerformAsync("MM-Add", new Complements { Nil = "Bot", At = "Stage" });
                return;
            }
            await Task.Delay(100, cancel);
        }
    }

    // The Personal Profile a guest registers with (MMM-PPR-V2.2): its User and its Persona, which has the guest's name.
    private static JsonObject Profile(string name) => new()
    {
        ["Header"] = "MMM-PPR-V2.2", ["MInstanceID"] = "MI1", ["humanID"] = $"{name}-human", ["PersonalProfileID"] = $"Profile-{name}-human",
        ["PersonalProfile"] = new JsonObject
        {
            ["Data"] = new JsonArray(new JsonObject { ["FirstName"] = name }),
            ["Processes"] = new JsonArray(new JsonObject
            {
                ["ProcessIDOrProcess"] = new JsonArray(new JsonObject { ["ProcessID"] = $"{name}-user" }),
                ["PersonaIDOrPersona"] = new JsonArray(new JsonObject { ["PersonaID"] = name })
            })
        }
    };
}
