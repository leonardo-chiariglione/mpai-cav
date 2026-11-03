namespace Mpai.Cav.Ams.Planning;

// A POINT OF A TRAJECTORY, in the map's frame: when (s from now), where, how fast, and the acceleration asked.
public readonly record struct TrajectoryPoint(double T, Pose Pose, double Speed, double Acceleration);

// WHAT A TRAJECTORY VIOLATES (M3287): the constraint, when (s from now), by how much (m, m/s...), and what. A hard violation makes the Motion Constraints refuse
// the trajectory (S-3); the others are reported (the use of the emergency set, a comfort limit).
public sealed record Violation(string Constraint, double At, double Depth, string What, bool Hard = true);

// WHAT THE CHECK KNOWS OF THE ROAD: the route and the lanes beside it (offsets, to the left), the speed limit, the size of the CAV.
public sealed record RoadContext(PolyPath Route, IReadOnlyList<double> Lanes, double SpeedLimit, double EgoLength = 4.5, double EgoWidth = 1.8);

public sealed partial class MotionConstraints
{
    public const double PhysicalBrake = 8.4;                    // m/s2 the tyres give on the dry road (0.9 g, 0.95 of it): what braking can be, whatever the sets say
    public const double LaneHalfWidth = 1.75;                    // m: further than this from the middle of its lane the CAV is out of it

    // The road users that may go anywhere, by class: how hard they change velocity, and by how much at the most (the object tracker's).
    private static readonly Dictionary<string, (double Acceleration, double MaxChange)> Vulnerable = new() { ["person"] = (2.0, 3.0), ["bicycle"] = (2.0, 4.0) };

    // THE CHECK (S-3): the trajectory of any strategy against the constraints, from the objects perceived and the road. Objects are taken to go on at the velocity they
    // have; a road user that may go anywhere is taken to be anywhere in the disc it can reach.
    public IReadOnlyList<Violation> Check(IReadOnlyList<TrajectoryPoint> trajectory, IReadOnlyList<Observation> objects, RoadContext road)
    {
        var found = new List<Violation>();
        var normal = Normal; var emergency = Emergency;
        var cap = trajectory.Count > 0 ? Cap(road.Route, road.Route.Project(trajectory[0].Pose.X, trajectory[0].Pose.Y).S, road.SpeedLimit, normal.Brake) : null;
        var reported = new HashSet<string>();
        // A CAV that is already out of its lane, or already faster than a corner allows, is not made worse by a trajectory that goes back or slows down: what the trajectory is
        // charged with is what it makes of it. C2: no further from the lane's middle than it is (and than the lane's edge); C4: no faster than the corner allows, or than the
        // CAV would be braking at the normal limit from the speed it has.
        var outside0 = trajectory.Count > 0 ? road.Lanes.Min(l => Math.Abs(road.Route.Project(trajectory[0].Pose.X, trajectory[0].Pose.Y).Offset - l)) : 0;
        var farthest = Math.Max(LaneHalfWidth, outside0 + 0.1);
        var v0 = trajectory.Count > 0 ? trajectory[0].Speed : 0;
        void Add(Violation v) { if (reported.Add(v.Constraint + v.What.Split(':')[0] + v.Hard)) found.Add(v); }

        for (var k = 0; k < trajectory.Count; k++)
        {
            var pt = trajectory[k]; var (c, s) = (Math.Cos(pt.Pose.Heading), Math.Sin(pt.Pose.Heading));

            // C1: the safe distances, in the CAV's frame at that instant
            foreach (var o in objects)
            {
                var (ox, oy) = (o.X + o.VX * pt.T, o.Y + o.VY * pt.T);
                var (dx, dy) = (ox - pt.Pose.X, oy - pt.Pose.Y);
                var (along, across) = (dx * c + dy * s, -dx * s + dy * c);
                if (along <= 0) continue;                                           // behind: the vehicle behind keeps its own distance
                var (voAlong, voAcross) = (o.VX * c + o.VY * s, -o.VX * s + o.VY * c);
                var reach = Vulnerable.TryGetValue(o.Class, out var u) ? DiscReach(u.Acceleration, u.MaxChange, pt.T) : 0;
                var gapAlong = along - (road.EgoLength + o.Length) / 2 - reach;
                var gapAcross = Math.Abs(across) - (road.EgoWidth + o.Width) / 2 - reach;
                var dLong = voAlong < -0.5 ? SafeLongitudinalOpposite(pt.Speed, -voAlong) : SafeLongitudinal(pt.Speed, Math.Max(0, voAlong));
                var dLat = across > 0 ? SafeLateral(voAcross, 0) : SafeLateral(0, voAcross);
                if (gapAlong < dLong && gapAcross < dLat)
                    Add(new Violation("C1", pt.T, Math.Min(dLong - gapAlong, dLat - gapAcross), $"{o.Id}: {gapAlong:0.0} m along, {gapAcross:0.0} m across, against {dLong:0.0} and {dLat:0.0}", pt.T <= P.CommitHorizon));
            }

            // C2: in the lane
            var (sRoute, offset) = road.Route.Project(pt.Pose.X, pt.Pose.Y);
            var outside = road.Lanes.Min(l => Math.Abs(offset - l));
            if (outside > farthest) Add(new Violation("C2", pt.T, outside - farthest, $"{outside:0.0} m from the middle of the nearest lane", pt.T <= P.CommitHorizon));

            // C4: the speed the route allows there; C5: the limit
            var allowed = cap is null ? double.MaxValue : Math.Max(cap.At(sRoute) + 0.5, v0 - normal.Brake * pt.T);
            if (pt.Speed > allowed)
                Add(new Violation("C4", pt.T, pt.Speed - allowed, $"{pt.Speed:0.0} m/s where the route allows {cap!.At(sRoute):0.0}", pt.T <= P.CommitHorizon));
            if (pt.Speed > road.SpeedLimit + 0.5) Add(new Violation("C5", pt.T, pt.Speed - road.SpeedLimit, $"{pt.Speed:0.0} m/s above the limit {road.SpeedLimit:0.0}", pt.T <= P.CommitHorizon));

            // C6, C7: reported, not refused: the emergency set is for the situations of C7
            if (-pt.Acceleration > normal.Brake + 1e-6)
                Add(new Violation(-pt.Acceleration > emergency.Brake + 1e-6 ? "C7+" : "C7", pt.T, -pt.Acceleration - normal.Brake, $"braking {-pt.Acceleration:0.0} m/s2", false));
            if (pt.Acceleration > normal.Acceleration + 1e-6) Add(new Violation("C6", pt.T, pt.Acceleration - normal.Acceleration, $"acceleration {pt.Acceleration:0.0} m/s2", false));
        }
        return found;
    }

    private static double DiscReach(double acceleration, double maxChange, double t)
    {
        var t1 = acceleration > 0 ? maxChange / acceleration : 0;
        return t <= t1 ? acceleration * t * t / 2 : acceleration * t1 * t1 / 2 + maxChange * (t - t1);
    }

    // THE FALL-BACK (C7, C8): the way along the route that stops the CAV, braking as gently as the situation lets it - the comfortable level, then the normal, then the
    // emergency - and, where the CAV is not in a lane, going back to the nearest one. Returns the way, the level used, and what the check still finds on it.
    public (IReadOnlyList<TrajectoryPoint> Way, string Level, IReadOnlyList<Violation> Violations) FallBack(
        double x, double y, double speed, IReadOnlyList<Observation> objects, RoadContext road, double horizon, double step, double strategyBrake = 0)
    {
        var (s0, d0) = road.Route.Project(x, y);
        var target = road.Lanes.OrderBy(l => Math.Abs(l - d0)).First();
        (string Level, double Brake)[] levels = [("comfort", Comfort.Brake), ("normal", Normal.Brake), ("emergency", Emergency.Brake)];
        (IReadOnlyList<TrajectoryPoint>, string, IReadOnlyList<Violation>)? last = null;
        foreach (var (level, levelBrake) in levels)
        {
            // Never less braking than the strategy itself was asking for - what it asked, within what the tyres give - and never more than the tyres give.
            var brake = Math.Min(Math.Max(levelBrake, strategyBrake), Math.Max(levelBrake, PhysicalBrake * Math.Min(1, Grip)));
            var way = new List<TrajectoryPoint>(); double s = s0, v = speed, d = d0;
            for (var k = 0; k <= (int)Math.Round(horizon / step); k++)
            {
                var t = k * step; var along = road.Route.At(s);
                d += Math.Clamp(target - d, -1.0 * step, 1.0 * step);                    // back into the lane at 1 m/s at the most
                way.Add(new TrajectoryPoint(t, new Pose(along.X - d * Math.Sin(along.Heading), along.Y + d * Math.Cos(along.Heading), along.Heading), v, -brake));                       // the braking asked goes on to the end: the MAS looks ahead, and a braking that ends where the CAV stops is not asked for, at a crawl
                var next = Math.Max(0, v - brake * step);
                s = Math.Min(road.Route.Length, s + (v + next) / 2 * step); v = next;
            }
            var found = Check(way, objects, road);
            last = (way, level, found);
            if (!found.Any(f => f.Hard)) break;
        }
        return last!.Value;
    }
}
