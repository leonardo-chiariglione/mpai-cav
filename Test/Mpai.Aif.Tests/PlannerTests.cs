using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Recordings;

using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// THE GENERAL PLANNER (the author, 2026/10/29): the CAV predicts the trajectories of the cars it perceives, verifies them against what it goes
// on perceiving, and computes its own trajectory to its destination without colliding with anything in the scene. These are the first
// situations, in the highway world, where the truth is known: what is perceived is the other vehicles' places and velocities and nothing of
// what they intend.
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class PlannerTests(ITestOutputHelper output)
{
    private static HighwayVehicle Car(string id, double x, int lane, double speed, bool truck = false, bool oncoming = false) =>
        HighwayWorld.Vehicle(id, x, lane, speed, truck, oncoming: oncoming);

    [Fact]
    public void AnEmptyRoadIsDrivenAtTheSpeedLimit()
    {
        var world = HighwayWorld.Scenario([], egoSpeed: 20);
        var drive = new PlannerDrive(world, [0]);
        drive.Run(30);
        output.WriteLine($"speed {world.Ego.Speed:F1} m/s, x {world.Ego.X:F0}, planning {drive.WorstStepMilliseconds:F1} ms at the worst");
        Assert.InRange(world.Ego.Speed, 32, 33.5);
        Assert.Empty(world.EgoCollisions);
    }

    [Fact]
    public void ASlowCarAheadIsFollowedAtItsSpeedAndNeverHit()
    {
        var world = HighwayWorld.Scenario([Car("L", 90, 0, 20)], egoSpeed: 30);
        var drive = new PlannerDrive(world, [0]);
        drive.Run(60);
        var lead = world.Others[0];
        output.WriteLine($"ego {world.Ego.Speed:F1} m/s, leader {lead.Speed:F1}, gap {lead.X - world.Ego.X - 4.65:F1} m, nearest {world.NearestToEgo:F1} m, hardest braking {drive.HardestBraking:F1}");
        Assert.Empty(world.EgoCollisions);
        Assert.True(Math.Abs(world.Ego.Speed - lead.Speed) < 2.5, $"ego {world.Ego.Speed:F1}, leader {lead.Speed:F1}");      // the leader's speed drifts, as a driver's does
        Assert.True(world.NearestToEgo > 4, $"nearest {world.NearestToEgo:F1}");
    }

    [Fact]
    public void ACarAheadIsPassedWhereTheLaneBesideItIsFree()
    {
        var world = HighwayWorld.Scenario([Car("L", 120, 0, 20)], egoSpeed: 30);
        var drive = new PlannerDrive(world, [0, HighwayRoad.LaneWidth]);
        drive.Run(60);
        var lead = world.Others[0];
        output.WriteLine($"ego x {world.Ego.X:F0} y {world.Ego.Y:F1}, leader x {lead.X:F0}; nearest {world.NearestToEgo:F1} m");
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.Ego.X > lead.X + 20, "it passed");
    }

    [Fact]
    public void ACarAheadIsNotPassedWhereTheLaneBesideItIsBusy()
    {
        var world = HighwayWorld.Scenario([Car("L", 100, 0, 20), Car("F", -60, 1, 38), Car("B", 80, 1, 28)], egoSpeed: 30);
        world.NpcsChangeLane = false;
        var drive = new PlannerDrive(world, [0, HighwayRoad.LaneWidth]);
        drive.Run(25);
        output.WriteLine($"ego x {world.Ego.X:F0} y {world.Ego.Y:F1} v {world.Ego.Speed:F1}; nearest {world.NearestToEgo:F1} m");
        Assert.Empty(world.EgoCollisions);
    }

    [Fact]
    public void AStoppedCarAheadIsStoppedBehind()
    {
        var world = HighwayWorld.Scenario([Car("S", 400, 0, 0)], egoSpeed: 30);
        var drive = new PlannerDrive(world, [0]);
        drive.Run(60);
        output.WriteLine($"ego x {world.Ego.X:F1} v {world.Ego.Speed:F2}, nearest {world.NearestToEgo:F1} m, hardest braking {drive.HardestBraking:F1}");
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.Ego.Speed < 0.5);
        Assert.True(world.NearestToEgo > 1.5);
    }

    [Fact]
    public void ACarThatBrakesHardAheadIsNotHit()
    {
        // The leader goes at 30 m/s along its lane and, 200 m on, brakes to a stop in 100 m: 4.5 m/s2, harder than the planner takes a car to.
        var lead = HighwayWorld.Scripted("L", new HighwayPath(60, 0, 0).Line(2000), new SpeedProfile((0, 30), (200, 30), (300, 0)), Turn.None);
        var world = HighwayWorld.Scenario([lead], egoSpeed: 30);
        var drive = new PlannerDrive(world, [0]);
        drive.Run(30);
        output.WriteLine($"ego {world.Ego.Speed:F1} m/s; nearest {world.NearestToEgo:F1} m; hardest braking {drive.HardestBraking:F1}");
        Assert.Empty(world.EgoCollisions);
    }

    // THE JUNCTION: an oncoming vehicle that turns across the CAV's way, timed to meet it - the CAV is told nothing: it sees where the vehicle is and how it
    // moves, as a vehicle that goes on straight, or may turn, until what it goes on seeing says which. Not a light, no message.
    [Theory]
    [InlineData(-3.0, Turn.Left)]
    [InlineData(-1.5, Turn.Left)]
    [InlineData(0.0, Turn.Left)]
    [InlineData(1.5, Turn.Left)]
    [InlineData(3.0, Turn.Left)]
    [InlineData(0.0, Turn.Right)]
    [InlineData(2.0, Turn.Right)]
    public void AnOncomingVehicleThatTurnsAcrossTheWayIsNotHit(double earlierBy, Turn turn)
    {
        var world = IntersectionScenarios.OncomingLeftTurn(earlierBy, signals: false, connected: false, turn: turn);
        var junction = world.Junction!;
        var drive = new PlannerDrive(world, [0], road: new HighwayRoadModel(junction, twoWay: true));
        double minimum = double.MaxValue, slowest = double.MaxValue;
        drive.Run(75, () =>
        {
            if (world.Ego.X > junction.X - 150) { minimum = Math.Min(minimum, world.NearestToEgo); slowest = Math.Min(slowest, world.Ego.Speed); }
            return world.Ego.X > junction.X + 60;
        });
        output.WriteLine($"earlier {earlierBy:+0.0;-0.0} {turn}: ego x {world.Ego.X:F0} (junction {junction.X}), nearest {world.NearestToEgo:F1} m, slowest {slowest:F1} m/s, hardest braking {drive.HardestBraking:F1}, at {world.Time:F0} s, worst planning {drive.WorstStepMilliseconds:F0} ms");
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.Ego.X > junction.X + 20, "it went through");
    }

    [Fact]
    public void DiagnoseTheLeftTurn()
    {
        var world = IntersectionScenarios.OncomingLeftTurn(0, signals: false, connected: false, turn: Turn.Left);
        var junction = world.Junction!;
        var drive = new PlannerDrive(world, [0], road: new HighwayRoadModel(junction, twoWay: true));
        var other = world.Others[0];
        drive.Run(75, () =>
        {
            if (world.Ego.X > junction.X - 140 && world.StepNumber % 1 == 0)
            {
                var tr = drive.Tracker.Objects.FirstOrDefault();
                var kinds = tr is null ? "-" : string.Join(",", tr.Hypotheses.Select(h => h.Kind));
                output.WriteLine($"t {world.Time:F1} ego x {world.Ego.X:F0} v {world.Ego.Speed:F1} a {drive.Last?.Acceleration:F1} clear {drive.Last?.Clear} feas {drive.Last?.Feasible}/{drive.Last?.Candidates} clr {drive.Last?.Clearance:F1} why {drive.Last?.Why} | car x {other.X:F0} y {other.Y:F1} v {other.Speed:F1} | {kinds}");
            }
            return world.Ego.X > junction.X + 60;
        });
    }

    [Fact]
    public void DiagnoseThePass()
    {
        var world = HighwayWorld.Scenario([Car("L", 120, 0, 20)], egoSpeed: 30);
        var drive = new PlannerDrive(world, [0, HighwayRoad.LaneWidth]);
        for (var k = 0; k < 600; k++)
        {
            drive.Step();
            if (k % 10 == 0 && k < 450) output.WriteLine($"t {world.Time:F1} ego x {world.Ego.X:F0} y {world.Ego.Y:F1} v {world.Ego.Speed:F1} a {drive.Last?.Acceleration:F1} tgt {drive.Last?.TargetOffset:F1} clr {drive.Last?.Clearance:F1} clear {drive.Last?.Clear} | lead x {world.Others[0].X:F0} v {world.Others[0].Speed:F1} why {drive.Last?.Why}");
        }
    }
}
