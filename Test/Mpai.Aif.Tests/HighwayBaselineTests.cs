using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ams;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE BASELINE OF TEST PHASE 1 (M3253): the CAV as it is today - the ESS on one front camera of the rig, the
// Autonomous Motion Subsystem of Stage 1 - driving the highway. Stage 1 follows the vehicle ahead in its
// lane at the speed limit and never changes lane, so a slow vehicle ahead holds the CAV back for as long as it
// is there. This run measures that: how soon the CAV reacts to it, how near it comes, how hard it brakes, and
// how much of the distance the planned speed would have given it the CAV has lost. The overtaking is to improve
// it; this is what it is to be compared with. Not an assertion: a measurement, written to Test/Reports.
[Trait("Group", "Models")]
[Trait("Blocks", "No")]
[Collection(Timing.Name)]
public class HighwayBaselineTests
{
    private const double Planned = 30;                  // m/s, 108 km/h: the speed the CAV wants, and the road's limit

    // The AIM settings of the repository with the scene-description AIM told where the rig's camera is.
    private static string SettingsFor(HighwaySimulation sim)
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        var bvs = settings["1OSD-BVS-V1.5-I02"]!.AsObject();
        foreach (var (key, value) in sim.EssSettings()) bvs[key] = value;
        var path = Path.Combine(Path.GetTempPath(), "mpai-highway-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }

    // What one step shows, to whoever watches the run.
    private sealed record StepInfo(HighwaySimulation Sim, Simulation.Sensed Sensed, JsonNode? Bed, double Held, double LatencyMs, int Step, int Alerts, double WallSeconds);

    // Runs one scenario for the seconds given and returns what it showed.
    private static Dictionary<string, string> Run(string name, HighwayWorld world, int seconds, Action<StepInfo>? observe = null)
    {
        var sim = new HighwaySimulation(world, Planned);
        var settings = SettingsFor(sim);
        try
        {
            using var cav = new CavInTheLoop(sim.Map, "H1", settings);
            double minGap = double.PositiveInfinity, hardest = 0, jerk = 0, lastA = 0, held = 0, firstBrakeGap = double.NaN, firstBrakeSpeed = double.NaN;
            double slowSeconds = 0, startHardest = 0, seenGap = double.NaN, seenTruthGap = double.NaN;
            int startHardestStep = -1, seenStep = -1;
            int uncommanded = 0, alerts = 0, steps = 0;
            var latencies = new List<double>();
            var speeds = new List<double>();
            var x0 = world.Ego.X;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            for (; steps < seconds * 10 && !sim.Collided; steps++)
            {
                var sensed = sim.Sense();
                var (alertList, command, latency, bed) = cav.StepWithBed(sensed);
                if (sim.GapAhead() is { } g) minGap = Math.Min(minGap, g);
                // When the ESS first reports a vehicle where the truth has one in the CAV's way (within 8 m of its gap).
                if (seenStep < 0 && sim.GapAhead() is { } truthGap)
                    foreach (var o in bed?["BasicEnvironmentObjects"]?.AsArray() ?? [])
                        if (o?["SpatialAttitude"]?["Position"]?["CartPosition"] is JsonArray pos && pos.Count >= 2
                            && Math.Abs((double)pos[1]!) < 2.5 && Math.Abs((double)pos[0]! - truthGap) < 8)
                        { seenStep = steps; seenGap = (double)pos[0]!; seenTruthGap = truthGap; break; }
                if (command is not null)
                {
                    latencies.Add(latency);
                    var points = command["AMSMessage"]!["Trajectory"]!["Trajectory"]!;
                    held = (TrafficObstacleAvoidance.Speed(points[1]!) - TrafficObstacleAvoidance.Speed(points[0]!)) / MotionSelectionPlanning.Step;
                }
                else uncommanded++;
                alerts += alertList.Count;
                observe?.Invoke(new StepInfo(sim, sensed, bed, held, latency, steps, alerts, wall.Elapsed.TotalSeconds));
                if (steps >= 20 && double.IsNaN(firstBrakeGap) && held < -0.3 && sim.GapAhead() is { } gb) { firstBrakeGap = gb; firstBrakeSpeed = sim.EgoSpeed; }
                sim.Advance(held);
                speeds.Add(sim.EgoSpeed);
                if (sim.EgoSpeed < Planned - 1) slowSeconds += HighwaySimulation.Step;
                // The first 2 s are the CAV starting up (the AMS has no Path yet); what comes after is driving.
                if (steps < 20) { if (-world.Ego.Acceleration > startHardest) { startHardest = -world.Ego.Acceleration; startHardestStep = steps; } }
                else
                {
                    hardest = Math.Max(hardest, -world.Ego.Acceleration);
                    jerk = Math.Max(jerk, Math.Abs(world.Ego.Acceleration - lastA) / HighwaySimulation.Step);
                }
                lastA = world.Ego.Acceleration;
            }
            latencies.Sort();
            var simulated = steps * HighwaySimulation.Step;
            var covered = world.Ego.X - x0;
            var lost = Planned * simulated - covered;
            return new Dictionary<string, string>
            {
                [$"{name}: outcome"] = $"{(sim.Collided ? "COLLISION" : "no collision")}; {steps} steps ({simulated:0.0} s simulated)",
                [$"{name}: speed"] = $"mean {speeds.Average():0.0} m/s of {Planned:0} planned; below planned by more than 1 m/s for {slowSeconds:0.0} s; at the end {sim.EgoSpeed:0.0} m/s",
                [$"{name}: distance"] = $"{covered:0} m covered, {lost:0} m ({lost / (Planned * simulated) * 100:0}%) less than at the planned speed",
                [$"{name}: nearest approach"] = double.IsPositiveInfinity(minGap) ? "no vehicle ahead" : $"{minGap:0.0} m bumper to bumper",
                [$"{name}: start-up, the first 2 s"] = $"hardest braking {startHardest:0.0} m/s2 at step {startHardestStep}",
                [$"{name}: the ESS first reports the vehicle ahead"] = seenStep < 0 ? "never" : $"at step {seenStep} ({seenStep / 10.0:0.0} s), at a distance of {seenGap:0} m (truth {seenTruthGap:0} m)",
                [$"{name}: first braking after the start-up"] = double.IsNaN(firstBrakeGap) ? "none" : $"at a gap of {firstBrakeGap:0} m, at {firstBrakeSpeed:0.0} m/s",
                [$"{name}: hardest braking, harshest jerk, after the start-up"] = $"{hardest:0.0} m/s2, {jerk:0.0} m/s3",
                [$"{name}: Alerts, steps without a command"] = $"{alerts}, {uncommanded}",
                [$"{name}: latency Basic Environment Descriptors to AMS-MAS Message"] =
                    latencies.Count == 0 ? "none" : $"median {latencies[latencies.Count / 2]:0} ms, 95% {latencies[(int)(latencies.Count * 0.95)]:0} ms"
            };
        }
        finally { try { File.Delete(settings); } catch { } }
    }

    [SkippableFact]
    [Trait("Duration", "Long")]
    public void TheCavAsItIsOnTheHighway()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var report = new Dictionary<string, string>();

        // S0: an empty road. The CAV should hold its planned speed.
        foreach (var (k, v) in Run("S0 empty road", HighwayWorld.Scenario([], Planned), 30)) report[k] = v;

        // S1: a slow truck in the CAV's lane, 150 m ahead, at 22 m/s (79 km/h). The CAV closes at 8 m/s.
        var truck = HighwayWorld.Vehicle("Slow", x: 2.4 + 150 + 6, lane: 0, speed: 22, truck: true);
        foreach (var (k, v) in Run("S1 slow truck 150 m ahead", HighwayWorld.Scenario([truck], Planned), 60)) report[k] = v;

        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "highway-stage0-baseline.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    // THE SAME RUN, WATCHED. With MPAI_HIGHWAY_LIVE set (to a seed, for the traffic of that seed, or to "slow", for the slow
    // truck), the run serves its page on http://127.0.0.1:7100/ and goes on for MPAI_HIGHWAY_SECONDS (90 by default); with
    // MPAI_HIGHWAY_HOLD set it then stays up that many seconds. Without MPAI_HIGHWAY_LIVE it does nothing.
    [SkippableFact]
    [Trait("Duration", "Long")]
    public void TheCavOnTheHighwayLive()
    {
        var mode = Environment.GetEnvironmentVariable("MPAI_HIGHWAY_LIVE");
        Skip.If(string.IsNullOrEmpty(mode), "MPAI_HIGHWAY_LIVE is not set: this is the run that is watched.");
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("MPAI_HIGHWAY_SECONDS"), out var n) ? n : 90;
        var hold = int.TryParse(Environment.GetEnvironmentVariable("MPAI_HIGHWAY_HOLD"), out var h) ? h : 0;

        var recordTo = Environment.GetEnvironmentVariable("MPAI_HIGHWAY_RECORD");
        HighwayWorld world; string label;
        if (int.TryParse(mode, out var seed))
        {
            world = new HighwayWorld(seed, new TrafficOptions(FlowPerLane: 700), egoSpeed: Planned);
            label = $"traffic of seed {seed}";
        }
        else if (mode is "behind" or "overtake")
        {
            // THE STAGED SCENARIO: the CAV in the right lane at its planned speed; a slow truck 200 m ahead; and fast cars
            // coming up behind it in the left lane - a first at 36 m/s from 90 m behind, a second at 40 m/s from 260 m, a
            // third at 39 m/s from 430 m - and one that follows it in its own lane. The drivers keep their lanes.
            (byte, byte, byte) green = (70, 110, 80), sand = (150, 140, 110), blue = CameraRenderer.Blue, dark = (40, 40, 44), white = CameraRenderer.White;
            world = HighwayWorld.Scenario([
                HighwayWorld.Vehicle("Truck", 2.4 + 200 + 6, 0, 22, truck: true),
                HighwayWorld.Vehicle("Fast1", -90, 1, 36, paint: blue),
                HighwayWorld.Vehicle("Fast2", -260, 1, 40, paint: white),
                HighwayWorld.Vehicle("Fast3", mode == "overtake" ? -520 : -430, 1, 39, paint: dark),
                HighwayWorld.Vehicle("Tail", -45, 0, 30, paint: sand)], Planned);
            world.NpcsChangeLane = false;
            label = mode == "overtake" ? "overtaking, by a prototype planner: wait for the fast cars, then pass the truck" : "a slow truck ahead, fast cars coming up behind";
        }
        else
        {
            world = HighwayWorld.Scenario([HighwayWorld.Vehicle("Slow", x: 2.4 + 150 + 6, lane: 0, speed: 22, truck: true)], Planned);
            label = "a slow truck 150 m ahead";
        }

        var model = Path.Combine(Repository.Root, "Models", "yolox_s.onnx");
        using var rearWatch = new HighwayRearWatch(model);
        var recording = string.IsNullOrEmpty(recordTo) ? null : new HighwayRecording(recordTo);
        using var view = new HighwayLiveView("http://127.0.0.1:7100");
        Console.WriteLine("The run is on http://127.0.0.1:7100/");
        view.Publish(LiveState(null, world, label, done: false, rear: null), null);

        IReadOnlyList<HighwayRearWatch.Seen> rear = [];
        int rearPic = -1, frontPic = -1;
        var plan = mode == "overtake" ? "following" : "";
        var memory = new Dictionary<int, (double Time, double Behind, double Left, double? Closing)>();
        Run(label, world, seconds, info =>
        {
            byte[]? rearPng = null;
            if (info.Step % 3 == 0)                                       // a look behind every 0.3 s
            {
                var (seen, window) = rearWatch.Look(world);
                rear = seen; rearPic = info.Step;
                rearPng = Mpai.Cav.Recordings.Png.Encode(640, 360, window);
                recording?.Rear(info.Step, window);
            }
            if (mode == "overtake" && info.Step % 3 == 0) plan = OvertakePlan(world, rear, plan, memory);
            var front = FramePng(info.Sensed);
            if (front is not null && info.Step % 2 == 0) { frontPic = info.Step; recording?.Front(info.Step, front); }
            var json = LiveState(info, world, label, done: false, rear: rear, front: frontPic, rearPic: rearPic, plan: plan);
            recording?.State(json);
            view.Publish(json, front, rearPng);
        });
        view.Publish(LiveState(null, world, label, done: true, rear: rear, last: true, front: frontPic, rearPic: rearPic, plan: plan), null);
        recording?.Finish(label);
        Thread.Sleep(hold * 1000);
    }

    // A PROTOTYPE PLANNER, OUTSIDE THE REFERENCE SOFTWARE (M3253 build step 5, tried in the harness): it takes the
    // lane to the left when a slower vehicle is near ahead, the left lane is clear ahead, and the rear watch sees no
    // vehicle in the left lane close behind or closing so that it would arrive within 10 s. It goes back to the right
    // when the vehicle it passed is 30 m behind and the right lane is free ahead. The vehicle ahead and the clear
    // road ahead are read from the simulation (the front camera already gives the CAV its leader); what is behind is
    // only what the rear watch saw. The CAV's own Stage 1 planner is not changed: the lane is changed in the world.
    private static string OvertakePlan(HighwayWorld world, IReadOnlyList<HighwayRearWatch.Seen> rear, string plan,
                                        Dictionary<int, (double Time, double Behind, double Left, double? Closing)> memory)
    {
        var ego = world.Ego; var now = world.Time;
        // What the rear watch has seen is remembered: a vehicle seen closing in the left lane is taken to be still there,
        // at the distance its closing speed predicts (12 m/s if that was not yet known), until it has passed - the detector
        // loses a vehicle that is near, and the CAV must not pull out in front of one it has just stopped seeing.
        foreach (var r in rear) memory[r.Id] = (now, r.Behind, r.Left, r.Closing);
        foreach (var id in memory.Keys.ToList())
        {
            var m = memory[id];
            if (now - m.Time > 8 || m.Behind - (m.Closing ?? 12) * (now - m.Time) < -10) memory.Remove(id);
        }
        if (ego.ChangingLane) return plan;
        var leftY = HighwayRoad.LaneCentre(1); var rightY = HighwayRoad.LaneCentre(0);
        if (ego.Lane == 0)
        {
            var (lead, gap) = world.Leader(ego, ego.Y);
            if (lead is null || gap > 60 || lead.Speed > Planned - 3) return "following";
            if (world.Leader(ego, leftY).Gap < 80) return "wants to pass: a vehicle ahead in the left lane";
            var unsafeBehind = memory.Values.Any(m =>
            {
                if (m.Left < 1.5 || m.Left > 5.5) return false;
                var closing = m.Closing ?? 12;
                var behind = m.Behind - closing * (now - m.Time);
                return m.Closing is null || behind < 30 || (closing > 0 && (behind - 10) / closing < 10);
            });
            if (unsafeBehind) return "wants to pass: waiting, a vehicle is coming up in the left lane";
            world.ChangeEgoLane(1);
            return "pulling out to pass";
        }
        if (world.Follower(ego, rightY).Gap > 30 && world.Leader(ego, rightY).Gap > 60)
        {
            world.ChangeEgoLane(0);
            return "passed: back to the right lane";
        }
        return "passing";
    }

    private static byte[]? FramePng(Simulation.Sensed sensed)
    {
        var json = sensed.Messages.FirstOrDefault(m => m.DataType == "OSD-BVO-V1.5").Json;
        return json is null ? null : Convert.FromBase64String(JsonNode.Parse(json)!["BasicVisualObjectData"]![0]!["Data"]!.GetValue<string>());
    }

    private static JsonArray Vec(double a, double b) => new(a, b);

    private static string LiveState(StepInfo? info, HighwayWorld world, string label, bool done, IReadOnlyList<HighwayRearWatch.Seen>? rear, bool last = false, int front = -1, int rearPic = -1, string plan = "")
    {
        var ego = world.Ego;
        var vehicles = new JsonArray(world.Others.Where(v => Math.Abs(v.X - ego.X) < 300).OrderBy(v => v.X).Select(v => (JsonNode)new JsonObject
        {
            ["id"] = v.Id, ["x"] = v.X, ["y"] = v.Y, ["len"] = v.Length, ["wid"] = v.Width, ["speed"] = v.Speed, ["truck"] = v.Truck,
            ["paint"] = new JsonArray((int)v.Paint.R, (int)v.Paint.G, (int)v.Paint.B)
        }).ToArray());

        var ess = new JsonArray();
        double? essGap = null;
        foreach (var o in info?.Bed?["BasicEnvironmentObjects"]?.AsArray() ?? [])
            if (o?["SpatialAttitude"]?["Position"]?["CartPosition"] is JsonArray pos && pos.Count >= 2)
            {
                double ahead = (double)pos[0]!, left = (double)pos[1]!;
                ess.Add(new JsonObject { ["ahead"] = ahead, ["left"] = left, ["cls"] = o["InstanceIdentifier"]?["InstanceIdentifier"]?.GetValue<string>() ?? "object" });
                if (Math.Abs(left) < 2.0 && ahead > 0 && (essGap is null || ahead < essGap)) essGap = ahead;
            }

        var sim = info?.Sim;
        var gap = sim?.GapAhead();
        var lead = world.Leader(ego, ego.Y).Vehicle;
        return new JsonObject
        {
            ["t"] = Math.Round(world.Time, 2), ["step"] = info?.Step ?? (last ? int.MaxValue - 1 : -1) + (last ? 0 : 0),
            ["wall"] = Math.Round(info?.WallSeconds ?? 0, 2), ["label"] = label, ["done"] = done,
            ["planned"] = Planned, ["limit"] = Planned,
            ["ego"] = new JsonObject { ["x"] = ego.X, ["y"] = ego.Y, ["speed"] = ego.Speed, ["acc"] = ego.Acceleration, ["length"] = ego.Length, ["width"] = ego.Width },
            ["vehicles"] = vehicles, ["ess"] = ess, ["plan"] = plan,
            ["rear"] = new JsonArray((rear ?? []).Select(r => (JsonNode)new JsonObject
                { ["id"] = r.Id, ["behind"] = Math.Round(r.Behind, 1), ["left"] = Math.Round(r.Left, 2), ["closing"] = r.Closing is { } c ? Math.Round(c, 2) : null }).ToArray()),
            ["front"] = front < 0 ? null : front, ["rearPic"] = rearPic < 0 ? null : rearPic,
            ["gap"] = gap is { } g ? Math.Round(g, 2) : null, ["essGap"] = essGap is { } eg ? Math.Round(eg, 2) : null,
            ["leadSpeed"] = lead is null ? null : Math.Round(lead.Speed, 2),
            ["command"] = info is null ? 0 : Math.Round(info.Held, 3), ["alerts"] = info?.Alerts ?? 0, ["latencyMs"] = Math.Round(info?.LatencyMs ?? 0, 1)
        }.ToJsonString();
    }
}
