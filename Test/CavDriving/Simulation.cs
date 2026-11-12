using Mpai.Cav.Driving;

namespace Mpai.Cav.DrivingSim;

public sealed class EgoTruth
{
    public Vec2 Position { get; set; }
    public double Heading { get; set; }
    public double Speed { get; set; }
    public double Length { get; init; } = 4.6;
    public double Width { get; init; } = 1.9;
}

// One traffic context with one variant: the road map, what hides the view, the road users, where the CAV starts and goes.
public sealed class Scenario
{
    public required string Context { get; init; }
    public required string Variant { get; init; }
    public required RoadMap Map { get; init; }
    public List<Box> Occluders { get; init; } = [];
    public required List<Agent> Agents { get; init; }
    public required string StartLane { get; init; }
    public double StartS { get; init; }
    public double StartSpeed { get; init; }
    public required string DestLane { get; init; }
    public required double DestS { get; init; }
    public double MaxTime { get; init; } = 90;
    public Dictionary<string, object> Parameters { get; init; } = [];
    public int Seed { get; init; } = 1;
}

public sealed class World
{
    public double Time { get; set; }
    public required RoadMap Map { get; init; }
    public required List<Agent> Agents { get; init; }
    public required List<Box> Occluders { get; init; }
    public required EgoTruth Ego { get; init; }

    public IEnumerable<(Vec2 Pos, double Heading, double Length, double Width, double Speed, object Self)> Occupants()
    {
        foreach (var a in Agents) if (a.Kind != RoadUserKind.Pedestrian || a.Started) yield return (a.Position, a.Heading, a.Length, a.Width, a.V, a);
        yield return (Ego.Position, Ego.Heading, Ego.Length, Ego.Width, Ego.Speed, Ego);
    }

    public Box BoxOf(Agent a) => new(a.Position, a.Heading, a.Length, a.Width);
}

// What the CAV senses: the road users in range and in sight (an occluder or a stopped vehicle between hides them), with
// noise; and how far it sees along each lane near it.
public sealed class Sensor(double range = 150, double positionNoise = 0.15, double headingNoise = 0.03)
{
    public SensorFrame Sense(World w, Random rng)
    {
        var ego = w.Ego;
        var blockers = new List<(Box Box, Agent? A)>(w.Occluders.Select(o => (o, (Agent?)null)));
        foreach (var a in w.Agents) if (a.Kind is RoadUserKind.Car or RoadUserKind.Truck) blockers.Add((w.BoxOf(a), a));
        var dets = new List<Detection>();
        foreach (var a in w.Agents)
        {
            if (a.Kind == RoadUserKind.Pedestrian && !a.Started && !Visible(ego.Position, a.Position, blockers, a)) continue;
            if (a.Position.DistanceTo(ego.Position) > range) continue;
            var f = Vec2.FromHeading(a.Heading) * (a.Length / 2 * 0.8);
            if (!(Visible(ego.Position, a.Position, blockers, a) || Visible(ego.Position, a.Position + f, blockers, a) || Visible(ego.Position, a.Position - f, blockers, a))) continue;
            var p = a.Position + new Vec2(Gauss(rng) * positionNoise, Gauss(rng) * positionNoise);
            dets.Add(new Detection(a.Kind, p, a.Heading + Gauss(rng) * headingNoise, a.Length, a.Width, a.Indicator));
        }
        var vis = new Dictionary<string, (double, double)>();
        var staticBlockers = blockers.Where(b => b.A is null || b.A.Parked || b.A.V < 0.1).ToList();
        foreach (var lane in w.Map.Lanes.Values)
        {
            var (sNear, _) = lane.Centre.Project(ego.Position);
            sNear = Math.Clamp(sNear, 0, lane.Centre.Length);
            if (lane.Centre.At(sNear).DistanceTo(ego.Position) > range) continue;
            bool Seen(double s) { var p = lane.Centre.At(s); return p.DistanceTo(ego.Position) <= range && Visible(ego.Position, p, staticBlockers, null); }
            if (!Seen(sNear)) continue;
            double from = sNear, to = sNear;
            while (to < lane.Centre.Length && Seen(Math.Min(lane.Centre.Length, to + 2))) to = Math.Min(lane.Centre.Length, to + 2);
            while (from > 0 && Seen(Math.Max(0, from - 2))) from = Math.Max(0, from - 2);
            vis[lane.Id] = (from, to);
        }
        return new SensorFrame(w.Time, ego.Position, ego.Heading, ego.Speed, dets, vis);
    }

    private static bool Visible(Vec2 from, Vec2 to, List<(Box Box, Agent? A)> blockers, Agent? target)
    {
        foreach (var (b, a) in blockers)
        {
            if (a is not null && ReferenceEquals(a, target)) continue;
            if (b.Blocks(from, to)) return false;
        }
        return true;
    }

    public static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}

public sealed class Simulation
{
    public const double Dt = 0.1;
    public const double RecordEvery = 0.5;

    public static RunRecord Run(Scenario sc)
    {
        var rng = new Random(sc.Seed);
        var startLane = sc.Map.Lanes[sc.StartLane];
        var ego = new EgoTruth { Position = startLane.Centre.At(sc.StartS), Heading = startLane.Centre.HeadingAt(sc.StartS), Speed = sc.StartSpeed };
        var world = new World { Map = sc.Map, Agents = sc.Agents, Occluders = sc.Occluders, Ego = ego };
        var sensor = new Sensor();
        var describer = new SceneDescriber(sc.Map);
        var predictor = new MotionPredictor(sc.Map);
        var driver = new Driver(sc.Map, sc.StartLane, sc.DestLane, sc.DestS);
        var rec = new RunRecord { Context = sc.Context, Variant = sc.Variant, Parameters = sc.Parameters, Route = driver.Route.ToList() };
        var truth = new Dictionary<int, List<(double T, Vec2 P)>>();
        var predictionsKept = new List<(double T, int Track, Vec2 TrackPos, RoadUserKind Kind, Prediction P)>();
        var closest = sc.Agents.ToDictionary(a => a.Id, _ => double.MaxValue);
        string result = "stopped (time out)"; double? collisionTime = null; int? collisionWith = null; double collisionSpeed = 0;
        int emergencies = 0; double maxBraking = 0; bool wasEmergency = false;
        double nextRecord = 0;
        var reasons = new List<(double T, string Reason, bool Emergency)>();

        for (world.Time = 0; world.Time <= sc.MaxTime + 1e-9; world.Time = Math.Round(world.Time + Dt, 6))
        {
            foreach (var a in sc.Agents)
            {
                if (!truth.TryGetValue(a.Id, out var list)) truth[a.Id] = list = [];
                list.Add((world.Time, a.Position));
            }
            var frame = sensor.Sense(world, rng);
            var scene = describer.Describe(frame);
            var preds = predictor.Predict(scene);
            if (driver.Arrived(scene)) { result = "arrived"; break; }
            var decision = driver.Decide(scene, preds);
            reasons.Add((world.Time, decision.Reason, decision.EmergencyStop));
            if (decision.EmergencyStop && ego.Speed > 0.5 && !wasEmergency) emergencies++;
            wasEmergency = decision.EmergencyStop && ego.Speed > 0.5;

            if (world.Time + 1e-9 >= nextRecord)
            {
                nextRecord += RecordEvery;
                rec.Timeline.Add(Snapshot.Of(world, scene, preds, decision));
                foreach (var p in preds.Where(p => !p.Assumed))
                {
                    var u = scene.RoadUsers.First(r => r.Id == p.RoadUserId);
                    // the errors are measured on road users described for 0.5 s or more (before, the velocity is not yet known)
                    if (u.SeenFor >= 0.5) predictionsKept.Add((world.Time, u.Id, u.Position, u.Kind, p));
                }
            }

            // the CAV moves along the way it chose
            double acc = Math.Clamp(decision.Acceleration, -8, 2.5);
            if (acc < 0) maxBraking = Math.Max(maxBraking, -acc * (ego.Speed > 0.05 ? 1 : 0));
            double v1 = Math.Max(0, ego.Speed + acc * Dt);
            if (acc <= 0 && v1 < 0.1) v1 = 0;            // not creeping: below 0.1 m/s and not speeding up, the CAV stands
            double ds = (ego.Speed + v1) / 2 * Dt;
            var path = decision.Chosen.Path;
            if (ds > 1e-6)
            {
                var (s0, _) = path.Project(ego.Position);
                ego.Position = path.At(s0 + ds); ego.Heading = path.HeadingAt(s0 + ds);
            }
            ego.Speed = v1;

            foreach (var a in sc.Agents) a.Step(world, Dt);

            // the truth: gaps and collisions
            foreach (var a in sc.Agents)
            {
                if (a.Kind == RoadUserKind.Pedestrian && !a.Started) continue;
                var gap = Discs.Gap(ego.Position, ego.Heading, ego.Length, ego.Width, a.Position, a.Heading, a.Length, a.Width);
                closest[a.Id] = Math.Min(closest[a.Id], gap);
                if (gap <= 0.01 && collisionTime is null) { collisionTime = world.Time; collisionWith = a.Id; collisionSpeed = ego.Speed; }
            }
            if (collisionTime is not null) { result = ego.Speed < 0.1 ? "collision (the CAV stopped, hit by another)" : "COLLISION"; break; }
        }

        rec.Outcome = new Outcome
        {
            Result = result, Time = Math.Round(world.Time, 1), EmergencyStops = emergencies, MaxBraking = Math.Round(maxBraking, 1),
            CollisionWith = collisionWith, CollisionSpeed = Math.Round(collisionSpeed, 2),
            Closest = sc.Agents.Where(a => closest[a.Id] < double.MaxValue).ToDictionary(a => $"{a.Id} {a.Kind}", a => Math.Round(closest[a.Id], 2)),
            LastReasons = reasons.TakeLast(30).Select(r => r.Reason).GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()}x {g.Key}").Take(3).ToList(),
            HeldBack = reasons.Where(r => r.Reason.StartsWith("held back")).Select(r => r.Reason).GroupBy(r => Strip(r)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count() * Dt:F1} s {g.Key}").Take(3).ToList(),
        };
        rec.Inference = InferenceErrors.Of(predictionsKept, truth, sc.Agents, rec);
        return rec;
    }

    private static string Strip(string r)
    {
        var i = r.LastIndexOf(" at ", StringComparison.Ordinal);
        r = i > 0 ? r[..i] : r;
        var j = r.IndexOf(" (p ", StringComparison.Ordinal);
        return j > 0 ? r[..j] : r;
    }
}
