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
    private const double Planned = HighwayScenarios.Planned;

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
    // overtake: the CAV's Path Selection Planning may change lane, and the camera that looks back is in the loop.
    private static Dictionary<string, string> Run(string name, HighwayWorld world, int seconds, Action<StepInfo>? observe = null, bool overtake = false, bool junction = false, bool junctionOvertake = false)
    {
        var sim = new HighwaySimulation(world, Planned);
        var settings = overtake || junction || junctionOvertake ? HighwayScenarios.Settings(sim, opposingLane: junctionOvertake) : SettingsFor(sim);
        if (junctionOvertake)      // the car that turns off: the CAV overtakes on a two-way road, leaning out to look, with the junction in its map
        {
            var tuned = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            OvertakeAtJunction.Patch(tuned);
            File.WriteAllText(settings, tuned.ToJsonString());
        }
        if (junction)      // as the junction tests drive it: the CAV keeps its lane, and a vehicle coming the other way is tracked fast
        {
            var patched = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
            patched["1CAV-PSP-V2.0-I01"]!["LaneChange"] = "0";
            patched["1CAV-BED-V2.0-I01"]!["ClosingSpeed"] = "45";
            patched["1CAV-BED-V2.0-I01"]!["YoungBeta"] = "0.6";
            File.WriteAllText(settings, patched.ToJsonString());
        }
        var remotes = junction ? world.Others.Where(v => v.CavId is not null).Select(v => new RemoteCav(v, sim.Map)).ToList() : [];
        try
        {
            using var cav = new CavInTheLoop(sim.Map, "H1", settings);
            double minGap = double.PositiveInfinity, hardest = 0, jerk = 0, lastA = 0, held = 0, firstBrakeGap = double.NaN, firstBrakeSpeed = double.NaN;
            double slowSeconds = 0, startHardest = 0, seenGap = double.NaN, seenTruthGap = double.NaN;
            double? north = null;
            int startHardestStep = -1, seenStep = -1;
            int uncommanded = 0, alerts = 0, steps = 0;
            var latencies = new List<double>();
            var speeds = new List<double>();
            var x0 = world.Ego.X;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            for (; steps < seconds * 10 && !sim.Collided; steps++)
            {
                var sensed = sim.Sense(camera: true, rear: overtake, detail: junction || junctionOvertake);
                foreach (var remote in remotes)     // a connected vehicle tells the CAV where it is and where it is going
                {
                    if (remote.Report(sim, sensed.FrameMs, 1500) is { } era) cav.Hear(era);
                    if (steps % 2 == 0) cav.Hear(remote.Intent(sensed.FrameMs, 14));
                }
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
                    north = HighwayOvertakingTests.Asked(command).North;
                }
                else uncommanded++;
                alerts += alertList.Count;
                observe?.Invoke(new StepInfo(sim, sensed, bed, held, latency, steps, alerts, wall.Elapsed.TotalSeconds));
                if (steps >= 20 && double.IsNaN(firstBrakeGap) && held < -0.3 && sim.GapAhead() is { } gb) { firstBrakeGap = gb; firstBrakeSpeed = sim.EgoSpeed; }
                sim.Advance(held, overtake || junctionOvertake ? north : null);
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
        else if (mode is "junction-route" or "junction-lights" or "junction-nothing")
        {
            // THE JUNCTION (HighwayIntersectionTests): an oncoming vehicle turns left across the CAV's way, timed to meet it. The same turn three ways: its route told
            // (a connected vehicle), its indicator seen in the picture, or nothing.
            world = IntersectionScenarios.OncomingLeftTurn(connected: mode == "junction-route", signals: mode != "junction-nothing");
            label = mode switch
            {
                "junction-route" => "junction: the oncoming car tells the CAV its route (it will turn left)",
                "junction-lights" => "junction: the CAV sees the indicator of the oncoming car in the picture",
                _ => "junction: the oncoming car turns left, and the CAV is told nothing"
            };
        }
        else if (mode is "junction-overtake")
        {
            // THE CAR THAT TURNS OFF (OvertakeAtJunction, Downloads\CAV-Overtaking-at-a-Junction.md): the CAV follows an ordinary car that slows and lights
            // its right indicator for a junction; the other lane is empty; the CAV reads the indicator, predicts the turn, passes.
            world = OvertakeAtJunction.LeadTurnsRight();
            label = "junction: the car ahead turns right - the CAV reads its indicator, sees the other lane empty, and passes it";
        }
        else if (mode is "behind" or "overtake")
        {
            // THE STAGED SCENARIO (HighwayScenarios.Overtaking): a slow truck 200 m ahead, fast cars coming up behind it in the
            // left lane, and one that follows it in its own lane. With "overtake" the CAV may change lane: it is its own planning
            // that waits for the fast cars, pulls out, passes the truck and returns.
            world = HighwayScenarios.Overtaking(thirdCarFar: mode == "overtake");
            label = mode == "overtake" ? "overtaking: the CAV decides - wait for the fast cars, then pass the truck" : "a slow truck ahead, fast cars coming up behind";
        }
        else
        {
            world = HighwayWorld.Scenario([HighwayWorld.Vehicle("Slow", x: 2.4 + 150 + 6, lane: 0, speed: 22, truck: true)], Planned);
            label = "a slow truck 150 m ahead";
        }

        var recording = string.IsNullOrEmpty(recordTo) ? null : new HighwayRecording(recordTo);
        using var view = new HighwayLiveView("http://127.0.0.1:7100");
        Console.WriteLine("The run is on http://127.0.0.1:7100/");
        view.Publish(LiveState(null, world, label, done: false, rear: []), null);

        IReadOnlyList<RearSeen> rear = [];
        int rearPic = -1, frontPic = -1, telePic = -1;
        var plan = "following";
        var lastY = world.Ego.Y;
        Run(label, world, seconds, info =>
        {
            byte[]? rearPng = null;
            if (info.Sensed.Messages.Where(m => m.DataType == "OSD-BVO-V1.5" && m.Json.Contains("\"BVR")).FirstOrDefault().Json is { } rearFrame)    // a look behind every 0.3 s
            {
                rearPic = info.Step;
                rearPng = Convert.FromBase64String(JsonNode.Parse(rearFrame)!["BasicVisualObjectData"]![0]!["Data"]!.GetValue<string>());
                recording?.RearPng(info.Step, rearPng);
            }
            // What the ESS says is behind: the objects of its Basic Environment Descriptors that are.
            rear = (info.Bed?["BasicEnvironmentObjects"]?.AsArray() ?? []).Select(o => (Pos: o?["SpatialAttitude"]?["Position"], Id: o?["BasicEnvironmentObjectID"]?.GetValue<string>() ?? "?"))
                .Where(o => o.Pos?["CartPosition"] is JsonArray p && p.Count >= 2 && (double)p[0]! < 0)
                .Select(o => new RearSeen(o.Id, -(double)o.Pos!["CartPosition"]![0]! - world.Ego.Length, (double)o.Pos!["CartPosition"]![1]!,
                                          o.Pos["CartVelocity"] is JsonArray v && v.Count >= 1 ? (double)v[0]! : null)).ToList();
            // The plan, as the CAV's Path says it: the lane it is in and which way it is moving.
            var y = world.Ego.Y;
            plan = world.Ego.Y < 0.15 && y <= lastY + 1e-6 ? "following" : y - lastY > 0.002 ? "pulling out to pass" : lastY - y > 0.002 ? "back to the right lane" : y > 3.3 ? "passing" : plan;
            lastY = y;
            var front = FramePng(info.Sensed, "BVO");
            var tele = FramePng(info.Sensed, "BVD");                                  // the telephoto window, every 0.2 s
            if (tele is not null) { telePic = info.Step; recording?.Tele(info.Step, tele); }
            if (front is not null && info.Step % 2 == 0) { frontPic = info.Step; recording?.Front(info.Step, front); }
            var json = LiveState(info, world, label, done: false, rear: rear, front: frontPic, rearPic: rearPic, plan: plan, telePic: telePic, extra: mode == "junction-overtake" ? PlannerView(world) : null);
            recording?.State(json);
            view.Publish(json, front, rearPng, tele);
        }, overtake: mode == "overtake", junction: mode is "junction-route" or "junction-lights" or "junction-nothing", junctionOvertake: mode == "junction-overtake");
        view.Publish(LiveState(null, world, label, done: true, rear: rear, last: true, front: frontPic, rearPic: rearPic, plan: plan, telePic: telePic), null);
        recording?.Finish(label);
        Thread.Sleep(hold * 1000);
    }

    // A vehicle the ESS says is behind: how far behind the ego's rear bumper, how far to its left, how fast it comes (m/s, positive: nearer).
    private sealed record RearSeen(string Id, double Behind, double Left, double? Closing);

    private static byte[]? FramePng(Simulation.Sensed sensed, string tag = "BVO")
    {
        var json = sensed.Messages.FirstOrDefault(m => m.DataType == "OSD-BVO-V1.5" && m.Json.Contains("\"" + tag)).Json;
        return json is null ? null : Convert.FromBase64String(JsonNode.Parse(json)!["BasicVisualObjectData"]![0]!["Data"]!.GetValue<string>());
    }

    private static JsonArray Vec(double a, double b) => new(a, b);

    private static string LiveState(StepInfo? info, HighwayWorld world, string label, bool done, IReadOnlyList<RearSeen>? rear, bool last = false, int front = -1, int rearPic = -1, string plan = "", int telePic = -1, JsonObject? extra = null)
    {
        var ego = world.Ego;
        var vehicles = new JsonArray(world.Others.Where(v => Math.Abs(v.X - ego.X) < 300).OrderBy(v => v.X).Select(v => (JsonNode)new JsonObject
        {
            ["id"] = v.Id, ["x"] = v.X, ["y"] = v.Y, ["len"] = v.Length, ["wid"] = v.Width, ["speed"] = v.Speed, ["truck"] = v.Truck,
            ["paint"] = new JsonArray((int)v.Paint.R, (int)v.Paint.G, (int)v.Paint.B), ["h"] = Math.Round(v.Heading, 3), ["ind"] = (int)v.Indicator(world.Time)
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
        var state = new JsonObject
        {
            ["t"] = Math.Round(world.Time, 2), ["step"] = info?.Step ?? (last ? int.MaxValue - 1 : -1) + (last ? 0 : 0),
            ["wall"] = Math.Round(info?.WallSeconds ?? 0, 2), ["label"] = label, ["done"] = done,
            ["planned"] = Planned, ["limit"] = Planned,
            ["ego"] = new JsonObject { ["x"] = ego.X, ["y"] = ego.Y, ["speed"] = ego.Speed, ["acc"] = ego.Acceleration, ["length"] = ego.Length, ["width"] = ego.Width },
            ["vehicles"] = vehicles, ["ess"] = ess, ["plan"] = plan, ["junction"] = world.Junction is { } j ? new JsonObject { ["x"] = j.X, ["half"] = Junction.LaneWidth } : null,
            ["rear"] = new JsonArray((rear ?? []).Select(r => (JsonNode)new JsonObject
                { ["id"] = r.Id, ["behind"] = Math.Round(r.Behind, 1), ["left"] = Math.Round(r.Left, 2), ["closing"] = r.Closing is { } c ? Math.Round(c, 2) : null }).ToArray()),
            ["front"] = front < 0 ? null : front, ["rearPic"] = rearPic < 0 ? null : rearPic,
            ["gap"] = gap is { } g ? Math.Round(g, 2) : null, ["essGap"] = essGap is { } eg ? Math.Round(eg, 2) : null,
            ["leadSpeed"] = lead is null ? null : Math.Round(lead.Speed, 2),
            ["command"] = info is null ? 0 : Math.Round(info.Held, 3), ["alerts"] = info?.Alerts ?? 0, ["latencyMs"] = Math.Round(info?.LatencyMs ?? 0, 1),
            ["tele"] = telePic < 0 ? null : telePic
        };
        if (extra is not null) foreach (var (k, v) in extra) state[k] = v?.DeepClone();
        return state.ToJsonString();
    }

    // What the CAV's planner knows and has decided, for the animation: its own words, the stretch of the lane that comes the other way it knows to be clear,
    // and where it predicts the car ahead will turn off.
    private static JsonObject PlannerView(HighwayWorld world)
    {
        var ego = world.Ego;
        var turn = Mpai.Cav.Ams.LaneChangePlanner.LastTurnAhead;
        return new JsonObject
        {
            ["follow"] = true, ["status"] = Mpai.Cav.Ams.LaneChangePlanner.LastStatus,
            ["free"] = Mpai.Cav.Ams.LaneChangePlanner.LastHorizon > 0 ? Math.Round(Mpai.Cav.Ams.LaneChangePlanner.LastHorizon) : null,
            ["turnAt"] = double.IsNaN(turn) ? null : Math.Round(ego.X + ego.Length / 2 + turn, 1)
        };
    }
}

