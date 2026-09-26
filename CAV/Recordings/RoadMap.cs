using System.Text.Json.Nodes;

namespace Mpai.Cav.Recordings;

// THE OFFLINE MAP OF THE SYNTHETIC CAV (M3233 3.1): way points at junctions, and
// road segments between them, each with its direction of travel, its lanes and its
// speed limit; on a local frame of east and north metres from an origin, whose
// geodetic coordinates the map states. Made from a seed, as the drives are.
public sealed record WayPoint(string Id, double East, double North);
public sealed record RoadSegment(string Id, string From, string To, double SpeedLimit, int Lanes);

public sealed class RoadMap
{
    public const double LaneWidth = CameraRenderer.Lane;

    public string Id { get; }
    public double OriginLat { get; }
    public double OriginLon { get; }
    public IReadOnlyList<WayPoint> WayPoints { get; }
    public IReadOnlyList<RoadSegment> Segments { get; }
    private readonly Dictionary<string, WayPoint> points;

    public RoadMap(string id, double originLat, double originLon, IReadOnlyList<WayPoint> wayPoints, IReadOnlyList<RoadSegment> segments)
    {
        (Id, OriginLat, OriginLon, WayPoints, Segments) = (id, originLat, originLon, wayPoints, segments);
        points = wayPoints.ToDictionary(w => w.Id);
    }

    public WayPoint Point(string id) => points[id];

    public double Length(RoadSegment s)
    {
        var (a, b) = (points[s.From], points[s.To]);
        return Math.Sqrt(Math.Pow(b.East - a.East, 2) + Math.Pow(b.North - a.North, 2));
    }

    // A grid of columns x rows way points, spacing metres apart, from the origin of
    // the drives (Turin); a segment each way between neighbours, three lanes each
    // way, its speed limit 50 or 70 km/h as the seed decides.
    public static RoadMap Grid(int seed, int columns = 3, int rows = 3, double spacing = 250)
    {
        var random = new Random(seed);
        var wayPoints = new List<WayPoint>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                wayPoints.Add(new WayPoint($"W{c}{r}", c * spacing, r * spacing));
        var segments = new List<RoadSegment>();
        void Road(string a, string b)
        {
            var limit = random.NextDouble() < 0.5 ? 50 / 3.6 : 70 / 3.6;
            segments.Add(new RoadSegment($"S{a}{b}", a, b, Math.Round(limit, 3), 3));
            segments.Add(new RoadSegment($"S{b}{a}", b, a, Math.Round(limit, 3), 3));
        }
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                if (c + 1 < columns) Road($"W{c}{r}", $"W{c + 1}{r}");
                if (r + 1 < rows) Road($"W{c}{r}", $"W{c}{r + 1}");
            }
        return new RoadMap($"MAP-seed{seed}", 45.0703, 7.6869, wayPoints, segments);
    }

    // THE ROUTE OF LEAST TIME between two way points: A* on the segments, each
    // costing its length over its speed limit; the heuristic the straight line at the
    // highest limit. Null where there is none.
    public IReadOnlyList<RoadSegment>? FastestRoute(string from, string to)
    {
        var fastest = Segments.Max(s => s.SpeedLimit);
        double H(string w) => Math.Sqrt(Math.Pow(points[w].East - points[to].East, 2) + Math.Pow(points[w].North - points[to].North, 2)) / fastest;
        var cost = new Dictionary<string, double> { [from] = 0 };
        var via = new Dictionary<string, RoadSegment>();
        var open = new PriorityQueue<string, double>();
        open.Enqueue(from, H(from));
        var closed = new HashSet<string>();
        while (open.TryDequeue(out var w, out _))
        {
            if (w == to) break;
            if (!closed.Add(w)) continue;
            foreach (var s in Segments.Where(s => s.From == w))
            {
                var c = cost[w] + Length(s) / s.SpeedLimit;
                if (cost.TryGetValue(s.To, out var known) && known <= c) continue;
                cost[s.To] = c; via[s.To] = s;
                open.Enqueue(s.To, c + H(s.To));
            }
        }
        if (!via.ContainsKey(to) && from != to) return null;
        var route = new List<RoadSegment>();
        for (var w = to; w != from; w = via[w].From) route.Insert(0, via[w]);
        return route;
    }

    public double TimeOf(IEnumerable<RoadSegment> route) => route.Sum(s => Length(s) / s.SpeedLimit);

    // Geodetic coordinates of a point of the map's frame.
    public (double Lat, double Lon) Geodetic(double east, double north)
    {
        const double earth = 6_371_000;
        return (OriginLat + north / earth * 180 / Math.PI, OriginLon + east / (earth * Math.Cos(OriginLat * Math.PI / 180)) * 180 / Math.PI);
    }

    // The map as the data of a Basic Offline Map Object.
    public JsonObject ToJson() => new()
    {
        ["MapID"] = Id, ["Origin"] = new JsonObject { ["Lat"] = OriginLat, ["Lon"] = OriginLon }, ["LaneWidth"] = LaneWidth,
        ["WayPoints"] = new JsonArray(WayPoints.Select(w => (JsonNode)new JsonObject { ["WayPointID"] = w.Id, ["East"] = w.East, ["North"] = w.North }).ToArray()),
        ["Segments"] = new JsonArray(Segments.Select(s => (JsonNode)new JsonObject
        {
            ["SegmentID"] = s.Id, ["From"] = s.From, ["To"] = s.To, ["SpeedLimit"] = s.SpeedLimit, ["Lanes"] = s.Lanes
        }).ToArray())
    };

    public static RoadMap FromJson(JsonNode map) => new(
        (string)map["MapID"]!, (double)map["Origin"]!["Lat"]!, (double)map["Origin"]!["Lon"]!,
        map["WayPoints"]!.AsArray().Select(w => new WayPoint((string)w!["WayPointID"]!, (double)w["East"]!, (double)w["North"]!)).ToList(),
        map["Segments"]!.AsArray().Select(s => new RoadSegment((string)s!["SegmentID"]!, (string)s["From"]!, (string)s["To"]!, (double)s["SpeedLimit"]!, (int)s["Lanes"]!)).ToList());
}

// A ROUTE AS A LINE TO FOLLOW: its way points joined, a position and a heading at
// each distance along it.
public sealed class RoutePath
{
    private readonly List<(double S, double East, double North, double Heading, RoadSegment Segment)> legs = [];
    public double Length { get; }
    public IReadOnlyList<RoadSegment> Segments { get; }

    public RoutePath(RoadMap map, IReadOnlyList<RoadSegment> route)
    {
        Segments = route;
        double s = 0;
        foreach (var seg in route)
        {
            var (a, b) = (map.Point(seg.From), map.Point(seg.To));
            legs.Add((s, a.East, a.North, Math.Atan2(b.North - a.North, b.East - a.East), seg));
            s += map.Length(seg);
        }
        Length = s;
    }

    // Where the line is at s (clamped to it), its heading (radians, anticlockwise
    // from east), and the segment it is on.
    public (double East, double North, double Heading, RoadSegment Segment) At(double s)
    {
        s = Math.Clamp(s, 0, Length);
        var leg = legs.Last(l => l.S <= s);
        var along = s - leg.S;
        return (leg.East + along * Math.Cos(leg.Heading), leg.North + along * Math.Sin(leg.Heading), leg.Heading, leg.Segment);
    }
}
