using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams;
using Mpai.Cav.Ess;
using Mpai.Cav.Map;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// A CAV THAT COMES THE OTHER WAY, and what it tells: its Full Environment Description of what it perceives - exactly, here, within
// its reach - sent as an Ego-Remote AMS Message each 200 ms to the CAV that is to hear it.
public sealed class RemoteCav
{
    private readonly Mpai.Cav.Ams.FullEnvironmentDescription fed;
    public HighwayVehicle Vehicle { get; }

    public RemoteCav(HighwayVehicle vehicle, RoadMap map)
    {
        Vehicle = vehicle;
        fed = new Mpai.Cav.Ams.FullEnvironmentDescription(AmsProvider.Fed, new Dictionary<string, string> { ["CAVID"] = vehicle.CavId! });
        fed.Know(JsonNode.Parse(map.ToOfflineMapObject(0))!);
    }

    // What it sends at this step, if it is time.
    public string? Report(HighwaySimulation sim, long ms, double reach) =>
        fed.ToSend(fed.Describe(HighwayTruthBed.OfVehicle(sim, Vehicle, ms, reach)))?.ToJsonString();

    // What a connected CAV tells: where it is going - a Sender Info message of the Ego-Remote AMS Message, the Trajectory it intends to adopt
    // (for the seconds given), for a staged vehicle the one its path and its speeds make.
    public string Intent(long ms, double seconds) => new JsonObject
    {
        ["Header"] = FullEnvironmentDescription.Remote, ["EgoRemoteAMSMessageID"] = $"{Vehicle.CavId}-INT{ms}", ["CAVIdentifier"] = Vehicle.CavId,
        ["EgoRemoteAMSMessageTime"] = EssJson.SimpleTime($"{Vehicle.CavId}-INT{ms}-T", ms),
        ["Message"] = new JsonObject { ["EgoCAVID"] = Vehicle.CavId, ["Trajectory"] = Plan(Vehicle, ms, seconds) }
    }.ToJsonString();

    // Where a staged vehicle will be in the next seconds, if it goes on as its path and its speeds say: a Trajectory, a point each 0.1 s.
    public static JsonObject Plan(HighwayVehicle v, long ms, double seconds)
    {
        var points = new JsonArray(); var s = v.PathS; var id = $"{v.CavId}-PLAN{ms}";
        for (var k = 0; k <= (int)(seconds * 10); k++)
        {
            var speed = v.Profile!.At(s); var (x, y, h) = v.Path!.At(s);
            var point = EssJson.Attitude($"{id}-{k}", ms + k * 100, (x, y, 0), (0.5, 0.5, 0.1), (speed * Math.Cos(h), speed * Math.Sin(h), 0));
            points.Add(new JsonObject { ["ExpectedSpaceTime"] = EssJson.SpaceTime($"{id}-{k}-ST", ms + k * 100, point) });
            s += speed * 0.1;
        }
        return new JsonObject { ["Header"] = "OSD-TRJ-V1.5", ["TrajectoryID"] = id, ["TrajectoryTime"] = EssJson.SimpleTime(id + "-T", ms), ["Trajectory"] = points };
    }
}

// THE TWO-WAY ROAD (the author, 2026/10/12): a two-lane road, a lane for each direction. The CAV is held up by a slow truck, and
// cannot pass it: what comes the other way is hidden by the truck, and the lane is long enough to meet it only if nothing is
// within the stretch the pass takes. What the CAVs that come the other way tell it - where they are, and what they see ahead of
// them - lets it know that the lane is free for as long as it needs: it pulls out when it is, passes and returns; without it it
// stays behind the truck.
public static class OncomingScenarios
{
    // The CAV at 28 m/s behind a truck at 20 m/s, 100 m ahead; coming the other way, a car (not a CAV) 500 m ahead at 24 m/s, which
    // the truck hides, a CAV 900 m ahead which sees that car and says so, and another CAV 2.5 km ahead.
    public static HighwayWorld TruckAndOncomingTraffic()
    {
        var world = HighwayWorld.Scenario([
            HighwayWorld.Vehicle("Truck", 2.4 + 100 + 6, 0, 20, truck: true),
            HighwayWorld.Vehicle("Oncoming", 508, 1, 24, paint: CameraRenderer.Blue, oncoming: true),
            HighwayWorld.Vehicle("CAV-1", 908, 1, 24, paint: CameraRenderer.White, oncoming: true, cav: "CAV-1"),
            HighwayWorld.Vehicle("CAV-2", 2500, 1, 24, paint: (40, 40, 44), oncoming: true, cav: "CAV-2")], HighwayScenarios.Planned - 2);
        world.NpcsChangeLane = false;
        return world;
    }
}

[Collection(Timing.Name)]
public class HighwayOncomingTests
{
    public sealed record Trace(double T, double X, double Y, double Speed, double TruckX, double Oncoming, double Meet);

    // Drives the scenario, the CAV on perfect perception of what its own sensors can see - the truck hides what is behind it, and
    // nothing is seen beyond 150 m - and, if cooperate, hearing the CAVs that come the other way.
    public static (List<Trace> Trace, HighwayWorld World) Drive(HighwayWorld world, int seconds, bool cooperate)
    {
        var sim = new HighwaySimulation(world, HighwayScenarios.Planned);
        var settings = HighwayScenarios.Settings(sim, opposingLane: true);
        var remotes = world.Others.Where(v => v.CavId is not null).Select(v => new RemoteCav(v, sim.Map)).ToList();
        var trace = new List<Trace>();
        try
        {
            using var cav = new CavInTheLoop(sim.Map, "H1", settings, withEss: false);
            double held = 0; double? north = null;
            for (var step = 0; step < seconds * 10 && !sim.Collided; step++)
            {
                var sensed = sim.Sense(camera: false);
                if (cooperate)
                    foreach (var remote in remotes)
                        if (remote.Report(sim, sensed.FrameMs, 1500) is { } era) cav.Hear(era);
                var (_, command, _, _) = cav.StepOnBed(HighwayTruthBed.Of(sim, sensed.FrameMs, occlude: true, sightRange: 150), [], sensed.FrameMs);
                if (command is not null) (held, north) = HighwayOvertakingTests.Asked(command);

                // How near the vehicle coming the other way that would meet the ego first is: metres between the bumpers, and
                // seconds to meeting it, at the speeds they have.
                var ego = world.Ego; var front = ego.X + ego.Length / 2;
                double gap = 9999, meet = 9999;
                foreach (var v in world.Others.Where(v => v.Direction < 0 && v.X - v.Length / 2 > front))
                {
                    var g = v.X - v.Length / 2 - front;
                    if (g < gap) { gap = g; meet = g / Math.Max(0.1, ego.Speed + v.Speed); }
                }
                trace.Add(new Trace(world.Time, ego.X, ego.Y, ego.Speed, world.Others.First(v => v.Id == "Truck").X, gap, meet));
                sim.Advance(held, north);
            }
        }
        finally { try { File.Delete(settings); } catch { } }
        return (trace, world);
    }

    private static void Report(string name, List<Trace> trace)
    {
        Directory.CreateDirectory(Path.Combine(Repository.Root, "Test", "Reports", "Highway"));
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "Highway", $"{name}.json"),
            JsonSerializer.Serialize(trace.Where((_, i) => i % 5 == 0), new JsonSerializerOptions { WriteIndented = true }));
    }

    // Alone, the CAV cannot know that the lane is free for as long as the pass takes: it stays behind the truck, at its speed, and
    // nothing happens to it.
    [Fact]
    [Trait("Duration", "Long")]
    public void WithoutTheOtherCavsItStaysBehindTheTruck()
    {
        var world = OncomingScenarios.TruckAndOncomingTraffic();
        var (trace, _) = Drive(world, 90, cooperate: false);
        Report("oncoming-alone", trace);
        Assert.Empty(world.EgoCollisions);
        Assert.True(trace.Max(t => t.Y) < 0.3, $"it left its lane: y = {trace.Max(t => t.Y):0.0} m");
        Assert.True(trace[^1].Speed < 22, $"it follows the truck: {trace[^1].Speed:0.0} m/s");
        Assert.True(trace[^1].X < trace[^1].TruckX, "behind the truck at the end");
    }

    // Told by the CAVs that come the other way, it waits while the car they see, and they, go by; and when the lane is free for
    // what the pass needs it pulls out, passes the truck and returns - the vehicle that would meet it first, nowhere near.
    [Fact]
    [Trait("Duration", "Long")]
    public void TheOtherCavsLetItPassTheTruckSafely()
    {
        var world = OncomingScenarios.TruckAndOncomingTraffic();
        var (trace, _) = Drive(world, 100, cooperate: true);
        Report("oncoming-cooperating", trace);

        Assert.Empty(world.EgoCollisions);
        var out0 = trace.First(t => t.Y > 1.0);
        Assert.True(out0.Oncoming > 500, $"it pulled out with {out0.Oncoming:0} m to the vehicle coming - the pass needs far more than its own sight");
        Assert.True(out0.T > 15, $"it waited while the traffic it was told of went by: pulled out at {out0.T:0.0} s");
        Assert.Contains(trace, t => t.Y > 3.3 && t.X > t.TruckX + 6);               // it passed the truck in the other lane
        var last = trace[^1];
        Assert.True(last.Y < 0.2, $"back in its lane at the end: y = {last.Y:0.00}");
        Assert.True(last.X > last.TruckX + 30, "ahead of the truck at the end");

        // In the other lane, the vehicle coming was always far off: in time and in distance.
        var inLane = trace.Where(t => t.Y > 1.75).ToList();
        Assert.True(inLane.Min(t => t.Meet) > 8, $"nearest meeting while out: {inLane.Min(t => t.Meet):0.0} s");
        Assert.True(inLane.Min(t => t.Oncoming) > 100, $"nearest vehicle coming while out: {inLane.Min(t => t.Oncoming):0} m");
        Assert.True(world.NearestToEgo > 1.5, $"nearest approach {world.NearestToEgo:0.0} m");
    }
}
