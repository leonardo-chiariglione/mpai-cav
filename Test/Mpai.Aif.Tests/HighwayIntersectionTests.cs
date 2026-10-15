using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE JUNCTION (the author, 2026/10/14): the two-way road of the oncoming round, with a road that crosses it at right angles. A
// vehicle turns - to the left or to the right - across the CAV's way, or into it, and the CAV is to understand that it turns: from
// what the vehicle says of itself (the CAV is connected: its route in the Ego-Remote AMS Message) or from its indicator, which the
// CAV sees; or it does not, and is told nothing - the same turn, three ways of knowing it.
public static class IntersectionScenarios
{
    // AN ONCOMING VEHICLE THAT GOES STRAIGHT ON through the junction, a CAV that tells its route: the control.
    public static HighwayWorld OncomingStraight()
    {
        var junction = new Junction(JunctionX);
        var path = new HighwayPath(JunctionX + 900, HighwayRoad.LaneCentre(1), Math.PI).Line(3000);
        var world = HighwayWorld.Scenario([HighwayWorld.Scripted("Straight", path, new SpeedProfile((0, 14), (3000, 14)), Turn.None, paint: CameraRenderer.Blue, cav: "CAV-T")], EgoSpeed);
        world.Junction = junction;
        world.NpcsChangeLane = false;
        return world;
    }

    public const double JunctionX = 1500;               // far enough for the CAV to be at its speed long before it first hears of the turn
    public const double EgoSpeed = 28;
    public const double TurnSpeed = 7;

    // Where the path of a vehicle crosses the ego's lane (y = 0), as a distance along the path, or null.
    public static double? CrossesEgoLane(HighwayPath path)
    {
        double? last = null;
        for (var s = 0.0; s <= path.Length; s += 0.05)
        {
            var (_, y, _) = path.At(s);
            if (last is { } l && l > 0.9 && y <= 0.9) return s;     // where its body (a car is 1.8 m wide) reaches the lane
            last = y;
        }
        return null;
    }

    // The time a vehicle takes to go from a distance along its path to another, at the speeds of the profile.
    public static double TimeFrom(SpeedProfile profile, double from, double to)
    {
        double t = 0;
        for (var s = from; s < to; s += 0.05) t += 0.05 / Math.Max(0.5, profile.At(s));
        return t;
    }

    // The time a vehicle takes to go from the start of its path to the distance, at the speeds of the profile.
    public static double TimeTo(SpeedProfile profile, double distance)
    {
        double t = 0;
        for (double s = 0; s < distance; s += 0.05) t += 0.05 / Math.Max(0.5, profile.At(s));
        return t;
    }

    // AN ONCOMING VEHICLE THAT TURNS LEFT ACROSS THE CAV'S WAY. It approaches at 14 m/s, slows to the speed of the turn and turns,
    // timed so that it reaches the CAV's lane as the CAV does - plus the seconds given: 0, the two meet. It signals from 40 m before
    // the turn (the indicator lit 1.5 times a second); said, it is a CAV that tells its route.
    public static HighwayWorld OncomingLeftTurn(double earlierBy = 0, bool signals = true, bool connected = true, Turn turn = Turn.Left, double approach = 14, double? egoArrives = null)
    {
        var junction = new Junction(JunctionX);
        var egoAt = egoArrives ?? (junction.X + 0.6) / EgoSpeed;                     // the time the CAV's front reaches the lane's crossing
        HighwayPath PathFor(double startX) => junction.Oncoming(startX, turn).Path;
        SpeedProfile ProfileFor(double length, double turnBegin) => new(
            (0, approach), (turnBegin - 30, approach), (turnBegin, TurnSpeed), (turnBegin + 12, TurnSpeed), (turnBegin + 40, 12), (length, 12));

        // The start that makes it arrive when the CAV does: bisection on how far it has to go.
        double lo = junction.WestboundLeftTurnStartsAt + 5, hi = junction.WestboundLeftTurnStartsAt + 2500;
        for (var i = 0; i < 40; i++)
        {
            var mid = (lo + hi) / 2;
            var path = PathFor(mid); var begin = path.TurnSpan().Begin;
            var profile = ProfileFor(path.Length, begin);
            var arrives = TimeTo(profile, CrossesEgoLane(path) ?? path.Length);
            if (arrives > egoAt - earlierBy) hi = mid; else lo = mid;
        }
        var start = (lo + hi) / 2;
        var p = PathFor(start);
        var vehicle = HighwayWorld.Scripted("Turning", p, ProfileFor(p.Length, p.TurnSpan().Begin), signals ? turn : Turn.None, signalAhead: 40,
            paint: CameraRenderer.Blue, cav: connected ? "CAV-T" : null);
        var world = HighwayWorld.Scenario([vehicle], EgoSpeed);
        world.Junction = junction;
        // The CAV's speed settles in the first half minute; between 25 and 30 s the vehicle is timed to the arrival it then has - the CAV has not
        // heard of the turn yet, nor can it see the vehicle - and not after: what the CAV does about it is its own.
        var crossing = CrossesEgoLane(p) ?? p.Length; var conflictX = junction.X + 0.6;
        world.Staging = w =>
        {
            if (w.Time < 25 || w.Time > 30) return;
            var car = w.Others[0];
            var egoEta = (conflictX - w.Ego.X - w.Ego.Length / 2) / Math.Max(1, w.Ego.Speed) - earlierBy;
            var carEta = TimeFrom(vehicle.Profile!, car.PathS, crossing) / Math.Max(0.5, car.SpeedFactor);
            car.SpeedFactor = Math.Clamp(car.SpeedFactor * (1 + 0.3 * (carEta - egoEta) / Math.Max(1.0, egoEta)), 0.5, 2.0);
        };
        world.NpcsChangeLane = false;
        return world;
    }
}

[Collection(Timing.Name)]
public class HighwayIntersectionTests
{
    public sealed record Trace(double T, double X, double Speed, double Acceleration, double Gap);

    // What the CAV knows of the vehicle that turns: nothing but where it is and how it moves; or, connected, where it says it will go; or its
    // indicator, which it sees lit (within 150 m); or the same from the picture: the ESS looks at the front camera and at the telephoto window.
    public enum Knows { Nothing, Route, Lights, Picture }

    // Drives the scenario for the seconds given on perfect perception of what is in sight (200 m): the CAV's AMS, no ESS. The
    // vehicle that turns, where it is a CAV, tells the CAV its route (the Trajectory it follows) when it is to.
    public static (List<Trace> Trace, HighwayWorld World) Drive(HighwayWorld world, int seconds, Knows knows, Action<double, JsonNode?>? onBed = null)
    {
        var sim = new HighwaySimulation(world, HighwayScenarios.Planned);
        var settings = HighwayScenarios.Settings(sim);
        var patched = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        patched["1CAV-PSP-V2.0-I01"]!["LaneChange"] = "0";                  // it keeps its lane; what it does is its speed
        patched["1CAV-BED-V2.0-I01"]!["ClosingSpeed"] = "45";               // a vehicle coming the other way closes in 40 m/s: its young track keeps up
        patched["1CAV-BED-V2.0-I01"]!["YoungBeta"] = "0.6";                 // and learns its speed fast
        File.WriteAllText(settings, patched.ToJsonString());
        var remotes = world.Others.Where(v => v.CavId is not null).Select(v => new RemoteCav(v, sim.Map)).ToList();
        var trace = new List<Trace>();
        try
        {
            using var cav = new CavInTheLoop(sim.Map, "H1", settings, withEss: knows == Knows.Picture);
            double held = 0; double? north = null;
            for (var step = 0; step < seconds * 10 && !sim.Collided; step++)
            {
                var sensed = sim.Sense(camera: knows == Knows.Picture, rear: false, detail: knows == Knows.Picture);
                if (knows == Knows.Route)
                    foreach (var remote in remotes)
                    {
                        if (remote.Report(sim, sensed.FrameMs, 1500) is { } era) cav.Hear(era);
                        if (step % 2 == 0) cav.Hear(remote.Intent(sensed.FrameMs, 14));      // where it is going, each 200 ms
                    }
                JsonNode? command, bed;
                if (knows == Knows.Picture) (_, command, _, bed) = cav.StepWithBed(sensed);
                else (_, command, _, bed) = cav.StepOnBed(HighwayTruthBed.Of(sim, sensed.FrameMs, occlude: false, sightRange: 200, indicatorRange: knows == Knows.Lights ? 150 : null), [], sensed.FrameMs);
                onBed?.Invoke(world.Time, bed);
                if (command is not null) (held, north) = HighwayOvertakingTests.Asked(command);
                var ego = world.Ego; var gap = world.Others.Count == 0 ? 9999 : world.Others.Min(v => HighwayWorld.Separation(ego, v));
                trace.Add(new Trace(world.Time, ego.X, ego.Speed, ego.Acceleration, gap));
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

    private static HighwayWorld Left(double earlierBy = 0, bool connected = true, bool signals = true) =>
        IntersectionScenarios.OncomingLeftTurn(earlierBy, signals, connected);

    // It sees the indicator of the vehicle, though the vehicle tells nothing: it knows the turn from that, as soon as it is lit and near enough
    // to be read (150 m: at the speeds the two have, three and a half seconds before they meet) - late, and it brakes hard; but it does.
    [Fact]
    [Trait("Duration", "Long")]
    public void SeeingTheIndicatorItLetsTheVehicleTurnAcrossItsWayThoughLate()
    {
        var world = Left(connected: false);
        var (trace, _) = Drive(world, 70, Knows.Lights);
        Report("junction-left-turn-lights", trace);
        Assert.Empty(world.EgoCollisions);
        Assert.True(trace.Min(t => t.Gap) > 1.0, $"nearest approach {trace.Min(t => t.Gap):0.0} m");
        var braking = trace.Where(t => t.T > 30 && t.Acceleration < -0.2).ToList();
        Assert.NotEmpty(braking);
        // The car is first in sight, its indicator lit, within 150 m: not before.
        Assert.True(braking[0].X > IntersectionScenarios.JunctionX - 200, $"it braked at x = {braking[0].X:0} m, {IntersectionScenarios.JunctionX - braking[0].X:0} m from the junction: not before it could see the indicator");
    }

    // A connected vehicle that goes straight on, its route told: nothing in it for the CAV, which holds its speed (the turn is not seen
    // where there is none).
    [Fact]
    [Trait("Duration", "Long")]
    public void ToldThatTheVehicleGoesStraightOnItHoldsItsSpeed()
    {
        var world = IntersectionScenarios.OncomingStraight();
        var (trace, _) = Drive(world, 70, Knows.Route);
        Report("junction-straight-route", trace);
        Assert.Empty(world.EgoCollisions);
        Assert.True(trace.Where(t => t.T > 30).Min(t => t.Speed) > 28.5, $"slowest after 30 s: {trace.Where(t => t.T > 30).Min(t => t.Speed):0.0} m/s");
    }

    // THE ESS READS THE INDICATOR FROM THE PICTURE: the front camera sees the car only when it is near; the telephoto window sees it far, and sees
    // its indicator lit - amber at the corner of its front. The CAV knows the turn as it did when it was given the indicator: it does not collide,
    // and it does not brake before the indicator was seen.
    [SkippableFact]
    [Trait("Group", "Models")]
    [Trait("Duration", "Long")]
    public void ReadingTheIndicatorFromThePictureItLetsTheVehicleTurnAcrossItsWay()
    {
        Skip.IfNot(File.Exists(Path.Combine(Repository.Root, "Models", "yolox_s.onnx")), "Models/yolox_s.onnx is absent: the model files are obtained separately.");
        var world = Left(connected: false);
        var firstRead = double.NaN; var distanceThen = double.NaN; var lit = 0;
        var (trace, _) = Drive(world, 70, Knows.Picture, (time, bed) =>
        {
            var seen = bed?["BasicEnvironmentObjects"]?.AsArray().FirstOrDefault(o => o?["TurnIndicator"] is not null);
            if (seen is null) return;
            lit++;
            if (double.IsNaN(firstRead)) { firstRead = time; distanceThen = (double)seen["SpatialAttitude"]!["Position"]!["CartPosition"]![0]!; }
        });
        Report("junction-left-turn-picture", trace);
        Assert.True(lit > 3, $"the ESS saw the indicator in {lit} sets of descriptors");
        Assert.Empty(world.EgoCollisions);
        Assert.True(trace.Min(t => t.Gap) > 1.0, $"nearest approach {trace.Min(t => t.Gap):0.0} m; the indicator was first read at {firstRead:0.0} s, {distanceThen:0} m ahead");
        Assert.True(distanceThen > 60, $"the indicator was first read {distanceThen:0} m ahead");
    }

    // Told nothing, the CAV goes on until the vehicle is in its lane: what it can do then is what its brakes can.
    [Fact]
    [Trait("Duration", "Long")]
    public void NotToldItMeetsTheVehicleThatTurnsAcrossItsWay()
    {
        var world = Left(connected: false);
        var (trace, _) = Drive(world, 70, Knows.Nothing);
        Report("junction-left-turn-alone", trace);
        Assert.True(world.EgoCollisions.Count > 0 || trace.Min(t => t.Gap) < 2.0, $"nearest approach {trace.Min(t => t.Gap):0.0} m");
    }

    // Told its route, the CAV slows as soon as the turn is known, no harder than it takes - and the two do not meet.
    [Fact]
    [Trait("Duration", "Long")]
    public void ToldItsRouteItLetsTheVehicleTurnAcrossItsWay()
    {
        var world = Left();
        var (trace, _) = Drive(world, 70, Knows.Route);
        Report("junction-left-turn-route", trace);
        Assert.Empty(world.EgoCollisions);
        Assert.True(trace.Min(t => t.Gap) > 2.0, $"nearest approach {trace.Min(t => t.Gap):0.0} m");
        var hardest = trace.Where(t => t.T > 2).MinBy(t => t.Acceleration)!;
        Assert.True(hardest.Acceleration > -3.5, $"hardest braking {-hardest.Acceleration:0.0} m/s2 at {hardest.T:0.0} s, x = {hardest.X:0} m, {hardest.Speed:0.0} m/s");
    }

    // The staged turn is real: a CAV that does not know of it, and goes on at its speed, meets the vehicle that turns across its way.
    [Fact]
    public void ACarThatGoesOnMeetsTheVehicleTurningAcrossItsWay()
    {
        var world = IntersectionScenarios.OncomingLeftTurn();
        var turning = world.Others[0];
        var lit = 0; double firstSignal = -1, turned = -1;
        for (var step = 0; step < 1000 && world.EgoCollisions.Count == 0; step++)
        {
            if (turning.Signals != Turn.None && firstSignal < 0) firstSignal = world.Time;
            if (turning.Indicator(world.Time) == Turn.Left) lit++;
            if (turned < 0 && Math.Abs(turning.Heading - Math.PI) > 0.2) turned = world.Time;
            world.EgoAcceleration = 0;
            world.Advance();
        }
        Assert.NotEmpty(world.EgoCollisions);
        Assert.True(firstSignal >= 0 && turned > firstSignal + 2, $"it signals {turned - firstSignal:0.0} s before it turns");
        Assert.True(lit > 10, "the indicator blinks");
        Assert.True(world.EgoCollisions[0].Time > 48 && world.EgoCollisions[0].Time < 60, $"the meeting at {world.EgoCollisions[0].Time:0.0} s");
    }

    // Timed to arrive a good time before the CAV, it is not in the way.
    [Fact]
    public void TurningEarlierItIsOutOfTheWay()
    {
        var world = IntersectionScenarios.OncomingLeftTurn(earlierBy: 4);
        for (var step = 0; step < 1000; step++) { world.EgoAcceleration = 0; world.Advance(); }
        Assert.Empty(world.EgoCollisions);
    }
}
