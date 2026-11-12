namespace Mpai.Cav.Driving;

public enum LaneKind { Road, Junction, Roundabout, Ramp }

// A lane of the road map: its centre line in the direction of travel, its width, its speed limit, where it leads, its neighbours.
public sealed class Lane
{
    public required string Id { get; init; }
    public required Polyline Centre { get; init; }
    public double Width { get; init; } = 3.5;
    public double SpeedLimit { get; init; } = 13.9;
    public LaneKind Kind { get; init; } = LaneKind.Road;
    public List<string> Next { get; } = [];
    public string? Left { get; set; }            // the lane to the left, same direction (a lane change is allowed)
    public string? Right { get; set; }           // the lane to the right, same direction
    public string? Oncoming { get; set; }        // the lane to the left, other direction (it may be used to pass)
    public bool YieldAtEnd { get; init; }        // a road user at the end of this lane gives way (a roundabout entry, a minor road)
    public override string ToString() => Id;
}

// A pedestrian crossing or a pavement next to the road: where pedestrians are expected.
public sealed record Walkway(string Id, Polyline Line, bool Crossing);

public sealed class RoadMap
{
    public Dictionary<string, Lane> Lanes { get; } = [];
    public List<Walkway> Walkways { get; } = [];

    public Lane Add(Lane lane) { Lanes[lane.Id] = lane; return lane; }

    public void Connect(string from, params string[] to) => Lanes[from].Next.AddRange(to);

    public void SideBySide(string left, string right) { Lanes[left].Right = right; Lanes[right].Left = left; }

    // The lanes a road user at p with heading h can be in: near the centre line and roughly facing along it, the best first.
    public List<(Lane Lane, double S, double D)> Matches(Vec2 p, double? heading, double slack = 0.6, double maxAngle = 1.0)
    {
        var found = new List<(Lane, double, double, double)>();
        foreach (var lane in Lanes.Values)
        {
            var (s, d) = lane.Centre.Project(p);
            if (s < -1 || s > lane.Centre.Length + 1) continue;
            if (Math.Abs(d) > lane.Width / 2 + slack) continue;
            double angle = heading is double h ? Math.Abs(Angles.Diff(h, lane.Centre.HeadingAt(s))) : 0;
            if (angle > maxAngle) continue;
            found.Add((lane, s, d, Math.Abs(d) + 3 * angle));
        }
        return found.OrderBy(f => f.Item4).Select(f => (f.Item1, f.Item2, f.Item3)).ToList();
    }

    // Is p on any lane (the carriageway)?
    public bool OnCarriageway(Vec2 p) => Lanes.Values.Any(l => { var (s, d) = l.Centre.Project(p); return s >= 0 && s <= l.Centre.Length && Math.Abs(d) <= l.Width / 2; });

    // The lanes reached from a lane within a distance: each chain of lanes a road user may follow, with the turn it makes.
    public List<List<Lane>> Chains(Lane start, double startS, double distance, int maxChains = 6)
    {
        var result = new List<List<Lane>>();
        void Walk(List<Lane> chain, double left)
        {
            var nexts = chain[^1].Next.Where(n => chain.All(c => c.Id != n)).Select(n => Lanes[n]).ToList();
            if (left <= 0 || nexts.Count == 0 || result.Count >= maxChains) { result.Add(chain); return; }
            foreach (var next in nexts) Walk([.. chain, next], left - next.Centre.Length);
        }
        Walk([start], distance - (start.Centre.Length - startS));
        return result;
    }

    // One line through a chain of lanes, from the start of the first lane.
    public static Polyline Line(IReadOnlyList<Lane> chain)
    {
        var line = chain[0].Centre;
        for (int i = 1; i < chain.Count; i++) line = line.Then(chain[i].Centre);
        return line;
    }

    // The route: the lanes from (lane, s) to the destination lane, by successors and lane changes (a lane change counts 40 m).
    public List<string>? Route(string fromLane, string toLane)
    {
        var dist = new Dictionary<string, double> { [fromLane] = 0 };
        var prev = new Dictionary<string, string>();
        var open = new PriorityQueue<string, double>();
        open.Enqueue(fromLane, 0);
        var done = new HashSet<string>();
        while (open.TryDequeue(out var id, out var d))
        {
            if (!done.Add(id)) continue;
            if (id == toLane) break;
            var lane = Lanes[id];
            var edges = lane.Next.Select(n => (n, lane.Centre.Length)).ToList();
            if (lane.Left is string l) edges.Add((l, 40));
            if (lane.Right is string r) edges.Add((r, 40));
            foreach (var (n, w) in edges)
            {
                var nd = d + w;
                if (!dist.TryGetValue(n, out var old) || nd < old) { dist[n] = nd; prev[n] = id; open.Enqueue(n, nd); }
            }
        }
        if (!done.Contains(toLane)) return null;
        var route = new List<string> { toLane };
        while (route[^1] != fromLane) route.Add(prev[route[^1]]);
        route.Reverse();
        return route;
    }
}
