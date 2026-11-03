using Mpai.Cav.Ams.Planning;
using Mpai.Cav.Recordings;

namespace Mpai.Aif.Tests;

// THE ROADS OF THE HIGHWAY AS THE CAV KNOWS THEM (its Offline Map): the lanes, and where the cross road is - the ways a vehicle may go on.
public sealed class HighwayRoadModel(Junction? junction, bool twoWay) : IRoadModel
{
    public IReadOnlyList<(string Name, PolyPath Path)> Continuations(double x, double y, double heading)
    {
        var list = new List<(string, PolyPath)>();
        var east = Math.Abs(Math.IEEERemainder(heading, 2 * Math.PI)) < Math.PI / 4;
        var west = Math.Abs(Math.IEEERemainder(heading - Math.PI, 2 * Math.PI)) < Math.PI / 4;
        if (!east && !west) return list;
        var lane = east && twoWay ? 0 : west ? 1 : (int)Math.Clamp(Math.Round(y / HighwayRoad.LaneWidth), 0, HighwayRoad.Lanes - 1);
        var laneY = HighwayRoad.LaneCentre(lane);
        list.Add(("Lane", PolyPath.Line(x, laneY, east ? 0 : Math.PI, 500, 10)));
        if (junction is not null && twoWay)
        {
            if (east && lane == 0)
            {
                foreach (var (left, start) in new[] { (true, junction.EastboundLeftTurnStartsAt), (false, junction.EastboundRightTurnStartsAt) })
                    if (start > x) list.Add((left ? "Left" : "Right", PolyPath.Turn(x, laneY, 0, start - x, left ? Math.PI / 2 : -Math.PI / 2, junction.TurnRadius, 300)));
            }
            else if (west)
            {
                foreach (var (left, start) in new[] { (true, junction.WestboundLeftTurnStartsAt), (false, junction.WestboundRightTurnStartsAt) })
                    if (start < x) list.Add((left ? "Left" : "Right", PolyPath.Turn(x, laneY, Math.PI, x - start, left ? Math.PI / 2 : -Math.PI / 2, junction.TurnRadius, 300)));
            }
        }
        return list;
    }
}

// THE CAV DRIVEN BY THE GENERAL PLANNER, in the highway world: each step it perceives what its sensors give of the other vehicles (their places and
// velocities, with the noise a perception has, within the reach of its sensors; nothing of what they intend), the tracker predicts and verifies,
// the planner computes the trajectory, and the first step of it - an acceleration and a lane - is what the vehicle does.
public sealed class PlannerDrive
{
    private readonly Random noise;
    private readonly PolyPath route;
    private readonly IReadOnlyList<double> lanes;
    private readonly double reachAhead, reachBehind, positionNoise, velocityNoise, limit;

    public HighwayWorld World { get; }
    public ObjectTracker Tracker { get; }
    public SpaceTimePlanner Planner { get; }
    public Plan? Last { get; private set; }
    public double HardestBraking { get; private set; }
    public double Time => World.Time;
    public double StepMilliseconds { get; private set; }
    public double WorstStepMilliseconds { get; private set; }
    public List<Told> Told { get; } = [];
    public List<string> Events { get; } = [];                      // what the CAV did that was hard, and what was around it

    public PlannerDrive(HighwayWorld world, IReadOnlyList<double> lanes, double limit = 33, int seed = 1, IRoadModel? road = null,
                        double reachAhead = 200, double reachBehind = 100, double positionNoise = 0.3, double velocityNoise = 0.2, PlannerSettings? settings = null)
    {
        World = world; this.lanes = lanes; this.limit = limit;
        (this.reachAhead, this.reachBehind, this.positionNoise, this.velocityNoise) = (reachAhead, reachBehind, positionNoise, velocityNoise);
        noise = new Random(seed * 31 + 7);
        route = PolyPath.Line(-200, 0, 0, 200_000, 50);
        Tracker = new ObjectTracker(road, acceleration: 3);
        Planner = new SpaceTimePlanner(settings);
    }

    private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - noise.NextDouble())) * Math.Cos(2 * Math.PI * noise.NextDouble());

    // What the sensors give: the other vehicles near enough, each place and velocity with its noise.
    public List<Observation> Perceive()
    {
        var ego = World.Ego;
        var seen = new List<Observation>();
        foreach (var o in World.Others)
        {
            var dx = o.X - ego.X;
            if (dx > reachAhead || dx < -reachBehind || Math.Abs(o.Y - ego.Y) > 60) continue;
            if (Math.Sqrt(dx * dx + Math.Pow(o.Y - ego.Y, 2)) > o.SeenWithin) continue;       // not yet out from behind what hides it
            if (noise.NextDouble() < 0.02) continue;                                   // a detection missed now and then
            var (vx, vy) = o.VelocityVector;
            seen.Add(new Observation(o.Id, World.Time, o.X + positionNoise * Gauss(), o.Y + positionNoise * Gauss(),
                                     vx + velocityNoise * Gauss(), vy + velocityNoise * Gauss(), o.Length, o.Width, o.Class));
        }
        return seen;
    }

    public void Step()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ego = World.Ego;
        Tracker.Update(World.Time, Perceive());
        foreach (var told in Told) Tracker.Hear(told);
        var state = new EgoState(World.Time, ego.X, ego.Y, ego.Heading, ego.Speed);
        var plan = Planner.Next(state, route, limit, Tracker.Objects, lanes);
        Last = plan;
        World.EgoAcceleration = plan.Acceleration;
        World.EgoTargetY = plan.TargetOffset;
        HardestBraking = Math.Max(HardestBraking, -plan.Acceleration);
        if (plan.Acceleration < -5 && Events.Count < 12)
            Events.Add($"t {World.Time:F1}: ego x {ego.X:F0} y {ego.Y:F1} v {ego.Speed:F1} a {plan.Acceleration:F1} clear {plan.Clear} {plan.Why} [{plan.Detail}] keep: {plan.Keep} | around " +
                       string.Join(" ", World.Others.Where(o => Math.Abs(o.X - ego.X) < 80 && Math.Abs(o.Y - ego.Y) < 8).OrderBy(o => o.X).Select(o => $"{o.Id}({o.X - ego.X:+0;-0},{o.Y:F1},{o.Speed:F0})")));
        StepMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        WorstStepMilliseconds = Math.Max(WorstStepMilliseconds, StepMilliseconds);
        var before = World.HardestBrakingForEgo; var collisions = World.EgoCollisions.Count;
        World.Advance();
        if (World.EgoCollisions.Count > collisions && Events.Count < 12)
            Events.Add($"t {World.Time:F1}: COLLISION with {World.EgoCollisions[^1].With}: ego x {ego.X:F1} y {ego.Y:F1} v {ego.Speed:F1} a {plan.Acceleration:F1} target {plan.TargetOffset:F1} [{plan.Detail}] | around " +
                       string.Join(" ", World.Others.Where(o => Math.Abs(o.X - ego.X) < 60 && Math.Abs(o.Y - ego.Y) < 8).OrderBy(o => o.X).Select(o => $"{o.Id}({o.X - ego.X:+0.0;-0.0},{o.Y:F1},{o.Speed:F1},a{o.Acceleration:F1})")));
        if (World.HardestBrakingForEgo > Math.Max(5, before) && Events.Count < 12)
            Events.Add($"t {World.Time:F1}: OTHERS BRAKE {World.HardestBrakingForEgo:F1}: ego x {ego.X:F0} y {ego.Y:F1} v {ego.Speed:F1} a {plan.Acceleration:F1} target {plan.TargetOffset:F1} [{plan.Detail}] | around " +
                       string.Join(" ", World.Others.Where(o => Math.Abs(o.X - ego.X) < 80 && Math.Abs(o.Y - ego.Y) < 8).OrderBy(o => o.X).Select(o => $"{o.Id}({o.X - ego.X:+0;-0},{o.Y:F1},{o.Speed:F0},a{o.Acceleration:F1})")));
    }

    public void Run(double seconds, Func<bool>? stop = null)
    {
        for (var k = 0; k < (int)Math.Round(seconds / HighwayWorld.Step); k++)
        {
            Step();
            if (stop?.Invoke() == true) break;
        }
    }
}
