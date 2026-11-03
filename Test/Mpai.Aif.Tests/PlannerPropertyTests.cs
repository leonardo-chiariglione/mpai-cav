using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Recordings;

using Xunit.Abstractions;

namespace Mpai.Aif.Tests;

// THE GENERAL PLANNER, BY PROPERTIES (the author, 2026/10/29: we cannot train the CAV for each situation: it follows one logic). Instead of a list of
// situations, random ones - random traffic, random junction encounters - and what must hold in all of them, whatever the situation:
//   no collision; the destination is reached (the CAV goes on); what it asks of its vehicle stays within what the vehicle can do; what it makes the
//   others do is no more than a driver would do. The seeds: MPAI_PLANNER_SEEDS (default 6).
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
[Trait("Duration", "Long")]
public class PlannerPropertyTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Seeds()
    {
        var n = int.TryParse(Environment.GetEnvironmentVariable("MPAI_PLANNER_SEEDS"), out var k) ? k : 6;
        for (var i = 1; i <= n; i++) yield return [i];
    }

    // RANDOM TRAFFIC on the two lanes: drivers with their own speeds, who follow and change lane. The CAV goes two minutes.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomTrafficOnTheHighway(int seed)
    {
        var rng = new Random(seed * 17 + 3);
        var options = new TrafficOptions(FlowPerLane: 500 + rng.Next(0, 1000), TruckShare: 0.05 + 0.25 * rng.NextDouble());
        var world = new HighwayWorld(seed, options, egoSpeed: 24 + 8 * rng.NextDouble());
        var drive = new PlannerDrive(world, [0, HighwayRoad.LaneWidth], seed: seed, road: new HighwayRoadModel(null, twoWay: false));
        drive.Run(120);
        output.WriteLine($"seed {seed}, {options.FlowPerLane:F0} vehicles/h, {options.TruckShare:P0} trucks: ego {world.Ego.X / world.Time:F1} m/s on average, x {world.Ego.X:F0}, nearest {world.NearestToEgo:F1} m, " +
                         $"others braked at most {world.HardestBrakingForEgo:F1}, ego {drive.HardestBraking:F1} m/s2, worst planning {drive.WorstStepMilliseconds:F0} ms, collisions {world.EgoCollisions.Count}");
        foreach (var e in drive.Events) output.WriteLine("  " + e);
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.Ego.X > 120 * 15, $"it went on: {world.Ego.X:F0} m in 120 s");
        Assert.True(world.HardestBrakingForEgo <= 6, $"others had to brake {world.HardestBrakingForEgo:F1} m/s2");
    }

    // RANDOM ENCOUNTERS AT THE JUNCTION: a vehicle that comes the other way and turns across the CAV's way, timed to meet it more or less, turning left or
    // right, coming fast or slow - the CAV is told nothing.
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomEncountersAtTheJunction(int seed)
    {
        var rng = new Random(seed * 101 + 11);
        var earlierBy = -4 + 8 * rng.NextDouble();
        var turn = rng.Next(2) == 0 ? Turn.Left : Turn.Right;
        var approach = 9 + 9 * rng.NextDouble();
        var world = IntersectionScenarios.OncomingLeftTurn(earlierBy, signals: false, connected: false, turn: turn, approach: approach);
        var junction = world.Junction!;
        var drive = new PlannerDrive(world, [0], seed: seed, road: new HighwayRoadModel(junction, twoWay: true));
        drive.Run(90, () => world.Ego.X > junction.X + 60);
        output.WriteLine($"seed {seed}: {turn} turn, earlier by {earlierBy:+0.0;-0.0} s, approach {approach:F0} m/s: nearest {world.NearestToEgo:F1} m, ego braked at most {drive.HardestBraking:F1} m/s2, x {world.Ego.X:F0} at {world.Time:F0} s");
        Assert.Empty(world.EgoCollisions);
        Assert.True(world.Ego.X > junction.X + 20, "it went through");
    }
}
