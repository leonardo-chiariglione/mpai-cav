using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Recordings;

using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// ROAD USERS THAT MAY GO ANYWHERE (the author, 2026/11/01: the CAV does not collide with what is around it - cars, pedestrians, cyclists - and where it
// cannot be sure it does not, it stops). A pedestrian or a cyclist is a disc of where it can be, which what is perceived of it verifies; the CAV keeps clear of
// every disc over its horizon, or takes the way that stays clearest at the hardest braking.
[Trait("Group", "Fast")]
public class RoadUserTests(ITestOutputHelper output)
{
    private const double Pavement = -3.0;           // m to the left of the right lane's centreline: beside the kerb

    private static (HighwayWorld World, PlannerDrive Drive) Drive(IEnumerable<HighwayVehicle> others, double egoSpeed, IReadOnlyList<double>? lanes = null, double limit = 14)
    {
        var world = HighwayWorld.Scenario(others, egoSpeed: egoSpeed);
        world.NpcsChangeLane = false;
        return (world, new PlannerDrive(world, lanes ?? [0], limit: limit));
    }

    [Fact]
    public void APedestrianWhoStepsOffTheKerbIsNeverHit()
    {
        var (world, drive) = Drive([HighwayWorld.RoadUser("P1", "person", 90, Pavement, Math.PI / 2, 1.5, movesFrom: 3, metres: 12)], 14);
        double slowest = 99;
        drive.Run(30, () => { slowest = Math.Min(slowest, world.Ego.Speed); return world.Ego.X > 130; });
        output.WriteLine($"ego x {world.Ego.X:F0} v {world.Ego.Speed:F1}, slowest {slowest:F1}, nearest {world.NearestToEgo:F2} m, hardest braking {drive.HardestBraking:F1}, unpredicted {drive.Tracker.Unpredicted.Count}");
        foreach (var e in drive.Events) output.WriteLine(e);
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.NearestToEgo > 0.3, $"nearest {world.NearestToEgo:F2}");
        Assert.True(world.Ego.X > 100, $"it went on past the pedestrian: x {world.Ego.X:F0}");
    }

    [Fact]
    public void ACyclistInTheCavsLaneIsNeverHitAndIsFollowedOrPassed()
    {
        var (world, drive) = Drive([HighwayWorld.RoadUser("C1", "bicycle", 50, -0.8, 0, 5, movesFrom: 0, metres: 3000)], 14, [0, HighwayRoad.LaneWidth]);
        drive.Run(40);
        output.WriteLine($"ego x {world.Ego.X:F0} y {world.Ego.Y:F1} v {world.Ego.Speed:F1}, cyclist x {world.Others[0].X:F0}, nearest {world.NearestToEgo:F2} m, hardest braking {drive.HardestBraking:F1}");
        foreach (var e in drive.Events) output.WriteLine(e);
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.NearestToEgo > 0.3, $"nearest {world.NearestToEgo:F2}");
    }

    [Fact]
    public void APedestrianWhoComesOutFromBehindAParkedCarIsNeverHit()
    {
        // Seen only from 25 m, and steps out at 7 s, as the CAV comes up to it, at the pace of a jog: the CAV is at 14 m/s, and can stop within 25 m only at its hardest braking.
        var (world, drive) = Drive([HighwayWorld.RoadUser("P2", "person", 120, Pavement, Math.PI / 2, 2.5, movesFrom: 7, metres: 12, seenWithin: 25)], 14);
        drive.Run(30, () => world.Ego.X > 160);
        output.WriteLine($"ego x {world.Ego.X:F0} v {world.Ego.Speed:F1}, nearest {world.NearestToEgo:F2} m, hardest braking {drive.HardestBraking:F1}");
        foreach (var e in drive.Events) output.WriteLine(e);
        Assert.Empty(world.EgoCollisions);
    }
}
