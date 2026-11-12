namespace Mpai.Cav.Driving;

// STEP 2 - WHERE THE OTHERS WILL BE. For each road user, one or more hypotheses of its motion over the next seconds, each with a
// probability, and how sure the CAV is of each place (a radius, Sigma, growing with time). Where a car may turn or change lane,
// there is a hypothesis for each way. "Assumed" road users stand for what the CAV cannot see: something stopped just beyond
// where it sees on its lane, a car coming on the oncoming lane from beyond the view, a pedestrian stepping out from a hidden area.
public sealed record PredictedPoint(double T, Vec2 Position, double Heading, double Speed);

public sealed record Hypothesis(string Label, double Probability, IReadOnlyList<PredictedPoint> Points, double SigmaAt0, double SigmaGrowth)
{
    public double Sigma(double t) => SigmaAt0 + SigmaGrowth * t;

    public PredictedPoint At(double t)
    {
        if (t <= Points[0].T) return Points[0];
        for (int i = 1; i < Points.Count; i++)
            if (Points[i].T >= t)
            {
                var a = Points[i - 1]; var b = Points[i]; var k = (t - a.T) / (b.T - a.T);
                return new PredictedPoint(t, a.Position + (b.Position - a.Position) * k, a.Heading + Angles.Diff(b.Heading, a.Heading) * k, a.Speed + (b.Speed - a.Speed) * k);
            }
        return Points[^1];
    }
}

public sealed record Prediction(int RoadUserId, RoadUserKind Kind, double Length, double Width, bool Assumed, IReadOnlyList<Hypothesis> Hypotheses)
{
    public Hypothesis MostLikely => Hypotheses.MaxBy(h => h.Probability)!;
}

public sealed class MotionPredictor(RoadMap map)
{
    public double Horizon { get; init; } = 6.0;
    public double Step { get; init; } = 0.25;
    public double LateralAcceleration { get; init; } = 2.5;   // m/s2 a driver keeps to in a curve
    public double PedestrianSpeed { get; init; } = 1.5;       // of an assumed pedestrian stepping out
    public double HardBraking { get; init; } = 8.0;           // m/s2, of a vehicle that brakes as hard as it can
    public const string BrakesHard = "brakes hard";
    private Lane? _egoLane;

    public List<Prediction> Predict(Scene scene)
    {
        var result = new List<Prediction>();
        _egoLane = scene.Ego.LaneId is string id && map.Lanes.TryGetValue(id, out var l) ? l : null;
        foreach (var u in scene.RoadUsers) result.Add(new Prediction(u.Id, u.Kind, u.Length, u.Width, false, Normalise(HypothesesOf(u))));
        result.AddRange(Assumed(scene));
        return result;
    }

    private static List<Hypothesis> Normalise(List<Hypothesis> hs)
    {
        var total = hs.Sum(h => h.Probability);
        return hs.Select(h => h with { Probability = h.Probability / total }).OrderByDescending(h => h.Probability).ToList();
    }

    private IEnumerable<double> Times() { for (double t = 0; t <= Horizon + 1e-9; t += Step) yield return t; }

    private List<Hypothesis> HypothesesOf(DescribedRoadUser u)
    {
        var hs = new List<Hypothesis>();
        if (u.Kind == RoadUserKind.Obstacle) { hs.Add(Stays(u, 1)); return hs; }
        if (u.Kind == RoadUserKind.Pedestrian) return Pedestrian(u);

        if (u.LaneId is null || !map.Lanes.TryGetValue(u.LaneId, out var lane))
        {
            if (u.Stationary) hs.Add(Stays(u, 1)); else hs.Add(ConstantVelocity(u, 1, "constant velocity (off the lanes)"));
            return hs;
        }

        double v0 = u.Speed, a0 = Math.Clamp(u.Acceleration, -8, 3);
        if (Math.Abs(a0) < 0.5) a0 = 0;                          // below this the estimate is noise
        if (u.Stationary) { hs.Add(Stays(u, 0.8)); v0 = 0; a0 = 1.5; }
        var chains = map.Chains(lane, u.S, Math.Max(v0, 8) * Horizon + 30);
        double startHeading = lane.Centre.HeadingAt(u.S);
        var weighted = new List<(List<Lane> Chain, double W, int Turn)>();
        foreach (var chain in chains)
        {
            var line = RoadMap.Line(chain);
            double reach = Math.Min(line.Length, u.S + Math.Max(v0, 8) * Horizon);
            var turn = Angles.Diff(line.HeadingAt(Math.Min(line.Length - 0.1, u.S + 80)), startHeading);
            int dir = turn > 0.5 ? 1 : turn < -0.5 ? -1 : 0;
            if (chain.Any(l => l.Kind == LaneKind.Roundabout)) dir = ExitSide(chain, line, u.S);
            double w = 1;
            if (u.Indicator != 0) w *= dir == u.Indicator ? 5 : 0.3;
            if (dir != 0 && a0 < -0.8 && v0 > 2) w *= 2;          // slowing down before a turn
            if (dir != 0 && v0 > 12 && a0 > -0.3) w *= 0.5;        // fast and not slowing: more likely straight on
            weighted.Add((chain, w, dir));
        }
        double share = u.Stationary ? 0.2 : 1.0;
        double sum = weighted.Sum(c => c.W);

        // at a give-way line (a roundabout entry, a minor road): one that slows or waits there gives way; one that comes on fast may not
        double toLine = lane.Centre.Length - u.S;
        if (lane.YieldAtEnd && toLine < 40 && toLine > -1)
        {
            // it gives way to the CAV only if the CAV has the priority (it is not itself at a give-way entry, waiting like the other)
            bool egoHasPriority = _egoLane is null || !_egoLane.YieldAtEnd;
            bool yielding = egoHasPriority && (u.Stationary || a0 < -0.5 || v0 < 3 && toLine < 10);
            double w = yielding ? 15 : 0.5;
            // (the ways on are worth 0.85 x share together; this one w times as much)
            // the line: the lane ends where it joins the other's centre line; the road user's centre waits half its length and half a lane before
            double line = lane.Centre.Length - (u.Length / 2 + 1.75 + 1.0);
            hs.Add(StopsAt(u, lane.Centre, u.S, Math.Max(u.S, line), v0, w * 0.85 * share, "gives way at the line"));
            if (u.Stationary) { hs.RemoveAll(h => h.Label == "stays"); }
        }
        foreach (var (chain, w, dir) in weighted)
        {
            var label = (dir switch { 1 => "turns left", -1 => "turns right", _ => "goes on" }) + " via " + string.Join(">", chain.Select(l => l.Id));
            if (u.Stationary) label = "starts and " + label;
            hs.Add(AlongLine(u, RoadMap.Line(chain), u.S, u.D, 0, v0, a0, share * w / sum * (u.Stationary ? 1 : 0.85), label));
        }
        if (!u.Stationary)
        {
            // a lane change: what the lateral speed or the indicator says
            foreach (var (side, other) in new[] { (1, lane.Left), (-1, lane.Right) })
            {
                if (other is null) continue;
                double toward = side * u.LateralSpeed;
                double w = Math.Clamp((toward - 0.15) / 0.5, 0, 1) * 0.8 + (u.Indicator == side ? 0.4 : 0);
                if (w <= 0) continue;
                var line = RoadMap.Line(map.Chains(lane, u.S, Math.Max(v0, 8) * Horizon + 30)[0]);
                hs.Add(AlongLine(u, line, u.S, u.D, side * lane.Width, v0, a0, w, side == 1 ? "changes lane to the left" : "changes lane to the right"));
            }
            hs.Add(ConstantVelocity(u, 0.05, "constant velocity"));
            // any vehicle may brake as hard as it can, at once (the case Responsibility-Sensitive Safety keeps a distance for)
            if (u.Kind is RoadUserKind.Car or RoadUserKind.Truck && v0 > 3)
                hs.Add(AlongLine(u, RoadMap.Line(chains[0]), u.S, u.D, 0, v0, -HardBraking, 0.12, BrakesHard));
        }
        return hs;
    }

    // For a roundabout: the side it leaves on, seen from where it entered: an early exit is a right turn, a late one a left turn.
    private static int ExitSide(List<Lane> chain, Polyline line, double s)
    {
        var turn = Angles.Diff(line.HeadingAt(line.Length - 0.1), line.HeadingAt(s));
        return turn > 0.5 ? 1 : turn < -0.5 ? -1 : 0;
    }

    private Hypothesis Stays(DescribedRoadUser u, double p) =>
        new("stays", p, Times().Select(t => new PredictedPoint(t, u.Position, u.Heading, 0)).ToList(), 0.2, 0.02);

    private Hypothesis ConstantVelocity(DescribedRoadUser u, double p, string label)
    {
        var h = u.Speed > 0.3 ? u.Velocity.Heading : u.Heading;
        return new(label, p, Times().Select(t => new PredictedPoint(t, u.Position + u.Velocity * t, h, u.Speed)).ToList(), 0.3, u.Vulnerable ? 0.35 : 0.3);
    }

    // Along a line of lanes from (s, d), the lateral offset going to dEnd (0 = the centre) in 3 s (a lane change) or 2 s,
    // speed v0 changed by a0 for 1.5 s, never above what the curve allows.
    private Hypothesis AlongLine(DescribedRoadUser u, Polyline line, double s0, double d0, double dTarget, double v0, double a0, double p, string label)
    {
        var points = new List<PredictedPoint>();
        double s = s0, v = v0;
        double tLateral = dTarget == 0 ? 2.0 : 3.0;
        foreach (var t in Times())
        {
            if (t > 0)
            {
                // a road user that brakes keeps braking (to a stop); one that speeds up does so for 1.5 s
                double a = a0 < -1 ? a0 : t <= 1.5 ? a0 : 0;
                double k = Math.Abs(line.CurvatureAt(s + v * 2));
                double vCurve = k > 1e-3 ? Math.Sqrt(LateralAcceleration / k) : double.MaxValue;
                v = Math.Max(0, v + a * Step);
                if (v > vCurve) v = Math.Max(vCurve, v - 3 * Step);
                s += v * Step;
            }
            double x = Math.Min(1, t / tLateral); double smooth = x * x * (3 - 2 * x);
            double d = d0 + (dTarget - d0) * smooth;
            if (dTarget == 0) d = u.Kind == RoadUserKind.Cyclist ? d0 : d0 * (1 - smooth);   // a cyclist keeps to its side of the lane
            var pos = line.At(s, d);
            points.Add(new PredictedPoint(t, pos, line.HeadingAt(s), v));
        }
        return new Hypothesis(label, p, points, 0.25, u.Vulnerable ? 0.3 : 0.2);
    }

    // Along a line, braking evenly to stand at sStop.
    private Hypothesis StopsAt(DescribedRoadUser u, Polyline line, double s0, double sStop, double v0, double p, string label)
    {
        double dist = Math.Max(0.1, sStop - s0), dec = v0 * v0 / (2 * dist);
        var points = new List<PredictedPoint>();
        double s = s0, v = v0;
        foreach (var t in Times())
        {
            if (t > 0) { v = Math.Max(0, v - dec * Step); s = Math.Min(sStop, s + v * Step); }
            points.Add(new PredictedPoint(t, line.At(s, u.D), line.HeadingAt(s), v));
        }
        return new Hypothesis(label, p, points, 0.25, 0.1);
    }

    private List<Hypothesis> Pedestrian(DescribedRoadUser u)
    {
        var hs = new List<Hypothesis>();
        bool onRoad = map.OnCarriageway(u.Position);
        if (u.Speed > 0.3)
        {
            hs.Add(ConstantVelocity(u, onRoad ? 0.8 : 0.6, "walks on"));
            hs.Add(Stays(u, 0.2) with { Label = "stops" });
        }
        else hs.Add(Stays(u, 0.6) with { Label = "stands" });
        if (!onRoad)
        {
            var near = map.Matches(u.Position, null, 3.0).FirstOrDefault();
            if (near.Lane is not null)
            {
                var dir = (near.Lane.Centre.At(near.S) - u.Position).Unit;
                // walking away from the road: it does not step in; standing still a while (2 s and more): less and less likely
                bool away = u.Speed > 0.3 && u.Velocity.Dot(dir) < 0;
                double w = away ? 0 : u.Speed > 0.3 ? 0.2 : u.SeenFor < 2 ? 0.2 : 0.05;
                if (w > 0)
                    hs.Add(new Hypothesis("steps into the road", w, Times().Select(t => new PredictedPoint(t, u.Position + dir * (PedestrianSpeed * t), dir.Heading, PedestrianSpeed)).ToList(), 0.3, 0.3));
            }
        }
        return hs;
    }

    // What the CAV cannot see but must allow for.
    private IEnumerable<Prediction> Assumed(Scene scene)
    {
        int id = -1;
        if (scene.Ego.LaneId is null || !map.Lanes.TryGetValue(scene.Ego.LaneId, out var egoLane)) yield break;

        // (1) on its own lanes ahead: something stopped just beyond the end of the view
        foreach (var chain in map.Chains(egoLane, scene.Ego.S, 150).Take(3))
        {
            double ahead = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                var l = chain[i]; double from = i == 0 ? scene.Ego.S : 0;
                if (!scene.Visibility.TryGetValue(l.Id, out var vis)) break;           // a lane not looked at: beyond the range
                if (vis.To < l.Centre.Length - 0.5 && vis.To > from)
                {
                    var p = l.Centre.At(vis.To + 2.5);
                    yield return new Prediction(id--, RoadUserKind.Obstacle, 4.5, 1.8, true,
                        [new Hypothesis($"assumed: stopped beyond the view on {l.Id}", 1, Times().Select(t => new PredictedPoint(t, p, l.Centre.HeadingAt(vis.To), 0)).ToList(), 0, 0)]);
                    break;
                }
                ahead += l.Centre.Length - from;
                if (ahead > 150) break;
            }
        }

        // (2) the oncoming lane: a car coming from beyond the view at the speed limit
        var oncomingIds = new HashSet<string>();
        foreach (var chain in map.Chains(egoLane, scene.Ego.S, 100).Take(3))
            foreach (var l in chain) if (l.Oncoming is string o) oncomingIds.Add(o);
        foreach (var o in oncomingIds)
        {
            var lane = map.Lanes[o];
            if (!scene.Visibility.TryGetValue(o, out var vis)) continue;
            var (sEgo, _) = lane.Centre.Project(scene.Ego.Position);
            if (sEgo < vis.From - 1) continue;              // the unseen part is behind the CAV
            if (vis.From <= 0.5) continue;                  // seen to its start (the lanes before it are not looked at)
            double s0 = vis.From - 3, v = lane.SpeedLimit;
            var pts = Times().Select(t => new PredictedPoint(t, lane.Centre.At(s0 + v * t), lane.Centre.HeadingAt(s0 + v * t), v)).ToList();
            yield return new Prediction(id--, RoadUserKind.Car, 4.5, 1.8, true, [new Hypothesis($"assumed: oncoming from beyond the view on {o}", 1, pts, 0.3, 0.2)]);
        }

        // (3) a pedestrian stepping out from a hidden area ahead
        foreach (var h in scene.HiddenAreas)
        {
            var rel = h.Edge - scene.Ego.Position;
            if (rel.Length > 45 || rel.Dot(Vec2.FromHeading(scene.Ego.Heading)) < 0) continue;
            var pts = Times().Select(t => new PredictedPoint(t, h.Edge + h.TowardRoad * (PedestrianSpeed * t), h.TowardRoad.Heading, PedestrianSpeed)).ToList();
            yield return new Prediction(id--, RoadUserKind.Pedestrian, 0.5, 0.5, true, [new Hypothesis($"assumed: pedestrian stepping out from behind road user {h.ByRoadUser}", 1, pts, 0, 0.1)]);
        }
    }
}
