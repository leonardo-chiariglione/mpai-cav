namespace Mpai.Cav.Driving;

public enum RoadUserKind { Car, Truck, Cyclist, Pedestrian, Obstacle }

// WHAT THE CAV SENSES at one instant (what the Environment Sensing Subsystem would give): its own pose, the things it detects
// (no identity: the description keeps them apart), and how far it sees along each lane near it.
public sealed record Detection(RoadUserKind Kind, Vec2 Position, double Heading, double Length, double Width, int Indicator = 0);

public sealed record SensorFrame(double Time, Vec2 EgoPosition, double EgoHeading, double EgoSpeed, IReadOnlyList<Detection> Detections,
                                 IReadOnlyDictionary<string, (double From, double To)> LaneVisibility);

// STEP 1 - THE SCENE. A road user as the CAV describes it: what it is, where it is, how it moves, on which lane.
public sealed record DescribedRoadUser(int Id, RoadUserKind Kind, Vec2 Position, Vec2 Velocity, double Heading, double Acceleration,
                                       double Length, double Width, string? LaneId, double S, double D, double LateralSpeed,
                                       int Indicator, bool Stationary, double SeenFor)
{
    public double Speed => Velocity.Length;
    public bool Vulnerable => Kind is RoadUserKind.Pedestrian or RoadUserKind.Cyclist;
}

// Something that hides part of the road: a stationary vehicle next to the lanes, behind which a pedestrian may be.
public sealed record HiddenArea(int ByRoadUser, Vec2 Edge, Vec2 TowardRoad);

public sealed record EgoState(Vec2 Position, double Heading, double Speed, string? LaneId, double S, double D);

public sealed record Scene(double Time, EgoState Ego, IReadOnlyList<DescribedRoadUser> RoadUsers, IReadOnlyList<HiddenArea> HiddenAreas,
                           IReadOnlyDictionary<string, (double From, double To)> Visibility, IReadOnlyList<string> Features);

// Builds the scene at each instant from the frames: keeps each road user apart over time (nearest detection of the same kind
// within a gate), smooths its position and estimates its velocity and acceleration (an alpha-beta-gamma filter), places it
// on the road map, and finds the hidden areas.
public sealed class SceneDescriber(RoadMap map)
{
    private sealed class Track
    {
        public int Id; public RoadUserKind Kind; public Vec2 P; public Vec2 V; public Vec2 A; public double Heading;
        public double Length, Width; public int Indicator; public double FirstSeen, LastSeen; public double LateralSpeed;
        public double? LastD; public string? LastLane; public int Updates; public Vec2 Measured;
    }

    private readonly List<Track> _tracks = [];
    private int _next = 1;
    private double? _lastTime;
    public double Gate { get; init; } = 3.0;
    public double KeepUnseen { get; init; } = 1.0;    // seconds a road user no longer detected is still described (where predicted)

    public Scene Describe(SensorFrame f)
    {
        double dt = _lastTime is double t0 ? Math.Max(1e-3, f.Time - t0) : 0.1;
        _lastTime = f.Time;
        // predict each track to now
        foreach (var t in _tracks) { t.P = t.P + t.V * dt + t.A * (0.5 * dt * dt); t.V = t.V + t.A * dt; }
        // associate: the closest pairs first
        var pairs = new List<(double Dist, Track T, int D)>();
        for (int i = 0; i < f.Detections.Count; i++)
            foreach (var t in _tracks)
                if (t.Kind == f.Detections[i].Kind)
                {
                    var dist = t.P.DistanceTo(f.Detections[i].Position);
                    if (dist < Gate + t.V.Length * 0.1) pairs.Add((dist, t, i));
                }
        var usedT = new HashSet<Track>(); var usedD = new HashSet<int>();
        // the gains of a critically damped alpha-beta-gamma filter (discount 0.85)
        const double theta = 0.85, alpha = 1 - theta * theta * theta, beta = 1.5 * (1 - theta) * (1 - theta) * (1 + theta), gamma = 0.5 * (1 - theta) * (1 - theta) * (1 - theta);
        foreach (var (_, t, i) in pairs.OrderBy(p => p.Dist))
        {
            if (usedT.Contains(t) || usedD.Contains(i)) continue;
            usedT.Add(t); usedD.Add(i);
            var d = f.Detections[i];
            if (t.Updates == 0)
            {   // the second sighting gives the first velocity
                t.V = (d.Position - t.Measured) * (1 / Math.Max(1e-3, f.Time - t.LastSeen)); t.P = d.Position; t.A = new(0, 0);
            }
            else
            {
                var r = d.Position - t.P;
                t.P = t.P + r * alpha;
                t.V = t.V + r * (beta / dt);
                t.A = t.A + r * (2 * gamma / (dt * dt));
            }
            t.Updates++; t.Measured = d.Position;
            var am = t.A.Length; if (am > 10) t.A = t.A * (10 / am);
            // a vehicle's heading is sensed (its box); a pedestrian's is that of its motion
            t.Heading = d.Kind == RoadUserKind.Pedestrian && t.V.Length > 0.3 ? t.V.Heading : d.Heading;
            t.Length = d.Length; t.Width = d.Width; t.Indicator = d.Indicator; t.LastSeen = f.Time;
        }
        for (int i = 0; i < f.Detections.Count; i++)
        {
            if (usedD.Contains(i)) continue;
            var d = f.Detections[i];
            _tracks.Add(new Track { Id = _next++, Kind = d.Kind, P = d.Position, Measured = d.Position, V = new(0, 0), A = new(0, 0), Heading = d.Heading, Length = d.Length, Width = d.Width, Indicator = d.Indicator, FirstSeen = f.Time, LastSeen = f.Time });
        }
        _tracks.RemoveAll(t => f.Time - t.LastSeen > KeepUnseen);

        // the CAV on the map
        var egoMatch = map.Matches(f.EgoPosition, f.EgoHeading, 1.5).FirstOrDefault();
        var ego = new EgoState(f.EgoPosition, f.EgoHeading, f.EgoSpeed, egoMatch.Lane?.Id, egoMatch.S, egoMatch.D);

        var users = new List<DescribedRoadUser>();
        foreach (var t in _tracks)
        {
            bool stationary = t.V.Length < 0.5 && f.Time - t.FirstSeen > 0.3;
            if (t.Kind == RoadUserKind.Obstacle) { stationary = true; t.V = new(0, 0); t.A = new(0, 0); }
            double? heading = t.Kind == RoadUserKind.Pedestrian ? null : t.Heading;
            var m = t.Kind == RoadUserKind.Pedestrian ? default : map.Matches(t.P, heading).FirstOrDefault();
            // the speed across the lane (positive to the left), from the filtered velocity
            t.LateralSpeed = m.Lane is not null && !stationary ? t.V.Dot(Vec2.FromHeading(m.Lane.Centre.HeadingAt(m.S)).Left) : 0;
            t.LastD = m.Lane is not null ? m.D : null; t.LastLane = m.Lane?.Id;
            double along = t.V.Length > 0.5 ? t.A.Dot(t.V.Unit) : 0;
            users.Add(new DescribedRoadUser(t.Id, t.Kind, t.P, stationary ? new(0, 0) : t.V, t.Heading, stationary ? 0 : along, t.Length, t.Width,
                                            m.Lane?.Id, m.S, m.D, t.LateralSpeed, t.Indicator, stationary, f.Time - t.FirstSeen));
        }

        // hidden areas: a stationary vehicle that is NOT on a lane (parked next to it) hides a strip of pavement and road
        var hidden = new List<HiddenArea>();
        foreach (var u in users.Where(u => u.Stationary && u.Kind is RoadUserKind.Car or RoadUserKind.Truck))
        {
            if (map.OnCarriageway(u.Position)) continue;
            var nearest = map.Matches(u.Position, null, 4.0).FirstOrDefault();
            if (nearest.Lane is null) continue;
            var lanePoint = nearest.Lane.Centre.At(nearest.S);
            var toward = (lanePoint - u.Position).Unit;
            var along = Vec2.FromHeading(nearest.Lane.Centre.HeadingAt(nearest.S));
            // the edge of the vehicle on the road side, at its far end (seen from the CAV) - where someone steps out
            var farSign = (u.Position - f.EgoPosition).Dot(along) >= 0 ? 1 : -1;
            var edge = u.Position + along * (farSign * (u.Length / 2 + 0.3)) + toward * (u.Width / 2);
            if ((edge - f.EgoPosition).Dot(along) > 0) hidden.Add(new HiddenArea(u.Id, edge, toward));
            var mid = u.Position + toward * (u.Width / 2 + 0.2);
            if ((mid - f.EgoPosition).Dot(along) > 0) hidden.Add(new HiddenArea(u.Id, mid, toward));
        }

        // what kind of place the CAV is in or near (100 m ahead on its lane chain)
        var features = new List<string>();
        if (egoMatch.Lane is not null)
            foreach (var chain in map.Chains(egoMatch.Lane, egoMatch.S, 100))
                foreach (var l in chain)
                {
                    var name = l.Kind switch { LaneKind.Junction => "junction", LaneKind.Roundabout => "roundabout", LaneKind.Ramp => "ramp", _ => null };
                    if (name is not null && !features.Contains(name)) features.Add(name);
                }
        if (map.Walkways.Any(w => w.Crossing && w.Line.Points.Any(p => p.DistanceTo(f.EgoPosition) < 60))) features.Add("crossing");
        if (users.Any(u => u.Kind == RoadUserKind.Pedestrian)) features.Add("pedestrians");
        if (users.Any(u => u.Kind == RoadUserKind.Cyclist)) features.Add("cyclists");

        return new Scene(f.Time, ego, users, hidden, f.LaneVisibility, features);
    }
}
