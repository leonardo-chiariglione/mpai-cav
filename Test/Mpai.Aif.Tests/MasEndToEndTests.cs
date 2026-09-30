using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cav.Ams;
using Mpai.Cav.Map;
using Mpai.Cav.Mas;
using Mpai.Cav.Recordings;
using Mpai.Rca;
using Mpai.Wdl;

namespace Mpai.Aif.Tests;

// THE MAS IN THE LOOP: the Module 1CAV-MAS-V2.0-I01 under the Controller, fed a
// simulation step by step. Each step, the devices' Responses of the last step, the
// Weather Data and the Spatial Data written to its boundary; its Spatial Attitude
// read; then the AMS-MAS Message of the step, and MRA's answer and the commands AMI
// gave on it - from the CAV's Spatial Attitude the Message carries - read. The
// simulation waits for the MAS.
public sealed class MasInTheLoop : IDisposable
{
    public const string Mas = "1CAV-MAS-V2.0-I01";
    private readonly ControllerApi api;
    private readonly string settings = Path.Combine(Path.GetTempPath(), "mpai-p9-settings-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string location = Path.Combine(Path.GetTempPath(), "mpai-p9-" + Guid.NewGuid().ToString("N"));
    private bool following;
    private long? lastMs;                                     // of the CAV's Spatial Attitude the last Message carried

    // The MAS with its heading at Start (degrees): MSA's setting, the repository's
    // settings otherwise.
    public MasInTheLoop(double initialHeading)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        all[MasProvider.Msa] = new JsonObject { ["InitialHeading"] = initialHeading.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        File.WriteAllText(settings, all.ToJsonString());
        api = new ControllerApi(Repository.Amds, settings, new MasProvider());
        var started = api.StartFlow(Mas);
        if (started != AifError.OK) throw new InvalidOperationException($"{Mas} did not start: {started}");
        api.SharedStorageInit(Mas, location);
    }

    public ControllerApi Api => api;

    private void Write(string dataType, string json, int timeoutMs)
    {
        var written = api.InputWrite(Mas, dataType, 1, json, timeoutMs);
        if (written != AifError.OK) throw new InvalidOperationException($"{dataType} not written to the MAS: {written}");
    }

    private JsonNode ReadAt(string dataType, Func<JsonNode, long> time, long ms, int timeoutMs)
    {
        while (true)
        {
            var read = api.OutputRead(Mas, dataType, 1, timeoutMs);
            if (read.Error != AifError.OK) throw new InvalidOperationException($"no {dataType} from the MAS for {ms}: {read.Error}");
            var json = JsonNode.Parse(read.Json!)!;
            if (time(json) >= ms) return json;                   // an older one is passed over
        }
    }

    // A step: the MAS's Spatial Attitude.
    public JsonNode Sense(Simulation.Sensed sensed, IEnumerable<(string DataType, string Json)> responses, int timeoutMs = 10_000)
    {
        foreach (var (dataType, json) in responses) Write(dataType, json, timeoutMs);
        foreach (var (dataType, json) in sensed.Messages.Where(m => m.DataType is MasTypes.Weather or MasTypes.SpatialData)) Write(dataType, json, timeoutMs);
        var attitude = ReadAt(MasTypes.Attitude, j => MasTypes.Ms(j["SpatialAttitudeTime"]), sensed.FrameMs, timeoutMs);
        while (api.OutputRead(Mas, MasTypes.Weather, 1, 0) is { Error: AifError.OK }) { }   // for the ESS: given it from the sensors here
        return attitude;
    }

    // The AMS-MAS Message of the step: MRA's answer to it, and the commands AMI gave
    // on it - at the time of the CAV's Spatial Attitude it carries.
    public (JsonNode Answer, List<(string DataType, string Json)> Commands) Answer(JsonNode message, int timeoutMs = 10_000)
    {
        Write(MasTypes.Message, message.ToJsonString(), timeoutMs);
        following |= message["AMSMessage"]?["Trajectory"] is not null;
        if (message["AMSMessage"]?["SpatialAttitude"] is { } carried) lastMs = MasTypes.Ms(carried["SpatialAttitudeTime"]);
        var commands = new List<(string, string)>();
        if (following && lastMs is { } ms)
        {
            // The Wheel Command comes last: once it is out, so are the others.
            var wheel = ReadAt(MasTypes.WheelCommand, j => MasTypes.Ms(j["WheelCommandTime"]), ms, timeoutMs);
            foreach (var type in new[] { MasTypes.BrakeCommand, MasTypes.MotorCommand })
                while (api.OutputRead(Mas, type, 1, 0) is { Error: AifError.OK, Json: { } c }) commands.Add((type, c));
            commands.Add((MasTypes.WheelCommand, wheel.ToJsonString()));
        }
        return (Answered(message, timeoutMs), commands);
    }

    private JsonNode Answered(JsonNode message, int timeoutMs)
    {
        // The answer to this Message, which names it: its time is that of the MAS's
        // latest Spatial Attitude, which may be of the frame before when MRA reads the
        // Message first - a race that made a step wait for an answer already given.
        var id = (string?)message["AMSMASMessageID"];
        while (true)
        {
            var read = api.OutputRead(Mas, MasTypes.Message, 1, timeoutMs);
            if (read.Error != AifError.OK) throw new InvalidOperationException($"no answer from the MAS to {id}: {read.Error}");
            var json = JsonNode.Parse(read.Json!)!;
            if ((string?)json["DescrMetadata"] == $"The answer to {id}.") return json;
        }
    }

    public void Dispose()
    {
        api.StopFlow(Mas);
        api.Dispose();
        try { File.Delete(settings); Directory.Delete(location, recursive: true); } catch { }
    }
}

// STEP 7 (M3237 3.8): THE CAV END TO END - the ESS, the AMS and the MAS, three Modules
// under the Controller, driven by one User Agent step by step; the loop closed
// through the mechanical subsystems. Each step: the Spatial Data to the MAS, its
// Spatial Attitude with the camera frame, the GNSS fix and the Weather Data to the
// ESS; the Basic Environment Descriptors, the Alerts and the MAS's last answer to the
// AMS; its AMS-MAS Message to the MAS; the commands to the devices. On the scenarios
// of Phase 8 but the cut-in (a known limitation of the ESS, M3233) and the one on ice
// in freezing snow. Judged: no collision; the Destination reached; within the speed
// limit; not sliding. The lane is reported, not judged (the author, 2026/09/28):
// GNSS is the CAV's only lateral reference in Stage 1, and its error comes in slow
// excursions of up to about 1 m, which land at the lane's margin one run and not the
// next - the localisation, not the steering, which Steps 4 and 6 judge. Lane-level
// localisation (the lane markings) is for a later stage. Reported: the lane at the start
// and after, and the speed;
// MSA and the ESS's Spatial Attitude against the truth; the latencies.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class MasEndToEndTests
{
    // Seconds after which the CAV is placed: its first GNSS fixes averaged.
    private const double Placed = 3;

    // LONG (Run-Matrix.ps1): run at a step only where it changed what this exercises.
    [SkippableFact]
    [Trait("Duration", "Long")]
    public void Step7EndToEnd()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        var map = RoadMap.Grid(3);
        var route = map.FastestRoute("W00", "W21")!;
        var scenarios = new Dictionary<string, Func<Simulation>>
        {
            ["a vehicle ahead slows and stops"] = AmsStage1Tests.Scenarios()["a vehicle ahead slows and stops"],
            ["a vehicle ahead brakes hard"] = AmsStage1Tests.Scenarios()["a vehicle ahead brakes hard"],
            ["a free road to the Destination"] = AmsStage1Tests.Scenarios()["a free road to the Destination"],
            ["a vehicle ahead slows and stops, on ice, in freezing snow"] = () => new Simulation(map, route,
                [new ScenarioVehicle("ahead", 0, 45, [(0, 13.0), (8, 6.0), (14, 0.0)], CameraRenderer.Silver)], seed: 7, egoSpeed: 12)
                { Surface = [(100, 300, 0.15)], Weather = (-3, 2) }
        };
        foreach (var (name, make) in scenarios)
        {
            var sim = make();
            sim.Mechanical(seed: 9);
            var corners = Enumerable.Range(1, sim.Path.Segments.Count - 1).Select(i => sim.Path.Segments.Take(i).Sum(sim.Map.Length)).ToList();
            using var ess = new EssInTheLoop();
            ess.Know(sim.Map.ToOfflineMapObject(0));
            using var mas = new MasInTheLoop(sim.Path.At(0).Heading * 180 / Math.PI);
            using var ams = new AmsInTheLoop(sim, "W21");
            var offsets = new List<double>();
            double straight = 0, start = 0, corner = 0, overLimit = 0, hardest = 0, msaError = 0, minGap = double.PositiveInfinity;
            var essErrors = new List<double>();
            var latencies = new List<double>();
            var skidded = false;
            IReadOnlyList<(string DataType, string Json)> responses = [];
            JsonNode? answer = null;
            var steps = 0;
            for (; steps < 900 && !sim.Arrived && !sim.Collided; steps++)
            {
                var sensed = sim.Sense();
                var ego = sensed.Truth["Ego"]!;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var attitude = mas.Sense(sensed, responses);
                // The MAS's Spatial Attitude of the instant first: a GNSS fix is set against it.
                var toEss = sensed.Messages.Where(m => m.DataType is not MasTypes.SpatialData).Prepend((MasTypes.Attitude, attitude.ToJsonString())).ToList();
                var (bed, alerts) = ess.Step(new Simulation.Sensed(toEss, sensed.Truth, sensed.FrameMs));
                var message = ams.Step(bed!, alerts, answer, sensed.FrameMs);
                List<(string DataType, string Json)> commands = [];
                if (message is not null) (answer, commands) = mas.Answer(message);
                latencies.Add(clock.Elapsed.TotalMilliseconds);

                var pose = MasTypes.Pose(attitude);
                msaError = Math.Max(msaError, Math.Sqrt(Math.Pow(pose.East - (double)ego["East"]!, 2) + Math.Pow(pose.North - (double)ego["North"]!, 2)));
                var egoEss = MasTypes.Pose(bed!["EgoSpatialAttitude"]!);
                essErrors.Add(Math.Sqrt(Math.Pow(egoEss.East - (double)ego["East"]!, 2) + Math.Pow(egoEss.North - (double)ego["North"]!, 2)));
                overLimit = Math.Max(overLimit, (double)ego["Speed"]! - (double)ego["SpeedLimit"]!);
                if (sim.GapAhead() is { } g) minGap = Math.Min(minGap, g);

                sim.Actuate(commands);
                responses = sim.Advance();
                hardest = Math.Max(hardest, -sim.EgoAcceleration);
                skidded |= sim.Mechanics!.Skidding;
                if (corners.Any(c => Math.Abs(sim.EgoS - c) < 25)) corner = Math.Max(corner, Math.Abs(sim.EgoOffset));
                else if (sim.Time < Placed) start = Math.Max(start, Math.Abs(sim.EgoOffset));
                else { straight = Math.Max(straight, Math.Abs(sim.EgoOffset)); offsets.Add(Math.Abs(sim.EgoOffset)); }
            }
            offsets.Sort();
            var p99 = offsets.Count == 0 ? 0 : offsets[(int)(offsets.Count * 0.99)];
            result[name] = $"{(sim.Collided ? "collision" : "no collision")}; {(sim.Arrived ? "Destination reached" : "Destination not reached")}; " +
                           $"{(overLimit <= 0.5 ? "within the speed limit" : "above the speed limit")}; {(skidded ? "slid" : "did not slide")}";
            latencies.Sort(); essErrors.Sort();
            report[name] = result[name] + $"; off the lane's centre at most {start:0.00} m in the first {Placed:0} s, on the straights after {p99:0.00} m (99%), at most {straight:0.00} m, {corner:0.00} m at the corners; " +
                           $"minimum gap {(double.IsPositiveInfinity(minGap) ? "-" : minGap.ToString("0.0"))} m; hardest braking {hardest:0.0} m/s2; {steps} steps; " +
                           $"MSA at most {msaError:0.0} m from the truth; the ESS's ego median {essErrors[essErrors.Count / 2]:0.00} m, max {essErrors[^1]:0.00} m; a step of the three Modules median {latencies[latencies.Count / 2]:0} ms, 95% {latencies[(int)(latencies.Count * 0.95)]:0} ms";
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mas-stage1-end-to-end.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("mas-stage1-end-to-end.json", result);
    }
}

// THE AMS IN THE LOOP, with the MAS's answer: the Module 1CAV-AMS-V2.0-I01, the Offline
// Map and the Destination given at Start; each step the Alerts, the MAS's last answer
// and the Basic Environment Descriptors written, and the AMS-MAS Message of the frame
// read back (none before the Route is planned).
public sealed class AmsInTheLoop : IDisposable
{
    private const string Ams = CavInTheLoop.Ams;
    private readonly ControllerApi api;
    private readonly string location = Path.Combine(Path.GetTempPath(), "mpai-p9-ams-" + Guid.NewGuid().ToString("N"));
    private readonly string? settings;

    public ControllerApi Api => api;

    // cavId: the CAV's identity in its city (M3241), which FED sends its Full
    // Environment Descriptors with.
    // destination: none - HCI gives it later (Say).
    public AmsInTheLoop(Simulation sim, string? destination, string? cavId = null)
    {
        var path = Path.Combine(Repository.Root, "AIMs", "aim-settings.json");
        if (cavId is not null)
        {
            var all = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            all[AmsProvider.Fed] = new JsonObject { ["CAVID"] = cavId };
            path = settings = Path.Combine(Path.GetTempPath(), $"mpai-p10-settings-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, all.ToJsonString());
        }
        api = new ControllerApi(Repository.Amds, path, new AmsProvider());
        var started = api.StartFlow(Ams);
        if (started != AifError.OK) throw new InvalidOperationException($"{Ams} did not start: {started}");
        api.SharedStorageInit(Ams, location);
        api.InputWrite(Ams, AmsTypes.Map, 1, sim.Map.ToOfflineMapObject(0), 5000);
        if (destination is not null) Say(AmsStage1Tests.Destination(destination));
    }

    // What the AMS told HCI: each AMS-HCI Message, in order.
    public List<JsonNode> Told { get; } = [];

    // An AMS-HCI Message from HCI.
    public void Say(string message)
    {
        var written = api.InputWrite(Ams, AmsTypes.Hci, 1, message, 5000);
        if (written != AifError.OK) throw new InvalidOperationException($"the AMS-HCI Message not written: {written}");
    }

    // data: each AMS Data the step gave, to see.
    public JsonNode? Step(JsonNode bed, IEnumerable<JsonNode> alerts, JsonNode? answer, long frameMs, int timeoutMs = 5000, Action<string>? data = null)
    {
        foreach (var alert in alerts) api.InputWrite(Ams, AmsTypes.Alert, 1, alert.ToJsonString(), timeoutMs);
        if (answer is not null) api.InputWrite(Ams, AmsTypes.Message, 1, answer.ToJsonString(), timeoutMs);
        api.InputWrite(Ams, AmsTypes.Bed, 1, bed.ToJsonString(), timeoutMs);
        JsonNode? message = null;
        while (message is null && api.OutputRead(Ams, AmsTypes.Message, 1, timeoutMs) is { Error: AifError.OK, Json: { } json })
        {
            var m = JsonNode.Parse(json)!;
            if (AmsTypes.Ms(m["AMSMASMessageTime"]) == frameMs) message = m;
        }
        while (api.OutputRead(Ams, AmsTypes.Data, 1, data is null ? 0 : 50) is { Error: AifError.OK, Json: { } d }) data?.Invoke(d);
        while (api.OutputRead(Ams, AmsTypes.Hci, 1, 0) is { Error: AifError.OK, Json: { } h }) Told.Add(JsonNode.Parse(h)!);
        return message;
    }

    public void Dispose()
    {
        api.StopFlow(Ams);
        api.Dispose();
        try { Directory.Delete(location, recursive: true); if (settings is not null) File.Delete(settings); } catch { }
    }
}

// STEP 7 (M3237 3.2): THE USER AGENT DRIVES THE MAS. A workflow over the Module
// 1CAV-MAS-V2.0-I01, in real time: the vehicle a device of the User Agent's Physical
// Layer - stepped every 0.1 s, its Spatial Data and its Responses streamed to the
// MAS, the MAS's commands delivered to it - and, standing in for the AMS, a planner
// that gives one AMS-MAS Message: 10 m/s along the Route. The interlock: when the
// workflow ends, and when the MAS degrades - the planner then gives a Message with
// nothing to follow, which AMI reports - the vehicle is brought to its safe state,
// stopped, and given nothing more. Judged: the vehicle followed; stopped by the
// interlock; why. Reported: the speed, the distance to a standstill.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class MasDeliverTests
{
    // THE VEHICLE AS A DEVICE: the simulation, moved by its mechanics, stepped every
    // Step of wall time; the commands delivered to it acted on at the next step; its
    // safe state an Emergency Brake Command, after which it takes no command.
    public sealed class VehicleDevice : IDevice, IDisposable
    {
        private readonly Simulation sim;
        private readonly Channel<(string, string)> produced = Channel.CreateUnbounded<(string, string)>();
        private readonly List<(string DataType, string Json)> pending = [];
        private readonly CancellationTokenSource stop = new();
        private readonly Task stepping;
        private bool safe;
        public double? SafeAtSpeed { get; private set; }
        public double? SafeAtDistance { get; private set; }
        public double MaxSpeed { get; private set; }
        public int Delivered { get; private set; }
        public Simulation Sim => sim;

        public VehicleDevice(Simulation sim)
        {
            this.sim = sim;
            sim.Mechanical(seed: 9);
            stepping = Task.Run(Run);
        }

        private async Task Run()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                IReadOnlyList<(string, string)> commands;
                lock (pending) { commands = pending.ToList(); pending.Clear(); }
                lock (sim)
                {
                    sim.Actuate(commands);
                    foreach (var r in sim.Advance()) produced.Writer.TryWrite(r);
                    foreach (var m in sim.Sense(camera: false).Messages.Where(m => m.DataType == MasTypes.SpatialData)) produced.Writer.TryWrite(m);
                    MaxSpeed = Math.Max(MaxSpeed, sim.EgoSpeed);
                }
                var due = TimeSpan.FromSeconds(sim.Time) - clock.Elapsed;
                if (due > TimeSpan.Zero) try { await Task.Delay(due, stop.Token); } catch (OperationCanceledException) { }
            }
        }

        public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
        {
            lock (pending) if (!safe) { pending.Add((dataType, json)); Delivered++; }
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<(string DataType, string Json)> ReadAsync(CancellationToken cancel) => produced.Reader.ReadAllAsync(cancel);

        public Task SafeStopAsync()
        {
            lock (pending)
            {
                safe = true;
                pending.Clear();
                lock (sim) { SafeAtSpeed = sim.EgoSpeed; SafeAtDistance = sim.Mechanics!.Distance; }
                pending.Add((MasTypes.BrakeCommand, MasTypes.Brake("SAFE", (sim.Start + TimeSpan.FromSeconds(sim.Time)).ToUnixTimeMilliseconds(), 9, emergency: true)));
            }
            return Task.CompletedTask;
        }

        // Until the vehicle stands still, at most seconds.
        public async Task StoppedAsync(double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds) { lock (sim) if (sim.EgoSpeed <= 0) return; await Task.Delay(50); }
        }

        public void Dispose() { stop.Cancel(); try { stepping.Wait(); } catch { } }
    }

    // THE PLANNER standing in for the AMS: every 100 ms an AMS-MAS Message, 10 m/s along
    // the Route's first leg (east) for 60 s, with the CAV's Spatial Attitude - here the
    // simulation's, where the AMS gives its own; then, if told, one with nothing to
    // follow, and no more.
    private sealed class PlannerDevice(Simulation sim, TimeSpan? faultAfter) : IDevice
    {
        public Task DeliverAsync(string dataType, string json, CancellationToken cancel) => Task.CompletedTask;
        public Task SafeStopAsync() => Task.CompletedTask;

        public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
        {
            var t0 = sim.Start.ToUnixTimeMilliseconds();
            var points = new JsonArray();
            for (var k = 0; k <= 600; k++)
            {
                var point = Mpai.Cav.Ess.EssJson.Attitude($"P-{k}", t0 + k * 100, (k, 0, 0), (0.5, 0.5, 0.1), (10, 0, 0));
                point["Position"]!["CartAccel"] = Mpai.Cav.Ess.EssJson.Triple((0, 0, 0));
                points.Add(new JsonObject { ["ExpectedSpaceTime"] = Mpai.Cav.Ess.EssJson.SpaceTime($"P-{k}-ST", t0 + k * 100, point) });
            }
            var trajectory = new JsonObject { ["Header"] = "OSD-TRJ-V1.5", ["TrajectoryID"] = "PLAN1-TRJ", ["TrajectoryTime"] = Mpai.Cav.Ess.EssJson.SimpleTime("PLAN1-TRJ-T", t0), ["Trajectory"] = points };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var n = 1; faultAfter is not { } until || clock.Elapsed < until; n++)
            {
                var ms = sim.Start.ToUnixTimeMilliseconds() + (long)Math.Round(sim.Time * 1000);
                var heading = sim.Mechanics?.Heading ?? 0;
                var attitude = Mpai.Cav.Ess.EssJson.Attitude($"PLAN{n}-SA", ms, (sim.Mechanics?.East ?? 0, sim.Mechanics?.North ?? 0, 0), (0.5, 0.5, 0.1),
                    (sim.EgoSpeed * Math.Cos(heading), sim.EgoSpeed * Math.Sin(heading), 0),
                    new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"PLAN{n}-SA-O", ["Orientation"] = new JsonArray(0.0, 0.0, Math.Round(heading * 180 / Math.PI, 3)) });
                yield return (MasTypes.Message, new JsonObject
                {
                    ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = $"PLAN{n}", ["AMSMASMessageTime"] = Mpai.Cav.Ess.EssJson.SimpleTime($"PLAN{n}-T", ms),
                    ["AMSMessage"] = new JsonObject { ["Trajectory"] = trajectory.DeepClone(), ["SpatialAttitude"] = attitude, ["Command"] = "Execute" }
                }.ToJsonString());
                await Task.Delay(100, cancel);
            }
            if (faultAfter is not null)
            {
                yield return (MasTypes.Message, new JsonObject
                {
                    ["Header"] = MasTypes.Message, ["AMSMASMessageID"] = "PLAN2", ["AMSMASMessageTime"] = Mpai.Cav.Ess.EssJson.SimpleTime("PLAN2-T", t0),
                    ["AMSMessage"] = new JsonObject { ["Command"] = "Execute" }
                }.ToJsonString());
            }
            await Task.Delay(-1, cancel);
        }
    }

    [Fact]
    public async Task Step7Deliver()
    {
        const string mas = MasInTheLoop.Mas;
        var result = new Dictionary<string, string>();
        var report = new Dictionary<string, string>();
        const string body = """
                stream Motion (CAV-SPD-V2.0) from device "Vehicle"
                stream Brakes (CAV-BRR-V2.0) from device "Vehicle"
                stream Motor (CAV-MRP-V2.0) from device "Vehicle"
                stream Wheels (CAV-WHR-V2.0) from device "Vehicle"
                stream Plan (CAV-AMM-V2.0) from device "Planner"
                deliver Brake (CAV-BRC-V2.0) to device "Vehicle"
                deliver Throttle (CAV-MRC-V2.0) to device "Vehicle"
                deliver Steer (CAV-WHC-V2.0) to device "Vehicle"
            """;
        foreach (var (name, fault, wait) in new[] { ("the workflow ends", (TimeSpan?)null, "4s"), ("the MAS degrades", TimeSpan.FromSeconds(2), "10s") })
        {
            var map = RoadMap.Grid(3);
            var sim = new Simulation(map, map.FastestRoute("W00", "W20")!, [], seed: 7, egoSpeed: 10);
            using var loop = new MasInTheLoop(sim.Path.At(0).Heading * 180 / Math.PI);
            using var vehicle = new VehicleDevice(sim);
            var said = new List<string>();
            var devices = new DeviceRegistry().RegisterDevice("Vehicle", vehicle).RegisterDevice("Planner", new PlannerDevice(sim, fault));
            var interpreter = new WorkflowInterpreter(loop.Api.Async(), devices, line => { lock (said) said.Add(line); });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await interpreter.RunAsync(new WorkflowReader().Read($"workflow DRIVE over {mas}\non Start:\n{body}\n    wait {wait}\n"), CancellationToken.None);
            var safeAt = clock.Elapsed.TotalSeconds;
            await vehicle.StoppedAsync(5);
            var stopped = sim.EgoSpeed <= 0;
            var why = said.FirstOrDefault(l => l.Contains("safe state")) ?? "no safe stop";
            result[name] = $"the vehicle {(vehicle.Delivered > 0 && vehicle.MaxSpeed > 5 ? "followed the Trajectory" : "did not follow")}; " +
                           $"{(stopped ? "stopped by the interlock" : "not stopped")}; {why}";
            report[name] = result[name] + $"; {vehicle.Delivered} commands delivered; top speed {vehicle.MaxSpeed:0.0} m/s; " +
                           $"safe stop at {vehicle.SafeAtSpeed:0.0} m/s, {sim.Mechanics!.Distance - vehicle.SafeAtDistance:0.0} m to a standstill; workflow over after {safeAt:0.0} s";
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "mas-stage1-deliver-mas.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Expected.Match("mas-stage1-deliver-mas.json", result);
    }
}
