namespace Mpai.Cav.Ams.Planning;

public sealed record PlannerSettings
{
    public double Horizon { get; init; } = 8;
    public double Step { get; init; } = 0.1;                  // the step of the check, s: at 30 m/s, 3 m - less than the vehicle's length
    public double Comfort { get; init; } = 2.0;               // m/s2, speeding up: the normal limit of ISO 15622 (M3287)
    public double ComfortBrake { get; init; } = 2.5;          // m/s2, braking
    public double HardBrake { get; init; } = 5;               // m/s2
    public double MaxBrake { get; init; } = 8;                // m/s2
    public double EgoLength { get; init; } = 4.5;
    public double EgoWidth { get; init; } = 1.8;
    public double MarginBase { get; init; } = 0.4;            // m, outline to outline, now
    public double MarginGrowth { get; init; } = 0.12;         // m per second ahead: what a prediction may be out by
    public double MarginMax { get; init; } = 1.1;
    public double ObjectBrake { get; init; } = 3;             // how hard the objects brake, at most (m/s2)
    public double ObjectSpeedUp { get; init; } = 0.5;
    public double LaneChangeSeconds { get; init; } = 4;
    public double LaneChangeCost { get; init; } = 8;          // m of progress a change of lane has to gain to be made
    public double ComfortClearance { get; init; } = 1.5;       // m over the margin the CAV likes to keep from anything it may come near
    public double ClearanceCost { get; init; } = 30;          // m of progress each metre short of it costs: the CAV slows early and gently, not at the last moment
    public double CrossingSeconds { get; init; } = 1.0;       // the time a vehicle that crosses its way keeps from it: before it gets there and after it has gone
    public double FollowerBrake { get; init; } = 3;           // m/s2: the most the CAV makes a vehicle behind it brake, when it takes its place in front of it
    public double FollowerComfort { get; init; } = 1.5;       // m/s2: how drivers speed up, and (the model of a driver that follows) the next one brakes
    public double FollowerSeconds { get; init; } = 1.5;       // the time gap drivers keep, s
    public double FollowerGap { get; init; } = 2.5;           // the gap they keep when stopped, m
    public double FollowerCost { get; init; } = 6;            // m of progress each metre nearer than that costs
    public double Jerk { get; init; } = 8;                    // m of progress each m/s2 a command differs from the last costs: no switching between braking and speeding up
    public double Stay { get; init; } = 6;                    // m of progress that going on to the lane it was going to is worth: not to change its mind each step
}

// WHERE THE CAV IS, for the planner: the map's frame.
public sealed record EgoState(double Time, double X, double Y, double Heading, double Speed);

// ITS TRAJECTORY: a place and a speed each Step seconds, and what is asked of the vehicle now.
public sealed class Plan
{
    public required IReadOnlyList<(double T, Pose Pose, double Speed, double Acceleration)> Points { get; init; }
    public double Acceleration { get; init; }                 // what the vehicle does now, m/s2 (negative: it brakes)
    public double Offset { get; init; }                       // the offset from the route, to the left, the vehicle goes to
    public double TargetOffset { get; init; }
    public bool Clear { get; init; }                          // no object perceived is nearer than the margin, anywhere on it
    public double Clearance { get; init; }                    // the least outline-to-outline clearance on it, over the margin
    public int Candidates { get; init; }
    public int Feasible { get; init; }
    public string? Detail { get; init; }                      // the way chosen: its target speed, its acceleration, its lane, its cost
    public string? Keep { get; init; }                        // the way that goes on as it is: whether it is clear, and if not what stops it
    public string? Why { get; init; }                         // where no way is clear: what the nearest to it comes too near, and when
}

// THE PLANNER: of the ways the CAV can go - along its route at the speeds it can have, to the lane it is in or to the lanes beside it - it takes
// the one that goes furthest and gentlest, among those that keep clear of every object perceived, wherever the hypotheses of the object put it
// at any of the next seconds. It plans again at each perception: it commits to nothing beyond the next step. Where no way keeps clear, it brakes
// as hard as the vehicle can and takes the way that stays clearest.
public sealed class SpaceTimePlanner(PlannerSettings? settings = null, MotionConstraints? constraints = null)
{
    private readonly PlannerSettings p = settings ?? new();
    private readonly MotionConstraints constraints = constraints ?? new();
    private const double JerkLimit = 3;                       // m/s2: the change of command that is charged for, at most - a way that is much the better is taken whatever the last step did
    private const double LaneCentreCost = 15;                  // m of progress each metre from the middle of a lane costs: the CAV keeps its lane, and goes back to the middle of it
    private IReadOnlyList<double> lanes = [0];
    private const double RearLaneOverlap = 0.6;                // m: how much of its width beside the CAV's a vehicle ahead may be and still be in its way
    private static bool Vulnerable(TrackedObject o) => o.Class is "person" or "bicycle";
    private const double OncomingSlack = 0.3;                 // m: how far out of its line a vehicle that keeps to its lane may be (a lane has about that more than a vehicle needs)
    private const double ConfinedMargin = 0.5;                // m: the margin kept from a vehicle that keeps to its lane: what the CAV's own placing is out by
    private const double LaneTolerance = 1.2;                  // m: how far from the middle of a lane the CAV still is in it
    private double? previousTarget;
    private double previousAcceleration;
    private double now;                                      // the instant of the planning: what was perceived earlier has had that time more
    private PolyPath? route;
    private (double X, double Y, double Cos, double Sin) egoFrame;
    private bool straight;
    private readonly Dictionary<TrackedObject, (double X, double Y, double V, bool Keeps)> lane = new();      // each object in the frame of the lane the CAV is in: where, how fast along it, and whether it keeps its lane
    private SpeedCap speedCap = new([double.MaxValue, double.MaxValue], 0);       // the speed the route ahead allows (the Motion Constraints, C4)

    // The constraints of the CAV (M3287) this planner is under; the grip of the road now is theirs.
    public MotionConstraints Constraints => constraints;
    public double Grip { get => constraints.Grip; set => constraints.Grip = value; }

    // route: the line of the lane the CAV goes along to its destination; laneOffsets: where the lanes beside it are (to the left, metres), the
    // offset of the lane the CAV is in included; limit: the speed limit, m/s.
    public Plan Next(EgoState ego, PolyPath route, double limit, IEnumerable<TrackedObject> objects, IReadOnlyList<double> laneOffsets)
    {
        now = ego.Time;
        this.route = route;
        lane.Clear();
        egoFrame = (ego.X, ego.Y, Math.Cos(ego.Heading), Math.Sin(ego.Heading));
        // The road ahead is straight for the frame of the lane: its heading changes by less than a few degrees over what the CAV can cover in the horizon.
        var sNow = route.Project(ego.X, ego.Y).S;
        straight = Math.Abs(Math.IEEERemainder(route.At(Math.Min(route.Length, sNow + Math.Max(ego.Speed, 5) * p.Horizon)).Heading - route.At(sNow).Heading, 2 * Math.PI)) < 0.06;
        var (s0, d0) = route.Project(ego.X, ego.Y);
        // Where the lanes are: the CAV goes to one of them. It keeps where it is only if it is in one; out of the road it goes back into the nearest.
        var inLane = laneOffsets.Any(l => Math.Abs(l - d0) < LaneTolerance);
        var targets = (inLane ? laneOffsets.Concat([d0]) : laneOffsets).Distinct().ToList();
        speedCap = constraints.Cap(route, s0, limit, p.ComfortBrake);
        lanes = laneOffsets;
        var steps = (int)Math.Round(p.Horizon / p.Step);
        var near = Relevant(ego, route, s0, limit, objects);
        var v0 = ego.Speed;

        var best = default(Candidate); var emergency = default(Candidate);
        int total = 0, feasible = 0; string? keep = null;
        foreach (var target in targets)
        {
            // Where the lane is, against where the CAV is: staying where it is, changing, or going to the lane it was going to.
            var lateral = Math.Abs(target - d0) < 0.3 ? 0 : 1;
            foreach (var profile in Profiles(v0, limit))
            {
                total++;
                var c = Roll(ego, route, s0, d0, target, profile, steps, limit);
                if (c is null) continue;
                var (clearance, violation) = Check(c, near, steps, out c.Culprit);
                (c.Clearance, c.Violation) = (clearance, violation);
                if (lateral == 0 && Math.Abs(profile.Target - Math.Round(v0, 2)) < 0.01) keep = clearance >= 0 ? $"clear ({clearance:F1})" : c.Culprit;
                // Where no way is clear: the one that is clear the longest, and the least out of it.
                if (emergency is null || violation > emergency.Violation + 1e-9 || (Math.Abs(violation - emergency.Violation) < 1e-9 && clearance > emergency.Clearance)) emergency = c;
                if (clearance < 0) continue;
                feasible++;
                c.Cost = Cost(c, d0, target, lateral);
                if (best is null || c.Cost < best.Cost) best = c;
            }
        }
        var chosen = best ?? emergency!;
        previousTarget = chosen.Target; previousAcceleration = chosen.Acceleration;
        var points = Enumerable.Range(0, steps + 1).Select(k => (k * p.Step, chosen.Poses[k], chosen.Speeds[k], k < steps ? (chosen.Speeds[k + 1] - chosen.Speeds[k]) / p.Step : 0)).ToList();
        return new Plan
        {
            Points = points, Acceleration = chosen.Acceleration, Offset = chosen.Offsets[Math.Min(steps, (int)Math.Round(0.5 / p.Step))], TargetOffset = chosen.Target,
            Detail = $"to {chosen.ProfileTarget:F1} m/s at {chosen.ProfileAcceleration:F1}, lane {chosen.Target:F1}, progress {chosen.Progress:F0}, cost {chosen.Cost:F0}, clearance {chosen.Clearance:F1}, cap {speedCap.First:F1}/{speedCap.Least:F1}@{speedCap.LeastAt:F0}m, d0 {d0:F2}",
            Keep = keep, Clear = best is not null, Clearance = chosen.Clearance, Candidates = total, Feasible = feasible, Why = best is null ? chosen.Culprit : null
        };
    }

    // The objects that can matter: the ones whose reach, over the horizon, comes within what the CAV can cover of its route.
    private List<(TrackedObject Object, Hypothesis Hypothesis)> Relevant(EgoState ego, PolyPath route, double s0, double limit, IEnumerable<TrackedObject> objects)
    {
        var reach = Math.Max(ego.Speed, limit) * p.Horizon + 60;
        var list = new List<(TrackedObject, Hypothesis)>();
        foreach (var o in objects)
        {
            var d = Math.Sqrt(Math.Pow(o.Last.X - ego.X, 2) + Math.Pow(o.Last.Y - ego.Y, 2));
            if (d > reach + o.Last.Speed * p.Horizon) continue;
            foreach (var h in o.Hypotheses) list.Add((o, h));
        }
        return list;
    }

    // THE SPEEDS the CAV may have: each target speed, reached comfortably or - to brake - as hard as it takes, then held.
    private IEnumerable<(double Target, double Acceleration)> Profiles(double v0, double limit)
    {
        var top = Math.Max(limit, 1);
        var speeds = new SortedSet<double>();
        for (double v = 0; v <= top + 1e-6; v += Math.Max(1.5, top / 12)) speeds.Add(Math.Round(v, 2));
        speeds.Add(Math.Min(top, Math.Max(0, Math.Round(v0, 2))));
        speeds.Add(top);
        foreach (var v in speeds)
        {
            if (v >= v0) { yield return (v, p.Comfort); continue; }
            yield return (v, -p.ComfortBrake);
            yield return (v, -p.HardBrake);
            yield return (v, -p.MaxBrake);
        }
    }

    private sealed class Candidate
    {
        public required Pose[] Poses; public required double[] Speeds; public required double[] Offsets; public required double[] Along;
        public double Target, Acceleration, Progress, Clearance, Cost, ProfileTarget, ProfileAcceleration;
        public string? Culprit;
        public double FollowerDeficit;                       // how much nearer in front of a vehicle behind it than it likes the CAV takes its place, at the worst
        public double Violation = double.PositiveInfinity;      // when the way first comes nearer than the margin
    }

    // The way along the route at the speeds of the profile, to the lane of the target offset.
    private Candidate? Roll(EgoState ego, PolyPath route, double s0, double d0, double target, (double Target, double Acceleration) profile, int steps, double limit)
    {
        var poses = new Pose[steps + 1]; var speeds = new double[steps + 1]; var offsets = new double[steps + 1]; var distances = new double[steps + 1];
        double s = s0, v = ego.Speed;
        var span = Math.Max(p.LaneChangeSeconds * 0.6, Math.Abs(target - d0) / 1.4);
        double accelerationNow = 0;
        for (var k = 0; k <= steps; k++)
        {
            var t = k * p.Step;
            var f = span > 0 ? Math.Clamp(t / span, 0, 1) : 1;
            var d = d0 + (target - d0) * f * f * (3 - 2 * f);
            var along = route.At(s);
            var heading = along.Heading;
            var dd = span > 0 && f < 1 ? (target - d0) * 6 * f * (1 - f) / span : 0;
            poses[k] = new Pose(along.X - d * Math.Sin(heading), along.Y + d * Math.Cos(heading), heading + Math.Atan2(dd, Math.Max(1, v)));
            speeds[k] = v; offsets[k] = d; distances[k] = s;
            if (s >= route.Length - 0.5 && v < 0.05) { for (var j = k + 1; j <= steps; j++) { poses[j] = poses[k]; speeds[j] = 0; offsets[j] = d; distances[j] = s; } break; }
            // The acceleration of the profile; at the end of the route, the one that stops there; at a corner, the speed it allows.
            var a = v < profile.Target ? Math.Min(Math.Abs(profile.Acceleration), (profile.Target - v) / p.Step) : v > profile.Target ? -Math.Min(Math.Abs(profile.Acceleration), (v - profile.Target) / p.Step) : 0;
            // The speed the route allows here, with the corners ahead: the CAV is as slow at a corner as its lateral acceleration allows, and slows for it
            // as gently as it can.
            var allowed = Math.Min(limit, speedCap.At(s + v * p.Step));
            if (v + a * p.Step > allowed) a = Math.Max(-p.HardBrake, (allowed - v) / p.Step);
            var toEnd = route.Length - s;
            if (v * v / (2 * p.ComfortBrake) > toEnd - 1 && toEnd < 400) a = Math.Min(a, -Math.Min(p.HardBrake, Math.Max(p.ComfortBrake, v * v / (2 * Math.Max(1, toEnd)))));
            if (k == 0) accelerationNow = a;
            var next = Math.Max(0, v + a * p.Step);
            s = Math.Min(route.Length, s + (v + next) / 2 * p.Step);
            v = next;
        }
        var progress = s - s0;
        return new Candidate { Poses = poses, Speeds = speeds, Offsets = offsets, Along = distances, Target = target, Acceleration = accelerationNow, Progress = progress, ProfileTarget = profile.Target, ProfileAcceleration = profile.Acceleration };
    }

    // THE CLEARANCE of the way over the margin, the least over the horizon: of each pose of the CAV, against each stretch each hypothesis of each
    // object can occupy at the time.
    private (double Clearance, double Violation) Check(Candidate c, List<(TrackedObject Object, Hypothesis Hypothesis)> near, int steps, out string? culprit)
    {
        culprit = null;
        var least = double.MaxValue;
        Span<(double X, double Y, double R)> mine = stackalloc (double, double, double)[3];
        for (var k = 0; k <= steps; k++)
        {
            var t = k * p.Step;
            var margin = Math.Min(p.MarginMax, p.MarginBase + p.MarginGrowth * t);
            var nm = Footprint.Circles(c.Poses[k], p.EgoLength, p.EgoWidth, mine);
            foreach (var (o, h) in near)
            {
                // A road user that may go anywhere: the disc it can reach by then, from where it was last perceived.
                if (h.Disc)
                {
                    var later = t + Math.Max(0, now - o.Last.Time);
                    var reach = h.DiscRadius(later);
                    var (cx, cy) = (o.Last.X + o.Last.VX * later, o.Last.Y + o.Last.VY * later);
                    for (var a = 0; a < nm; a++)
                    {
                        var dist = Math.Sqrt(Math.Pow(mine[a].X - cx, 2) + Math.Pow(mine[a].Y - cy, 2)) - mine[a].R - Math.Max(o.Width, o.Length) / 2 - reach - margin;
                        if (dist < least) { least = dist; if (least < 0) { culprit = $"{o.Id}/{h.Kind}@{t:F1}s(r {reach:F1})"; return (least, t); } }
                    }
                    continue;
                }
                // TRAFFIC THAT KEEPS TO ITS LANE, along the route or against it, without moving sideways, is taken in the frame of the route: it goes on in the lane it is in, at the
                // speed it has along the route. Where the CAV is beside it, the margin of its lane is kept; where it is in the CAV's way, the safe distance of the Motion Constraints. A line
                // drawn from the heading that is perceived, a hundred metres on, would put it wherever the error of that heading puts it.
                // A way the road branches into - a vehicle that may turn off the lane at a junction, across the CAV's way - is NOT the lane: it is taken along its own line, with its reach, below.
                if (h.Told is null && straight && !(h.Kind.StartsWith("Road:", StringComparison.Ordinal) && h.Kind != "Road:Lane"))
                {
                    if (!lane.TryGetValue(o, out var l))
                    {
                        var (dx0, dy0) = (o.Last.X - egoFrame.X, o.Last.Y - egoFrame.Y);
                        var (xo, yo) = (dx0 * egoFrame.Cos + dy0 * egoFrame.Sin, -dx0 * egoFrame.Sin + dy0 * egoFrame.Cos);
                        var (vxo, vyo) = (o.Last.VX * egoFrame.Cos + o.Last.VY * egoFrame.Sin, -o.Last.VX * egoFrame.Sin + o.Last.VY * egoFrame.Cos);
                        var keeps = o.Last.Speed > 1 && Math.Abs(vyo) < 0.4 && Math.Abs(vyo) < 0.1 * Math.Abs(vxo) + 0.4 && Math.Abs(yo) < 8 && !Vulnerable(o);
                        lane[o] = l = (xo, yo, vxo, keeps);
                    }
                    if (l.Keeps)
                    {
                        var (px, py) = (c.Poses[k].X - egoFrame.X, c.Poses[k].Y - egoFrame.Y);
                        var (xk, yk) = (px * egoFrame.Cos + py * egoFrame.Sin, -px * egoFrame.Sin + py * egoFrame.Cos);
                        var lateral = Math.Abs(yk - l.Y) - (p.EgoWidth + o.Width) / 2;
                        if (lateral < ConfinedMargin)
                        {
                            var between = l.X + l.V * t - xk;
                            var gap = Math.Abs(between) - (p.EgoLength + o.Length) / 2;
                            // A vehicle behind, going the same way, in the way of the CAV's place: the CAV does not take a place in front of it nearer than the one that makes its driver brake harder
                            // than `FollowerBrake` (the same rule as for any vehicle behind, below, which this lane frame would otherwise skip).
                            if (between <= 0 && l.V > 0 && k % 3 == 0)
                            {
                                var closingBehind = Math.Max(0, l.V - c.Speeds[k]);
                                var wantedBehind = (p.FollowerGap + l.V * p.FollowerSeconds + l.V * closingBehind / (2 * Math.Sqrt(p.FollowerComfort * p.FollowerBrake))) * Math.Sqrt(p.FollowerComfort / p.FollowerBrake);
                                c.FollowerDeficit = Math.Max(c.FollowerDeficit, wantedBehind - gap);
                            }
                            var safe = between <= 0 ? 0 : l.V >= 0 ? constraints.SafeLongitudinal(c.Speeds[k], l.V) : constraints.SafeLongitudinalOpposite(c.Speeds[k], -l.V);
                            var clear = gap - safe;
                            if (clear < least) { least = clear; if (least < 0) { culprit = $"{o.Id}/{h.Kind}@{t:F1}s(in its lane, {gap:F0} m, safe distance {safe:F0})"; return (least, t); } }
                        }
                        else least = Math.Min(least, lateral - ConfinedMargin + p.ComfortClearance);
                        continue;
                    }
                }
                // A vehicle ahead in the CAV's way, going the same way: the CAV keeps the safe distance of the Motion Constraints from it - the response time, and the braking each may
                // apply - and not what the vehicle might do over the whole horizon, which would keep the CAV seconds behind a vehicle that goes on at its speed.
                if (h.Told is null && h.Speed >= 0 && Math.Abs(Math.IEEERemainder(h.Path.At(h.S0).Heading - c.Poses[k].Heading, 2 * Math.PI)) < 0.35)
                {
                    var oc = h.Path.At(Math.Min(h.Path.Length, h.S0 + h.Speed * t));
                    var (ox, oy) = (oc.X - c.Poses[k].X, oc.Y - c.Poses[k].Y);
                    var (cx0, cy0) = (Math.Cos(c.Poses[k].Heading), Math.Sin(c.Poses[k].Heading));
                    var along = ox * cx0 + oy * cy0;
                    var across = Math.Abs(-ox * cy0 + oy * cx0);
                    if (along > 0 && across < (p.EgoWidth + o.Width) / 2 + RearLaneOverlap)
                    {
                        var gap = along - (p.EgoLength + o.Length) / 2;
                        var clear = gap - constraints.SafeLongitudinal(c.Speeds[k], h.Speed);
                        if (clear < least) { least = clear; if (least < 0) { culprit = $"{o.Id}/{h.Kind}@{t:F1}s(safe distance, {gap:F0} m)"; return (least, t); } }
                        continue;
                    }
                }
                var brake = Math.Max(p.ObjectBrake, Math.Min(p.MaxBrake, o.ObservedBraking));         // as hard as it has been seen to brake, if harder than drivers are taken to
                var (lo, hi) = h.Reach(t, brake, p.ObjectSpeedUp);
                // A vehicle that crosses the CAV's way keeps clear of it in time too: where it may be from a second before to a second after.
                if (Math.Abs(Math.IEEERemainder(h.Path.At((lo + hi) / 2).Heading - c.Poses[k].Heading, Math.PI)) > 0.35 && p.CrossingSeconds > 0)
                    (lo, hi) = (h.Reach(Math.Max(0, t - p.CrossingSeconds), brake, p.ObjectSpeedUp).Lo, h.Reach(t + p.CrossingSeconds, brake, p.ObjectSpeedUp).Hi);
                hi = Math.Max(hi, lo);
                // A vehicle behind the CAV, in its way: where the CAV takes its place in front of it, it makes it brake. What a driver does is brake to keep the gap he
                // keeps (the time gap, and more where he is closing): the CAV does not take a place nearer than the one that makes him brake harder than `FollowerBrake`.
                if (h.Told is null && k % 3 == 0 && Math.Abs(Math.IEEERemainder(h.Path.At(hi).Heading - c.Poses[k].Heading, 2 * Math.PI)) < 0.35)
                {
                    var front = h.Path.At(hi);
                    var (ux, uy) = (Math.Cos(c.Poses[k].Heading), Math.Sin(c.Poses[k].Heading));
                    var ahead = (c.Poses[k].X - front.X) * ux + (c.Poses[k].Y - front.Y) * uy;                    // along the CAV's way, from the object's front to the CAV's centre
                    var across = Math.Abs(-(c.Poses[k].X - front.X) * uy + (c.Poses[k].Y - front.Y) * ux);
                    if (ahead > 0 && across < p.EgoWidth + o.Width)
                    {
                        var gap = ahead - (p.EgoLength + o.Length) / 2;
                        var closing = Math.Max(0, h.Speed - c.Speeds[k]);
                        var wanted = (p.FollowerGap + h.Speed * p.FollowerSeconds + h.Speed * closing / (2 * Math.Sqrt(p.FollowerComfort * p.FollowerBrake))) * Math.Sqrt(p.FollowerComfort / p.FollowerBrake);
                        c.FollowerDeficit = Math.Max(c.FollowerDeficit, wanted - gap);
                    }
                }
                // The object's circles, along its axis, sweep the stretch of its line it may be on, from the reach of its rear circle to its front one's.
                var end = Math.Max(0, o.Length / 2 - o.Width / 2);
                var radius = o.Width / 2 + h.Spread * Math.Max(0, hi - h.S0);          // a direction that is estimated is out by a little, and a long way on by more
                // Traffic that comes the other way keeps to its side of the road, and traffic in a lane of its own that goes along it, without moving sideways, keeps to that lane: it may be
                // out of its line by what its lane leaves it, no more. A vehicle that moves sideways - a cut-in - is not one of them.
                var parallel = Math.Abs(Math.IEEERemainder(h.Path.At(lo).Heading - c.Poses[k].Heading, Math.PI)) < 0.35;
                var sideways = Math.Abs(-o.Last.VX * Math.Sin(h.Path.At(h.S0).Heading) + o.Last.VY * Math.Cos(h.Path.At(h.S0).Heading));
                var confined = parallel && (Math.Abs(Math.IEEERemainder(h.Path.At(lo).Heading - c.Poses[k].Heading, 2 * Math.PI)) > 2.5 || sideways < 0.4);
                var gapNeeded = confined ? Math.Min(margin, ConfinedMargin) : margin;
                if (confined) radius = Math.Min(radius, o.Width / 2 + OncomingSlack);
                for (var a = 0; a < nm; a++)
                {
                    var dist = h.Path.Distance(mine[a].X, mine[a].Y, lo - end, hi + end) - mine[a].R - radius - gapNeeded;
                    if (dist < least) { least = dist; if (least < 0) { culprit = $"{o.Id}/{h.Kind}@{t:F1}s({lo:F0}-{hi:F0})"; return (least, t); } }
                }
            }
        }
        return (least == double.MaxValue ? 100 : least, double.PositiveInfinity);
    }

    // How good a way is: lower is better. The distance not covered (against what the speed limit would have covered), the effort, the lane.
    private double Cost(Candidate c, double d0, double target, int lateral)
    {
        var cost = -c.Progress;
        double effort = 0;
        for (var k = 1; k < c.Speeds.Length; k++) effort += Math.Abs(c.Speeds[k] - c.Speeds[k - 1]);
        cost += 0.35 * effort * p.Horizon / 6;
        if (lateral == 1) cost += p.LaneChangeCost;
        cost += LaneCentreCost * lanes.Min(l => Math.Abs(l - target));
        cost += p.ClearanceCost * Math.Max(0, p.ComfortClearance - c.Clearance) + p.Jerk * Math.Min(JerkLimit, Math.Abs(c.Acceleration - previousAcceleration)) + p.FollowerCost * c.FollowerDeficit;
        if (previousTarget is { } pt && Math.Abs(pt - target) > 0.3) cost += p.Stay;
        if (target > 0.3) cost += 1;          // the lane to the left is for passing: the lane it is to be in, the right one
        return cost;
    }
}
