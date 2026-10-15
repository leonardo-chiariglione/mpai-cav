using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE STAGED SCENARIOS OF TEST PHASE 1 (M3253), shared by the runs that watch and the tests.
public static class HighwayScenarios
{
    public const double Planned = 30;                   // m/s, 108 km/h: the speed the CAV wants, and the road's limit

    // THE OVERTAKING: the CAV in the right lane at its planned speed; a slow truck 200 m ahead; and fast cars coming
    // up behind it in the left lane - a first at 36 m/s from 90 m behind, a second at 40 m/s from 260 m, a third at
    // 39 m/s from 520 m - and one that follows it in its own lane. The drivers keep their lanes.
    public static HighwayWorld Overtaking(bool thirdCarFar = true)
    {
        (byte, byte, byte) sand = (150, 140, 110), blue = CameraRenderer.Blue, dark = (40, 40, 44), white = CameraRenderer.White;
        var world = HighwayWorld.Scenario([
            HighwayWorld.Vehicle("Truck", 2.4 + 200 + 6, 0, 22, truck: true),
            HighwayWorld.Vehicle("Fast1", -90, 1, 36, paint: blue),
            HighwayWorld.Vehicle("Fast2", -260, 1, 40, paint: white),
            HighwayWorld.Vehicle("Fast3", thirdCarFar ? -520 : -430, 1, 39, paint: dark),
            HighwayWorld.Vehicle("Tail", -45, 0, 30, paint: sand)], Planned);
        world.NpcsChangeLane = false;
        return world;
    }

    // The AIM settings of the repository with the Path Selection Planning told to overtake (and, with a simulation,
    // the scene-description AIMs told where the rig's cameras are).
    public static string Settings(HighwaySimulation? sim = null, bool opposingLane = false, double remoteReach = 1500)
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")))!.AsObject();
        settings["1CAV-PSP-V2.0-I01"] = new JsonObject { ["LaneChange"] = "1", ["DesiredSpeed"] = Planned.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (opposingLane)
        {
            settings["1CAV-PSP-V2.0-I01"]!["OpposingLane"] = "1";
            settings["1CAV-PSP-V2.0-I01"]!["RemoteReach"] = remoteReach.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (sim is not null)
        {
            foreach (var (key, value) in sim.EssSettings()) settings["1OSD-BVS-V1.5-I02"]![key] = value;
            foreach (var (key, value) in sim.RearEssSettings()) settings["1OSD-BVS-V1.5-I02"]![key] = value;
            foreach (var (key, value) in sim.DetailEssSettings()) settings["1OSD-BVS-V1.5-I02"]![key] = value;
        }
        var path = Path.Combine(Path.GetTempPath(), "mpai-highway-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }
}

// PERFECT PERCEPTION OF THE HIGHWAY: the Basic Environment Descriptors the ESS would give if it saw everything within
// 200 m ahead and 250 m behind exactly: each vehicle's distance from the ego's front - to its rear where it is ahead, to
// its front where it is behind - its place to the left of the ego, its speed relative to the ego's. For finding what the
// AMS does with a right picture of the road before the ESS's own is in the loop.
public static class HighwayTruthBed
{
    // occlude: a vehicle is seen only where a line from the ego's eye (its front, in its lane) to it - its centre or either end of
    // the face towards the ego - is not blocked by another; sightRange: the farthest the ego sees ahead (200 m, 250 m behind, when it is
    // not occluded).
    public static JsonNode Of(HighwaySimulation sim, long ms, bool occlude = false, double sightRange = 200, double? indicatorRange = null)
    {
        var world = sim.World; var ego = world.Ego;
        var id = $"TRUTH{world.StepNumber:D6}";
        var attitude = EssJson.Attitude($"{id}-EGO", ms, (ego.X, ego.Y, 0), (0.1, 0.1, 0.1),
            (ego.Speed * Math.Cos(ego.Heading), ego.Speed * Math.Sin(ego.Heading), 0),
            new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"{id}-EGO-O", ["Orientation"] = new JsonArray(0.0, 0.0, ego.Heading * 180 / Math.PI) });
        var objects = new JsonArray();
        foreach (var v in world.Others)
        {
            var centre = v.X - ego.X;
            var x = centre > 0 ? centre - v.Length / 2 - ego.Length / 2 : centre + v.Length / 2 - ego.Length / 2;
            if (x > (occlude ? sightRange : 200) || x < -250 || Math.Abs(v.Y - ego.Y) > 9) continue;
            if (occlude && !Seen(world, v)) continue;
            var entry = new JsonObject
            {
                ["BasicEnvironmentObjectID"] = v.Id,
                ["InstanceIdentifier"] = EssJson.Identifier(v.Truck ? "truck" : "car", 1),
                ["SpatialAttitude"] = EssJson.Attitude($"{id}-{v.Id}", ms, (x, v.Y - ego.Y, 0), (0.1, 0.1, 0.1),
                    v.Scripted ? (v.VelocityVector.X - ego.Speed, v.VelocityVector.Y, 0) : (v.Velocity - ego.Speed, 0, 0)),
                ["ExistenceConfidence"] = 1.0, ["Motion"] = "Dynamic",
                ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual" })
            };
            // Its indicator, if it is lit now and near enough to be read (perfect perception of it: the ESS reads it from the picture in the next round).
            if (indicatorRange is { } reach && Math.Abs(centre) < reach && v.Indicator(world.Time) is var shown and not Turn.None) entry["TurnIndicator"] = shown.ToString();
            objects.Add(entry);
        }
        return new JsonObject
        {
            ["Header"] = "CAV-BED-V2.0", ["BasicEnvironmentDescriptorsID"] = id, ["BasicEnvironmentDescriptorsTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["EgoSpatialAttitude"] = attitude, ["BasicEnvironmentObjectCount"] = objects.Count, ["BasicEnvironmentObjects"] = objects
        };
    }

    // Is the vehicle seen from the ego's eye, or does another hide it?
    private static bool Seen(HighwayWorld world, HighwayVehicle target)
    {
        var ego = world.Ego;
        (double X, double Y) eye = (ego.X + ego.Length / 2, ego.Y);
        var face = target.X > ego.X ? target.X - target.Length / 2 : target.X + target.Length / 2;
        var aims = new (double X, double Y)[] { (target.X, target.Y), (face, target.Y - target.Width / 2 + 0.1), (face, target.Y + target.Width / 2 - 0.1) };
        foreach (var aim in aims)
        {
            var blocked = world.Others.Any(b => !ReferenceEquals(b, target) &&
                Crosses(eye, aim, b.X - b.Length / 2 + 0.05, b.X + b.Length / 2 - 0.05, b.Y - b.Width / 2 + 0.05, b.Y + b.Width / 2 - 0.05));
            if (!blocked) return true;
        }
        return false;
    }

    // Does the segment from a to b cross the rectangle?
    private static bool Crosses((double X, double Y) a, (double X, double Y) b, double x0, double x1, double y0, double y1)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - x0), (dx, x1 - a.X), (-dy, a.Y - y0), (dy, y1 - a.Y) })
        {
            if (p == 0) { if (q < 0) return false; continue; }
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        return true;
    }

    // WHAT ANOTHER CAV PERCEIVES, in its own frame: every vehicle within reach ahead of it and 100 m behind, seen exactly (the
    // ego, which is the CAV it reports to, left out). The CAV is the vehicle given: in the lane that comes the other way,
    // looking back along the road.
    public static JsonNode OfVehicle(HighwaySimulation sim, HighwayVehicle me, long ms, double reach)
    {
        var world = sim.World; var d = me.Direction;
        var id = $"{me.CavId}-{world.StepNumber:D6}";
        var attitude = EssJson.Attitude($"{id}-EGO", ms, (me.X, me.Y, 0), (0.1, 0.1, 0.1), me.Scripted ? (me.VelocityVector.X, me.VelocityVector.Y, 0) : (me.Velocity, 0, 0),
            new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = $"{id}-EGO-O", ["Orientation"] = new JsonArray(0.0, 0.0, me.Scripted ? me.Heading * 180 / Math.PI : d < 0 ? 180.0 : 0.0) });
        var (ch, sh) = (Math.Cos(me.Heading), Math.Sin(me.Heading));
        var objects = new JsonArray();
        foreach (var o in world.Others)
        {
            if (ReferenceEquals(o, me)) continue;
            var ahead = me.Scripted ? (o.X - me.X) * ch + (o.Y - me.Y) * sh : d * (o.X - me.X);
            if (ahead > reach || ahead < -100) continue;
            objects.Add(new JsonObject
            {
                ["BasicEnvironmentObjectID"] = o.Id,
                ["InstanceIdentifier"] = EssJson.Identifier(o.Truck ? "truck" : "car", 1),
                ["SpatialAttitude"] = me.Scripted
                    ? EssJson.Attitude($"{id}-{o.Id}", ms, (ahead, -(o.X - me.X) * sh + (o.Y - me.Y) * ch, 0), (0.1, 0.1, 0.1),
                        ((o.VelocityVector.X - me.VelocityVector.X) * ch + (o.VelocityVector.Y - me.VelocityVector.Y) * sh,
                         -(o.VelocityVector.X - me.VelocityVector.X) * sh + (o.VelocityVector.Y - me.VelocityVector.Y) * ch, 0))
                    : EssJson.Attitude($"{id}-{o.Id}", ms, (ahead, d * (o.Y - me.Y), 0), (0.1, 0.1, 0.1), (d * (o.Velocity - me.Velocity), 0, 0)),
                ["ExistenceConfidence"] = 1.0, ["Motion"] = "Dynamic",
                ["Contributions"] = new JsonArray(new JsonObject { ["Technology"] = "Visual" })
            });
        }
        return new JsonObject
        {
            ["Header"] = "CAV-BED-V2.0", ["BasicEnvironmentDescriptorsID"] = id, ["BasicEnvironmentDescriptorsTime"] = EssJson.SimpleTime(id + "-T", ms),
            ["EgoSpatialAttitude"] = attitude, ["BasicEnvironmentObjectCount"] = objects.Count, ["BasicEnvironmentObjects"] = objects
        };
    }
}

// THE CAV OVERTAKES. The lane change is a decision of the CAV's own Path Selection Planning (Stage 2); the Motion
// Selection Planning follows the Path it gives, laterally too; the simulation steers the ego along the Trajectory.
// First on perfect perception - the decision and the motion alone - then with the ESS's own picture.
[Collection(Timing.Name)]
public class HighwayOvertakingTests
{
    // What the AMS-MAS Message asks for at the next step: the acceleration, and where to be (north of the line).
    public static (double Acceleration, double North) Asked(JsonNode command)
    {
        var points = command["AMSMessage"]!["Trajectory"]!["Trajectory"]!;
        var held = (TrafficObstacleAvoidance.Speed(points[1]!) - TrafficObstacleAvoidance.Speed(points[0]!)) / MotionSelectionPlanning.Step;
        var north = (double)points[1]!["ExpectedSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]![1]!;
        return (held, north);
    }

    public sealed record Trace(double T, double X, double Y, double Speed, double Acceleration, double TruckX, double Gap);

    // Drives the scenario for the seconds given; the ego's trace. perfect: the AMS is given the truth, no ESS runs; else the
    // ESS looks at the pictures of the front camera and the camera that looks back.
    public static (List<Trace> Trace, HighwayWorld World) Drive(HighwayWorld world, int seconds, bool perfect, Action<int, HighwaySimulation, JsonNode?, JsonNode?>? observe = null)
    {
        var sim = new HighwaySimulation(world, HighwayScenarios.Planned);
        var settings = HighwayScenarios.Settings(sim);
        var trace = new List<Trace>();
        try
        {
            using var cav = new CavInTheLoop(sim.Map, "H1", settings, withEss: !perfect);
            double held = 0; double? north = null;
            for (var step = 0; step < seconds * 10 && !sim.Collided; step++)
            {
                var sensed = sim.Sense(camera: !perfect);
                JsonNode? command, bed;
                if (perfect) (_, command, _, bed) = cav.StepOnBed(HighwayTruthBed.Of(sim, sensed.FrameMs), [], sensed.FrameMs);
                else (_, command, _, bed) = cav.StepWithBed(sensed);
                if (command is not null) (held, north) = Asked(command);
                observe?.Invoke(step, sim, command, bed);
                trace.Add(new Trace(world.Time, world.Ego.X, world.Ego.Y, world.Ego.Speed, world.Ego.Acceleration,
                    world.Others.FirstOrDefault(v => v.Id == "Truck")?.X ?? double.NaN, Math.Min(sim.GapAhead() ?? 9999, 9999)));
                sim.Advance(held, north);
            }
        }
        finally { try { File.Delete(settings); } catch { } }
        return (trace, world);
    }

    [Fact]
    [Trait("Duration", "Long")]
    public void TheCavOvertakesTheTruckOnPerfectPerception()
    {
        var world = HighwayScenarios.Overtaking();
        Check(world, Drive(world, 120, perfect: true).Trace, "overtake-perfect-perception");
    }

    // THE CAV OVERTAKES ON ITS OWN SENSES: the ESS looks at the front camera's pictures and, every 0.3 s, at the window of
    // the picture of the camera that looks back; the AMS decides, from what the ESS says of what is ahead and behind.
    [SkippableFact]
    [Trait("Group", "Models")]
    [Trait("Blocks", "No")]
    [Trait("Duration", "Long")]
    public void TheCavOvertakesTheTruckOnItsOwnSenses()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var world = HighwayScenarios.Overtaking();
        Check(world, Drive(world, 120, perfect: false).Trace, "overtake");
    }

    // On an empty road there is nothing to pass: the CAV keeps its lane, at its planned speed.
    [Fact]
    public void TheCavKeepsItsLaneOnAnEmptyRoad()
    {
        var world = HighwayWorld.Scenario([], HighwayScenarios.Planned);
        var (trace, _) = Drive(world, 40, perfect: true);
        Assert.All(trace, t => Assert.True(Math.Abs(t.Y) < 0.01, $"left its lane at {t.T:0.0} s"));
        Assert.True(trace[^1].Speed > HighwayScenarios.Planned - 1);
    }

    // A slow truck alone, the left lane free: the CAV pulls out as soon as it is near, passes and returns.
    [Fact]
    [Trait("Duration", "Long")]
    public void TheCavPassesASlowTruckWhenTheLeftLaneIsFree()
    {
        var world = HighwayWorld.Scenario([HighwayWorld.Vehicle("Truck", 2.4 + 150 + 6, 0, 22, truck: true)], HighwayScenarios.Planned);
        var (trace, _) = Drive(world, 90, perfect: true);
        Check(world, trace, "overtake-free-left-lane");
        Assert.True(trace.First(t => t.Y > 1).T < 25, "it pulled out soon after the truck came within the decision distance");
    }

    // Random traffic, drivers who change lane as they do: no collision, and nobody forced to brake hard.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Trait("Duration", "Long")]
    public void TheCavDrivesInTrafficWithoutCollision(int seed)
    {
        var world = new HighwayWorld(seed, new TrafficOptions(FlowPerLane: 700), egoSpeed: HighwayScenarios.Planned);
        var (trace, _) = Drive(world, 120, perfect: true);
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports", "Highway"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "Highway", $"traffic-seed-{seed}.json"),
            JsonSerializer.Serialize(new { Seed = seed, Seconds = trace[^1].T, Metres = trace[^1].X, Collisions = world.EgoCollisions.Count, Nearest = world.NearestToEgo,
                                           HardestBrakingForEgo = world.HardestBrakingForEgo, LaneChanges = trace.Zip(trace.Skip(1)).Count(p => (p.First.Y > 1.75) != (p.Second.Y > 1.75)) },
                                    new JsonSerializerOptions { WriteIndented = true }));
        Assert.Empty(world.EgoCollisions);
    }

    private static void Check(HighwayWorld world, List<Trace> trace, string name)
    {
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports", "Highway"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "Highway", $"{name}.json"),
            JsonSerializer.Serialize(trace.Where((_, i) => i % 5 == 0), new JsonSerializerOptions { WriteIndented = true }));

        Assert.Empty(world.EgoCollisions);
        var left = trace.Where(t => t.Y > 3.0).ToList();
        Assert.NotEmpty(left);                                                   // it pulled out
        Assert.Contains(trace, t => t.Y > 3.3 && t.X > t.TruckX + 6);            // it passed the truck in the left lane
        var last = trace[^1];
        Assert.True(last.Y < 0.2, $"back in the right lane at the end: y = {last.Y:0.00}");
        Assert.True(last.X > last.TruckX + 30, "ahead of the truck at the end");
        Assert.True(world.HardestBrakingForEgo < 4.0, $"nobody had to brake hard for the CAV: {world.HardestBrakingForEgo:0.0} m/s2");
        Assert.True(world.NearestToEgo > 1.5, $"nearest approach {world.NearestToEgo:0.0} m");
    }
}
