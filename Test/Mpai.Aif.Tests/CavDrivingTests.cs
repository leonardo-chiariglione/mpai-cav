using Mpai.Cav.Driving;
using Mpai.Cav.DrivingSim;

namespace Mpai.Aif.Tests;

// N-19 (night of 2026-11-12): the new driving core of the CAV, each step on its own.
//   Step 1 - the scene (SceneDescriber): road users kept apart over time, their motion estimated, placed on the map; hidden areas.
//   Step 2 - the motion of the others (MotionPredictor): hypotheses with probabilities; what the CAV cannot see is assumed.
//   Step 3 - to the destination without collisions (Driver): the hard rule, pedestrians first.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class CavDrivingTests
{
    private static RoadMap TwoWay(double length = 600, double width = 3.5, double limit = 13.9)
    {
        var map = new RoadMap();
        var e = map.Add(new Lane { Id = "E", Centre = Polyline.Straight(new(0, 0), new(length, 0)), Width = width, SpeedLimit = limit });
        var w = map.Add(new Lane { Id = "W", Centre = Polyline.Straight(new(length, width), new(0, width)), Width = width, SpeedLimit = limit });
        e.Oncoming = "W"; w.Oncoming = "E";
        return map;
    }

    private static readonly Dictionary<string, (double, double)> AllSeen = new() { ["E"] = (0, 600), ["W"] = (0, 600) };

    private static SensorFrame Frame(double t, Vec2 ego, double egoSpeed, params Detection[] d) => new(t, ego, 0, egoSpeed, d, AllSeen);

    // A junction: E-W road, a lane from the West-bound lane turning left (South) at x = 500.
    private static RoadMap Junction()
    {
        var map = new RoadMap();
        map.Add(new Lane { Id = "Ea", Centre = Polyline.Straight(new(0, 0), new(490, 0)) });
        map.Add(new Lane { Id = "JE", Centre = Polyline.Straight(new(490, 0), new(510, 0)), Kind = LaneKind.Junction });
        map.Add(new Lane { Id = "Eb", Centre = Polyline.Straight(new(510, 0), new(1000, 0)) });
        map.Add(new Lane { Id = "Wa", Centre = Polyline.Straight(new(1000, 3.5), new(510, 3.5)) });
        map.Add(new Lane { Id = "JW", Centre = Polyline.Straight(new(510, 3.5), new(490, 3.5)), Kind = LaneKind.Junction });
        map.Add(new Lane { Id = "Wb", Centre = Polyline.Straight(new(490, 3.5), new(0, 3.5)) });
        map.Add(new Lane { Id = "JWS", Centre = Polyline.Arc(new(510, -6.5), 10, Math.PI / 2, Math.PI), Kind = LaneKind.Junction, SpeedLimit = 6 });
        map.Add(new Lane { Id = "Sout", Centre = Polyline.Straight(new(500, -6.5), new(500, -300)) });
        map.Connect("Ea", "JE"); map.Connect("JE", "Eb"); map.Connect("Wa", "JW", "JWS"); map.Connect("JW", "Wb"); map.Connect("JWS", "Sout");
        return map;
    }

    private static DescribedRoadUser User(int id, RoadUserKind kind, Vec2 p, Vec2 v, string? lane, double s, double d = 0, double lateral = 0, int indicator = 0,
                                          double acceleration = 0, double seenFor = 3, double length = 4.5, double width = 1.8) =>
        new(id, kind, p, v, v.Length > 0.3 ? v.Heading : 0, acceleration, length, width, lane, s, d, lateral, indicator, v.Length < 0.3, seenFor);

    private static Scene SceneOf(RoadMap map, Vec2 ego, double egoHeading, double egoSpeed, string egoLane, IReadOnlyList<DescribedRoadUser> users,
                                 IReadOnlyList<HiddenArea>? hidden = null, Dictionary<string, (double, double)>? vis = null)
    {
        var (s, d) = map.Lanes[egoLane].Centre.Project(ego);
        vis ??= map.Lanes.ToDictionary(l => l.Key, l => (0.0, l.Value.Centre.Length));
        return new Scene(0, new EgoState(ego, egoHeading, egoSpeed, egoLane, s, d), users, hidden ?? [], vis, []);
    }

    // ---------- Step 1: the scene ----------

    [Fact]
    public void Step1_ARoadUserIsKeptApartOverTimeAndItsMotionEstimated()
    {
        var map = TwoWay();
        var describer = new SceneDescriber(map);
        var rng = new Random(3);
        Scene? scene = null;
        for (int k = 0; k <= 30; k++)
        {
            double t = k * 0.1;
            var car = new Vec2(50 + 10 * t + Sensor.Gauss(rng) * 0.15, Sensor.Gauss(rng) * 0.15);
            scene = describer.Describe(Frame(t, new Vec2(0, 0), 0, new Detection(RoadUserKind.Car, car, 0, 4.5, 1.8)));
        }
        var u = Assert.Single(scene!.RoadUsers);
        Assert.Equal(RoadUserKind.Car, u.Kind);
        Assert.Equal("E", u.LaneId);
        Assert.InRange(u.Speed, 9.5, 10.5);
        Assert.InRange(u.Position.X, 79, 81);
        Assert.False(u.Stationary);
    }

    [Fact]
    public void Step1_AVanParkedBesideTheLaneHidesAnAreaAndAStoppedCarOnTheLaneDoesNot()
    {
        var map = TwoWay();
        Scene Last(Vec2 at)
        {
            var describer = new SceneDescriber(map);
            Scene? s = null;
            for (int k = 0; k <= 10; k++) s = describer.Describe(Frame(k * 0.1, new Vec2(150, 0), 10, new Detection(RoadUserKind.Truck, at, 0, 6, 2.2)));
            return s!;
        }
        var parked = Last(new Vec2(200, -3.8));
        Assert.True(Assert.Single(parked.RoadUsers).Stationary);
        Assert.NotEmpty(parked.HiddenAreas);
        Assert.All(parked.HiddenAreas, h => Assert.True(h.TowardRoad.Y > 0.9));      // toward the road (North)
        Assert.Empty(Last(new Vec2(200, 0)).HiddenAreas);
    }

    [Fact]
    public void Step1_TheSceneSaysWhatKindOfPlaceTheCavIsNear()
    {
        var map = Contexts.RoundaboutMap();
        var scene = new SceneDescriber(map).Describe(new SensorFrame(0, map.Lanes["In3"].Centre.At(120), Math.PI / 2, 10, [], new Dictionary<string, (double, double)>()));
        Assert.Equal("In3", scene.Ego.LaneId);
        Assert.Contains("roundabout", scene.Features);
    }

    // ---------- Step 2: the motion of the others ----------

    [Fact]
    public void Step2_ACarBeforeAJunctionMayTurnTheIndicatorSaysWhich()
    {
        var map = Junction();
        var predictor = new MotionPredictor(map);
        var ego = new Vec2(300, 0);
        var car = (int indicator) => User(1, RoadUserKind.Car, new Vec2(560, 3.5), new Vec2(-10, 0), "Wa", 440, indicator: indicator, acceleration: -1.2);

        var signalled = predictor.Predict(SceneOf(map, ego, 0, 13, "Ea", [car(1)])).Single(p => p.RoadUserId == 1);
        Assert.StartsWith("turns left", signalled.MostLikely.Label);

        var silent = predictor.Predict(SceneOf(map, ego, 0, 13, "Ea", [car(0)])).Single(p => p.RoadUserId == 1);
        Assert.Contains(silent.Hypotheses, h => h.Label.StartsWith("turns left") && h.Probability >= 0.1);
        Assert.Contains(silent.Hypotheses, h => h.Label.StartsWith("goes on") && h.Probability >= 0.1);
        Assert.InRange(silent.Hypotheses.Sum(h => h.Probability), 0.999, 1.001);
    }

    [Fact]
    public void Step2_ACarMovingAcrossItsLaneMayChangeLane()
    {
        var map = new RoadMap();
        map.Add(new Lane { Id = "R", Centre = Polyline.Straight(new(0, 0), new(1000, 0)), SpeedLimit = 25 });
        map.Add(new Lane { Id = "L", Centre = Polyline.Straight(new(0, 3.5), new(1000, 3.5)), SpeedLimit = 25 });
        map.SideBySide("L", "R");
        var cutter = User(1, RoadUserKind.Car, new Vec2(80, 3.0), new Vec2(18, -0.9), "L", 80, d: -0.5, lateral: -0.9);
        var p = new MotionPredictor(map).Predict(SceneOf(map, new Vec2(60, 0), 0, 22, "R", [cutter])).Single(x => x.RoadUserId == 1);
        var change = Assert.Single(p.Hypotheses, h => h.Label == "changes lane to the right");
        Assert.True(change.Probability >= 0.3, $"p {change.Probability:F2}");
        Assert.InRange(change.At(3).Position.Y, -0.5, 0.5);            // in the CAV's lane in 3 s
    }

    [Fact]
    public void Step2_APedestrianAtTheKerbMayStepOutLessSoTheLongerItStands()
    {
        var map = TwoWay();
        var predictor = new MotionPredictor(map);
        var fresh = User(1, RoadUserKind.Pedestrian, new Vec2(100, -3.0), new Vec2(0, 0), null, 0, seenFor: 0.5, length: 0.5, width: 0.5);
        var waiting = fresh with { SeenFor = 4 };
        double StepOut(DescribedRoadUser u) =>
            predictor.Predict(SceneOf(map, new Vec2(50, 0), 0, 10, "E", [u])).Single(x => x.RoadUserId == 1).Hypotheses.Where(h => h.Label == "steps into the road").Sum(h => h.Probability);
        Assert.True(StepOut(fresh) >= 0.1);
        Assert.True(StepOut(waiting) < 0.1);
    }

    [Fact]
    public void Step2_ACarThatSlowsAtAGiveWayLineGivesWay()
    {
        var map = Contexts.RoundaboutMap();
        var lane = map.Lanes["In1"];
        double s = lane.Centre.Length - 15;
        var at = lane.Centre.At(s);
        var dir = Vec2.FromHeading(lane.Centre.HeadingAt(s));
        var slowing = User(1, RoadUserKind.Car, at, dir * 4, "In1", s, acceleration: -1.5);
        var ego = map.Lanes["B0"].Centre.At(5);
        var p = new MotionPredictor(map).Predict(SceneOf(map, ego, map.Lanes["B0"].Centre.HeadingAt(5), 6, "B0", [slowing])).Single(x => x.RoadUserId == 1);
        Assert.Equal("gives way at the line", p.MostLikely.Label);
        Assert.True(p.MostLikely.Probability >= 0.9);
        Assert.True(p.MostLikely.At(6).Position.DistanceTo(lane.Centre.At(lane.Centre.Length)) > 4);   // it waits before the ring
    }

    [Fact]
    public void Step2_WhatTheCavCannotSeeIsAssumed()
    {
        var map = TwoWay();
        var vis = new Dictionary<string, (double, double)> { ["E"] = (0, 190), ["W"] = (300, 600) };   // W runs from x = 600: seen from x = 300 to 0
        var hidden = new[] { new HiddenArea(9, new Vec2(203.3, -2.7), new Vec2(0, 1)) };
        var preds = new MotionPredictor(map).Predict(SceneOf(map, new Vec2(170, 0), 0, 10, "E", [], hidden, vis));
        Assert.Contains(preds, p => p.Assumed && p.Kind == RoadUserKind.Obstacle && p.Hypotheses[0].Points[0].Position.X > 160);   // stopped beyond the view
        Assert.Contains(preds, p => p.Assumed && p.Kind == RoadUserKind.Car && p.Hypotheses[0].Points[1].Position.X < p.Hypotheses[0].Points[0].Position.X);   // coming the other way
        Assert.Contains(preds, p => p.Assumed && p.Kind == RoadUserKind.Pedestrian);   // from behind the van
    }

    // ---------- Step 3: to the destination without collisions ----------

    [Fact]
    public void Step3_OnAFreeRoadTheCavReachesItsDestinationWithinTheSpeedLimit()
    {
        var map = TwoWay(limit: 13.9);
        var run = Simulation.Run(new Scenario { Context = "test", Variant = "free", Map = map, Agents = [], StartLane = "E", StartS = 20, StartSpeed = 0, DestLane = "E", DestS = 400, MaxTime = 60 });
        Assert.Equal("arrived", run.Outcome.Result);
        Assert.All(run.Timeline, s => Assert.True(s.Speed <= 13.9 + 0.2, $"{s.Speed} at {s.T}"));
        Assert.Equal(0, run.Outcome.EmergencyStops);
    }

    [Fact]
    public void Step3_WhenNoMotionIsFreeOfCollisionTheCavStops()
    {
        var map = TwoWay();
        var ped = User(1, RoadUserKind.Pedestrian, new Vec2(108, 0), new Vec2(0, 0), null, 0, length: 0.5, width: 0.5);
        var scene = SceneOf(map, new Vec2(100, 0), 0, 14, "E", [ped]);
        var driver = new Driver(map, "E", "E", 500);
        var d = driver.Decide(scene, new MotionPredictor(map).Predict(scene));
        Assert.True(d.EmergencyStop);
        Assert.Equal(-driver.EmergencyBraking, d.Acceleration);
        Assert.StartsWith("no collision-free motion", d.Reason);
    }

    [Fact]
    public void Step3_PedestriansFirst_AClearanceEnoughBesideACarIsNotEnoughBesideAPedestrian()
    {
        var map = TwoWay();
        var driver = new Driver(map, "E", "E", 500);
        var path = Polyline.Straight(new Vec2(125, 0), new Vec2(225, 0), 1);     // it passes the road user (at x = 130) at 10 m/s
        Prediction Standing(RoadUserKind kind, double y, double length, double width) =>
            new(1, kind, length, width, false, [new Hypothesis("stays", 1, Enumerable.Range(0, 25).Select(k => new PredictedPoint(k * 0.25, new Vec2(130, y), 0, 0)).ToList(), 0.1, 0)]);
        // the same 0.8 m between the bodies: beside a parked car, beside a pedestrian
        double carY = -(driver.EgoWidth / 2 + 0.9 + 0.1 + 0.8), pedY = -(driver.EgoWidth / 2 + 0.25 + 0.05 + 0.8);
        Assert.Null(driver.Conflict(path, 10, 0, [Standing(RoadUserKind.Car, carY, 4.5, 1.8)], 13.9, new Vec2(125, 0), 0));
        Assert.NotNull(driver.Conflict(path, 10, 0, [Standing(RoadUserKind.Pedestrian, pedY, 0.5, 0.5)], 13.9, new Vec2(125, 0), 0));
    }

    [Fact]
    public void Step3_APedestrianSteppingOutFromBehindAVanIsNotHit()
    {
        var run = Simulation.Run(Contexts.All().Single(s => s.Context.StartsWith("8-") && s.Variant == "steps-out-at-12m"));
        Assert.Equal("arrived", run.Outcome.Result);
        Assert.True(run.Outcome.Closest["2 Pedestrian"] >= 1.0, $"closest {run.Outcome.Closest["2 Pedestrian"]}");
    }
}
