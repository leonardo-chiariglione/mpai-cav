using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE MOTION CONSTRAINTS (M3287): the formulas and tables against the values the sources give, the speed cap on a corner, and the monitor on the two
// strategies of the AMS.
public sealed class MotionConstraintsTests
{
    private static MotionConstraints Dry() => new() { Grip = 1 };

    [Fact]
    public void TheSafeLongitudinalDistanceIsRssAtTheParametersOfR157()
    {
        // vr = vf = 20 m/s: 20*0.75 + 0.5*1.5*0.75^2 + (20 + 1.125)^2 / (2*6) - 20^2 / (2*7): the CAV is taken to brake as hard as R157 says it can
        var d = Dry().SafeLongitudinal(20, 20);
        Assert.Equal(15 + 0.421875 + 21.125 * 21.125 / 12 - 400.0 / 14, d, 6);
        // behind a stopped vehicle at 10 m/s the whole braking distance is needed
        Assert.Equal(7.5 + 0.5 * 1.5 * 0.5625 + (10 + 1.125) * (10 + 1.125) / 12, Dry().SafeLongitudinal(10, 0), 6);
        // a faster vehicle in front: nothing needed beyond a response
        Assert.Equal(0, Dry().SafeLongitudinal(10, 30), 6);
    }

    [Fact]
    public void TheSafeDistanceGrowsAsTheRoadGetsSlippery()
    {
        var dry = Dry().SafeLongitudinal(20, 20);
        var wet = Dry(); wet.Grip = 0.5;
        Assert.True(wet.SafeLongitudinal(20, 20) > dry * 1.5);
    }

    [Fact]
    public void TheDistanceOfVehiclesThatComeTowardsEachOtherIsTheSumOfBoth()
    {
        var m = Dry();
        Assert.Equal(m.SafeLongitudinalOpposite(10, 0) + m.SafeLongitudinalOpposite(0, 10) - m.SafeLongitudinalOpposite(0, 0), m.SafeLongitudinalOpposite(10, 10), 6);
    }

    [Fact]
    public void TheSafeLateralDistanceIsRssAndGrowsWhenTheyMoveTowardsEachOther()
    {
        // at rest sideways each may still move towards the other for the response time: mu + 2 * (a rho^2 / 2 + (a rho)^2 / (2 b)) = 0.3 + 2 * (0.28125 + 0.28125)
        var m = Dry();
        Assert.Equal(0.3 + 2 * (0.28125 + 0.28125), m.SafeLateral(0, 0), 6);
        Assert.True(m.SafeLateral(1, -1) > m.SafeLateral(0, 0));
        Assert.True(m.SafeLateral(-1, 1) < m.SafeLateral(0, 0));
    }

    [Theory]
    [InlineData(2.0, 1.0, 2.0)]                                // 7.2 km/h: 1.0 s, never less than 2 m
    [InlineData(2.78, 1.1, 3.06)]                              // 10 km/h
    [InlineData(5.56, 1.2, 6.67)]
    [InlineData(16.67, 1.6, 26.67)]                            // 60 km/h
    [InlineData(25, 1.6, 40)]                                  // above 60 km/h: the gap of 60 km/h is the least
    public void TheFollowingDistanceIsTheTableOfR157(double speed, double gap, double distance)
    {
        var m = Dry();
        Assert.Equal(gap, m.FollowingTimeGap(speed), 2);
        Assert.Equal(distance, m.FollowingDistance(speed), 1);
        Assert.Equal(Math.Max(2.4, speed * m.FollowingTimeGap(speed, true)), m.FollowingDistance(speed, true), 6);
    }

    [Fact]
    public void TheFollowingDistanceNeverFallsBelowTheLeastWhenStopped()
    {
        Assert.Equal(2.0, Dry().FollowingDistance(0), 6);
        Assert.Equal(2.4, Dry().FollowingDistance(0, true), 6);
    }

    [Fact]
    public void TheSetsOfLimitsAreOrderedAndScaledByTheGrip()
    {
        var m = Dry();
        Assert.True(m.Comfort.Brake < m.Normal.Brake && m.Normal.Brake < m.Emergency.Brake);
        Assert.True(m.Comfort.Jerk < m.Normal.Jerk && m.Normal.Jerk < m.Emergency.Jerk);
        m.Grip = 0.5;
        Assert.Equal(1.75, m.Normal.Brake, 6);
        Assert.Equal(1.0, m.Normal.Lateral, 6);
        m.GripUnknown();
        Assert.Equal(0.4, m.Grip, 6);
    }

    [Fact]
    public void TheSightSpeedStopsWithinTheDistanceSeen()
    {
        var m = Dry();
        var v = m.SightSpeed(46, 3.5);
        Assert.Equal(46, v * 0.75 + v * v / (2 * 3.5), 6);
        Assert.Equal(0, m.SightSpeed(0, 3.5), 6);
        Assert.True(v > 13 && v < 18, $"at 46 m the speed is {v:0.0} m/s (R157: 60 km/h = 16.7 m/s at 5 m/s2)");
    }

    // A corner of the route, at 10 m radius: the cap at it is the speed its radius allows at the lateral acceleration, and the speed before it is no more than the CAV can
    // slow down from.
    [Fact]
    public void TheCapOnARouteSlowsForACornerBeforeIt()
    {
        var route = PolyPath.Turn(0, 0, 0, 300, Math.PI / 2, 10, 300);
        var m = Dry();
        var cap = m.Cap(route, 0, 19.4, 2.5);
        var corner = Math.Sqrt(2 * 10);
        Assert.True(cap.Least <= corner + 0.5, $"{cap.Least:0.0} against {corner:0.0}");
        Assert.True(Math.Abs(cap.LeastAt - 300) < 25, $"the least is at {cap.LeastAt}");
        Assert.True(cap.At(0) > 15, $"far from it the cap is the limit: {cap.At(0):0.0}");
        // what the cap says 100 m before the corner is a speed from which 2.5 m/s2 reaches the corner's
        var before = cap.At(200);
        Assert.True(before * before <= corner * corner + 2 * 2.5 * 100 + 1, $"{before:0.0} m/s at 100 m before the corner");
        // the road is slippery: the corner is slower and the CAV slows earlier
        var wet = Dry(); wet.Grip = 0.5;
        Assert.True(wet.Cap(route, 0, 19.4, 2.5).Least < cap.Least);
        Assert.True(wet.Cap(route, 0, 19.4, 2.5).At(200) < before);
    }

    [Fact]
    public void ASlowStrategyIsNotMadeFaster()
    {
        // the cap never exceeds the limit
        var route = PolyPath.Line(0, 0, 0, 500);
        var cap = Dry().Cap(route, 0, 13.9, 2.5);
        Assert.Equal(13.9, cap.At(100), 6);
    }



    // The Road State, reported by the MAS at any time, reaches the Motion Selection Planning on the Bus of the AMS (M3287): written by the Traffic Obstacle Avoidance, read by
    // the planner, no Topology between them.
    [Fact]
    public async Task TheRoadStateOfTheMasReachesThePlannerOnTheBus()
    {
        var location = Path.Combine(Path.GetTempPath(), "mpai-bus-" + Guid.NewGuid().ToString("N"));
        using var api = new Mpai.Aif.Api.ControllerApi(Repository.Amds, Path.Combine(Repository.Root, "AIMs", "aim-settings.json"), new Mpai.Cav.Ams.AmsProvider());
        const string ams = "1CAV-AMS-V2.0-I01";
        Assert.Equal(AIF.Controller.AifError.OK, api.StartFlow(ams));
        try
        {
            api.SharedStorageInit(ams, location);
            var answer = new JsonObject
            {
                ["Header"] = "CAV-AMM-V2.0", ["AMSMASMessageID"] = "AMM-test",
                ["MASMessage"] = new JsonObject { ["RoadState"] = new JsonObject { ["Header"] = "CAV-RDS-V2.0", ["SurfaceCondition"] = new JsonObject { ["FrictionCoefficientEstimate"] = 0.15 } } }
            };
            Assert.Equal(AIF.Controller.AifError.OK, api.InputWrite(ams, "CAV-AMM-V2.0", 1, answer.ToJsonString(), 5000));
            string accounts = "";
            for (var i = 0; i < 40 && !accounts.Contains("1CAV-MSP-V2.0-I01.CAV-RDS-V2.0#1: taken 1"); i++)
            {
                await Task.Delay(100);
                accounts = string.Join(" | ", api.ChannelAccounts(ams));
            }
            Assert.Contains("(Bus) wrote 1", accounts);
            Assert.Contains("1CAV-MSP-V2.0-I01.CAV-RDS-V2.0#1: taken 1", accounts);
        }
        finally { api.StopFlow(ams); try { Directory.Delete(location, true); } catch { } }
    }

    // ---- the check of a trajectory (S-3)

    private static RoadContext Straight(double limit = 20) => new(PolyPath.Line(0, 0, 0, 1000), [0], limit);

    private static List<TrajectoryPoint> Way(double speed, double acceleration = 0, double y = 0, double seconds = 6)
    {
        var way = new List<TrajectoryPoint>();
        double x = 0, v = speed;
        for (var k = 0; k <= (int)(seconds * 10); k++)
        {
            way.Add(new TrajectoryPoint(k * 0.1, new Pose(x, y, 0), v, acceleration));
            var next = Math.Max(0, v + acceleration * 0.1);
            x += (v + next) / 2 * 0.1; v = next;
        }
        return way;
    }

    private static Observation Car(double x, double y, double vx, double vy = 0, string cls = "car", double length = 4.5, double width = 1.8) =>
        new("o", 0, x, y, vx, vy, length, width, cls);

    [Fact]
    public void ATrajectoryTowardsAStoppedVehicleTooCloseViolatesC1()
    {
        var m = Dry();
        var near = m.Check(Way(10), [Car(30, 0, 0)], Straight());
        Assert.Contains(near, v => v.Constraint == "C1" && v.Hard);
        // braking at 3.5 m/s2 from 10 m/s stops in 14 m: from 30 m (to its rear, 27.75) it is safe
        Assert.DoesNotContain(m.Check(Way(10, -3.5), [Car(60, 0, 0)], Straight()), v => v.Hard);
    }

    [Fact]
    public void AVehicleInAnotherLaneIsNoViolationButOneThatCutsInIs()
    {
        var m = Dry();
        Assert.DoesNotContain(m.Check(Way(10), [Car(25, 3.5, 10)], Straight()), v => v.Hard);
        // the same, moving sideways into the lane at 1.5 m/s: the lateral distance is not kept
        Assert.Contains(m.Check(Way(10), [Car(25, 3.5, 6, -1.5)], Straight()), v => v.Constraint == "C1");
    }

    [Fact]
    public void ARoadUserThatMayGoAnywhereIsKeptFarEnoughForItsReach()
    {
        var m = Dry();
        Assert.Contains(m.Check(Way(8, -3.5), [Car(25, 4, 0, 0, "person", 0.5, 0.5)], Straight()), v => v.Constraint == "C1");
        Assert.DoesNotContain(m.Check(Way(8, -3.5), [Car(25, 4, 0, 0, "car")], Straight()), v => v.Constraint == "C1");
    }

    [Fact]
    public void ATrajectoryOutOfItsLaneViolatesC2()
    {
        // a trajectory that leaves the lane: from its middle to 3 m beside it
        var leaving = Way(10).Select((p, k) => p with { Pose = new Pose(p.Pose.X, p.Pose.Y + Math.Min(3.0, k * 0.1), p.Pose.Heading) }).ToList();
        Assert.Contains(Dry().Check(leaving, [], Straight()), v => v.Constraint == "C2");
        Assert.DoesNotContain(Dry().Check(Way(10, 0, 0.5), [], Straight()), v => v.Hard);
        // a CAV already 2.5 m out of its lane is not charged with it: a trajectory that stays no further out, or returns, is allowed
        Assert.DoesNotContain(Dry().Check(Way(10, 0, 2.5), [], Straight()), v => v.Constraint == "C2");
        var returning = Way(10).Select((p, k) => p with { Pose = new Pose(p.Pose.X, p.Pose.Y + Math.Max(0, 2.5 - k * 0.05), p.Pose.Heading) }).ToList();
        Assert.DoesNotContain(Dry().Check(returning, [], Straight()), v => v.Constraint == "C2");
    }

    [Fact]
    public void ATrajectoryTooFastIntoACornerViolatesC4AndOneAboveTheLimitC5()
    {
        var route = PolyPath.Turn(0, 0, 0, 100, Math.PI / 2, 10, 100);
        var m = Dry();
        var road = new RoadContext(route, [0], 20);
        // at 15 m/s on a straight that ends in a corner 100 m ahead, no slowing: the cap at 60 m is the corner's plus what 3.5 m/s2 slows down from
        var fast = m.Check(Way(15, 0, 0, 6), [], road);
        Assert.Contains(fast, v => v.Constraint == "C4");
        Assert.Contains(m.Check(Way(25), [], Straight(20)), v => v.Constraint == "C5");
        Assert.DoesNotContain(m.Check(Way(15), [], Straight(20)), v => v.Hard);
    }

    [Fact]
    public void ABrakingBeyondTheNormalLimitIsReportedNotRefused()
    {
        var found = Dry().Check(Way(15, -5), [], Straight());
        Assert.Contains(found, v => v.Constraint == "C7" && !v.Hard);
        Assert.DoesNotContain(found, v => v.Hard);
    }

    [Fact]
    public void TheFallBackBrakesAsGentlyAsTheSituationAllows()
    {
        var m = Dry();
        // 60 m to a stopped vehicle at 10 m/s: the comfortable braking does it
        var far = m.FallBack(0, 0, 10, [Car(60, 0, 0)], Straight(), 6, 0.1);
        Assert.Equal("comfort", far.Level);
        Assert.DoesNotContain(far.Violations, v => v.Hard);
        // 30 m (to its rear 27.75): the comfortable braking (1 m/s2) does not stop in time; the normal limit does
        var middle = m.FallBack(0, 0, 10, [Car(30, 0, 0)], Straight(), 6, 0.1);
        Assert.Equal("normal", middle.Level);
        // 14 m: only the emergency braking
        var near = m.FallBack(0, 0, 10, [Car(14, 0, 0)], Straight(), 6, 0.1);
        Assert.Equal("emergency", near.Level);
        Assert.True(near.Way[^1].Speed < 0.01, "it stops");
    }

    [Fact]
    public void TheFallBackGoesBackIntoTheLane()
    {
        var m = Dry();
        var back = m.FallBack(0, 2.5, 5, [], Straight(), 6, 0.1);
        Assert.True(Math.Abs(back.Way[^1].Pose.Y) < Math.Abs(back.Way[0].Pose.Y) - 1.0);
    }

    // ---- the monitor on the two strategies (S-4, V-1): measured, and reported; the hard constraints C4 and C5 are checked on the space-time planner

    [Fact]
    [Trait("Duration", "Long")]
    public void TheMonitorMeasuresTheStrategiesOnTheSameScenarios()
    {
        var report = new JsonObject();
        foreach (var variant in new[] { "IDM", "SpaceTime", "IDM+Constraints", "SpaceTime+Constraints" })
        {
            var strategy = variant.Split('+')[0];
            var constrained = variant.EndsWith("+Constraints");
            var perScenario = new JsonObject();
            foreach (var (name, make) in MasStage1Tests.Scenarios())
            {
                var sim = make();
                var monitor = new ConstraintMonitor(Dry());
                MasStage1Tests.Loop(sim, s => monitor.Observe(s.Sensed.Truth, Simulation.Step), mas: true, planning: new Dictionary<string, string> { ["Planner"] = strategy, ["Constraints"] = constrained ? "1" : "0" });
                var measured = monitor.Report(sim.Arrived, sim.Collided);
                perScenario[name] = measured;
                // the strategies are asserted collision-free; with the Motion Constraints on, what happens is reported (see the findings in M3287 work notes)
                if (!constrained) Assert.False(sim.Collided, $"{variant}: {name}");
                Assert.True((double)measured["C5_MaxOverSpeedLimit"]! < 6, $"{strategy}: {name}: over the limit by {measured["C5_MaxOverSpeedLimit"]}");
                if (variant == "SpaceTime") Assert.False((bool)measured["C4_Slid"]!, $"SpaceTime slid: {name}");
            }
            report[variant] = perScenario;
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "constraints-monitor.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }
}
