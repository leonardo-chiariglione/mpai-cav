namespace Mpai.Cav.Driving;

// STEP 3 - TO THE DESTINATION WITHOUT COLLISIONS. At each instant the CAV chooses a way (keep its lane, change lane, pass a
// slow or stopped road user using the lane beside) and an acceleration. HARD RULE (the author, 2026-11-01): no collision,
// otherwise stop. A choice is allowed only if, after holding it for a moment, the CAV can still brake to a stop without coming
// near any road user in any of its likely hypotheses, nor any assumed road user - so a way to stop safely always exists.
// Pedestrians and cyclists first: they are given a wider margin and more time around them.
// If no choice is allowed, the CAV brakes as hard as it can: it stops.
public sealed record PathOption(string Label, Polyline Path, string? TargetLane, double Cost, double SpeedCap)
{
    public double MustReach { get; init; }      // a pass: the distance along the way where the CAV is back in its lane
}

public sealed record Decision(double Time, string Option, double Acceleration, double DesiredAcceleration, bool EmergencyStop, string Reason,
                              int Checked, double DesiredSpeed, PathOption Chosen)
{
    public List<string> Alternatives { get; init; } = [];     // every way looked at: its acceleration and what limited it, or why it was not allowed
}

public sealed class Driver
{
    private readonly RoadMap _map;
    private readonly List<string> _route;
    private readonly string _destLane;
    private readonly double _destS;
    private string? _committed;
    private string? _lastOption;
    private PathOption? _lastChosen;
    private bool _egoGivesWay, _entering;
    private string? _egoLaneId;

    public double EgoLength { get; init; } = 4.6;
    public double EgoWidth { get; init; } = 1.9;
    public double MaxAcceleration { get; init; } = 2.0;
    public double ComfortBraking { get; init; } = 3.0;
    public double FallbackBraking { get; init; } = 5.0;      // the braking a safe stop is planned with
    public double EmergencyBraking { get; init; } = 8.0;
    public double CommitTime { get; init; } = 0.8;           // a choice is held this long before the fallback braking starts
    public double LateralAcceleration { get; init; } = 2.0;
    public double VehicleMargin { get; init; } = 0.4;
    public double VulnerableMargin { get; init; } = 1.0;     // pedestrians and cyclists first
    public double AssumedMargin { get; init; } = 0.2;
    public double StandstillGap { get; init; } = 2.0;        // the CAV stops this far from a stopped vehicle in a queue
    public double LoneStandstillGap { get; init; } = 6.0;    // and this far from one stopped alone (it may have to go round it)
    private HashSet<int> _lone = [];
    public double MinProbability { get; init; } = 0.1;       // hypotheses below it are not respected (the most likely always is)
    public double TimeGap { get; init; } = 1.0;              // the CAV does not go where a moving road user was or will be within this time
    public double GiveWayGap { get; init; } = 2.0;           // the same where the CAV gives way
    public double DetectionDelay { get; init; } = 0.5;       // s the scene description takes to show that a vehicle brakes hard
    // m a stopped CAV keeps from where moving road users will pass. 0.8 (with 0.2 more on their width) was tried on 2026-11-12:
    // fewer close passes, but one CAV stopped at a roundabout entry was hit and one more run did not end; kept at 0.3 for now.
    public double StopClearance { get; init; } = 0.3;

    public Driver(RoadMap map, string startLane, string destLane, double destS)
    {
        _map = map; _destLane = destLane; _destS = destS;
        _route = map.Route(startLane, destLane) ?? throw new ArgumentException($"No route from {startLane} to {destLane}.");
    }

    public IReadOnlyList<string> Route => _route;

    // Arrived: in the destination lane (its centre within half a lane), at the point (stopped within 2 m before it) or past it.
    public bool Arrived(Scene scene)
    {
        var lane = _map.Lanes[_destLane];
        var (s, d) = lane.Centre.Project(scene.Ego.Position);
        if (Math.Abs(d) > lane.Width / 2) return false;
        return s >= _destS - 2.0 && scene.Ego.Speed < 0.5 || s >= _destS + 1.0;
    }

    public Decision Decide(Scene scene, IReadOnlyList<Prediction> predictions)
    {
        var ego = scene.Ego;
        if (ego.LaneId is null) return Stop(scene, "the CAV is not on a lane of the map", null);
        // a stopped road user with nothing stopped or slow just ahead of it on its lane is not queueing: it is stopped alone
        _lone = scene.RoadUsers.Where(u => u.Stationary && !u.Vulnerable && !scene.RoadUsers.Any(o => !ReferenceEquals(o, u) && o.Speed < 3 &&
                    (o.Position - u.Position).Dot(Vec2.FromHeading(u.Heading)) is > 0 and < 15 && Math.Abs((o.Position - u.Position).Dot(Vec2.FromHeading(u.Heading).Left)) < 2))
                .Select(u => u.Id).ToHashSet();
        var egoLane = _map.Lanes[ego.LaneId];
        _egoLaneId = ego.LaneId;
        _egoGivesWay = egoLane.YieldAtEnd || egoLane.Kind is LaneKind.Roundabout or LaneKind.Junction;
        _entering = egoLane.Kind is not (LaneKind.Roundabout or LaneKind.Junction);
        var options = Options(scene, predictions);
        int checkedCount = 0;
        var relevant = Relevant(scene, predictions);

        // For each way, the highest acceleration (not above the one it wants) that keeps a safe stop.
        var results = new List<(PathOption O, double A, double Desired, double Progress, string? Limit)>();
        var alternatives = new List<string>();
        foreach (var o in options)
        {
            double vDes = DesiredSpeed(o, ego.Speed);
            double desired = Math.Clamp(1.2 * (vDes - ego.Speed), -ComfortBraking, MaxAcceleration);
            string? limit = null; double? chosen = null;
            foreach (var a in Accelerations().Where(a => a <= desired + 1e-9).OrderByDescending(a => a).Prepend(desired))
            {
                checkedCount++;
                var hit = Conflict(o.Path, ego.Speed, a, relevant, vDes, ego.Position, ego.Heading, o.MustReach);
                if (hit is null) { chosen = a; break; }
                limit ??= hit;
            }
            if (chosen is double ca)
            {
                // how far the way leads in 8 s: at the speed it wants, or behind the nearest road user on it (at that one's speed)
                double reach = vDes * 8;
                foreach (var u in scene.RoadUsers)
                {
                    var (su, du) = o.Path.Project(u.Position);
                    if (su <= 0 || su > 150 || Math.Abs(du) > (u.Width + EgoWidth) / 2 + 0.3) continue;
                    double along = u.Velocity.Dot(Vec2.FromHeading(o.Path.HeadingAt(su)));
                    if (along < -1) continue;                                   // coming the other way: for the safety check, not for the score
                    reach = Math.Min(reach, Math.Max(0, su - (u.Length + EgoLength) / 2 - (_lone.Contains(u.Id) ? LoneStandstillGap : StandstillGap)) + Math.Max(0, along) * 8);
                }
                double progress = reach + 2 * (ca - desired);
                results.Add((o, ca, desired, progress - o.Cost, ca < desired - 0.01 ? limit : null));
                alternatives.Add($"{o.Label}: a {ca:F1} (wants {desired:F1}), score {progress - o.Cost:F1}{(ca < desired - 0.01 ? ", held back by " + limit : "")}");
            }
            else alternatives.Add($"{o.Label}: not allowed, {limit}");
        }
        if (results.Count == 0)
        {
            // brake on the way the CAV is on (not steering back into what it was going round)
            var current = options.FirstOrDefault(o => o.Label == _lastOption) ?? _lastChosen ?? options.FirstOrDefault();
            return Stop(scene, "no collision-free motion: " + (FirstConflict(options, ego.Speed, relevant) ?? "?"), current) with { Alternatives = alternatives };
        }

        var best = results.MaxBy(r => r.Progress + (r.O.Label == _committed ? 6 : 0) + (r.O.Label == "keep lane" ? 0.5 : 0));
        _committed = best.O.Label == "keep lane" ? null : best.O.Label;
        _lastOption = best.O.Label; _lastChosen = best.O;
        string reason = best.Limit is null ? (best.A < -0.01 ? "slowing to the speed it wants" : "free") : "held back by " + best.Limit;
        return new Decision(scene.Time, best.O.Label, best.A, best.Desired, false, reason, checkedCount, DesiredSpeed(best.O, ego.Speed), best.O) { Alternatives = alternatives };
    }

    private Decision Stop(Scene scene, string reason, PathOption? keep) =>
        new(scene.Time, keep?.Label ?? "stop", -EmergencyBraking, -EmergencyBraking, true, reason, 0, 0,
            keep ?? new PathOption("stop", Polyline.Straight(scene.Ego.Position, scene.Ego.Position + Vec2.FromHeading(scene.Ego.Heading) * 10), null, 0, 0));

    private static IEnumerable<double> Accelerations() => [2.0, 1.5, 1.0, 0.5, 0.0, -0.5, -1.0, -1.5, -2.0, -3.0, -4.0, -5.0, -6.0];

    // The predictions that matter: not road users behind the CAV on its own lane (they keep their distance from it).
    private List<Prediction> Relevant(Scene scene, IReadOnlyList<Prediction> predictions)
    {
        var fwd = Vec2.FromHeading(scene.Ego.Heading);
        var users = scene.RoadUsers.ToDictionary(u => u.Id);
        return predictions.Where(p =>
        {
            if (p.Assumed || !users.TryGetValue(p.RoadUserId, out var u)) return true;
            bool behind = (u.Position - scene.Ego.Position).Dot(fwd) < -EgoLength / 2;
            bool sameLane = u.LaneId is not null && u.LaneId == scene.Ego.LaneId && Math.Abs(u.D - scene.Ego.D) < 1.5;
            return !(behind && sameLane && !u.Vulnerable);
        }).ToList();
    }

    private string? FirstConflict(List<PathOption> options, double v, List<Prediction> relevant) =>
        options.Select(o => Conflict(o.Path, v, -6, relevant, 0, null, 0)).FirstOrDefault(c => c is not null);

    // The speed the CAV wants on a way: the speed limit, the curves ahead, the end of the way, the destination.
    private double DesiredSpeed(PathOption o, double v)
    {
        double cap = o.SpeedCap;
        for (double s = 0; s < Math.Min(o.Path.Length, 120); s += 2)
        {
            double k = Math.Abs(o.Path.CurvatureAt(s));
            if (k > 1e-3) cap = Math.Min(cap, Math.Sqrt(Math.Sqrt(LateralAcceleration / k) * Math.Sqrt(LateralAcceleration / k) + 2 * 1.5 * s));
        }
        return Math.Max(0, cap);
    }

    // The ways the CAV may take from where it is.
    private List<PathOption> Options(Scene scene, IReadOnlyList<Prediction> predictions)
    {
        var ego = scene.Ego;
        var lane = _map.Lanes[ego.LaneId!];
        var list = new List<PathOption>();

        var keepChain = RouteChain(lane);
        var keepLine = RoadMap.Line(keepChain);
        var (s0, d0) = keepLine.Project(ego.Position);
        double keepCap = Cap(keepChain, keepLine, s0);
        if (!_route.Contains(lane.Id) && _map.Lanes.TryGetValue(_destLane, out var dl))
        {
            // off the route (after a lane change to pass): this lane must be left before the destination, so it is a dead end there
            var (sDest, _) = keepLine.Project(dl.Centre.At(_destS));
            keepCap = Math.Min(keepCap, Math.Sqrt(2 * 2.0 * Math.Max(0, sDest - s0 - 30)));
        }
        list.Add(new PathOption("keep lane", Build(keepLine, s0, d0, _ => 0, Math.Max(10, 2 * ego.Speed)), null, 0, keepCap));

        // a lane change to a lane beside, same direction
        foreach (var (side, other) in new[] { ("left", lane.Left), ("right", lane.Right) })
        {
            if (other is null) continue;
            var chain = RouteChain(_map.Lanes[other]);
            var line = RoadMap.Line(chain);
            var (s1, d1) = line.Project(ego.Position);
            double length = Math.Clamp(3 * ego.Speed, 20, 50);
            // toward the route: the route goes through the lane beside after this one (or this lane is off the route)
            bool toward = _route.Contains(other) && (!_route.Contains(lane.Id) || _route.IndexOf(other) > _route.IndexOf(lane.Id));
            double cost = toward ? -15 : 4;
            list.Add(new PathOption($"change lane to the {side} ({other})", Build(line, s1, d1, _ => 0, length), other, cost, Cap(chain, line, s1)));
        }

        // passing a slow or stopped road user ahead on the lane, on the lane to the left
        if (lane.Oncoming is not null || lane.Left is not null)
        {
            var ahead = scene.RoadUsers
                .Where(u => u.Kind != RoadUserKind.Pedestrian)
                .Select(u => (U: u, P: keepLine.Project(u.Position)))
                .Where(x => x.P.S > s0 - (x.U.Length + EgoLength) / 2 && x.P.S - s0 < 60 && Math.Abs(x.P.D) < lane.Width / 2 + x.U.Width / 2 - 0.2)   // ahead or alongside
                .Where(x => x.U.SeenFor >= 1.0)                                    // known for a second at least (before, its speed is not known)
                .Where(x => x.U.Stationary || x.U.Speed < Math.Min(6, 0.5 * lane.SpeedLimit) && x.U.Acceleration > -1.0)   // not one still braking
                .OrderBy(x => x.P.S).FirstOrDefault();
            if (ahead.U is not null)
            {
                var u = ahead.U;
                double clearance = u.Vulnerable ? 1.8 : 1.0;
                double dPass = ahead.P.D + u.Width / 2 + clearance + EgoWidth / 2;
                double sEnd = ahead.P.S + u.Length / 2 + EgoLength + 6;
                double ramp = Math.Clamp(1.5 * ego.Speed, 8, 25);
                // out at once (the way eases out from where the CAV is), alongside until past it, then back in
                Func<double, double> profile = s =>
                {
                    if (s <= sEnd) return dPass;
                    if (s < sEnd + ramp) return dPass * (1 - Smooth((s - sEnd) / ramp));
                    return 0;
                };
                list.Add(new PathOption($"pass road user {u.Id} ({u.Kind})", Build(keepLine, s0, d0, profile, ramp), null, 8, Cap(keepChain, keepLine, s0))
                    { MustReach = sEnd + ramp - s0 });
            }
        }
        return list;
    }

    private static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    // The speed cap of a way: the speed limits, the end of the lanes (if the way ends), the destination.
    private double Cap(List<Lane> chain, Polyline line, double s0)
    {
        double cap = double.MaxValue, start = 0;
        foreach (var l in chain)
        {
            double dist = Math.Max(0, start - s0);
            cap = Math.Min(cap, Math.Sqrt(l.SpeedLimit * l.SpeedLimit + 2 * 1.5 * dist));
            if (l.Id == _destLane)
            {
                double toDest = Math.Sqrt(2 * 1.5 * Math.Max(0, start + _destS - s0 - 1));
                // not yet in the destination lane (a lane change into it): crawl on, up to 20 m past the point, to finish the change
                if (_egoLaneId != _destLane && start + _destS + 20 - s0 > 0) toDest = Math.Max(toDest, 2.0);
                cap = Math.Min(cap, toDest);
            }
            start += l.Centre.Length;
        }
        if (chain[^1].Id != _destLane && chain[^1].Next.Count == 0)
            cap = Math.Min(cap, Math.Sqrt(2 * 2.0 * Math.Max(0, line.Length - s0 - EgoLength / 2 - 1)));   // the lane ends: stop before
        return cap;
    }

    // From a lane, the lanes the CAV follows: the next lane on the route; off the route, the one from which the destination is nearest.
    private List<Lane> RouteChain(Lane lane)
    {
        var chain = new List<Lane> { lane };
        double length = lane.Centre.Length;
        while (length < 250 && chain[^1].Id != _destLane)
        {
            var last = chain[^1];
            if (last.Next.Count == 0) break;
            var onRoute = last.Next.FirstOrDefault(n => _route.Contains(n) && _route.IndexOf(n) > _route.IndexOf(last.Id));
            string next = onRoute ?? last.Next.OrderBy(n => _map.Route(n, _destLane)?.Count ?? 999).First();
            if (chain.Any(c => c.Id == next)) break;
            chain.Add(_map.Lanes[next]);
            length += _map.Lanes[next].Centre.Length;
        }
        return chain;
    }

    // A way: along a line from (s0, d0), the lateral offset going from d0 to the profile over 'settle' metres.
    private static Polyline Build(Polyline line, double s0, double d0, Func<double, double> profile, double settle)
    {
        var pts = new List<Vec2>();
        double end = Math.Min(line.Length + 30, s0 + 200);
        for (double s = s0; s <= end; s += 1.0)
        {
            // ease-out (steepest at the start): replanned at every instant from where the CAV is, the way still moves it sideways
            double x = Math.Clamp((s - s0) / settle, 0, 1), k = x * (2 - x);
            double d = d0 * (1 - k) + profile(s) * k;
            pts.Add(line.At(s, d));
        }
        if (pts.Count < 2) pts.Add(line.At(s0 + 1, d0));
        return new Polyline(pts);
    }

    private static bool IsVulnerable(RoadUserKind k) => k is RoadUserKind.Pedestrian or RoadUserKind.Cyclist;

    // Does holding acceleration a for the commit time, then braking to a stop, come too near anything? The reason if so.
    // Three rules: (1) while moving, the CAV keeps a margin (wider for pedestrians and cyclists) and a time gap from every respected
    // hypothesis; (2) where it comes to a stop must not be in the way of any of them in the seconds after (it must not stop in
    // someone's way); (3) an assumed pedestrian (one that may step out from a hidden area) is allowed for with the CAV braking at
    // once, front only: the CAV must be able to stop before the pedestrian's way, or be past it.
    // The way is allowed if EITHER after the commit time the CAV brakes to a stop (the fallback of the hard rule) OR it carries on
    // speeding up to the speed the way allows (needed to merge into moving traffic: stopping half-way would be in its way) - each
    // checked against every respected hypothesis. A vehicle coming from behind is taken to brake gently (2 m/s2 after 1 s) if the CAV
    // is ahead of it, as a driver lets in someone who merges at a fair distance.
    // A pass (mustReach > 0) carried on must end, the CAV back in its lane, within the horizon.
    public string? Conflict(Polyline path, double v0, double a, IReadOnlyList<Prediction> predictions, double vCap, Vec2? egoPos, double egoHeading, double mustReach = 0)
    {
        var braking = Conflict(path, v0, a, predictions, false, egoPos, egoHeading, vCap, 0);
        if (braking is null || vCap <= 0.5) return braking;
        var going = Conflict(path, v0, a, predictions, true, egoPos, egoHeading, vCap, mustReach);
        return going is null ? null : braking;
    }

    private string? Conflict(Polyline path, double v0, double a, IReadOnlyList<Prediction> predictions, bool carryOn, Vec2? egoPos, double egoHeading, double vCap, double mustReach)
    {
        const double dt = 0.25, horizon = 6.0;
        var phantoms = predictions.Where(p => p.Assumed && p.Kind == RoadUserKind.Pedestrian).ToList();
        var others = predictions.Where(p => !(p.Assumed && p.Kind == RoadUserKind.Pedestrian)).ToList();
        // the vehicles behind the CAV now (they will let it in)
        // (only going the same way, within 30 degrees, and not where the CAV itself gives way: a roundabout, a give-way entry)
        var behind = new HashSet<Prediction>();
        var gap0 = new Dictionary<Prediction, double>();
        if (egoPos is Vec2 ep)
            foreach (var p in others)
            {
                if (p.Hypotheses.Count == 0) continue;
                var p0 = p.MostLikely.Points[0];
                gap0[p] = Discs.Gap(ep, egoHeading, EgoLength, EgoWidth, p0.Position, p0.Heading, p.Length, p.Width);
                if (!p.Assumed && !IsVulnerable(p.Kind) && !_egoGivesWay &&
                    Math.Abs(Angles.Diff(p0.Heading, egoHeading)) < 0.52 &&
                    (p0.Position - ep).Dot(Vec2.FromHeading(egoHeading)) < -(EgoLength + p.Length) / 2)
                    behind.Add(p);
            }

        double s = 0, v = v0, sH = 0, vH = v0;
        for (double t = dt; t <= horizon + 1e-9; t += dt)
        {
            double acc = t <= CommitTime + 1e-9 ? a : carryOn ? (v < vCap ? Math.Max(a, 1.0) : 0) : -FallbackBraking;
            v = Math.Max(0, v + acc * dt);
            if (carryOn) v = Math.Min(v, Math.Max(vCap, v0));
            s += v * dt;
            // against a vehicle that brakes as hard as it can, the CAV brakes as hard as it can too, after the commit time
            vH = Math.Max(0, vH + (t <= CommitTime + DetectionDelay + 1e-9 ? a : -EmergencyBraking) * dt);
            sH += vH * dt;
            if (sH >= 0.01)
                foreach (var p in others)
                    foreach (var h in p.Hypotheses)
                    {
                        if (h.Label != MotionPredictor.BrakesHard) continue;
                        var q = h.At(t);
                        var posH = path.At(sH);
                        if (posH.DistanceTo(q.Position) > (EgoLength + p.Length) / 2 + 6) continue;
                        double limitH = gap0.TryGetValue(p, out var gh) && gh < AssumedMargin ? Math.Max(0.05, gh - 0.05) : AssumedMargin;
                        if (Discs.Gap(posH, path.HeadingAt(sH), EgoLength, EgoWidth, q.Position, q.Heading, p.Length, p.Width + 0.2) < limitH)
                            return $"{Who(p)}{p.Kind} '{h.Label}' (p {h.Probability:F2}) at {t:F1} s";
                    }
            if (s < 0.01) continue;          // the CAV has not moved: standing still it brings itself near nothing
            var pos = path.At(s); var head = path.HeadingAt(s);
            bool stopped = v < 0.05;
            foreach (var p in others)
            {
                bool vulnerable = IsVulnerable(p.Kind);
                double baseMargin = p.Assumed ? AssumedMargin : vulnerable ? VulnerableMargin : VehicleMargin;
                var ml = p.MostLikely;
                foreach (var h in p.Hypotheses)
                {
                    if (h.Probability < MinProbability && !ReferenceEquals(h, ml)) continue;
                    if (h.Label == MotionPredictor.BrakesHard) continue;          // checked above, against the CAV braking as hard
                    bool moving = h.Points[0].Speed > 0.3 || h.Points[^1].Speed > 0.3;
                    if (stopped)
                    {
                        // (2) not in the way: where the CAV stops, against where the road user will be from now to the horizon
                        for (double tt = t; tt <= horizon + 1e-9; tt += dt)
                        {
                            var q = behind.Contains(p) ? h.At(Yielding(h, tt)) : h.At(tt);
                            if (pos.DistanceTo(q.Position) > (EgoLength + p.Length) / 2 + 6) continue;
                            if (Discs.Gap(pos, head, EgoLength, EgoWidth, q.Position, q.Heading, p.Length, p.Width) < (moving ? StopClearance : 0.1))
                                return $"{Who(p)}{p.Kind} '{h.Label}' (p {h.Probability:F2}): the CAV would stop in its way";
                        }
                        continue;
                    }
                    // (1) moving: margin and time gap (none toward a vehicle behind: it keeps its distance, braking gently if it must)
                    bool fromBehind = behind.Contains(p);
                    // where the CAV gives way (an entry, a roundabout, a junction) it accepts only a wider gap in the traffic
                    double gap = p.Assumed || !moving || fromBehind ? 0 : vulnerable ? TimeGap * 0.5 : _egoGivesWay ? GiveWayGap : TimeGap;
                    foreach (var tau in gap == 0 ? [0.0] : new[] { -gap, -gap / 2, 0, gap / 2, gap })
                    {
                        double tt = t + tau; if (tt < 0 || tt > horizon) continue;
                        var q = fromBehind ? h.At(Yielding(h, tt)) : h.At(tt);
                        if (pos.DistanceTo(q.Position) > (EgoLength + p.Length) / 2 + 8) continue;
                        // how unsure the CAV is of the place: along the road user's way (a vehicle) or all round (a pedestrian, a cyclist)
                        double sigma = h.Sigma(tt);
                        double length = p.Length + 2 * sigma;
                        double width = p.Width + (vulnerable ? sigma : 0.2);
                        var centre = q.Position;
                        if (!moving && !p.Assumed && !vulnerable)
                        {
                            // the stop distance: behind a stopped vehicle, for a CAV in line with it (not one going round it)
                            var axis = Vec2.FromHeading(q.Heading);
                            var rel = pos - q.Position;
                            bool inLine = Math.Abs(rel.Dot(axis.Left)) < (p.Width + EgoWidth) / 2 + 0.2;
                            if (inLine && rel.Dot(axis) < 0)
                            {
                                double stand = _lone.Contains(p.RoadUserId) ? LoneStandstillGap : StandstillGap;
                                length += stand; centre = centre - axis * (stand / 2);
                            }
                        }
                        // already nearer than the margin (after a pass that stalled): a move that brings it no nearer is allowed
                        double limit = gap0.TryGetValue(p, out var g0) && g0 < baseMargin ? Math.Max(0.05, g0 - 0.05) : baseMargin;
                        if (Discs.Gap(pos, head, EgoLength, EgoWidth, centre, q.Heading, length, width) < limit)
                            return $"{Who(p)}{p.Kind} '{h.Label}' (p {h.Probability:F2}) at {t:F1} s";
                    }
                }
            }
            if (stopped)
            {
                // (2b) keep the junction clear: a CAV not yet in a junction or roundabout does not stop with any part of it inside one
                if (_entering && InsideJunction(pos, head) is string where)
                    return $"the CAV would stop inside {where}";
                break;
            }
        }

        if (carryOn && mustReach > 0 && s < mustReach) return $"the pass would not end within {horizon:F0} s";

        // (3) assumed pedestrians: the CAV brakes after a short reaction; its front must not reach the pedestrian
        if (phantoms.Count > 0)
        {
            double reaction = 0.2;
            s = 0; v = v0;
            for (double t = dt / 2; t <= horizon + 1e-9; t += dt / 2)
            {
                double acc = t <= reaction + 1e-9 ? a : -FallbackBraking;
                v = Math.Max(0, v + acc * dt / 2);
                s += v * dt / 2;
                if (v < 0.05) break;
                var front = path.At(s + EgoLength / 2 - EgoWidth / 2);
                foreach (var p in phantoms)
                {
                    var q = p.Hypotheses[0].At(t);
                    if (front.DistanceTo(q.Position) < EgoWidth / 2 + p.Width / 2 + AssumedMargin)
                        return $"{p.Kind} '{p.Hypotheses[0].Label}' at {t:F1} s";
                }
            }
        }
        return null;
    }

    // Where on its hypothesis a vehicle behind is at time t if it brakes at 2 m/s2 after 1 s: the time it has reached, lagging.
    private static double Yielding(Hypothesis h, double t)
    {
        if (t <= 1) return t;
        double v = Math.Max(1, h.At(1).Speed), dtb = t - 1;
        double tStop = v / 2.0;
        double lost = dtb <= tStop ? dtb * dtb : tStop * tStop + 0;   // distance lost to braking (0.5 * 2 * dt^2), then it stands
        double lostTime = Math.Min(dtb, lost / v + (dtb > tStop ? dtb - tStop : 0));
        return t - lostTime;
    }

    private string? InsideJunction(Vec2 pos, double head)
    {
        foreach (var (c, r) in Discs.Of(pos, head, EgoLength, EgoWidth))
            foreach (var lane in _map.Lanes.Values)
            {
                if (lane.Kind is not (LaneKind.Junction or LaneKind.Roundabout)) continue;
                var (s, d) = lane.Centre.Project(c);
                if (s >= 0 && s <= lane.Centre.Length && Math.Abs(d) < lane.Width / 2 + r) return $"the {(lane.Kind == LaneKind.Junction ? "junction" : "roundabout")} ({lane.Id})";
            }
        return null;
    }

    private static string Who(Prediction p) => p.Assumed ? "" : $"road user {p.RoadUserId} ";
}

