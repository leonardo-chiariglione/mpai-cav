using System.Text.Json.Nodes;

using Mpai.Cav.Ess;
using Mpai.Cav.Map;
using Mpai.Cav.Recordings;
using Mpai.Core;

namespace Mpai.Aif.Tests;

// THE DEMONSTRATION RECORDINGS (MPAI-72): the two drives of CaoTests - the CAV as one
// Module from the spoken Destination to arrival, and the two CAVs - run as the tests
// run them, each step written to the folder MPAI_CAV_DEMO names: the camera frames,
// what the detector finds in them, where every vehicle is on the map, what the CAV
// says and its speech, what the passenger says. A video is made from them elsewhere.
// Not judged, not in the matrix: without MPAI_CAV_DEMO they are skipped.
[Trait("Group", "Demo")]
[Collection(Timing.Name)]
public class CaoDemo
{
    private static string? Folder => Environment.GetEnvironmentVariable("MPAI_CAV_DEMO");

    // One drive's record: steps.jsonl, a line for each step; cam/, the frames; audio/.
    private sealed class Recorder : IDisposable
    {
        private readonly string dir;
        private readonly StreamWriter steps;
        private readonly BasicVisualSceneDescription ess = new("demo", new Dictionary<string, string>(), Repository.Root);
        private readonly Dictionary<string, int> spoken = [];
        private int audio;

        public Recorder(string scene, Simulation sim)
        {
            dir = Path.Combine(Folder!, scene);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(Path.Combine(dir, "cam"));
            Directory.CreateDirectory(Path.Combine(dir, "audio"));
            File.WriteAllText(Path.Combine(dir, "map.json"), new JsonObject
            {
                ["WayPoints"] = new JsonArray(sim.Map.WayPoints.Select(w => (JsonNode)new JsonObject { ["Id"] = w.Id, ["East"] = w.East, ["North"] = w.North, ["Name"] = w.Name }).ToArray()),
                ["Segments"] = new JsonArray(sim.Map.Segments.Select(s => (JsonNode)new JsonObject { ["Id"] = s.Id, ["From"] = s.From, ["To"] = s.To, ["SpeedLimit"] = s.SpeedLimit }).ToArray()),
                ["Route"] = new JsonArray(Enumerable.Range(0, (int)sim.Path.Length + 1).Select(s => { var p = sim.Path.At(s); return (JsonNode)new JsonArray(Math.Round(p.East, 2), Math.Round(p.North, 2)); }).ToArray())
            }.ToJsonString());
            steps = new StreamWriter(Path.Combine(dir, "steps.jsonl"));
        }

        // A vehicle of the simulation on the map: its centre, on the Route, in its lane.
        public static JsonObject Where(Simulation sim, string id, double s, int lane)
        {
            var p = sim.Path.At(s);
            var left = lane * RoadMap.LaneWidth;
            return new JsonObject { ["Id"] = id, ["East"] = Math.Round(p.East - left * Math.Sin(p.Heading), 2), ["North"] = Math.Round(p.North + left * Math.Cos(p.Heading), 2), ["Heading"] = Math.Round(p.Heading * 180 / Math.PI, 2) };
        }

        public static JsonObject Ego(Simulation sim) => new()
        {
            ["S"] = Math.Round(sim.EgoS, 2), ["Speed"] = Math.Round(sim.EgoSpeed, 2),
            ["East"] = Math.Round(sim.Mechanics!.East, 2), ["North"] = Math.Round(sim.Mechanics.North, 2), ["Heading"] = Math.Round(sim.Mechanics.Heading * 180 / Math.PI, 2)
        };

        // The frame saved; what the detector finds in it.
        public JsonArray Camera(string name, int step, Simulation.Sensed sensed)
        {
            var frame = JsonNode.Parse(sensed.Messages.First(m => m.DataType == "OSD-BVO-V1.5").Json)!;
            var png = Convert.FromBase64String((string)frame["BasicVisualObjectData"]![0]!["Data"]!);
            File.WriteAllBytes(Path.Combine(dir, "cam", $"{name}{step:D4}.png"), png);
            return new JsonArray(ess.Describe(png).Select(o => (JsonNode)new JsonObject
            {
                ["Class"] = o.Class, ["Score"] = Math.Round(o.Score, 2), ["Ahead"] = Math.Round(o.Ahead, 1),
                ["Box"] = new JsonArray(Math.Round(o.Box.X1), Math.Round(o.Box.Y1), Math.Round(o.Box.X2), Math.Round(o.Box.Y2))
            }).ToArray());
        }

        // What the CAV said since the last step: its text; and, as it comes - a step or
        // more after the text - its speech, saved, numbered as the text it speaks.
        public JsonArray Said(string name, CaoInTheLoop cav)
        {
            var said = new JsonArray();
            spoken.TryGetValue(name + "#text", out var from);
            for (var i = from; i < cav.Said.Count; i++) said.Add(new JsonObject { ["Text"] = cav.Said[i], ["Index"] = i });
            spoken[name + "#text"] = cav.Said.Count;
            spoken.TryGetValue(name + "#speech", out var fromSpeech);
            for (var i = fromSpeech; i < cav.Spoken.Count; i++)
            {
                var file = $"cav{name}{i:D2}.wav";
                File.WriteAllBytes(Path.Combine(dir, "audio", file), cav.Spoken[i]);
                said.Add(new JsonObject { ["Speech"] = i, ["Audio"] = file });
            }
            spoken[name + "#speech"] = cav.Spoken.Count;
            return said;
        }

        // What the passenger says: the Basic Audio Object, its samples saved.
        public JsonObject Passenger(string text, string json)
        {
            var node = JsonNode.Parse(json)!;
            var o = MpaiJson.FromJson<BasicAudioObject>(json)!;
            var rate = (int?)node["AudioQualifier"]?["Formats"]?["ContentFormat"]?["RawData"]?["SampleSpace"]?["SamplingFrequency"] ?? 22050;
            var file = $"passenger{++audio:D2}.pcm";
            File.WriteAllBytes(Path.Combine(dir, "audio", file), o.Data);
            return new JsonObject { ["Text"] = text, ["Audio"] = file, ["Rate"] = rate };
        }

        public void Write(JsonObject step) => steps.WriteLine(step.ToJsonString());

        public void Dispose() { steps.Dispose(); ess.Dispose(); }
    }

    // Scene 1: the drive of Step4OneModule.
    [SkippableFact]
    public void Scene1OneModule()
    {
        Skip.If(Folder is null, "MPAI_CAV_DEMO names no folder.");
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var sim = new Simulation(map, map.FastestRoute("W00", "W21")!, [], seed: 7);
        sim.Mechanical(seed: 9);
        using var cav = new CaoInTheLoop(sim);
        using var rec = new Recorder("scene1", sim);
        const string voice = "en_GB-alan-medium";
        bool agreed = false, arrived = false;
        IReadOnlyList<(string DataType, string Json)> responses = [];
        for (var steps = 0; steps < 1500 && !sim.Collided && !arrived && (agreed || steps < 300); steps++)
        {
            var step = new JsonObject { ["Step"] = steps, ["T"] = Math.Round(sim.Time, 1) };
            var passenger = new JsonArray();
            if (steps == 5) { var line = "Please take me to the central station."; var json = HciSpeechTests.Passenger(line, voice); cav.Hears(json); passenger.Add(rec.Passenger(line, json)); }
            var sensed = sim.Sense();
            step["Detections"] = rec.Camera("", steps, sensed);
            var commands = cav.Step(sensed, responses);
            var said = rec.Said("", cav);
            foreach (var s in said)
            {
                var text = (string?)s!["Text"] ?? "";
                if (!agreed && text.Contains("Shall we")) { var line = "Yes, let's go."; var json = HciSpeechTests.Passenger(line, voice); cav.Hears(json); passenger.Add(rec.Passenger(line, json)); agreed = true; }
                if (text.Contains("arrived")) arrived = true;
            }
            step["Ego"] = Recorder.Ego(sim);
            step["Passenger"] = passenger;
            step["Said"] = said;
            step["Heard"] = new JsonArray(cav.Recognised.Select(x => (JsonNode)x).ToArray());
            rec.Write(step);
            sim.Actuate(commands);
            responses = sim.Advance();
        }
    }

    // Scene 2: the two CAVs of Step5TwoCaos, B's map and both cameras.
    [SkippableFact]
    public async Task Scene2TwoCaos()
    {
        Skip.If(Folder is null, "MPAI_CAV_DEMO names no folder.");
        const double StoppedAt = RemoteStage1Tests.StoppedAt, AStart = RemoteStage1Tests.AStart;
        var map = RoadMap.Grid(3).Named(CavDialogueTests.Places);
        var route = map.FastestRoute("W00", "W21")!;
        var simA = new Simulation(map, route, [new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)], seed: 7);
        simA.StartAt(AStart);
        simA.Mechanical(seed: 9);
        var simB = new Simulation(map, route,
            [new ScenarioVehicle("A", 0, AStart, [(0, 0.0)], CameraRenderer.Silver), new ScenarioVehicle("stopped", 0, StoppedAt, [(0, 0.0)], CameraRenderer.DarkRed)], seed: 8);
        simB.Mechanical(seed: 10);
        using var a = new CaoInTheLoop(simA, "CAV-A");
        using var b = new CaoInTheLoop(simB, "CAV-B");
        using var rec = new Recorder("scene2", simB);

        var turin = new AIF.Trust.TrustAuthority("Turin");
        var port = 45000 + Environment.ProcessId % 1000;
        var discoveries = new List<AIF.Channels.UdpDiscovery>();
        AIF.Channels.ExternalHub? hubB = null;
        foreach (var (id, cav) in new[] { ("CAV-A", a), ("CAV-B", b) })
        {
            var trust = new AIF.Controller.CityTrust(turin, turin.Admit(id));
            var discovery = new AIF.Channels.UdpDiscovery(port);
            discoveries.Add(discovery);
            var hub = cav.Api.StartExternal(CaoInTheLoop.Cao, new AIF.Channels.ExternalHub.Options
            {
                ControllerId = id, ModuleType = "CAV-CAO-V2.0", Discovery = discovery, Admission = trust.Admission,
                InRange = _ => Math.Abs(simA.EgoS - simB.EgoS) <= 300
            }, trust.Sign, trust.Verify);
            if (id == "CAV-B") hubB = hub;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (hubB!.Linked.Count == 0 && clock.ElapsedMilliseconds < 5000) await Task.Delay(20);

        const string voice = "en_GB-alan-medium";
        var agreed = new HashSet<string>();
        IReadOnlyList<(string DataType, string Json)> respA = [], respB = [];
        for (var steps = 0; steps < 900 && !simA.Collided && !simB.Collided; steps++)
        {
            var step = new JsonObject { ["Step"] = steps, ["T"] = Math.Round(simB.Time, 1) };
            var passenger = new JsonArray();
            if (steps == 5)
                foreach (var (name, cav) in new[] { ("A", a), ("B", b) })
                {
                    var line = "Please take me to the central station."; var json = HciSpeechTests.Passenger(line, voice); cav.Hears(json);
                    var p = rec.Passenger(line, json); p["CAV"] = name; passenger.Add(p);
                }
            simB.Place("A", simA.EgoS, simA.EgoSpeed);
            var sensedA = simA.Sense();
            var sensedB = simB.Sense();
            step["DetectionsA"] = rec.Camera("A", steps, sensedA);
            step["DetectionsB"] = rec.Camera("B", steps, sensedB);
            var cmdA = a.Step(sensedA, respA);
            var cmdB = b.Step(sensedB, respB);
            var saidAll = new JsonObject();
            foreach (var (name, cav) in new[] { ("A", a), ("B", b) })
            {
                var said = rec.Said(name, cav);
                foreach (var s in said)
                    if (!agreed.Contains(name) && ((string?)s!["Text"] ?? "").Contains("Shall we"))
                    {
                        var line = "Yes, let's go."; var json = HciSpeechTests.Passenger(line, voice); cav.Hears(json);
                        var p = rec.Passenger(line, json); p["CAV"] = name; passenger.Add(p); agreed.Add(name);
                    }
                saidAll[name] = said;
            }
            step["EgoA"] = Recorder.Ego(simA);
            step["EgoB"] = Recorder.Ego(simB);
            var stopped = simB.Around().First(x => x.Id == "stopped");
            step["Stopped"] = Recorder.Where(simB, "stopped", simB.EgoS + stopped.Ahead + Simulation.VehicleLength, 0);
            // B's map: the objects of its Full Environment Descriptors, relative to B.
            var fed = new JsonArray();
            foreach (var o in b.Data?["FED"]?["FullEnvironmentObjects"]?.AsArray() ?? [])
                if (EssJson.Vector(o?["BasicEnvironmentObject"]?["SpatialAttitude"]?["Position"]?["CartPosition"]) is { } p)
                    fed.Add(new JsonObject { ["Source"] = (string?)o!["Source"], ["X"] = Math.Round(p.X, 2), ["Y"] = Math.Round(p.Y, 2), ["Lane"] = (int?)o["Placement"]?["Lane"] });
            step["FedB"] = fed;
            step["Passenger"] = passenger;
            step["Said"] = saidAll;
            step["ReceivedB"] = hubB.Received;
            rec.Write(step);
            simA.Actuate(cmdA);
            simB.Actuate(cmdB);
            respA = simA.Advance();
            respB = simB.Advance();
            if (agreed.Count == 2 && simA.Time > 15 && simA.EgoSpeed < 0.05 && simB.EgoSpeed < 0.05) break;
        }
        foreach (var d in discoveries) await d.DisposeAsync();
    }
}
