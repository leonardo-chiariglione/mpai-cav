using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Map;

using AIF.Controller;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE ESS IN THE LOOP: the Module 1CAV-ESS-V2.0-I01 under the Controller, fed a
// simulation step by step - each step's sensor data written to its boundary, and
// the step over when the Basic Environment Descriptors of its frame come out. The
// simulation waits for the CAV: nothing is dropped for want of time (M3233).
public sealed class EssInTheLoop : IDisposable
{
    public const string Ess = "1CAV-ESS-V2.0-I01";
    private readonly Mpai.Aif.Api.ControllerApi api;

    public EssInTheLoop()
    {
        api = new Mpai.Aif.Api.ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), new EssProvider(Repository.Root));
        var started = api.StartFlow(Ess);
        if (started != AifError.OK) throw new InvalidOperationException($"{Ess} did not start: {started}");
    }

    public ControllerApiStatus Status => new(api.Status(Ess));

    public (JsonNode? Descriptors, List<JsonNode> Alerts) Step(Simulation.Sensed sensed, int timeoutMs = 60_000)
    {
        foreach (var (dataType, json) in sensed.Messages)
        {
            var written = api.InputWrite(Ess, dataType, 1, json, timeoutMs);
            if (written != AifError.OK) throw new InvalidOperationException($"{dataType} not written: {written}");
        }
        JsonNode? descriptors = null;
        while (descriptors is null)
        {
            var read = api.OutputRead(Ess, "CAV-BED-V2.0", 1, timeoutMs);
            if (read.Error != AifError.OK) throw new InvalidOperationException($"no Basic Environment Descriptors for {sensed.FrameMs}: {read.Error}");
            var json = JsonNode.Parse(read.Json!)!;
            if ((long)(double)json["BasicEnvironmentDescriptorsTime"]!["SimpleTimeData"]![0]!["StartTime"]! == sensed.FrameMs) descriptors = json;
        }
        var alerts = new List<JsonNode>();
        while (api.OutputRead(Ess, "CAV-ALT-V1.1", 1, 0) is { Error: AifError.OK, Json: { } a }) alerts.Add(JsonNode.Parse(a)!);
        return (descriptors, alerts);
    }

    public void Dispose()
    {
        api.StopFlow(Ess);
        api.Dispose();
    }
}

// THE CAV IN THE LOOP: the ESS and the AMS, two Modules under the Controller,
// driven by one User Agent (M3233 3.10): each step the sensor data to the ESS, its
// Basic Environment Descriptors and Alerts carried to the AMS, and the AMS-MAS
// Message of that frame read back - the simulation waits for it.
public sealed class CavInTheLoop : IDisposable
{
    public const string Ams = "1CAV-AMS-V1.1-I01";
    private readonly EssInTheLoop ess = new();
    private readonly Mpai.Aif.Api.ControllerApi api;
    private readonly string location = Path.Combine(Path.GetTempPath(), "mpai-p8s8-" + Guid.NewGuid().ToString("N"));

    public CavInTheLoop(Simulation sim, string destination)
    {
        api = new Mpai.Aif.Api.ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), new AmsProvider());
        var started = api.StartFlow(Ams);
        if (started != AifError.OK) throw new InvalidOperationException($"{Ams} did not start: {started}");
        api.SharedStorageInit(Ams, location);
        api.InputWrite(Ams, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0), 5000);
        api.InputWrite(Ams, AmsTypes.Hci, 1, AmsStage1Tests.Destination(destination), 5000);
    }

    public (List<JsonNode> Alerts, JsonNode? Command, double LatencyMs) Step(Simulation.Sensed sensed, int timeoutMs = 5000)
    {
        var (alerts, command, latency, _) = StepWithBed(sensed, timeoutMs);
        return (alerts, command, latency);
    }

    public (List<JsonNode> Alerts, JsonNode? Command, double LatencyMs, JsonNode? Bed) StepWithBed(Simulation.Sensed sensed, int timeoutMs = 5000)
    {
        var (bed, alerts) = ess.Step(sensed);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var alert in alerts) api.InputWrite(Ams, AmsTypes.Alert, 1, alert.ToJsonString(), timeoutMs);
        api.InputWrite(Ams, AmsTypes.Bed, 1, bed!.ToJsonString(), timeoutMs);
        JsonNode? command = null;
        while (command is null && api.OutputRead(Ams, AmsTypes.Message, 1, timeoutMs) is { Error: AifError.OK, Json: { } json })
        {
            var message = JsonNode.Parse(json)!;
            if (AmsTypes.Ms(message["AMSMASMessageTime"]) == sensed.FrameMs) command = message;   // an older one is passed over
        }
        var latency = clock.Elapsed.TotalMilliseconds;
        while (api.OutputRead(Ams, AmsTypes.Data, 1, 0) is { Error: AifError.OK }) { }           // the AMS Data, not used here
        return (alerts, command, latency, bed);
    }

    public void Dispose()
    {
        api.StopFlow(Ams);
        api.Dispose();
        ess.Dispose();
        try { Directory.Delete(location, recursive: true); } catch { }
    }
}

public sealed record ControllerApiStatus(Mpai.Aif.Api.ControllerApi.ModuleStatus Module)
{
    public override string ToString() => string.Join("; ", Module.Aims.OrderBy(a => a.Aim).Select(a => $"{a.Aim} {a.Status}"));
}

// PHASE 8 (M3233): the Autonomous Motion Subsystem, Stage 1, the loop closed by a
// stepped simulation. Run alone: the detector keeps the processor busy (Timing).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class AmsStage1Tests
{
    // The scenario of Step 1: a map from a seed; the Route of least time from one
    // corner to a way point past a junction; a vehicle ahead in the ego's lane,
    // faster, and a slower one in the left lane, which the ego overtakes.
    public static Simulation OpenScenario()
    {
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        return new Simulation(map, route,
        [
            new("ahead", 0, 40, [(0, 19.0)], CameraRenderer.Silver),
            new("left", 1, 70, [(0, 8.0)], CameraRenderer.DarkRed)
        ], seed: 7, egoSpeed: 10);
    }

    // Step 1: the map and the simulation, open - the ego keeping the speed limit,
    // whatever it meets - with the ESS in the loop for 20 s: every frame described;
    // what the ESS says against the truth; the same seed, the same world.
    [SkippableFact]
    public void Step1SimulationWithTheEss()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();

        // The same seed, the same world: two runs of the simulation alone, the same policy.
        string Run()
        {
            var sim = OpenScenario();
            var truths = new List<string>();
            for (var i = 0; i < 200; i++) { var sensed = sim.Sense(); truths.Add(sensed.Truth.ToJsonString()); sim.Advance(KeepTheLimit(sim)); }
            return string.Concat(truths);
        }
        result["the same seed, the same world"] = Run() == Run() ? "yes" : "no";

        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        result["the Route of the scenario"] = $"{string.Join(" ", route.Select(s => s.Id))}, {map.TimeOf(route):0.0} s at the limits";

        var s = OpenScenario();
        using var ess = new EssInTheLoop();
        int steps = 0, described = 0, aheadFrames = 0, aheadFound = 0, leftFrames = 0, leftFound = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? statusAtEnd = null;
        for (var i = 0; i < 200 && !s.Arrived; i++)
        {
            var sensed = s.Sense();
            var (bed, _) = ess.Step(sensed);
            steps++;
            if (bed is not null) described++;
            var objects = bed!["BasicEnvironmentObjects"]!.AsArray()
                .Select(o => o!["SpatialAttitude"]!["Position"]!["CartPosition"]!.AsArray()).Select(p => (X: (double)p[0]!, Y: (double)p[1]!)).ToList();
            foreach (var v in sensed.Truth["Vehicles"]!.AsArray())
            {
                var d = (double)v!["Distance"]!;
                if (d < 12 || d > 50) continue;
                var lane = (int)v["Lane"]!;
                var found = objects.Any(o => Math.Abs(o.X - d) < 0.25 * d + 2 && (lane == 0 ? Math.Abs(o.Y) < 1.75 : o.Y > 1.75 && o.Y < 5.25));
                if (lane == 0) { aheadFrames++; if (found) aheadFound++; } else { leftFrames++; if (found) leftFound++; }
            }
            s.Advance(KeepTheLimit(s));
        }
        statusAtEnd = ess.Status.ToString();

        result["steps, each frame described"] = steps == described ? $"{steps}, all described" : $"{steps}, {described} described";
        result["the vehicle ahead, 12 to 50 m, in the descriptors"] = aheadFrames == 0 ? "never in view" : aheadFound >= aheadFrames * 0.9 ? "90% of frames or more" : "fewer than 90% of frames";
        result["the vehicle in the left lane, 12 to 50 m, in the descriptors"] = leftFrames == 0 ? "never in view" : leftFound >= leftFrames * 0.9 ? "90% of frames or more" : "fewer than 90% of frames";
        result["the ESS's AIMs at the end"] = statusAtEnd;
        report["the vehicle ahead, 12 to 50 m, in the descriptors"] = $"{aheadFound} of {aheadFrames}";
        report["the vehicle in the left lane, 12 to 50 m, in the descriptors"] = $"{leftFound} of {leftFrames}";
        report["time a step takes"] = $"{clock.Elapsed.TotalMilliseconds / Math.Max(1, steps):0} ms";
        report["ego at the end"] = $"{s.EgoS:0} m along the Route of {s.Path.Length:0} m, at {s.EgoSpeed:0.0} m/s";

        foreach (var (k, v) in result) report.TryAdd(k, v);
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ams-stage1-simulation.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ams-stage1-simulation.json", result);
    }

    // Open: the speed limit of the segment, reached at 1.5 m/s2, whatever is ahead.
    public static double KeepTheLimit(Simulation s)
    {
        var limit = s.Path.At(s.EgoS).Segment.SpeedLimit;
        return Math.Clamp((limit - s.EgoSpeed) / 1.0, -2, 1.5);
    }

    // Step 3: Full Environment Description on 40 s of the open scenario, on perfect
    // perception (TruthBed): the CAV placed on the segment it is on, at the distance
    // along it it is; each vehicle in its lane; every output valid against the Full
    // Environment Descriptors V2.0.
    [Fact]
    public async Task Step3FullEnvironmentDescription()
    {
        var sim = OpenScenario();
        var inputs = new List<(TimeSpan, string, int, string)> { (TimeSpan.Zero, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0)) };
        var truths = new Dictionary<long, JsonObject>();
        for (var i = 0; i < 400; i++)
        {
            var sensed = sim.Sense();
            truths[sensed.FrameMs] = sensed.Truth;
            inputs.Add((TimeSpan.FromSeconds(sim.Time) + TimeSpan.FromTicks(1), AmsTypes.Bed, 1, TruthBed.Of(sim, sensed)));
            sim.Advance(KeepTheLimit(sim));
        }
        var ports = new DrivePorts(inputs);
        await new FullEnvironmentDescription(AmsProvider.Fed).RunAsync(ports, AimContext.None);

        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "FullEnvironmentDescriptors.json"))];
        var map = sim.Map;
        int given = 0, valid = 0, onSegment = 0, lanes = 0, placed = 0;
        var along = new List<double>();
        foreach (var (_, _, _, json) in ports.Written)
        {
            given++;
            using (var doc = JsonDocument.Parse(json)) lock (AIF.Metadata.PublishedSchemas.Lock) if (schema.Evaluate(doc.RootElement).IsValid) valid++;
            var fed = JsonNode.Parse(json)!;
            var truth = truths[AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"])];
            var segment = (string)truth["Ego"]!["Segment"]!;
            if ((string?)fed["EgoPlacement"]?["SegmentID"] == segment)
            {
                onSegment++;
                var seg = map.Segments.First(s => s.Id == segment);
                var start = map.Point(seg.From);
                var expected = Math.Sqrt(Math.Pow((double)truth["Ego"]!["East"]! - start.East, 2) + Math.Pow((double)truth["Ego"]!["North"]! - start.North, 2));
                along.Add(Math.Abs((double)fed["EgoPlacement"]!["Along"]! - expected));
            }
            foreach (var o in fed["FullEnvironmentObjects"]!.AsArray())
            {
                var id = (string)o!["BasicEnvironmentObject"]!["BasicEnvironmentObjectID"]!;
                var lane = (int)truth["Vehicles"]!.AsArray().First(v => (string)v!["Id"]! == id)!["Lane"]!;
                if (o["Placement"] is null) continue;
                placed++;
                if ((int?)o["Placement"]!["Lane"] == lane) lanes++;
            }
        }
        var sorted = along.Order().ToList();
        var result = new Dictionary<string, string>
        {
            ["Full Environment Descriptors given"] = $"{given}, {valid} valid against their schema",
            ["the CAV on the segment it is on"] = $"{onSegment} of {given}",
            ["the CAV's distance along its segment, error"] = sorted.Count == 0 ? "none" : $"median {sorted[sorted.Count / 2]:0.00} m, max {sorted[^1]:0.00} m",
            ["the vehicles in their lanes"] = $"{lanes} of {placed} placed",
            ["the segments driven"] = string.Join(" ", truths.Values.Select(t => (string)t["Ego"]!["Segment"]!).Distinct())
        };
        Expected.Match("ams-stage1-fed.json", result);
    }

    // What FED gives on perfect perception, for the first steps of a scenario.
    private static async Task<List<(TimeSpan At, string DataType, int PortNumber, string Json)>> Feds(Simulation sim, int steps)
    {
        var inputs = new List<(TimeSpan, string, int, string)> { (TimeSpan.Zero, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0)) };
        for (var i = 0; i < steps; i++)
        {
            var sensed = sim.Sense();
            inputs.Add((TimeSpan.FromSeconds(sim.Time) + TimeSpan.FromTicks(1), AmsTypes.Bed, 1, TruthBed.Of(sim, sensed)));
            sim.Advance(KeepTheLimit(sim));
        }
        var ports = new DrivePorts(inputs);
        await new FullEnvironmentDescription(AmsProvider.Fed).RunAsync(ports, AimContext.None);
        return ports.Written;
    }

    public static string Destination(string wayPoint, long ms = 0) => new JsonObject
    {
        ["Header"] = "CAV-AHM-V1.1", ["AMSHCIMessageID"] = "AHM-" + wayPoint, ["AMSHCIMessageTime"] = EssJson.SimpleTime("AHM-" + wayPoint + "-T", ms),
        ["HCIMessage"] = new JsonObject
        {
            ["RequestedRoutes"] = new JsonArray(new JsonObject
            {
                ["Route"] = new JsonObject
                {
                    ["Header"] = "CAV-RTE-V1.1", ["RouteID"] = "REQ-" + wayPoint, ["OfflineMapID"] = "",
                    ["RouteSegments"] = new JsonArray(new JsonObject { ["WayPoint1ID"] = "HERE", ["WayPoint2ID"] = wayPoint })
                }
            })
        }
    }.ToJsonString();

    // The least time over every simple path of the map - by enumeration, not by the A* the
    // AIM uses - from a way point to another.
    private static double BruteForce(Mpai.Cav.Map.RoadMap map, string from, string to)
    {
        var best = double.MaxValue;
        void Walk(string at, HashSet<string> seen, double time)
        {
            if (time >= best) return;
            if (at == to) { best = time; return; }
            foreach (var s in map.Segments.Where(s => s.From == at && !seen.Contains(s.To)))
            {
                seen.Add(s.To);
                Walk(s.To, seen, time + map.Length(s) / s.SpeedLimit);
                seen.Remove(s.To);
            }
        }
        Walk(from, [from], 0);
        return best;
    }

    // Step 4: Route and Path Selection Planning on what FED gives. For every way point
    // of the map as the Destination: the Route RSP gives against the least time over
    // every simple path; the Path PSP gives on the Route's centreline, as long as it.
    [Fact]
    public async Task Step4RouteAndPath()
    {
        var map = OpenScenario().Map;
        var feds = await Feds(OpenScenario(), 20);
        var result = new Dictionary<string, string>();
        int optimal = 0, destinations = 0, pathsOnRoute = 0;
        var lengthErrors = new List<double>();
        foreach (var destination in map.WayPoints.Select(w => w.Id).Where(w => w != "W10"))
        {
            destinations++;
            var inputs = new List<(TimeSpan, string, int, string)>
            {
                (TimeSpan.Zero, AmsTypes.Map, 1, map.ToOfflineMapObject(0)),
                (TimeSpan.FromTicks(1), AmsTypes.Hci, 1, Destination(destination))
            };
            inputs.AddRange(feds.Select(f => (f.At + TimeSpan.FromTicks(2), f.DataType, f.PortNumber, f.Json)));
            var rsp = new DrivePorts(inputs);
            await new RouteSelectionPlanning(AmsProvider.Rsp).RunAsync(rsp, AimContext.None);
            var routes = rsp.Written.Where(w => w.DataType == AmsTypes.Route).ToList();
            if (routes.Count != 1) { result[$"to {destination}"] = $"{routes.Count} Routes given"; continue; }
            var segments = JsonNode.Parse(routes[0].Json)!["RouteSegments"]!.AsArray()
                .Select(s => map.Segments.First(g => g.From == (string)s!["WayPoint1ID"]! && g.To == (string)s["WayPoint2ID"]!)).ToList();
            // The CAV starts on SW00W10: the rest of the Route from W10.
            var rest = segments.Skip(1).ToList();
            if (Math.Abs(map.TimeOf(rest) - BruteForce(map, "W10", destination)) < 1e-6) optimal++;

            var psp = new DrivePorts([(TimeSpan.Zero, AmsTypes.Map, 1, map.ToOfflineMapObject(0)),
                                      (TimeSpan.FromTicks(1), AmsTypes.Interaction, 1, rsp.Written.First(w => w.DataType == AmsTypes.Interaction).Json)]);
            await new PathSelectionPlanning(AmsProvider.Psp).RunAsync(psp, AimContext.None);
            var path = JsonNode.Parse(psp.Written.First(w => w.DataType == AmsTypes.Path).Json)!["Path"]!.AsArray();
            var line = new Mpai.Cav.Map.RoutePath(map, segments);
            var off = path.Max(p =>
            {
                var c = p!["PointOfView"]!["CartPosition"]!.AsArray();
                var (e, n) = ((double)c[0]!, (double)c[1]!);
                return Enumerable.Range(0, (int)(line.Length * 2) + 1).Min(k => { var q = line.At(k / 2.0); return Math.Sqrt(Math.Pow(q.East - e, 2) + Math.Pow(q.North - n, 2)); });
            });
            if (off < 0.5) pathsOnRoute++;
            lengthErrors.Add(Math.Abs((path.Count - 1) * PathSelectionPlanning.Spacing - line.Length));
        }
        result["Destinations"] = destinations.ToString();
        result["Routes of the least time"] = $"{optimal} of {destinations}";
        result["Paths on their Route's centreline"] = $"{pathsOnRoute} of {destinations}";
        result["Path length against the Route's, worst"] = $"within {Math.Ceiling(lengthErrors.Max()):0} m";
        Expected.Match("ams-stage1-route-path.json", result);
    }

    // ---- the loop closed on perfect perception (Steps 5 and 6) ---------------------

    // The scenarios of M3233 3.1, on the map of the open scenario, the CAV starting at
    // 12 m/s on its Route from W00 to W21 (through a junction).
    public static IReadOnlyDictionary<string, Func<Simulation>> Scenarios()
    {
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        Simulation Make(params ScenarioVehicle[] vehicles) => new(map, route, vehicles, seed: 7, egoSpeed: 12);
        return new Dictionary<string, Func<Simulation>>
        {
            ["a vehicle ahead slows and stops"] = () => Make(new ScenarioVehicle("ahead", 0, 45, [(0, 13.0), (8, 6.0), (14, 0.0)], CameraRenderer.Silver)),
            ["a slower vehicle cuts in"] = () => Make(new ScenarioVehicle("cutter", 1, 30, [(0, 9.0)], CameraRenderer.DarkRed, LaneChange: (5, 0))),
            ["a vehicle ahead brakes hard"] = () => Make(new ScenarioVehicle("ahead", 0, 22, [(0, 13.0), (3, 0.0)], CameraRenderer.Silver, Braking: 8)),
            ["a free road to the Destination"] = () => Make()
        };
    }

    public sealed record DriveOutcome(bool Collided, double MinGap, double OverLimit, bool Arrived, double HardestBraking, double HarshestJerk, int Steps)
    {
        public string Judged => $"{(Collided ? "collision" : "no collision")}; {(Arrived ? "Destination reached" : "Destination not reached")}; {(OverLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}";
        public string Measured => $"minimum gap {(double.IsPositiveInfinity(MinGap) ? "-" : MinGap.ToString("0.0"))} m, hardest braking {HardestBraking:0.0} m/s2, harshest jerk {HarshestJerk:0.0} m/s3, {Steps} steps";
    }

    // The CAV's AMS on perfect perception, step by step: truth -> FED -> MSP (-> TOA) ->
    // the acceleration of the next point of the Trajectory -> the simulation.
    public static DriveOutcome Drive(Simulation sim, bool withToa, int maxSteps = 600)
    {
        var map = sim.Map;
        var fed = new FullEnvironmentDescription(AmsProvider.Fed);
        fed.Know(JsonNode.Parse(map.ToOfflineMapObject(0))!);
        var msp = new MotionSelectionPlanning(AmsProvider.Msp, new Dictionary<string, string>());
        var toa = new TrafficObstacleAvoidance(AmsProvider.Toa, new Dictionary<string, string>());
        var line = new RoutePath(map, sim.Path.Segments);
        var path = new JsonArray();
        for (var s = 0.0; ; s += PathSelectionPlanning.Spacing)
        {
            var (e, n, h, _) = line.At(Math.Min(s, line.Length));
            path.Add(new JsonObject { ["PointOfView"] = new JsonObject { ["CartPosition"] = new JsonArray(e, n, 0.0), ["Orientation"] = new JsonArray(0.0, 0.0, h * 180 / Math.PI) } });
            if (s >= line.Length) break;
        }
        msp.Follow(new JsonObject { ["Path"] = path });

        double minGap = double.PositiveInfinity, overLimit = 0, hardest = 0, jerk = 0, lastA = 0;
        var steps = 0;
        for (; steps < maxSteps && !sim.Arrived && !sim.Collided; steps++)
        {
            var sensed = sim.Sense(camera: false);
            if (sim.GapAhead() is { } g) minGap = Math.Min(minGap, g);
            overLimit = Math.Max(overLimit, sim.EgoSpeed - (double)sensed.Truth["Ego"]!["SpeedLimit"]!);
            var described = fed.Describe(JsonNode.Parse(TruthBed.Of(sim, sensed))!);
            var trajectory = msp.Plan(described);
            JsonNode points = trajectory["Trajectory"]!;
            if (withToa) { toa.Observe(described); points = toa.Refine(trajectory)["AMSMessage"]!["Trajectory"]!["Trajectory"]!; }
            var a = (TrafficObstacleAvoidance.Speed(points[1]!) - TrafficObstacleAvoidance.Speed(points[0]!)) / MotionSelectionPlanning.Step;
            sim.Advance(a);
            hardest = Math.Max(hardest, -sim.EgoAcceleration);
            if (steps > 0) jerk = Math.Max(jerk, Math.Abs(sim.EgoAcceleration - lastA) / Simulation.Step);
            lastA = sim.EgoAcceleration;
        }
        return new DriveOutcome(sim.Collided, minGap, overLimit, sim.Arrived, hardest, jerk, steps);
    }

    // Step 5: Motion Selection Planning alone - no Traffic Obstacle Avoidance - on each
    // scenario, the loop closed on perfect perception.
    [Fact]
    public void Step5MotionSelection()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        foreach (var (name, make) in Scenarios())
        {
            var outcome = Drive(make(), withToa: false);
            result[name] = outcome.Judged;
            report[name] = outcome.Judged + "; " + outcome.Measured;
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ams-stage1-msp.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ams-stage1-msp.json", result);
    }

    // Step 6: with Traffic Obstacle Avoidance: the same scenarios.
    [Fact]
    public void Step6TrafficObstacleAvoidance()
    {
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        foreach (var (name, make) in Scenarios())
        {
            var outcome = Drive(make(), withToa: true);
            result[name] = outcome.Judged;
            report[name] = outcome.Judged + "; " + outcome.Measured;
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ams-stage1-toa.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ams-stage1-toa.json", result);
    }

    // Step 7: AMS Memory and Record Always (M3233 3.8). The AMS run as a Module on the
    // open scenario: every decision kept in the Private Storage with the Full
    // Environment Descriptors it was taken on; for each AMS-MAS Message, the AMS Data
    // of its instant; and, the AMS declaring Record Always, what it received and what
    // it decided recorded without being asked, and read back.
    [Fact]
    public void Step7MemoryAndRecord()
    {
        const string ams = "1CAV-AMS-V1.1-I01";
        using var api = new Mpai.Aif.Api.ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), new AmsProvider());
        var location = Path.Combine(Path.GetTempPath(), "mpai-p8s7-" + Guid.NewGuid().ToString("N"));
        var result = new Dictionary<string, string>();
        try
        {
            api.StartFlow(ams);
            api.SharedStorageInit(ams, location);
            var recordId = api.RecordId(ams);
            result["the AMS recorded without being asked"] = recordId is null ? "no" : "yes";

            var sim = OpenScenario();
            api.InputWrite(ams, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0), 2000);
            api.InputWrite(ams, AmsTypes.Hci, 1, Destination("W21"), 2000);
            var messages = new List<JsonNode>();
            var data = new List<JsonNode>();
            for (var i = 0; i < 40; i++)
            {
                var sensed = sim.Sense(camera: false);
                api.InputWrite(ams, AmsTypes.Bed, 1, TruthBed.Of(sim, sensed), 2000);
                if (api.OutputRead(ams, AmsTypes.Message, 1, 500) is { Ok: true, Json: { } m }) messages.Add(JsonNode.Parse(m)!);
                while (api.OutputRead(ams, AmsTypes.Data, 1, messages.Count > data.Count ? 500 : 0) is { Ok: true, Json: { } d }) data.Add(JsonNode.Parse(d)!);
                sim.Advance(KeepTheLimit(sim));
            }
            api.RecordStop(ams, out _);
            api.StopFlow(ams);

            // The AMS Data: one for each AMS-MAS Message, valid, carrying what 3.8 says.
            var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V1.1", "data", "AMSData.json"))];
            var valid = data.Count(d => { using var doc = JsonDocument.Parse(d.ToJsonString()); lock (AIF.Metadata.PublishedSchemas.Lock) return schema.Evaluate(doc.RootElement).IsValid; });
            var ids = data.Select(d => (string?)d["AMSMASMessage"]?["AMSMASMessageID"]).ToHashSet();
            result["AMS-MAS Messages given out"] = messages.Count > 0 ? "some" : "none";
            result["every AMS-MAS Message has its AMS Data"] = messages.All(m => ids.Contains((string?)m["AMSMASMessageID"])) ? "yes" : "no";
            result["the AMS Data valid against its schema"] = valid == data.Count && data.Count > 0 ? "all" : $"{valid} of {data.Count}";
            result["each AMS Data carries the Route, the Path, the Trajectory, the FED"] =
                data.Count > 0 && data.All(d => d["RouteID"] is not null && d["Path"] is not null && d["Trajectory"] is not null && d["FED"] is not null) ? "yes" : "no";

            // The decisions, in the Private Storage, each with the FED it was taken on.
            var storage = api.ModuleStorageAt(ams, location);
            var keys = storage.MPAI_AIFM_RuledStorage_List("AMSData", "decisions/");
            var kinds = keys.Select(k => k[(k.IndexOf('-') + 1)..]).ToHashSet();
            result["decisions kept"] = string.Join(", ", new[] { (AmsTypes.Route, "Route"), (AmsTypes.Path, "Path"), (AmsTypes.Message, "AMS-MAS Message") }
                .Select(k => $"{k.Item2} {(kinds.Contains(k.Item1) ? "yes" : "no")}"));
            var withFed = 0;
            foreach (var key in keys)
                if (storage.MPAI_AIFM_RuledStorage_Get(key, out var bytes) == AIF.SharedStorage.StorageOutcome.OK &&
                    JsonNode.Parse(bytes) is { } record && record["FED"] is { } fed && record["Decision"] is { } decision &&
                    AmsTypes.Ms(fed["FullEnvironmentDescriptorsTime"]) <= Math.Max(AmsTypes.Ms(decision["RouteTime"]), Math.Max(AmsTypes.Ms(decision["PathTime"]), AmsTypes.Ms(decision["AMSMASMessageTime"]))) + 1)
                    withFed++;
            result["each decision kept with the FED it was taken on"] = keys.Count > 0 && withFed == keys.Count ? "yes" : $"{withFed} of {keys.Count}";

            // Record Always: what the AMS received and decided, read back.
            var records = RecordTests.Records(storage, recordId!);
            var recorded = records.Select(r => $"{r["Direction"]} {r["DataType"]}").Distinct().Order(StringComparer.Ordinal);
            result["its record, read back"] = string.Join(", ", recorded);
            result["its header"] = RecordTests.Header(storage, recordId!) is { } h ? $"always {h["Always"]}" : "none";
        }
        finally
        {
            try { api.StopFlow(ams); } catch { }
            try { Directory.Delete(location, recursive: true); } catch { }
        }
        Expected.Match("ams-stage1-amm.json", result);
    }

    // Step 8: the CAV end to end (M3233 3.9). The ESS and the AMS driven together on
    // each scenario of 3.1, the loop closed: what the AMS commands moves the CAV, which
    // changes what the ESS senses next. Judged: no collision; the speed within the
    // limit; the Destination reached; an Alert answered by braking before the time to
    // collision falls below 1 s. Reported: the minimum gap, the hardest braking, the
    // harshest jerk, the steps without a command, the latency from a Basic Environment
    // Descriptors instance to its AMS-MAS Message.
    //
    // A KNOWN LIMITATION, recorded (the author, 2026/09/27): the cut-in ends in a
    // collision. The camera is the ESS's only sensor in Stage 1, and its detector,
    // trained on photographs, stops recognising a vehicle a few metres ahead (a car
    // at 10.9 m 0.42, at 8.2 m a "truck" 0.10, nearer a "bench"). The BED keeps what
    // it can no longer see in the ego's lane, so a vehicle ahead that slows and stops
    // is not driven into; but the cutting-in vehicle changes lane 5 m ahead, unseen,
    // and nothing tells the ESS it is now in the ego's lane. Near-range sensing
    // (ultrasound, RADAR) is for the phase that adds sensors.
    [SkippableFact]
    public void Step8EndToEnd()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        foreach (var (name, make) in Scenarios())
        {
            var sim = make();
            using var cav = new CavInTheLoop(sim, "W21");
            double minGap = double.PositiveInfinity, overLimit = 0, hardest = 0, jerk = 0, lastA = 0, held = 0;
            var latencies = new List<double>();
            int steps = 0, uncommanded = 0, alerts = 0;
            bool awaitingBrake = false, braked = false;
            double? lowestTtcAtBraking = null;
            for (; steps < 600 && !sim.Arrived && !sim.Collided; steps++)
            {
                var sensed = sim.Sense();
                var (alertList, command, latency) = cav.Step(sensed);
                if (sim.GapAhead() is { } g) minGap = Math.Min(minGap, g);
                overLimit = Math.Max(overLimit, sim.EgoSpeed - (double)sensed.Truth["Ego"]!["SpeedLimit"]!);
                if (command is not null)
                {
                    latencies.Add(latency);
                    var points = command["AMSMessage"]!["Trajectory"]!["Trajectory"]!;
                    held = (TrafficObstacleAvoidance.Speed(points[1]!) - TrafficObstacleAvoidance.Speed(points[0]!)) / MotionSelectionPlanning.Step;
                }
                else uncommanded++;                                       // the last command holds
                if (alertList.Count > 0) { alerts += alertList.Count; awaitingBrake = true; }
                if (awaitingBrake && held < -0.5)
                {
                    awaitingBrake = false; braked = true;
                    lowestTtcAtBraking = Math.Min(lowestTtcAtBraking ?? double.PositiveInfinity, TimeToCollision(sensed.Truth));
                }
                sim.Advance(held);
                hardest = Math.Max(hardest, -sim.EgoAcceleration);
                if (steps > 0) jerk = Math.Max(jerk, Math.Abs(sim.EgoAcceleration - lastA) / Simulation.Step);
                lastA = sim.EgoAcceleration;
            }
            var alertJudged = alerts == 0 ? "no Alert"
                : !braked ? "an Alert, no braking asked after it"
                : lowestTtcAtBraking >= 1 ? "each Alert answered by braking before the time to collision fell below 1 s"
                : "braking asked when the time to collision was below 1 s";
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Arrived ? "Destination reached" : "Destination not reached")}; " +
                           $"{(overLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}; {alertJudged}";
            latencies.Sort();
            report[name] = result[name] + $"; minimum gap {(double.IsPositiveInfinity(minGap) ? "-" : minGap.ToString("0.0"))} m, hardest braking {hardest:0.0} m/s2, " +
                           $"harshest jerk {jerk:0.0} m/s3, {steps} steps, {uncommanded} without a command, {alerts} Alert(s)" +
                           (lowestTtcAtBraking is { } ttc && !double.IsPositiveInfinity(ttc) ? $", time to collision at braking {ttc:0.0} s" : "") +
                           (latencies.Count > 0 ? $"; latency BED -> AMS-MAS Message median {latencies[latencies.Count / 2]:0} ms, 95% {latencies[(int)(latencies.Count * 0.95)]:0} ms" : "");
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "ams-stage1-end-to-end.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("ams-stage1-end-to-end.json", result);
    }

    // The time to collision with the vehicle ahead in the ego's lane, from the truth;
    // infinite when the ego is not closing on it.
    private static double TimeToCollision(JsonObject truth)
    {
        var ego = (double)truth["Ego"]!["Speed"]!;
        var gap = truth["Gap"] is JsonValue v ? (double)v : double.PositiveInfinity;
        var lead = truth["Vehicles"]!.AsArray().Where(x => (int)x!["Lane"]! == 0).OrderBy(x => (double)x!["Distance"]!).FirstOrDefault();
        var closing = ego - (lead is null ? ego : (double)lead["Speed"]!);
        return closing > 0 && !double.IsPositiveInfinity(gap) ? gap / closing : double.PositiveInfinity;
    }
}
